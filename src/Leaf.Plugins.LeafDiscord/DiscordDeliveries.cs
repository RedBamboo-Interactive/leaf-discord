using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordDelivery(
    Guid EntityId,
    string Direction,
    string Kind,
    string BindingId,
    long Generation,
    string State,
    string? DiscordId,
    string? MessageUid,
    int ChunkIndex,
    string? PayloadHash,
    JsonNode? Payload,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    DateTimeOffset CreatedAt)
{
    /// <summary>The outbound Sentinel verdict and the action taken on it, once recorded.</summary>
    public JsonObject? OutboundReview { get; init; }
}

public sealed record DiscordOwnerDirectMessageOperation(
    DiscordDelivery Delivery,
    string? ChannelId,
    bool? BindingExisted,
    DiscordOwnerDirectMessageResult? Result);

public sealed class DiscordIdempotencyConflictException(string message) : Exception(message);

public sealed class DiscordDeliveryStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-delivery";
    internal const string ProgressKind = "progress";
    internal const string MessageKind = "message";
    internal const string ForumPostKind = "forum_post";
    internal const string OwnerDirectMessageKind = "owner_dm";
    internal const string ToolUseMarker = "\U0001F7EA";
    internal const string MutatingToolMarker = "\U0001F7E7";
    internal const string ToolResultMarker = "\U0001F7E9";
    /// <summary>
    /// A reply chunk waiting for its outbound Sentinel review. It is never sent, and the drain
    /// does not pass it, so a later reply cannot overtake a reply under review.
    /// </summary>
    internal const string ReviewingState = "reviewing";
    internal const string HeldState = "held";

    private static readonly HashSet<string> MutatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "write", "notebookedit", "todowrite",
        "croncreate", "crondelete", "pushnotification",
    };

    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "powershell",
    };

    public async Task<(DiscordDelivery Delivery, bool IsNew)> ReceiveAsync(
        DiscordBinding binding, string discordMessageId, JsonObject payload,
        CancellationToken ct = default)
    {
        var slug = InboundSlug(binding.ApplicationId, discordMessageId);
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        if (existing is not null)
        {
            var delivery = Map(existing);
            return (delivery, delivery.State == "received");
        }

        var canonical = payload.ToJsonString();
        var entity = await store.UpsertBySlugAsync(TypeSlug, slug,
            $"Discord inbound {discordMessageId}", new JsonObject
            {
                ["direction"] = "inbound",
                ["binding_id"] = binding.BindingId,
                ["generation"] = binding.Generation,
                ["state"] = "received",
                ["discord_id"] = discordMessageId,
                ["chunk_index"] = 0,
                ["payload_hash"] = Hash(canonical),
                ["payload"] = payload.DeepClone(),
                ["attempts"] = 0,
            }, ct);
        return (Map(entity), true);
    }

    public async Task<DiscordDelivery> MarkAdmittedAsync(
        DiscordDelivery delivery, ExternalConversationAdmission admission,
        CancellationToken ct = default)
        => Map(await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "admitted",
            ["message_uid"] = admission.MessageUid,
            ["payload"] = null,
            // The transcript keeps only display text; retain who wrote it for attribution.
            ["attribution"] = DiscordInboundAttributions.FromPayload(delivery.Payload),
            ["channel_gap"] = DiscordChannelGaps.Summary(delivery.Payload?["channel_gap"]),
            ["last_error"] = null,
        }, ct: ct));

    /// <summary>
    /// Finds the author of one inbound Discord message admitted (or received) on a binding.
    /// </summary>
    public async Task<DiscordInboundAttribution?> FindInboundAttributionAsync(
        DiscordBinding binding, string discordMessageId, CancellationToken ct = default)
    {
        var entity = await store.GetBySlugAsync(TypeSlug,
            InboundSlug(binding.ApplicationId, discordMessageId), ct);
        if (entity is null
            || !string.Equals(JsonRead.String(entity.Data, "direction"), "inbound",
                StringComparison.Ordinal)
            || !string.Equals(JsonRead.String(entity.Data, "binding_id"), binding.BindingId,
                StringComparison.Ordinal))
            return null;
        return DiscordInboundAttributions.Parse(entity.Data["attribution"])
            ?? DiscordInboundAttributions.Parse(
                DiscordInboundAttributions.FromPayload(entity.Data["payload"]));
    }

    /// <summary>Finds the inbound delivery recorded for one Discord message, if any.</summary>
    public async Task<DiscordDelivery?> FindInboundAsync(
        DiscordBinding binding, string discordMessageId, CancellationToken ct = default)
    {
        var entity = await store.GetBySlugAsync(TypeSlug,
            InboundSlug(binding.ApplicationId, discordMessageId), ct);
        return entity is null ? null : Map(entity);
    }

    /// <summary>
    /// Whether the binding's current session already holds one Discord message: an inbound
    /// turn admitted in this generation, or (for bot and webhook authors) one of this
    /// generation's own outbound deliveries. A binding without a session holds nothing.
    /// </summary>
    public async Task<bool> SessionHasDiscordMessageAsync(
        DiscordBinding binding, string discordMessageId, bool botAuthored,
        CancellationToken ct = default)
    {
        if (binding.SessionId is null) return false;
        if (await ContainsAdmittedInboundAsync(
                binding.BindingId, binding.Generation, discordMessageId, ct))
            return true;
        if (!botAuthored) return false;
        return (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = binding.BindingId,
                ["discord_id"] = discordMessageId,
            },
            Limit = 10,
        }, ct)).Select(Map).Any(item => item.Generation == binding.Generation);
    }

    private static string InboundSlug(string applicationId, string discordMessageId)
        => $"discord-in-{DiscordBindingStore.Hash($"{applicationId}:{discordMessageId}")}";

    public async Task<IReadOnlyList<DiscordDelivery>> PendingInboundAsync(
        string bindingId, CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "inbound",
                ["binding_id"] = bindingId,
            },
            Limit = 500,
        }, ct)).Select(Map)
            .Where(item => item.State == "received" && item.Payload is not null)
            .OrderBy(item => item.CreatedAt)
            .ToList();

    public async Task<bool> ContainsAdmittedInboundAsync(
        string bindingId, long generation, string discordMessageId,
        CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "inbound",
                ["binding_id"] = bindingId,
                ["discord_id"] = discordMessageId,
            },
            Limit = 10,
        }, ct)).Select(Map).Any(item =>
            item.Generation == generation && item.State == "admitted");

    public async Task<DiscordDelivery> EnqueueOutboundAsync(
        DiscordBinding binding, ExternalConversationMessage message, int chunkIndex,
        string content, CancellationToken ct = default)
        => await EnqueueOutboundCoreAsync(
            binding, message, chunkIndex, content, [], MessageKind, ct);

    public async Task<DiscordDelivery> EnqueueOutboundAsync(
        DiscordBinding binding, ExternalConversationMessage message, int chunkIndex,
        string content, IReadOnlyList<ExternalConversationOutputAsset> outputAssets,
        CancellationToken ct = default)
        => await EnqueueOutboundCoreAsync(
            binding, message, chunkIndex, content, outputAssets, MessageKind, ct);

    /// <summary>
    /// Enqueues a reply chunk in <see cref="ReviewingState"/>. The stable delivery identity is the
    /// one <see cref="EnqueueOutboundAsync(DiscordBinding, ExternalConversationMessage, int, string, IReadOnlyList{ExternalConversationOutputAsset}, CancellationToken)"/>
    /// uses, so a re-read of the same settled message finds this row instead of adding one.
    /// </summary>
    public async Task<DiscordDelivery> EnqueueOutboundForReviewAsync(
        DiscordBinding binding, ExternalConversationMessage message, int chunkIndex,
        string content, IReadOnlyList<ExternalConversationOutputAsset> outputAssets,
        CancellationToken ct = default)
        => await EnqueueOutboundCoreAsync(
            binding, message, chunkIndex, content, outputAssets, MessageKind, ct,
            initialState: ReviewingState);

    /// <summary>Reply chunks of the binding's current generation still waiting for review,
    /// oldest first.</summary>
    public async Task<IReadOnlyList<DiscordDelivery>> PendingReviewAsync(
        string bindingId, long generation, CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = bindingId,
                ["state"] = ReviewingState,
            },
            Limit = 500,
        }, ct)).Select(Map)
            .Where(item => item.Generation == generation)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.ChunkIndex)
            .ToList();

    /// <summary>
    /// Records the verdict and the chosen action on every chunk of one reply before the action
    /// runs, so a restart replays the decision instead of asking the Sentinel again.
    /// </summary>
    public async Task<IReadOnlyList<DiscordDelivery>> RecordOutboundReviewAsync(
        IReadOnlyList<DiscordDelivery> chunks, JsonObject review, string turnKey,
        CancellationToken ct = default)
    {
        var recorded = new List<DiscordDelivery>(chunks.Count);
        foreach (var chunk in chunks)
            recorded.Add(Map(await store.PatchAsync(chunk.EntityId, new JsonObject
            {
                ["outbound_review"] = review.DeepClone(),
                ["review_action"] = JsonRead.String(review, "action"),
                ["review_turn"] = turnKey,
            }, ct: ct)));
        return recorded;
    }

    /// <summary>Moves reviewed chunks out of review: to queued (deliver) or held.</summary>
    public async Task ResolveReviewAsync(
        IReadOnlyList<DiscordDelivery> chunks, bool deliver, CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
        {
            var current = await store.GetAsync(chunk.EntityId, ct);
            if (current is null
                || JsonRead.String(current.Data, "state") != ReviewingState)
                continue;
            _ = await store.PatchAsync(chunk.EntityId, new JsonObject
            {
                ["state"] = deliver ? "queued" : HeldState,
                ["next_attempt_at"] = DateTimeOffset.UtcNow.ToString("O"),
            }, ct: ct);
        }
    }

    /// <summary>The first chunk of every reply reviewed during one inbound turn.</summary>
    public async Task<IReadOnlyList<DiscordDelivery>> ReviewedDuringTurnAsync(
        string bindingId, string turnKey, CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = bindingId,
                ["review_turn"] = turnKey,
            },
            Limit = 100,
        }, ct)).Select(Map)
            .Where(item => item.ChunkIndex == 0 && item.OutboundReview is not null)
            .OrderBy(item => item.CreatedAt)
            .ToList();

    /// <summary>The outbound review recorded for one reply of the binding's current generation.</summary>
    public async Task<JsonObject?> FindOutboundReviewAsync(
        DiscordBinding binding, string messageUid, CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = binding.BindingId,
                ["message_uid"] = messageUid,
            },
            Limit = 100,
        }, ct)).Select(Map)
            .Where(item => item.Generation == binding.Generation && item.OutboundReview is not null)
            .OrderBy(item => item.ChunkIndex)
            .Select(item => item.OutboundReview)
            .FirstOrDefault();

    public async Task<DiscordDelivery> ReserveForumPostAsync(
        string applicationId,
        string guildId,
        string forumId,
        string idempotencyKey,
        string title,
        string content,
        CancellationToken ct = default)
    {
        var identity = $"{applicationId}:{guildId}:{forumId}:{idempotencyKey}";
        var slug = $"discord-forum-post-{DiscordBindingStore.Hash(identity)}";
        var payload = new JsonObject
        {
            ["application_id"] = applicationId,
            ["guild_id"] = guildId,
            ["forum_id"] = forumId,
            ["title"] = title,
            ["content"] = content,
        };
        var payloadHash = Hash(payload.ToJsonString());
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        if (existing is not null)
        {
            var delivery = Map(existing);
            if (!string.Equals(delivery.Kind, ForumPostKind, StringComparison.Ordinal)
                || !string.Equals(delivery.PayloadHash, payloadHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "A stable forum-post identity was reused with different content or scope");
            return delivery;
        }

        return Map(await store.UpsertBySlugAsync(TypeSlug, slug,
            $"Discord forum post {title}", new JsonObject
            {
                ["direction"] = "outbound",
                ["kind"] = ForumPostKind,
                ["binding_id"] = $"discord-forum:{applicationId}:{forumId}",
                ["generation"] = 1,
                ["state"] = "queued",
                ["message_uid"] = idempotencyKey,
                ["chunk_index"] = 0,
                ["payload_hash"] = payloadHash,
                ["payload"] = payload,
                ["attempts"] = 0,
                ["next_attempt_at"] = DateTimeOffset.UtcNow.ToString("O"),
            }, ct));
    }

    /// <summary>
    /// Reserves one owner-directed DM opening. The identity is the bot application and
    /// idempotency key; the hash covers recipient and content, so reusing a key for a
    /// different message is refused before any Discord or transcript side effect.
    /// </summary>
    public async Task<DiscordOwnerDirectMessageOperation> ReserveOwnerDirectMessageAsync(
        string applicationId,
        string idempotencyKey,
        JsonObject identity,
        JsonObject payload,
        CancellationToken ct = default)
    {
        var slug = $"discord-owner-dm-{DiscordBindingStore.Hash(
            $"{applicationId}:{idempotencyKey}")}";
        var payloadHash = Hash(identity.ToJsonString());
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        if (existing is not null)
        {
            var operation = MapOwnerDirectMessage(existing);
            if (!string.Equals(operation.Delivery.Kind, OwnerDirectMessageKind, StringComparison.Ordinal)
                || !string.Equals(operation.Delivery.PayloadHash, payloadHash, StringComparison.Ordinal))
                throw new DiscordIdempotencyConflictException(
                    "This idempotency key was already used for a different Discord DM recipient or content.");
            return operation;
        }

        return MapOwnerDirectMessage(await store.UpsertBySlugAsync(TypeSlug, slug,
            $"Discord owner DM {idempotencyKey}", new JsonObject
            {
                ["direction"] = "outbound",
                ["kind"] = OwnerDirectMessageKind,
                ["binding_id"] = $"discord-owner-dm:{applicationId}",
                ["generation"] = 1,
                ["state"] = "queued",
                ["message_uid"] = idempotencyKey,
                ["chunk_index"] = 0,
                ["payload_hash"] = payloadHash,
                ["payload"] = payload,
                ["attempts"] = 0,
                ["next_attempt_at"] = DateTimeOffset.UtcNow.ToString("O"),
            }, ct));
    }

    /// <summary>Records Discord's acceptance before any binding or session is created.</summary>
    public async Task<DiscordOwnerDirectMessageOperation> MarkOwnerDirectMessageDeliveredAsync(
        DiscordOwnerDirectMessageOperation operation,
        string channelId,
        string discordMessageId,
        bool bindingExisted,
        CancellationToken ct = default)
        => MapOwnerDirectMessage(await store.PatchAsync(operation.Delivery.EntityId, new JsonObject
        {
            ["state"] = "delivered",
            ["discord_id"] = discordMessageId,
            ["channel_id"] = channelId,
            ["binding_existed"] = bindingExisted,
            ["last_error"] = null,
        }, ct: ct));

    public async Task<DiscordOwnerDirectMessageOperation> CompleteOwnerDirectMessageAsync(
        DiscordOwnerDirectMessageOperation operation,
        DiscordOwnerDirectMessageResult result,
        CancellationToken ct = default)
        => MapOwnerDirectMessage(await store.PatchAsync(operation.Delivery.EntityId, new JsonObject
        {
            ["state"] = "sent",
            ["result"] = JsonSerializer.SerializeToNode(result),
            ["last_error"] = null,
        }, ct: ct));

    private static DiscordOwnerDirectMessageOperation MapOwnerDirectMessage(LeafEntity entity)
    {
        var data = entity.Data;
        var delivery = Map(entity);
        return new DiscordOwnerDirectMessageOperation(
            delivery,
            JsonRead.String(data, "channel_id"),
            data["binding_existed"] is JsonValue existed && existed.TryGetValue<bool>(out var value)
                ? value
                : null,
            delivery.State == "sent" && data["result"] is JsonObject result
                ? result.Deserialize<DiscordOwnerDirectMessageResult>()
                : null);
    }

    public async Task<DiscordDelivery?> EnqueueProgressAsync(
        DiscordBinding binding,
        ExternalConversationMessage message,
        string? commentary,
        string? marker,
        CancellationToken ct = default)
    {
        var stateSlug = $"discord-progress-{DiscordBindingStore.Hash(
            $"{binding.BindingId}:{binding.Generation}:{message.MessageUid}")}";
        var stateEntity = await store.GetBySlugAsync(TypeSlug, stateSlug, ct);
        var state = stateEntity?.Data ?? [];
        var stateEpoch = JsonRead.String(state, "epoch");
        var stateSequence = JsonRead.Long(state, "sequence", 0);
        if (string.Equals(stateEpoch, message.Cursor.Epoch, StringComparison.Ordinal)
            && stateSequence >= message.Cursor.Sequence)
            return null;

        var latestCommentary = commentary ?? JsonRead.String(state, "commentary") ?? "";
        var markerCount = checked((int)JsonRead.Long(state, "marker_count", 0));
        var visibleMarkers = JsonRead.String(state, "markers") ?? "";
        if (!string.IsNullOrWhiteSpace(marker))
        {
            markerCount = checked(markerCount + 1);
            visibleMarkers = string.IsNullOrEmpty(visibleMarkers)
                ? marker
                : $"{visibleMarkers} {marker}";
        }

        var stateData = new JsonObject
        {
            ["direction"] = "progress_state",
            ["kind"] = ProgressKind,
            ["binding_id"] = binding.BindingId,
            ["generation"] = binding.Generation,
            ["message_uid"] = message.MessageUid,
            ["epoch"] = message.Cursor.Epoch,
            ["sequence"] = message.Cursor.Sequence,
            ["commentary"] = latestCommentary,
            ["markers"] = visibleMarkers,
            ["marker_count"] = markerCount,
            ["discord_id"] = JsonRead.String(state, "discord_id"),
        };
        if (stateEntity is null)
            _ = await store.UpsertBySlugAsync(
                TypeSlug, stateSlug, $"Discord progress {message.MessageUid}", stateData, ct);
        else
            _ = await store.ReplaceDataAsync(
                stateEntity.Id, stateData, $"Discord progress {message.MessageUid}", ct);

        return await EnqueueOutboundCoreAsync(
            binding, message, 0,
            ComposeProgress(latestCommentary, visibleMarkers),
            [], ProgressKind, ct);
    }

    internal static string ToolUseMarkerFor(string? toolName, string? toolInput)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return ToolUseMarker;
        if (MutatingTools.Contains(toolName)) return MutatingToolMarker;
        if (!ShellTools.Contains(toolName)) return ToolUseMarker;

        var command = ParseShellCommand(toolInput);
        return command is not null && LooksReadOnly(command) && !LooksMutating(command)
            ? ToolUseMarker
            : MutatingToolMarker;
    }

    public async Task<string?> FindProgressReceiptAsync(
        DiscordDelivery delivery,
        CancellationToken ct = default)
    {
        var state = await store.GetBySlugAsync(
            TypeSlug, ProgressStateSlug(delivery), ct);
        var durableReceipt = state is null
            ? null
            : JsonRead.String(state.Data, "discord_id");
        if (!string.IsNullOrWhiteSpace(durableReceipt))
            return durableReceipt;

        return (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["kind"] = ProgressKind,
                ["binding_id"] = delivery.BindingId,
                ["generation"] = delivery.Generation,
                ["message_uid"] = delivery.MessageUid,
            },
            Limit = 500,
        }, ct)).Select(Map)
            .Where(item => item.State == "sent" && item.DiscordId is not null)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => item.DiscordId)
            .FirstOrDefault();
    }

    private async Task<DiscordDelivery> EnqueueOutboundCoreAsync(
        DiscordBinding binding, ExternalConversationMessage message, int chunkIndex,
        string content, IReadOnlyList<ExternalConversationOutputAsset> outputAssets,
        string kind, CancellationToken ct, string initialState = "queued")
    {
        var isOutreach = message.MessageUid.StartsWith(
            "discord-outreach-", StringComparison.Ordinal);
        var isReconciledOutreach = isOutreach
            && !string.Equals(
                message.Cursor.Epoch, "leaf-discord-outreach", StringComparison.Ordinal);
        var cursor = isOutreach
            ? new ExternalConversationCursor("leaf-discord-outreach", 0)
            : message.Cursor;
        var slug = $"discord-out-{DiscordBindingStore.Hash(
            $"{binding.BindingId}:{binding.Generation}:{cursor.Epoch}:{cursor.Sequence}:{message.MessageUid}:{chunkIndex}")}";
        var payload = new JsonObject { ["content"] = content };
        if (outputAssets.Count > 0)
        {
            payload["assets"] = new JsonArray(outputAssets.Select(asset => (JsonNode?)new JsonObject
            {
                ["asset_id"] = asset.AssetId,
                ["description"] = asset.Description,
            }).ToArray());
        }
        // Preserve the historical text-only payload hash so queued deliveries and
        // replayed transcript rows remain ABI-compatible across this upgrade.
        var payloadHash = outputAssets.Count == 0
            ? Hash(content)
            : Hash(payload.ToJsonString());
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        if (existing is not null)
        {
            var delivery = Map(existing);
            if (string.Equals(delivery.PayloadHash, payloadHash, StringComparison.Ordinal))
                return delivery;

            // A direct outreach is delivered before its canonical transcript copy is
            // reconciled. Once Discord accepted that chunk, its durable receipt wins
            // over later delivery-only projection changes and must never be resent.
            if (isReconciledOutreach
                && kind == MessageKind
                && delivery.Kind == MessageKind
                && delivery.State == "sent"
                && !string.IsNullOrWhiteSpace(delivery.DiscordId))
                return delivery;

            // A pre-media bridge may have persisted the canonical Markdown as a
            // queued/retry row immediately before this upgrade and crashed before
            // advancing the binding cursor. Upgrade only that exact legacy shape
            // in place so its stable delivery identity and Discord nonce survive.
            var legacyContent = delivery.Payload?["content"]?.GetValue<string>();
            var isUpgradeableLegacyAssetRow = kind == MessageKind
                && outputAssets.Count > 0
                && delivery.State is "queued" or "retry"
                && delivery.Payload?["assets"] is null
                && string.Equals(legacyContent, message.Content, StringComparison.Ordinal)
                && string.Equals(delivery.PayloadHash, Hash(legacyContent ?? ""), StringComparison.Ordinal);
            if (!isUpgradeableLegacyAssetRow)
                throw new InvalidOperationException(
                    "A stable outbound delivery identity was reused with different content");
            return Map(await store.PatchAsync(delivery.EntityId, new JsonObject
            {
                ["payload_hash"] = payloadHash,
                ["payload"] = payload,
            }, ct: ct));
        }
        var entity = await store.UpsertBySlugAsync(TypeSlug, slug,
            $"Discord outbound {message.MessageUid}/{chunkIndex}", new JsonObject
            {
                ["direction"] = "outbound",
                ["kind"] = kind,
                ["binding_id"] = binding.BindingId,
                ["generation"] = binding.Generation,
                ["state"] = initialState,
                ["message_uid"] = message.MessageUid,
                ["chunk_index"] = chunkIndex,
                ["payload_hash"] = payloadHash,
                ["payload"] = payload,
                ["attempts"] = 0,
                ["next_attempt_at"] = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);
        return Map(entity);
    }

    public async Task<IReadOnlyList<DiscordDelivery>> PendingOutboundAsync(
        string bindingId, CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = bindingId,
            },
            Limit = 500,
        }, ct)).Select(Map)
            .Where(item => item.State == ReviewingState
                           || item.State is "queued" or "retry"
                           && (item.NextAttemptAt is null || item.NextAttemptAt <= DateTimeOffset.UtcNow))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.ChunkIndex)
            .ToList();

    public async Task<bool> HasPendingOutboundMessageAsync(
        string bindingId, CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = bindingId,
            },
            Limit = 500,
        }, ct)).Select(Map).Any(item =>
            item.Kind == MessageKind && item.State is "queued" or "retry" or ReviewingState);

    public async Task<DiscordDelivery?> FindSentOutboundAsync(
        string bindingId,
        long generation,
        string messageUid,
        CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = bindingId,
                ["message_uid"] = messageUid,
            },
            Limit = 100,
        }, ct)).Select(Map)
            .Where(item => item.Generation == generation
                           && item.State is "sent" or "edited"
                           && !string.IsNullOrWhiteSpace(item.DiscordId))
            .OrderBy(item => item.ChunkIndex)
            .FirstOrDefault();

    public async Task<IReadOnlyList<DiscordDelivery>> FindOwnedOutboundAsync(
        string bindingId,
        long generation,
        string messageUid,
        CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = bindingId,
                ["message_uid"] = messageUid,
            },
            Limit = 100,
        }, ct)).Select(Map)
            .Where(item => item.Generation == generation
                           && item.Kind == MessageKind
                           && item.State is "sent" or "edited" or "deleted"
                           && !string.IsNullOrWhiteSpace(item.DiscordId))
            .OrderBy(item => item.ChunkIndex)
            .ToList();

    public async Task MarkEditedAsync(
        DiscordDelivery delivery, CancellationToken ct = default)
        => _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "edited",
            ["edited_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["last_error"] = null,
        }, ct: ct);

    public async Task MarkDeletedAsync(
        DiscordDelivery delivery, CancellationToken ct = default)
    {
        var current = await store.GetAsync(delivery.EntityId, ct);
        if (current is not null
            && string.Equals(JsonRead.String(current.Data, "state"), "deleted",
                StringComparison.Ordinal))
            return;
        _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "deleted",
            ["deleted_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["last_error"] = null,
        }, ct: ct);
    }

    public async Task CancelPendingOutboundAsync(
        string bindingId,
        long generation,
        string messageUid,
        CancellationToken ct = default)
    {
        var candidates = await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["binding_id"] = bindingId,
                ["message_uid"] = messageUid,
            },
            Limit = 100,
        }, ct);
        foreach (var delivery in candidates.Select(Map).Where(item =>
                     item.Generation == generation && item.State is "queued" or "retry"))
            _ = await store.PatchAsync(delivery.EntityId, new JsonObject
            {
                ["state"] = "cancelled",
                ["payload"] = null,
                ["last_error"] = null,
            }, ct: ct);
    }

    public async Task MarkSentAsync(
        DiscordDelivery delivery, string discordMessageId, CancellationToken ct = default)
    {
        if (delivery.Kind == ProgressKind)
        {
            var state = await store.GetBySlugAsync(
                TypeSlug, ProgressStateSlug(delivery), ct);
            if (state is not null)
                _ = await store.PatchAsync(state.Id, new JsonObject
                {
                    ["discord_id"] = discordMessageId,
                }, ct: ct);
        }
        _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "sent",
            ["discord_id"] = discordMessageId,
            ["payload"] = null,
            ["last_error"] = null,
        }, ct: ct);
    }

    public async Task MarkRetryAsync(
        DiscordDelivery delivery, string error, TimeSpan delay, CancellationToken ct = default)
        => _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "retry",
            ["attempts"] = checked(delivery.Attempts + 1),
            ["next_attempt_at"] = DateTimeOffset.UtcNow.Add(delay).ToString("O"),
            ["last_error"] = Truncate(error, 500),
        }, ct: ct);

    public async Task MarkFailedAsync(
        DiscordDelivery delivery, string error, CancellationToken ct = default)
        => _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "failed",
            ["attempts"] = checked(delivery.Attempts + 1),
            ["last_error"] = Truncate(error, 500),
        }, ct: ct);

    private static DiscordDelivery Map(LeafEntity entity)
    {
        var data = entity.Data;
        DateTimeOffset? next = null;
        if (DateTimeOffset.TryParse(JsonRead.String(data, "next_attempt_at"), out var parsed)) next = parsed;
        return new DiscordDelivery(
            entity.Id,
            JsonRead.String(data, "direction") ?? "inbound",
            JsonRead.String(data, "kind") ?? MessageKind,
            JsonRead.String(data, "binding_id") ?? "",
            JsonRead.Long(data, "generation", 1),
            JsonRead.String(data, "state") ?? "received",
            JsonRead.String(data, "discord_id"),
            JsonRead.String(data, "message_uid"),
            checked((int)JsonRead.Long(data, "chunk_index", 0)),
            JsonRead.String(data, "payload_hash"),
            data["payload"]?.DeepClone(),
            checked((int)JsonRead.Long(data, "attempts", 0)),
            next,
            JsonRead.String(data, "last_error"),
            entity.CreatedAt)
        {
            OutboundReview = data["outbound_review"]?.DeepClone() as JsonObject,
        };
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length];

    private static string ProgressStateSlug(DiscordDelivery delivery)
        => $"discord-progress-{DiscordBindingStore.Hash(
            $"{delivery.BindingId}:{delivery.Generation}:{delivery.MessageUid}")}";

    private static string ComposeProgress(string commentary, string visibleMarkers)
    {
        var rail = string.IsNullOrEmpty(visibleMarkers)
            ? ""
            : $"-# {visibleMarkers}";
        var maximumCommentaryLength = string.IsNullOrEmpty(rail) ? 2000 : 1998 - rail.Length;
        var prose = Truncate(commentary.Trim(), Math.Max(0, maximumCommentaryLength));
        if (string.IsNullOrEmpty(prose)) return rail;
        return string.IsNullOrEmpty(rail) ? prose : $"{prose}\n\n{rail}";
    }

    private static string? ParseShellCommand(string? toolInput)
    {
        if (string.IsNullOrWhiteSpace(toolInput)) return null;
        try
        {
            using var document = JsonDocument.Parse(toolInput);
            return document.RootElement.TryGetProperty("command", out var command)
                   && command.ValueKind == JsonValueKind.String
                ? command.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksReadOnly(string command)
        => Regex.IsMatch(command,
            @"\b(?:rg|ripgrep)\b|\bSelect-String\b|\bfindstr(?:\.exe)?\b|\bGet-ChildItem\b|(?:^|[;&|]\s*)(?:ls|dir)\b|\bGet-Content\b|(?:^|[;&|]\s*)(?:cat|type)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool LooksMutating(string command)
        => Regex.IsMatch(command,
               @"\b(?:Set-Content|Add-Content|Out-File|Remove-Item|Move-Item|Copy-Item|New-Item|Rename-Item|Clear-Content|git\s+(?:add|commit|push|reset|checkout|restore|clean)|npm\s+(?:install|publish)|pnpm\s+(?:install|publish))\b",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
           || Regex.IsMatch(command, @"(?:^|\s)(?:\d?>>?|&>)\s*[^=&]",
               RegexOptions.CultureInvariant);
}
