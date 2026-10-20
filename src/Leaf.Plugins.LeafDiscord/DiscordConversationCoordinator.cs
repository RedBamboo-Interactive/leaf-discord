using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

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
    ILogger<DiscordConversationCoordinator> logger) : IDisposable
{
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
        JsonObject? behaviorContext = null)
    {
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            binding = await bindings.GetAsync(binding.EntityId, ct)
                ?? throw new InvalidOperationException("Discord binding disappeared during delivery");
            if (!binding.Enabled) return null;

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
            var assessment = injection.Assess(content, ownerDisplayName);
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
            };
            var (delivery, isNew) = await deliveries.ReceiveAsync(binding, discordMessageId, payload, ct);
            if (!isNew || delivery.State != "received") return null;

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
                content,
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
                }), ct);
            if (serverMap.NextState is { } disclosed)
                binding = await bindings.RecordServerMapDisclosureAsync(binding, disclosed, ct);
            await deliveries.MarkAdmittedAsync(delivery, admission, ct);
            await bindings.MarkReceivedAsync(binding, discordMessageId, ct);
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

    public async Task RetryPendingAsync(DiscordBinding binding, CancellationToken ct = default)
    {
        foreach (var delivery in await deliveries.PendingInboundAsync(binding.BindingId, ct))
        {
            if (delivery.Generation != binding.Generation || delivery.DiscordId is null
                || delivery.Payload is not JsonObject payload)
                continue;
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
            await SubmitAsync(binding, delivery.DiscordId,
                new ExternalRequestor(externalId, displayName,
                    JsonRead.String(requestorNode ?? [], "username")), content, recovered, ct,
                payload["guild_context"]?.DeepClone() as JsonObject,
                payload["behavior_context"]?.DeepClone() as JsonObject);
        }
    }

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
                    content = inspected.Content,
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
                messageUid, "assistant", "text", inspected.Content, DateTimeOffset.UtcNow,
                new JsonObject { ["origin"] = "agent-outreach" })
            {
                DeliveryContent = inspectedDelivery.Content,
                OutputAssets = inspectedAssets,
            };
            var chunks = DiscordMessageChunker.Split(inspectedDelivery.Content);
            var assetGroups = inspectedAssets.Chunk(10).ToArray();
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

                var chunks = DiscordMessageChunker.Split(inspected.Content);
                var assetGroups = outputAssets.Chunk(10).ToArray();
                var deliveryCount = Math.Max(chunks.Count, assetGroups.Length);
                for (var i = 0; i < deliveryCount; i++)
                    await deliveries.EnqueueOutboundAsync(
                        binding,
                        message,
                        i,
                        i < chunks.Count ? chunks[i] : "",
                        i < assetGroups.Length ? assetGroups[i] : [],
                        ct);
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
