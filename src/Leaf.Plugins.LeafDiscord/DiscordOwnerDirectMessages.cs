using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordOwnerDirectMessageRequest(
    string RecipientUserId,
    string Content,
    string IdempotencyKey,
    string? DeliveryContent = null,
    IReadOnlyList<DiscordBindingOutputAssetRequest>? OutputAssets = null,
    bool NotifyReplies = true);

public sealed record DiscordOwnerDirectMessageResult(
    Guid BindingEntityId,
    string BindingId,
    long Generation,
    string SessionId,
    Guid DeliveryId,
    string MessageUid,
    string ChannelId,
    string DiscordMessageId,
    bool BindingCreated,
    bool Replayed = false);

/// <summary>Discord definitively refused to open or deliver the DM.</summary>
public sealed class DiscordDirectMessageRefusedException(DiscordRestException inner)
    : Exception(Describe(inner), inner)
{
    public HttpStatusCode DiscordStatusCode { get; } = inner.StatusCode;

    private static string Describe(DiscordRestException inner)
        => inner.Message.Contains("50007", StringComparison.Ordinal)
            ? "Discord refused the direct message: the user does not accept DMs from this bot " +
              $"(server-member DMs disabled, bot blocked, or no shared server). {inner.Message}"
            : $"Discord refused the direct message. {inner.Message}";
}

/// <summary>Discord is temporarily unable to accept the DM; the same key may be retried.</summary>
public sealed class DiscordDirectMessageUnavailableException(DiscordRestException inner)
    : Exception($"Discord is temporarily unavailable for this direct message. {inner.Message}", inner)
{
    public TimeSpan? RetryAfter { get; } = inner.RetryAfter;
}

public sealed class DiscordRecipientNotAllowlistedException()
    : UnauthorizedAccessException("Recipient is not in this bot's configured Discord DM allowlist.");

public sealed class DiscordBindingUnavailableException(string message) : Exception(message);

