using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

internal sealed record DiscordPreparedOutreach(
    string Content,
    string DeliveryContent,
    IReadOnlyList<ExternalConversationOutputAsset> OutputAssets);

internal sealed record DiscordPreparedCorrection(
    string? DeliveryContent,
    IReadOnlyList<ExternalConversationOutputAsset>? OutputAssets);

public sealed class DiscordConversationCoordinator(
    DiscordBindingStore bindings,
    DiscordDeliveryStore deliveries,
    DiscordBotConfigurationStore botConfigurations,
    DiscordBehaviorStore behaviors,
    DiscordRuntimeOptionsResolver runtimeOptions,
    DiscordUserIdentityResolver userIdentities,
    IComputeGateway compute,
    IEnumerable<IExternalAgentConversationProvider> providers,
    PromptInjectionDetector injection,
    DiscordEgressGuard egress,
    DiscordTypingLeases typing,
    ILogger<DiscordConversationCoordinator> logger,
    IUntrustedContentReview? sentinel = null,
    TimeProvider? time = null) : IDisposable
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    internal DiscordConversationCoordinator(
        DiscordBindingStore bindings,
        DiscordDeliveryStore deliveries,
        DiscordBotConfigurationStore botConfigurations,
        DiscordRuntimeOptionsResolver runtimeOptions,
        DiscordUserIdentityResolver userIdentities,
        IComputeGateway compute,
        IEnumerable<IExternalAgentConversationProvider> providers,
        PromptInjectionDetector injection,
        DiscordEgressGuard egress,
        DiscordTypingLeases typing,
        ILogger<DiscordConversationCoordinator> logger)
        : this(bindings, deliveries, botConfigurations,
            new DiscordBehaviorStore(botConfigurations.EntityStore),
            runtimeOptions, userIdentities, compute, providers, injection,
            egress, typing, logger)
    {
    }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly List<IDisposable> subscriptions = [];

    public event Action<DiscordBinding>? Reconciled;

    /// <summary>
    /// Raised when Sentinel-blocked work (a refused inbound message or an unavailable outbound
    /// review) should be retried at the given time, so the host can wake the binding then.
    /// </summary>
    public event Action<DiscordBinding, DateTimeOffset>? RetryDue;

    public void Start()
    {
        if (subscriptions.Count > 0) return;
        foreach (var provider in providers)
            subscriptions.Add(provider.SubscribeSettled(OnSettledAsync));
    }

    public async Task<ExternalConversationAdmission?> SubmitAsync(
        DiscordBinding binding,
        string discordMessageId,
        ExternalRequestor requestor,
        string content,
        IReadOnlyList<ExternalConversationAttachment>? attachments = null,
        CancellationToken ct = default,
        JsonObject? guildContext = null,
        JsonObject? behaviorContext = null,
        JsonObject? relayContext = null,
        DiscordChannelGapSource? channelGap = null)
    {
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            binding = await bindings.GetAsync(binding.EntityId, ct)
                ?? throw new InvalidOperationException("Discord binding disappeared during delivery");
            if (!binding.Enabled) return null;

            // A retried delivery reuses the gap captured when it was first received, so it
            // can neither duplicate nor reshuffle the messages its session sees.
            var existing = await deliveries.FindInboundAsync(binding, discordMessageId, ct);
            if (existing is not null && existing.State != "received") return null;
            var channelGapRecord = existing is not null
                ? existing.Payload?["channel_gap"]?.DeepClone() as JsonObject
                : await ReadChannelGapAsync(binding, discordMessageId, channelGap, ct);
            var sessionContent = DiscordChannelGaps.Compose(channelGapRecord, content);

            if (behaviorContext is null)
            {
                var config = await botConfigurations.FindByApplicationIdAsync(
                    binding.ApplicationId, ct);
                behaviorContext = config is null
                    ? new DiscordBehavior(
                        null, Guid.Empty,
                        binding.Kind == "dm"
                            ? DiscordBehaviorScopes.DmDefault
                            : DiscordBehaviorScopes.Default,
                        binding.GuildId, binding.ConversationId,
                        binding.ResponseMode, null, "", true,
                        Persisted: false).ToTrustedMetadata()
                    : (await behaviors.ResolveAsync(config, binding, ct))
                        .ToTrustedMetadata();
            }

            var ownerDisplayName = await userIdentities.ResolveDisplayNameAsync(
                binding.OwnerId, ct);
            var assessment = injection.Assess(sessionContent, ownerDisplayName);
            var verifiedUser = await userIdentities.ResolveAsync(
                requestor.ExternalId, binding.OwnerId, ct);
            var payload = new JsonObject
            {
                ["content"] = content,
                ["requestor"] = new JsonObject
                {
                    ["id"] = requestor.ExternalId,
                    ["display_name"] = requestor.DisplayName,
                    ["username"] = requestor.Username,
                },
                ["injection_risk"] = new JsonObject
                {
                    ["score"] = assessment.Score,
                    ["signals"] = new JsonArray(assessment.Signals.Select(signal => (JsonNode?)signal).ToArray()),
                    ["advisory_only"] = true,
                },
                ["verified_leaf_user"] = VerifiedUserJson(verifiedUser),
                ["attachments"] = new JsonArray((attachments ?? []).Select(item => (JsonNode?)new JsonObject
                {
                    ["id"] = item.Id,
                    ["name"] = item.Name,
                    ["media_type"] = item.MediaType,
                    ["size"] = item.Size,
                    ["sha256"] = item.Sha256,
                    ["scope_token"] = item.ScopeToken,
                }).ToArray()),
                ["guild_context"] = guildContext?.DeepClone(),
                ["behavior_context"] = behaviorContext.DeepClone(),
                ["relay_context"] = relayContext?.DeepClone(),
                ["channel_gap"] = channelGapRecord,
            };
            var (delivery, isNew) = await deliveries.ReceiveAsync(binding, discordMessageId, payload, ct);
            if (!isNew || delivery.State != "received") return null;

            // Fail closed: a message waits while the Sentinel is unavailable, and every later
            // message of the binding waits behind it, so the session never sees them out of order.
            var now = clock.GetUtcNow();
            if (delivery.SentinelRefused && now - delivery.CreatedAt > InboundRefusalWindow)
            {
                await deliveries.RefuseInboundAsync(delivery, ct);
                logger.LogWarning(
                    "Discord message {MessageId} on {BindingId} was refused for good: the Sentinel stayed unavailable",
                    discordMessageId, binding.BindingId);
                return null;
            }
            if (await deliveries.HasEarlierRefusedInboundAsync(binding, delivery, ct))
            {
                delivery = await deliveries.DeferInboundAsync(delivery, delivery.Attempts, now, ct);
                return RefusedAdmission(delivery);
            }

            var (provider, handle, attached) = await EnsureConversationAsync(binding, ct);
            binding = attached;
            // The delivery payload keeps the full captured map so a retry re-plans against
            // whatever the session actually holds; only the session-bound turn is compacted.
            var serverMap = DiscordServerMapDisclosure.Plan(binding, guildContext);
            var sessionGuildContext = serverMap.GuildContext;
            var sentinelCompute = await ResolveComputeAsync(binding.SentinelQualityMode);
            var admission = await provider.SendAsync(handle, new ExternalConversationInput(
                discordMessageId,
                requestor,
                sessionContent,
                attachments ?? [],
                new JsonObject
                {
                    ["transport"] = "discord",
                    ["binding_id"] = binding.BindingId,
                    ["generation"] = binding.Generation,
                    ["owner_user_id"] = binding.OwnerId,
                    ["agent_id"] = binding.AgentId,
                    ["sentinel_agent_id"] = binding.AgentId,
                    ["session_quality_mode"] = binding.SessionQualityMode,
                    ["sentinel_quality_mode"] = binding.SentinelQualityMode,
                    ["sentinel_provider"] = sentinelCompute.Provider,
                    ["sentinel_quality_tier"] = sentinelCompute.QualityTier,
                    ["sentinel_model"] = sentinelCompute.Model,
                    ["sentinel_effort"] = sentinelCompute.Effort,
                    ["injection_risk_score"] = assessment.Score,
                    ["untrusted"] = true,
                    ["verified_leaf_user"] = VerifiedUserJson(verifiedUser),
                    ["discord_guild_context"] = sessionGuildContext?.DeepClone(),
                    ["discord_behavior"] = behaviorContext.DeepClone(),
                    ["leaf_community_peer"] = relayContext?.DeepClone(),
                    ["outbound_sentinel_reviews"] = await OutboundReviewsForSessionAsync(binding, ct),
                }), ct);
            if (admission.Disposition == ExternalConversationDispositions.RefusedSentinelUnavailable)
            {
                var attempts = delivery.Attempts + 1;
                var retryAt = now + InboundRetryDelay(attempts);
                var firstRefusal = !delivery.SentinelRefused;
                delivery = await deliveries.DeferInboundAsync(delivery, attempts, retryAt, ct);
                // One static notice per binding per quarter hour, not one per message.
                if (firstRefusal)
                    await EnqueueNoticeAsync(binding,
                        $"sentinel-unavailable-in:{now.ToUnixTimeSeconds() / 900}",
                        InboundUnavailableNotice, ct);
                logger.LogWarning(
                    "Discord message {MessageId} on {BindingId} is waiting: the Sentinel is unavailable (attempt {Attempts})",
                    discordMessageId, binding.BindingId, attempts);
                RetryDue?.Invoke(binding, retryAt);
                return admission;
            }
            if (serverMap.NextState is { } disclosed)
                binding = await bindings.RecordServerMapDisclosureAsync(binding, disclosed, ct);
            await deliveries.MarkAdmittedAsync(delivery, admission, ct);
            await bindings.MarkReceivedAsync(binding, discordMessageId, ct);
            binding = await bindings.MarkDeliveredAsync(binding, discordMessageId, ct);
            await deliveries.CancelPendingOutboundAsync(
                binding.BindingId,
                binding.Generation,
                NoticeMessageUid(binding, discordMessageId),
                ct);
            return admission;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<JsonObject?> ReadChannelGapAsync(
        DiscordBinding binding, string discordMessageId, DiscordChannelGapSource? source,
        CancellationToken ct)
    {
        // Every DM is admitted, so a DM session has no unseen channel history to carry.
        if (source is null || binding.Kind == "dm") return null;
        try
        {
            var gap = await source(binding, ct);
            return gap is null ? null : DiscordChannelGaps.ToRecord(gap);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Earlier history is context, not a precondition: admit the turn and say so.
            logger.LogWarning(ex,
                "Discord channel gap for message {MessageId} on {BindingId} could not be read",
                discordMessageId, binding.BindingId);
            return DiscordChannelGaps.ToRecord(DiscordChannelGap.UnavailableGap);
        }
    }

    public async Task RetryPendingAsync(DiscordBinding binding, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        foreach (var delivery in await deliveries.PendingInboundAsync(binding.BindingId, ct))
        {
            if (delivery.Generation != binding.Generation || delivery.DiscordId is null
                || delivery.Payload is not JsonObject payload)
                continue;
            // A message waiting on the Sentinel holds every later one until it is due.
            if (delivery.SentinelRefused && delivery.NextAttemptAt > now) break;
            var requestorNode = payload["requestor"] as JsonObject;
            var externalId = JsonRead.String(requestorNode ?? [], "id");
            var displayName = JsonRead.String(requestorNode ?? [], "display_name");
            var content = JsonRead.String(payload, "content");
            if (externalId is null || displayName is null || content is null) continue;
            var recovered = (payload["attachments"] as JsonArray)?.OfType<JsonObject>()
                .Select(item => new ExternalConversationAttachment(
                    JsonRead.String(item, "id") ?? "",
                    JsonRead.String(item, "name") ?? "attachment",
                    JsonRead.String(item, "media_type") ?? "application/octet-stream",
                    JsonRead.Long(item, "size", 0),
                    JsonRead.String(item, "sha256") ?? "",
                    JsonRead.String(item, "scope_token") ?? ""))
                .ToArray() ?? [];
            var admission = await SubmitAsync(binding, delivery.DiscordId,
                new ExternalRequestor(externalId, displayName,
                    JsonRead.String(requestorNode ?? [], "username")), content, recovered, ct,
                payload["guild_context"]?.DeepClone() as JsonObject,
                payload["behavior_context"]?.DeepClone() as JsonObject,
                payload["relay_context"]?.DeepClone() as JsonObject);
            if (admission?.Disposition == ExternalConversationDispositions.RefusedSentinelUnavailable)
                break;
        }
    }

    internal const string InboundUnavailableNotice =
        "I can't read messages right now, my safety check is unavailable. " +
        "I'll read yours once it's back; if I don't answer, try again in a bit.";

    internal const string OutboundUnavailableNotice =
        "I wrote a reply but couldn't send it, my safety check is unavailable. Try again in a bit.";

    /// <summary>How long a refused message keeps waiting for the Sentinel before it is dropped
    /// from the session (it stays stored as <c>refused</c>).</summary>
    internal static readonly TimeSpan InboundRefusalWindow = TimeSpan.FromMinutes(30);

    /// <summary>Unavailable outbound reviews are asked this many times before the reply is withheld.</summary>
    internal const int MaxOutboundReviewAttempts = 4;

    internal static TimeSpan InboundRetryDelay(int attempts)
        => TimeSpan.FromSeconds(Math.Min(480, 30 * Math.Pow(2, Math.Max(0, attempts - 1))));

    internal static TimeSpan OutboundRetryDelay(int attempts)
        => TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Max(0, attempts - 1)));

    private static ExternalConversationAdmission RefusedAdmission(DiscordDelivery delivery)
        => new($"discord-refused-{delivery.DiscordId}",
            ExternalConversationDispositions.RefusedSentinelUnavailable);

    public Task<(DiscordBinding Binding, string MessageUid)> SendProactiveAsync(
        DiscordBinding binding,
        string requestId,
        string content,
        CancellationToken ct = default)
        => SendProactiveCoreAsync(
            binding, requestId, content, null, null, null, null, [], ct);

    public Task<(DiscordBinding Binding, string MessageUid)> RecordDeliveredProactiveAsync(
        DiscordBinding binding,
        string requestId,
        string content,
        string discordMessageId,
        CancellationToken ct = default)
        => SendProactiveCoreAsync(
            binding, requestId, content, null, null, discordMessageId, null, [], ct);

    public Task<(DiscordBinding Binding, string MessageUid)> RecordDeliveredProactiveAsync(
        DiscordBinding binding,
        string requestId,
        string content,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset> outputAssets,
        string discordMessageId,
        CancellationToken ct = default)
        => SendProactiveCoreAsync(
            binding, requestId, content, null, null, discordMessageId,
            deliveryContent, outputAssets, ct);


    public async Task<DiscordBinding> EnsureReadyAsync(
        DiscordBinding binding, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            binding = await bindings.GetAsync(binding.EntityId, ct)
                ?? throw new InvalidOperationException("Discord binding disappeared during setup");
            var (_, _, attached) = await EnsureConversationAsync(binding, ct);
            return attached;
        }
        finally
        {
            gate.Release();
        }
    }
    public Task<(DiscordBinding Binding, string MessageUid)> SendExistingProactiveAsync(
        DiscordBinding binding,
        string requestId,
        string content,
        CancellationToken ct = default)
        => SendExistingProactiveAsync(
            binding, requestId, content, null, [], ct);

    public Task<(DiscordBinding Binding, string MessageUid)> SendExistingProactiveAsync(
        DiscordBinding binding,
        string requestId,
        string content,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset> outputAssets,
        CancellationToken ct = default)
    {
        if (binding.SessionId is null || binding.ConversationHandleId is null)
            throw new DiscordBindingSessionChangedException(
                "The Discord binding has no existing Agent session");
        return SendProactiveCoreAsync(
            binding, requestId, content, binding.Generation, binding.SessionId,
            null, deliveryContent, outputAssets, ct);
    }

    internal DiscordPreparedCorrection InspectCorrection(
        string? content,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset>? outputAssets)
    {
        string? inspectedCanonical = null;
        if (content is not null)
        {
            var inspected = egress.Inspect(content);
            if (!inspected.Allowed)
                throw new ArgumentException(
                    "Discord correction content was rejected by the egress guard");
            inspectedCanonical = inspected.Content;
        }

        string? inspectedDelivery = null;
        if (deliveryContent is not null || inspectedCanonical is not null)
        {
            var inspected = egress.Inspect(deliveryContent ?? inspectedCanonical!);
            if (!inspected.Allowed)
                throw new ArgumentException(
                    "Discord correction delivery content was rejected by the egress guard");
            inspectedDelivery = inspected.Content;
        }

        var inspectedAssets = outputAssets?.Select(asset =>
        {
            var description = egress.Inspect(asset.Description);
            return asset with
            {
                Description = description.Allowed
                              && !string.IsNullOrWhiteSpace(description.Content)
                    ? description.Content
                    : "Shared image",
            };
        }).ToArray();
        if (content is not null
            && deliveryContent is null
            && LooksLikePrivateAssetImageMarkdown(inspectedCanonical!))
            throw new ArgumentException(
                "Private RedLeaf Asset Markdown requires explicit Discord delivery content");

        return new DiscordPreparedCorrection(inspectedDelivery, inspectedAssets);
    }

    private async Task<(DiscordBinding Binding, string MessageUid)> SendProactiveCoreAsync(
        DiscordBinding binding,
        string requestId,
        string content,
        long? expectedGeneration,
        string? expectedSessionId,
        string? deliveredDiscordMessageId,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset>? outputAssets,
        CancellationToken ct)
    {
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            binding = await bindings.GetAsync(binding.EntityId, ct)
                ?? throw new InvalidOperationException("Discord binding disappeared during outreach");
            if (!binding.Enabled)
                throw new InvalidOperationException("Discord binding is disabled");
            if (expectedGeneration is not null
                && (binding.Generation != expectedGeneration
                    || !string.Equals(binding.SessionId, expectedSessionId,
                        StringComparison.Ordinal)
                    || binding.ConversationHandleId is null))
                throw new DiscordBindingSessionChangedException(
                    "The Discord binding session changed before the message was admitted");

            var prepared = PrepareOutreach(content, deliveryContent, outputAssets);

            var (_, handle, attached) = await EnsureConversationAsync(binding, ct);
            binding = attached;
            var messageUid =
                $"discord-outreach-{DiscordBindingStore.Hash($"{binding.BindingId}:{binding.Generation}:{requestId}")}";
            using var inject = new HttpRequestMessage(
                HttpMethod.Post, $"/ai-session/sessions/{handle.SessionId}/inject")
            {
                Content = JsonContent.Create(new
                {
                    role = "assistant",
                    content = prepared.Content,
                    messageUid,
                    metadata = new
                    {
                        app = "leaf-discord",
                        bindingId = binding.BindingId,
                        generation = binding.Generation,
                        origin = "agent-outreach",
                    },
                }),
            };
            using var response = await compute.SendAsync(inject, provenance: null, ct);
            await EnsureInjectionAcceptedAsync(response, messageUid, ct);

            var message = new ExternalConversationMessage(
                new ExternalConversationCursor("leaf-discord-outreach", 0),
                messageUid, "assistant", "text", prepared.Content, DateTimeOffset.UtcNow,
                new JsonObject { ["origin"] = "agent-outreach" })
            {
                DeliveryContent = prepared.DeliveryContent,
                OutputAssets = prepared.OutputAssets,
            };
            var chunks = DiscordMessageChunker.Split(prepared.DeliveryContent);
            var assetGroups = prepared.OutputAssets.Chunk(10).ToArray();
            var deliveryCount = Math.Max(chunks.Count, assetGroups.Length);
            for (var i = 0; i < deliveryCount; i++)
            {
                var delivery = await deliveries.EnqueueOutboundAsync(
                    binding,
                    message,
                    i,
                    i < chunks.Count ? chunks[i] : "",
                    i < assetGroups.Length ? assetGroups[i] : [],
                    ct);
                if (deliveredDiscordMessageId is not null)
                {
                    if (deliveryCount != 1)
                        throw new InvalidOperationException(
                            "An already-delivered Discord message must fit in one chunk");
                    await deliveries.MarkSentAsync(delivery, deliveredDiscordMessageId, ct);
                }
            }
            return (binding, messageUid);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Applies the outreach egress contract without side effects, so a caller that must
    /// deliver to Discord before a transcript exists sends exactly what the outbox would.
    /// </summary>
    internal DiscordPreparedOutreach PrepareOutreach(
        string content,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset>? outputAssets)
    {
        var inspected = egress.Inspect(content);
        if (!inspected.Allowed)
            throw new ArgumentException("Discord outreach content was rejected by the egress guard");
        var inspectedDelivery = egress.Inspect(deliveryContent ?? inspected.Content);
        if (!inspectedDelivery.Allowed)
            throw new ArgumentException(
                "Discord outreach delivery content was rejected by the egress guard");
        var inspectedAssets = (outputAssets ?? []).Select(asset =>
        {
            var description = egress.Inspect(asset.Description);
            return asset with
            {
                Description = description.Allowed
                    && !string.IsNullOrWhiteSpace(description.Content)
                        ? description.Content
                        : "Shared image",
            };
        }).ToArray();
        if (inspectedAssets.Length == 0
            && deliveryContent is null
            && LooksLikePrivateAssetImageMarkdown(inspected.Content))
            throw new ArgumentException(
                "Private RedLeaf Asset Markdown requires typed Discord output Assets");
        return new DiscordPreparedOutreach(
            inspected.Content, inspectedDelivery.Content, inspectedAssets);
    }

    private static bool LooksLikePrivateAssetImageMarkdown(string content)
        => content.Contains("![", StringComparison.Ordinal)
           && content.Contains("/api/assets/", StringComparison.Ordinal);

    public async Task<DiscordBinding> ClearAsync(DiscordBinding binding, string requestId,
        CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            binding = await bindings.GetAsync(binding.EntityId, ct)
                ?? throw new InvalidOperationException("Discord binding disappeared during clear");
            if (string.Equals(binding.LastClearRequestId, requestId, StringComparison.Ordinal))
                return binding;
            var config = await botConfigurations.FindByApplicationIdAsync(
                binding.ApplicationId, ct)
                ?? throw new InvalidOperationException(
                    $"Discord application '{binding.ApplicationId}' is not configured.");
            var provider = ResolveProvider(config.AgentId);
            var nextGeneration = checked(binding.Generation + 1);
            var nextBinding = binding with
            {
                AgentId = config.AgentId,
                SessionQualityMode = config.SessionQualityMode,
                SentinelQualityMode = config.SentinelQualityMode,
            };
            var handle = await provider.OpenAsync(await OpenRequestAsync(nextBinding, nextGeneration,
                $"discord-clear:{binding.BindingId}:{nextGeneration}:{requestId}"), ct);
            return await bindings.AdvanceGenerationAndAttachAsync(
                binding, handle, requestId, ct, config.AgentId,
                config.SessionQualityMode, config.SentinelQualityMode);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ReconcileAsync(DiscordBinding binding, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = await bindings.GetAsync(binding.EntityId, ct);
            if (current is null || !current.Enabled
                || current.Generation != binding.Generation
                || !string.Equals(current.SessionId, binding.SessionId, StringComparison.Ordinal)
                || !string.Equals(current.ConversationHandleId, binding.ConversationHandleId,
                    StringComparison.Ordinal))
                return true;
            return await ReconcileCoreAsync(current, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ReconcileCoreAsync(
        DiscordBinding binding, CancellationToken ct)
    {
        if (binding.SessionId is null || binding.ConversationHandleId is null) return true;
        var typingSnapshot = typing.Capture(binding.BindingId);
        var provider = ResolveProvider(binding.AgentId);
        var handle = ToHandle(binding, provider.ProviderId);
        var cursor = binding.Epoch is null ? null : new ExternalConversationCursor(binding.Epoch, binding.Sequence);
        var showToolActivity = (await botConfigurations.FindByApplicationIdAsync(
            binding.ApplicationId, ct))?.ShowToolActivity ?? true;
        while (true)
        {
            var page = await provider.ReadSettledAsync(handle, cursor, 100, ct);
            foreach (var message in page.Messages
                         .OrderBy(message => message.Cursor.Sequence)
                         .Where(message => message.Role == "assistant"))
            {
                if (DiscordControlMessages.IsNoReply(message.Content)) continue;
                if (message.Kind is "tool_use" or "tool_result")
                {
                    typing.Activate(binding.BindingId, message.MessageUid);
                    if (!showToolActivity) continue;
                    await deliveries.EnqueueProgressAsync(
                        binding,
                        message,
                        commentary: null,
                        marker: message.Kind == "tool_use"
                            ? DiscordDeliveryStore.ToolUseMarkerFor(message.ToolName, message.ToolInput)
                            : DiscordDeliveryStore.ToolResultMarker,
                        ct);
                    continue;
                }
                if (message.Kind is not ("text" or "commentary")) continue;

                var deliveryContent = message.Kind == "text"
                    ? message.DeliveryContent ?? message.Content ?? ""
                    : message.Content ?? "";
                var inspected = egress.Inspect(deliveryContent);
                if (!inspected.Allowed)
                {
                    logger.LogWarning("Discord egress rejected {MessageUid}: {Findings}",
                        message.MessageUid, string.Join(',', inspected.Findings));
                    continue;
                }
                var outputAssets = message.Kind == "text"
                    ? message.OutputAssets.Select(asset =>
                    {
                        var description = egress.Inspect(asset.Description);
                        return asset with
                        {
                            Description = description.Allowed
                                && !string.IsNullOrWhiteSpace(description.Content)
                                    ? description.Content
                                    : "Shared image",
                        };
                    }).ToArray()
                    : [];
                if (!string.IsNullOrWhiteSpace(inspected.Content) || outputAssets.Length > 0)
                    typing.Activate(binding.BindingId, message.MessageUid);
                if (message.Kind == "commentary")
                {
                    await deliveries.EnqueueProgressAsync(
                        binding, message, inspected.Content, marker: null, ct);
                    continue;
                }

                // A reply reaches Discord only after its outbound Sentinel review. The review is
                // durable in the outbox (see ReviewPendingOutboundAsync). Without a verdict the
                // reply is not delivered.
                var reviewed = !message.MessageUid.StartsWith(
                    "discord-outreach-", StringComparison.Ordinal);
                var chunks = DiscordMessageChunker.Split(inspected.Content);
                var assetGroups = outputAssets.Chunk(10).ToArray();
                var deliveryCount = Math.Max(chunks.Count, assetGroups.Length);
                for (var i = 0; i < deliveryCount; i++)
                {
                    var chunk = i < chunks.Count ? chunks[i] : "";
                    IReadOnlyList<ExternalConversationOutputAsset> assets =
                        i < assetGroups.Length ? assetGroups[i] : [];
                    _ = reviewed
                        ? await deliveries.EnqueueOutboundForReviewAsync(
                            binding, message, i, chunk, assets, ct)
                        : await deliveries.EnqueueOutboundAsync(
                            binding, message, i, chunk, assets, ct);
                }
            }

            if (page.NextCursor is not null)
            {
                if (page.HasMore && cursor is not null
                    && page.NextCursor == cursor)
                    throw new InvalidOperationException(
                        "External conversation page claimed more records without advancing its cursor");
                binding = await bindings.AdvanceCursorAsync(binding, page.NextCursor, ct);
                cursor = binding.Epoch is null
                    ? page.NextCursor
                    : new ExternalConversationCursor(binding.Epoch, binding.Sequence);
            }
            else if (page.HasMore)
            {
                throw new InvalidOperationException(
                    "External conversation page claimed more records without a cursor");
            }

            if (page.HasMore) continue;
            if (page.IsQuiescent && typingSnapshot is { } snapshot)
                typing.CompleteQuiescent(snapshot);
            return page.IsQuiescent;
        }
    }

    internal const string WithheldReplyNotice =
        "I wrote a reply here but withheld it because it could have shared private information. " +
        "Ask again and I'll answer without it.";

    /// <summary>
    /// Runs the outbound Sentinel review for every reply chunk of the binding's current generation
    /// that is waiting in <see cref="DiscordDeliveryStore.ReviewingState"/>, one reply at a time in
    /// delivery order. The model call runs outside the binding gate. The verdict and the chosen
    /// action are written to the rows before the action, so after a restart the same action is
    /// replayed (each one is idempotent) instead of reviewing again. Returns whether any reply
    /// left review, so the caller can drain the outbox.
    /// </summary>
    public async Task<bool> ReviewPendingOutboundAsync(
        DiscordBinding binding, CancellationToken ct = default)
    {
        var progressed = false;
        while (true)
        {
            var current = await bindings.GetAsync(binding.EntityId, ct);
            if (current is null || !current.Enabled) return progressed;
            var pending = await deliveries.PendingReviewAsync(
                current.BindingId, current.Generation, ct);
            if (pending.Count == 0) return progressed;
            var messageUid = pending[0].MessageUid;
            var chunks = pending
                .Where(item => string.Equals(item.MessageUid, messageUid, StringComparison.Ordinal))
                .ToList();
            // A reply waiting to ask the Sentinel again also holds every later reply.
            if (chunks.Any(item => item.ReviewNextAt > clock.GetUtcNow())) return progressed;
            var review = chunks.Select(item => item.OutboundReview).FirstOrDefault(item => item is not null)
                ?? await ReviewOutboundAsync(current, chunks, ct);
            if (review is null) return progressed;
            if (!await ApplyOutboundReviewAsync(current, chunks, review, ct)) return progressed;
            progressed = true;
        }
    }

    /// <summary>The recorded decision, or null when the Sentinel was unavailable and the reply
    /// stays in review to be asked again later.</summary>
    private async Task<JsonObject?> ReviewOutboundAsync(
        DiscordBinding binding, IReadOnlyList<DiscordDelivery> chunks, CancellationToken ct)
    {
        var messageUid = chunks[0].MessageUid ?? "";
        var content = string.Join("\n", chunks.Select(chunk =>
        {
            var text = chunk.Payload?["content"]?.GetValue<string>() ?? "";
            var assets = (chunk.Payload?["assets"] as JsonArray)?.OfType<JsonObject>()
                .Select(asset => JsonRead.String(asset, "description"))
                .Where(description => !string.IsNullOrWhiteSpace(description))
                .Select(description => $"[image: {description}]") ?? [];
            return string.Join("\n", new[] { text }.Concat(assets)
                .Where(part => !string.IsNullOrEmpty(part)));
        }));
        var turn = TurnKey(binding);
        var watch = Stopwatch.StartNew();
        var result = await RunOutboundReviewAsync(binding, messageUid, content, ct);
        watch.Stop();
        var verdict = result?.Verdict ?? UntrustedContentVerdict.Unavailable();
        if (!verdict.Available)
        {
            // Fail closed: no verdict, no delivery. Ask again a bounded number of times while
            // the reply (and everything after it) waits; then withhold it.
            var attempts = chunks.Max(chunk => chunk.ReviewAttempts) + 1;
            if (sentinel is not null && attempts < MaxOutboundReviewAttempts)
            {
                var retryAt = clock.GetUtcNow() + OutboundRetryDelay(attempts);
                await deliveries.DeferReviewAsync(chunks, attempts, retryAt, ct);
                logger.LogWarning(
                    "Discord outbound review of {MessageUid} on {BindingId} is unavailable (attempt {Attempts}); retrying",
                    messageUid, binding.BindingId, attempts);
                RetryDue?.Invoke(binding, retryAt);
                return null;
            }
        }
        var hold = verdict.Available
                   && (verdict.Risk == "high" || verdict.Handling == "require_private_approval");
        // One rewrite per inbound turn: a held rewrite, or any later held reply before the next
        // participant message, is withheld for good and Discord sees a short notice instead.
        var outcome = !verdict.Available ? "withheld"
            : !hold ? "delivered"
            : (await deliveries.ReviewedDuringTurnAsync(binding.BindingId, turn, ct))
                .Any(item => JsonRead.String(item.OutboundReview!, "outcome") == "held")
                ? "withheld"
                : "held";
        logger.LogInformation(
            "Discord outbound review of {MessageUid} on {BindingId}: {Outcome} (available {Available}, risk {Risk}, {ElapsedMs} ms)",
            messageUid, binding.BindingId, outcome, verdict.Available, verdict.Risk, watch.ElapsedMilliseconds);
        return OutboundReviewRecord(verdict, outcome, turn, result?.JobId, result?.TraceId,
            watch.ElapsedMilliseconds);
    }

    private async Task<UntrustedContentReviewResult?> RunOutboundReviewAsync(
        DiscordBinding binding, string messageUid, string content, CancellationToken ct)
    {
        if (sentinel is null) return null;
        try
        {
            UntrustedContentCompute? compute = null;
            if (await runtimeOptions.ResolveQualityModeAsync(binding.SentinelQualityMode, ct) is { } mode)
                compute = new UntrustedContentCompute
                {
                    Provider = string.IsNullOrWhiteSpace(mode.Provider) ? null : mode.Provider,
                    QualityTier = string.IsNullOrWhiteSpace(mode.QualityTier) ? null : mode.QualityTier,
                    Model = mode.Model,
                    Effort = mode.Effort,
                };
            return await sentinel.ReviewAsync(new UntrustedContentReviewRequest
            {
                Content = content,
                Origin = new UntrustedContentOrigin(
                    UntrustedContentSources.Discord, $"{binding.BindingId}:{binding.Generation}",
                    binding.OwnerId, LeafDiscordPlugin.PluginId),
                Compute = compute,
                AgentRef = binding.AgentId,
                Direction = UntrustedContentDirection.Outbound,
                Audience = await AudienceAsync(binding, ct),
                IdempotencyKey = $"sentinel-outbound:{binding.BindingId}:{binding.Generation}:{messageUid}",
                JobName = "Discord outbound review",
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The Sentinel never throws for runtime failures; anything else is still advisory.
            logger.LogWarning(ex, "Discord outbound review of {MessageUid} failed", messageUid);
            return null;
        }
    }

    /// <summary>Who will read the reply, from the bridge's own binding data only.</summary>
    private async Task<string> AudienceAsync(DiscordBinding binding, CancellationToken ct)
    {
        if (binding.Kind == "dm")
        {
            var counterpart = binding.CounterpartyUserId is null
                ? null
                : await userIdentities.ResolveAsync(binding.CounterpartyUserId, binding.OwnerId, ct);
            return counterpart?.IsConversationOwner == true
                ? "a Discord direct message with the verified owner (Discord is still an external service)"
                : "a Discord direct message with a participant who is not the owner";
        }
        return binding.Kind is "thread" or "forum_post"
            ? "a thread in a Discord server, readable by every member who can see it"
            : "a channel in a Discord server, readable by every member who can see it";
    }

    private async Task<bool> ApplyOutboundReviewAsync(
        DiscordBinding binding, IReadOnlyList<DiscordDelivery> chunks, JsonObject review,
        CancellationToken ct)
    {
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = await bindings.GetAsync(binding.EntityId, ct);
            // After /clear the old generation's rows stay inert; the drain never sends them.
            if (current is null || current.Generation != binding.Generation) return false;
            if (chunks.All(chunk => chunk.OutboundReview is null))
                chunks = await deliveries.RecordOutboundReviewAsync(
                    chunks, review, JsonRead.String(review, "turn") ?? TurnKey(current), ct);
            var messageUid = chunks[0].MessageUid ?? "";
            switch (JsonRead.String(review, "outcome"))
            {
                case "held":
                    await SendOutboundNoticeAsync(current, messageUid, review, ct);
                    break;
                case "withheld":
                    var available = review["available"] is JsonValue value
                                    && value.TryGetValue<bool>(out var isAvailable) && isAvailable;
                    await EnqueueNoticeAsync(current, $"sentinel-withheld:{messageUid}",
                        available ? WithheldReplyNotice : OutboundUnavailableNotice, ct);
                    break;
            }
            await deliveries.ResolveReviewAsync(
                chunks, JsonRead.String(review, "outcome") == "delivered", ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Tells the session its reply was held and asks for one rewrite, as a bridge turn. The
    /// request ID is stable per held reply, so a replay after a restart is one turn, not two.
    /// </summary>
    private async Task SendOutboundNoticeAsync(
        DiscordBinding binding, string messageUid, JsonObject review, CancellationToken ct)
    {
        var (provider, handle, attached) = await EnsureConversationAsync(binding, ct);
        var signals = (review["signals"] as JsonArray)?
            .Select(signal => signal?.GetValue<string>()).OfType<string>().ToArray() ?? [];
        await provider.SendAsync(handle, new ExternalConversationInput(
            $"sentinel-outbound-{DiscordBindingStore.Hash(messageUid)}",
            new ExternalRequestor("leaf-discord", "Discord bridge"),
            "Sentinel held the previous reply before it reached Discord"
            + (signals.Length == 0 ? "" : $" ({string.Join(", ", signals)})")
            + ". A rewrite was requested.",
            [],
            new JsonObject
            {
                ["transport"] = "discord",
                ["binding_id"] = attached.BindingId,
                ["generation"] = attached.Generation,
                ["owner_user_id"] = attached.OwnerId,
                ["agent_id"] = attached.AgentId,
                ["untrusted"] = false,
                ["outbound_sentinel_notice"] = SessionReview(review, messageUid),
            }), ct);
    }

    /// <summary>Outbound reviews recorded during the previous inbound turn, for the next one.</summary>
    private async Task<JsonArray?> OutboundReviewsForSessionAsync(
        DiscordBinding binding, CancellationToken ct)
    {
        var reviewed = await deliveries.ReviewedDuringTurnAsync(
            binding.BindingId, TurnKey(binding), ct);
        return reviewed.Count == 0
            ? null
            : new JsonArray(reviewed.Select(item =>
                (JsonNode?)SessionReview(item.OutboundReview!, item.MessageUid ?? "")).ToArray());
    }

    /// <summary>
    /// The current inbound turn: the binding generation and the newest admitted Discord message.
    /// Bridge notices never advance it, so a rewrite belongs to the turn it rewrites.
    /// </summary>
    internal static string TurnKey(DiscordBinding binding)
        => $"{binding.Generation}:{(binding.LastDeliveredGeneration == binding.Generation
            ? binding.LastDeliveredSnowflake ?? "-"
            : "-")}";

    internal static JsonObject OutboundReviewRecord(
        UntrustedContentVerdict verdict, string outcome, string turn, string? jobId, string? traceId,
        long elapsedMs)
        => new()
        {
            ["direction"] = "outbound",
            ["outcome"] = outcome,
            ["risk"] = verdict.Risk,
            ["signals"] = new JsonArray(verdict.Signals.Select(signal => (JsonNode?)signal).ToArray()),
            ["handling"] = verdict.Handling,
            ["available"] = verdict.Available,
            ["isolation"] = verdict.Isolation,
            ["turn"] = turn,
            ["job_id"] = jobId,
            ["trace_id"] = traceId,
            ["elapsed_ms"] = elapsedMs,
            ["reviewed_at"] = DateTimeOffset.UtcNow.ToString("O"),
        };

    private static JsonObject SessionReview(JsonObject review, string messageUid)
        => new()
        {
            ["message_uid"] = messageUid,
            ["outcome"] = JsonRead.String(review, "outcome"),
            ["risk"] = JsonRead.String(review, "risk"),
            ["signals"] = review["signals"]?.DeepClone() ?? new JsonArray(),
            ["handling"] = JsonRead.String(review, "handling"),
            ["available"] = review["available"]?.DeepClone(),
        };

    public async Task EnqueueNoticeAsync(
        DiscordBinding binding, string requestId, string content, CancellationToken ct = default)
    {
        var inspected = egress.Inspect(content);
        if (!inspected.Allowed) return;
        var message = new ExternalConversationMessage(
            new ExternalConversationCursor("leaf-discord-system", 0),
            NoticeMessageUid(binding, requestId),
            "assistant", "text", inspected.Content, DateTimeOffset.UtcNow);
        var chunks = DiscordMessageChunker.Split(inspected.Content);
        for (var i = 0; i < chunks.Count; i++)
            await deliveries.EnqueueOutboundAsync(binding, message, i, chunks[i], ct);
    }

    internal static string NoticeMessageUid(DiscordBinding binding, string requestId)
        => $"leaf-discord-notice-{DiscordBindingStore.Hash(
            $"{binding.BindingId}:{binding.Generation}:{requestId}")}";

    private async Task<(IExternalAgentConversationProvider Provider,
        ExternalConversationHandle Handle, DiscordBinding Binding)> EnsureConversationAsync(
        DiscordBinding binding, CancellationToken ct)
    {
        var provider = ResolveProvider(binding.AgentId);
        if (binding.SessionId is not null && binding.ConversationHandleId is not null)
            return (provider, ToHandle(binding, provider.ProviderId), binding);

        var handle = await provider.OpenAsync(await OpenRequestAsync(binding, binding.Generation,
            $"discord-open:{binding.BindingId}:{binding.Generation}"), ct);
        var attached = await bindings.AttachConversationAsync(binding, handle, ct);
        return (provider, handle, attached);
    }

    private async Task<ExternalConversationOpenRequest> OpenRequestAsync(
        DiscordBinding binding, long generation, string idempotencyKey)
    {
        var session = await ResolveComputeAsync(binding.SessionQualityMode);
        var sentinel = await ResolveComputeAsync(binding.SentinelQualityMode);
        return new(
            binding.BindingId,
            generation,
            binding.AgentId,
            binding.OwnerId,
            binding.AudienceProfile,
            new ExternalConversationScope(
                "discord", binding.ApplicationId, binding.ConversationId, binding.Kind,
                binding.GuildId, binding.ParentConversationId),
            idempotencyKey,
            session,
            sentinel);
    }

    private async Task<ExternalConversationComputeSelection> ResolveComputeAsync(string modeId)
    {
        var mode = await runtimeOptions.ResolveQualityModeAsync(modeId)
            ?? throw new InvalidOperationException($"Discord quality mode '{modeId}' was not found");
        return new ExternalConversationComputeSelection(
            mode.Id, mode.Provider, mode.QualityTier, mode.Model, mode.Effort);
    }

    private static ExternalConversationHandle ToHandle(DiscordBinding binding, string providerId)
        => new(providerId, binding.BindingId, binding.Generation,
            binding.ConversationHandleId!, binding.SessionId!);

    private IExternalAgentConversationProvider ResolveProvider(string agentId)
        => providers.FirstOrDefault(provider => provider.CanHandle(agentId))
           ?? throw new InvalidOperationException($"No external conversation provider handles Agent '{agentId}'");

    private static JsonObject? VerifiedUserJson(DiscordVerifiedLeafUser? user)
        => user is null ? null : new JsonObject
        {
            ["user_id"] = user.UserId,
            ["display_name"] = user.DisplayName,
            ["is_conversation_owner"] = user.IsConversationOwner,
        };

    private async Task OnSettledAsync(ExternalConversationSettled notification, CancellationToken ct)
    {
        try
        {
            var binding = await bindings.FindByBindingIdAsync(notification.BindingId, ct);
            if (binding is null || binding.Generation != notification.Generation
                || binding.SessionId != notification.SessionId)
                return;
            await ReconcileAsync(binding, ct);
            Reconciled?.Invoke(binding);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord settled-turn reconciliation failed for {BindingId}",
                notification.BindingId);
        }
    }

    internal static async Task EnsureInjectionAcceptedAsync(
        HttpResponseMessage response,
        string expectedMessageUid,
        CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"RedCompute refused Discord outreach transcript injection ({(int)response.StatusCode})");

        JsonDocument result;
        try
        {
            result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "RedCompute returned an invalid Discord outreach injection receipt", ex);
        }

        using (result)
        {
            var root = result.RootElement;
            var injected = root.TryGetProperty("injected", out var accepted)
                && accepted.ValueKind is JsonValueKind.True;
            var messageUid = root.TryGetProperty("messageUid", out var uid)
                && uid.ValueKind is JsonValueKind.String
                    ? uid.GetString()
                    : null;
            if (!injected || !string.Equals(
                    messageUid, expectedMessageUid, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "RedCompute did not confirm the canonical Discord outreach transcript injection");
        }
    }

    public void Dispose()
    {
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
        foreach (var gate in gates.Values) gate.Dispose();
        gates.Clear();
    }
}

public sealed class DiscordBindingSessionChangedException(string message)
    : InvalidOperationException(message);