/// <summary>
/// Owner-directed DM opening. Discord accepts the message before any binding or Agent
/// session is created, so a refusal leaves no Leaf conversation state behind. The send is
/// reserved durably and nonce-enforced, so retries never deliver twice. After acceptance
/// the binding and session are created or reused through the same path as a first
/// inbound DM, and the opening is recorded as an assistant turn with its outbox receipt.
/// </summary>
internal sealed class DiscordOwnerDirectMessenger(
    DiscordBindingStore bindings,
    DiscordBehaviorStore behaviors,
    DiscordDeliveryStore deliveries,
    DiscordConversationCoordinator conversations,
    DiscordRestTransport rest,
    IAssets assets)
{
    private const string RequestIdPrefix = "owner-dm:";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    public async Task<DiscordOwnerDirectMessageResult> OpenAsync(
        LeafDiscordConfig config,
        string ownerUserId,
        DiscordOwnerDirectMessageRequest request,
        CancellationToken ct = default)
    {
        var recipientUserId = request.RecipientUserId?.Trim() ?? "";
        if (!ulong.TryParse(recipientUserId, out var recipientId) || recipientId == 0)
            throw new ArgumentException("Recipient Discord user ID must be a snowflake.");
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new ArgumentException("Discord DM content is required.");
        if (request.Content.Length > 32_000)
            throw new ArgumentException("Discord DM content exceeds the 32,000 character safety limit.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
        var outputAssets = (request.OutputAssets ?? [])
            .Select(asset => new ExternalConversationOutputAsset(asset.AssetId, asset.Description))
            .ToArray();
        if (outputAssets.Length > 10)
            throw new ArgumentException("A Discord message can contain at most 10 output Assets.");
        if (outputAssets.Length > 0 && request.DeliveryContent is null)
            throw new ArgumentException("Discord output Assets require explicit delivery content.");
        if (outputAssets.Any(asset => string.IsNullOrWhiteSpace(asset.AssetId)))
            throw new ArgumentException("Every Discord output Asset requires an Asset ID.");
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("The Leaf owner user ID is required.");
        if (string.IsNullOrWhiteSpace(config.BotToken))
            throw new InvalidOperationException("Discord bot is not configured.");
        if (!config.DmAllowlist.Contains(recipientUserId))
            throw new DiscordRecipientNotAllowlistedException();

        var content = request.Content.Trim();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var prepared = conversations.PrepareOutreach(content, request.DeliveryContent, outputAssets);
        var chunks = DiscordMessageChunker.Split(prepared.DeliveryContent);
        if (chunks.Count > 1)
            throw new ArgumentException(
                "An opening DM must fit in one Discord message (2,000 characters). " +
                "Send follow-ups through POST /bindings/{bindingEntityId}/messages.");
        if (chunks.Count == 0 && prepared.OutputAssets.Count == 0)
            throw new ArgumentException("The Discord DM has no deliverable content.");

        var gate = gates.GetOrAdd($"{config.ApplicationId}:{idempotencyKey}",
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var operation = await deliveries.ReserveOwnerDirectMessageAsync(
                config.ApplicationId,
                idempotencyKey,
                Identity(recipientUserId, content, request.DeliveryContent, outputAssets),
                Payload(chunks.Count == 0 ? "" : chunks[0], prepared.OutputAssets),
                ct);
            if (operation.Result is not null)
                return operation.Result with { Replayed = true };

            // Reviewed before Discord sees it; a replay of a delivered DM returned above.
            await conversations.ReviewOutgoingOrThrowAsync(
                new DiscordOutgoingContext(ownerUserId, config.AgentId,
                    $"{config.ApplicationId}:dm:{recipientUserId}",
                    await conversations.DirectMessageAudienceAsync(recipientUserId, ownerUserId, ct)),
                prepared.DeliveryContent, prepared.OutputAssets, prepared.HiddenCharacters,
                $"owner-dm:{config.ApplicationId}:{idempotencyKey}", ct);
            DiscordDirectMessageChannel channel;
            try
            {
                channel = await rest.CreateDirectMessageChannelAsync(
                    config.BotToken, recipientUserId, ct);
            }
            catch (DiscordRestException ex)
            {
                throw await RecordDiscordFailureAsync(operation, ex, ct);
            }
            if (operation.ChannelId is not null
                && !string.Equals(operation.ChannelId, channel.ChannelId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Discord returned a different DM channel than the one this message was delivered to.");

            var existing = await bindings.FindAsync(config.ApplicationId, channel.ChannelId, ct);
            if (existing is not null)
                EnsureReusable(existing, config, ownerUserId);
            var bindingExisted = operation.BindingExisted ?? existing is not null;

            var discordMessageId = operation.Delivery.DiscordId;
            if (discordMessageId is null)
            {
                var outbound = await DiscordBotRuntime.ResolveOutboundPayloadAsync(
                    operation.Delivery, config, assets, ct);
                try
                {
                    discordMessageId = await rest.SendIdempotentAsync(
                        config.BotToken,
                        channel.ChannelId,
                        outbound.Content,
                        DiscordRestTransport.Nonce(operation.Delivery),
                        outbound.Files,
                        ct);
                }
                catch (DiscordRestException ex)
                {
                    throw await RecordDiscordFailureAsync(operation, ex, ct);
                }
                operation = await deliveries.MarkOwnerDirectMessageDeliveredAsync(
                    operation, channel.ChannelId, discordMessageId, bindingExisted, ct);
            }

            var behavior = await behaviors.ResolveAsync(
                config, "dm", null, channel.ChannelId, null, DiscordResponseModes.Direct, ct);
            var binding = await bindings.GetOrCreateAsync(new CreateDiscordBinding(
                config.ApplicationId,
                channel.ChannelId,
                "dm",
                null,
                null,
                config.AgentId,
                ownerUserId,
                config.AudienceProfile,
                behavior.ResponseMode,
                channel.RecipientName,
                null,
                recipientUserId,
                config.SessionQualityMode), ct);
            binding = await conversations.EnsureReadyAsync(binding, ct);
            var recorded = await conversations.RecordDeliveredProactiveAsync(
                binding,
                RequestIdPrefix + idempotencyKey,
                content,
                request.DeliveryContent,
                outputAssets,
                discordMessageId,
                ct);
            var delivery = await deliveries.FindSentOutboundAsync(
                    recorded.Binding.BindingId, recorded.Binding.Generation, recorded.MessageUid, ct)
                ?? throw new InvalidOperationException(
                    "The delivered Discord DM has no durable outbox receipt.");
            var result = new DiscordOwnerDirectMessageResult(
                recorded.Binding.EntityId,
                recorded.Binding.BindingId,
                recorded.Binding.Generation,
                recorded.Binding.SessionId
                    ?? throw new InvalidOperationException("The Discord DM binding has no Agent session."),
                delivery.EntityId,
                recorded.MessageUid,
                channel.ChannelId,
                discordMessageId,
                !bindingExisted);
            await deliveries.CompleteOwnerDirectMessageAsync(operation, result, ct);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static void EnsureReusable(
        DiscordBinding binding, LeafDiscordConfig config, string ownerUserId)
    {
        if (!binding.Enabled)
            throw new DiscordBindingUnavailableException(
                "The existing Discord DM binding for this recipient is disabled.");
        if (!string.Equals(binding.Kind, "dm", StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "The Discord channel for this recipient is not a direct-message binding.");
        if (!string.Equals(binding.AgentId, config.AgentId, StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(binding.OwnerId, ownerUserId, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(binding.OwnerId, "local-user", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException(
                "The existing Discord DM binding belongs to another Agent or owner.");
    }

    private async Task<Exception> RecordDiscordFailureAsync(
        DiscordOwnerDirectMessageOperation operation,
        DiscordRestException exception,
        CancellationToken ct)
    {
        if ((int)exception.StatusCode == 429 || (int)exception.StatusCode >= 500)
        {
            await deliveries.MarkRetryAsync(operation.Delivery, exception.Message,
                exception.RetryAfter ?? TimeSpan.FromSeconds(5), ct);
            return new DiscordDirectMessageUnavailableException(exception);
        }
        await deliveries.MarkFailedAsync(operation.Delivery, exception.Message, ct);
        return new DiscordDirectMessageRefusedException(exception);
    }

    private static JsonObject Identity(
        string recipientUserId,
        string content,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset> outputAssets)
        => new()
        {
            ["recipient_user_id"] = recipientUserId,
            ["content"] = content,
            ["delivery_content"] = deliveryContent,
            ["assets"] = AssetsJson(outputAssets),
        };

    private static JsonObject Payload(
        string content, IReadOnlyList<ExternalConversationOutputAsset> outputAssets)
    {
        var payload = new JsonObject { ["content"] = content };
        if (outputAssets.Count > 0) payload["assets"] = AssetsJson(outputAssets);
        return payload;
    }

    private static JsonArray AssetsJson(IReadOnlyList<ExternalConversationOutputAsset> outputAssets)
        => new(outputAssets.Select(asset => (JsonNode?)new JsonObject
        {
            ["asset_id"] = asset.AssetId,
            ["description"] = asset.Description,
        }).ToArray());
}
