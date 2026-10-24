using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed record LeafDiscordConfig(
    string ApplicationId,
    string InteractionPublicKey,
    string? BotToken,
    bool Enabled,
    string AgentId,
    string SessionQualityMode,
    string AudienceProfile,
    string AvatarAssetId,
    IReadOnlySet<string> DmAllowlist,
    IReadOnlySet<string> ClearAllowlist,
    long MaxAttachmentBytes)
{
    public Guid EntityId { get; init; }
    public string EntitySlug { get; init; } = "";
    public string? AvatarFingerprint { get; init; }
    public bool AvatarManaged { get; init; }
    public IReadOnlyDictionary<string, string> GuildAvatarAssetIds { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> GuildAvatarFingerprints { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public string? OwnerUserId { get; init; }
    public string PresenceContextMode { get; init; } = DiscordPresenceContextModes.Off;
    public bool PresenceExtensionFeedEnabled { get; init; }
    /// <summary>Show tool-call squares in the progress message while the Agent works.</summary>
    public bool ShowToolActivity { get; init; } = true;

    public bool Ready => Enabled && !string.IsNullOrWhiteSpace(BotToken)
        && !string.IsNullOrWhiteSpace(ApplicationId) && !string.IsNullOrWhiteSpace(AgentId);
}

public sealed class LeafDiscordConfigStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-config";
    public const string Slug = "leaf-discord-config-singleton";
    internal IEntityStore EntityStore => store;

    public async Task<LeafEntity> EnsureAsync(CancellationToken ct = default)
    {
        var existing = await store.GetBySlugAsync(TypeSlug, Slug, ct);
        if (existing is not null) return existing;

        return await store.UpsertBySlugAsync(TypeSlug, Slug, "Discord", new JsonObject
        {
            ["application_id"] = "",
            ["interaction_public_key"] = "",
            ["enabled"] = false,
            ["default_agent"] = "nova",
            ["session_quality_mode"] = "deep-codex",
            ["audience_profile"] = "nova-discord-external",
            ["avatar_asset_id"] = "",
            ["dm_allowlist"] = new JsonArray(),
            ["clear_allowlist"] = new JsonArray(),
            ["max_attachment_bytes"] = 10 * 1024 * 1024,
            ["presence_context_mode"] = DiscordPresenceContextModes.Off,
            ["presence_extension_feed_enabled"] = false,
            ["show_tool_activity"] = true,
            ["avatar_managed"] = false,
            ["guild_avatar_asset_ids"] = new JsonObject(),
            ["guild_avatar_fingerprints"] = new JsonObject(),
        }, ct);
    }

    public async Task<LeafDiscordConfig> GetAsync(CancellationToken ct = default)
    {
        var entity = await EnsureAsync(ct);
        return Map(entity);
    }

    public async Task<LeafDiscordConfig?> GetExistingAsync(CancellationToken ct = default)
    {
        var entity = await store.GetBySlugAsync(TypeSlug, Slug, ct);
        return entity is null ? null : Map(entity);
    }

    internal static LeafDiscordConfig Map(LeafEntity entity)
    {
        var data = entity.Data;
        return new LeafDiscordConfig(
            JsonRead.String(data, "application_id") ?? "",
            JsonRead.String(data, "interaction_public_key") ?? "",
            JsonRead.String(data, "bot_token"),
            JsonRead.Bool(data, "enabled"),
            JsonRead.String(data, "default_agent") ?? "nova",
            JsonRead.String(data, "session_quality_mode") ?? "deep-codex",
            JsonRead.String(data, "audience_profile") ?? "nova-discord-external",
            JsonRead.String(data, "avatar_asset_id") ?? "",
            JsonRead.StringSet(data, "dm_allowlist"),
            JsonRead.StringSet(data, "clear_allowlist"),
            Math.Clamp(JsonRead.Long(data, "max_attachment_bytes", 10 * 1024 * 1024), 1, 25 * 1024 * 1024))
        {
            EntityId = entity.Id,
            EntitySlug = entity.Slug,
            AvatarManaged = JsonRead.Bool(data, "avatar_managed"),
            GuildAvatarAssetIds = JsonRead.StringDictionary(data, "guild_avatar_asset_ids"),
            GuildAvatarFingerprints = JsonRead.StringDictionary(
                data, "guild_avatar_fingerprints"),
            PresenceContextMode = DiscordPresenceContextModes.Normalize(
                JsonRead.String(data, "presence_context_mode")),
            PresenceExtensionFeedEnabled = JsonRead.Bool(
                data, "presence_extension_feed_enabled"),
            ShowToolActivity = JsonRead.Bool(data, "show_tool_activity", true),
        };
    }

    public async Task<LeafDiscordConfigView> GetViewAsync(CancellationToken ct = default)
        => View(await GetAsync(ct));

    public async Task<LeafDiscordConfigView> SaveAsync(
        LeafDiscordConfigUpdate update, CancellationToken ct = default)
    {
        var existingEntity = await EnsureAsync(ct);
        var existing = await GetAsync(ct);
        var normalized = NormalizeUpdate(existing, update);
        var saved = await store.PatchAsync(existingEntity.Id, new JsonObject
        {
            ["application_id"] = normalized.ApplicationId,
            ["interaction_public_key"] = normalized.InteractionPublicKey,
            ["bot_token"] = normalized.BotToken,
            ["enabled"] = normalized.Enabled,
            ["default_agent"] = normalized.AgentId,
            ["session_quality_mode"] = normalized.SessionQualityMode,
            ["dm_allowlist"] = new JsonArray(normalized.DmAllowlist
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["clear_allowlist"] = new JsonArray(normalized.ClearAllowlist
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["max_attachment_bytes"] = normalized.MaxAttachmentBytes,
            ["presence_context_mode"] = normalized.PresenceContextMode,
            ["presence_extension_feed_enabled"] = normalized.PresenceExtensionFeedEnabled,
            ["show_tool_activity"] = normalized.ShowToolActivity,
            ["avatar_asset_id"] = existing.AvatarAssetId,
            ["avatar_managed"] = existing.AvatarManaged,
            ["avatar_fingerprint"] = existing.AvatarFingerprint,
            ["guild_avatar_asset_ids"] = JsonRead.StringObject(existing.GuildAvatarAssetIds),
            ["guild_avatar_fingerprints"] = JsonRead.StringObject(
                existing.GuildAvatarFingerprints),
        }, ct: ct);
        return View(new LeafDiscordConfig(
            JsonRead.String(saved.Data, "application_id") ?? normalized.ApplicationId,
            JsonRead.String(saved.Data, "interaction_public_key") ?? normalized.InteractionPublicKey,
            JsonRead.String(saved.Data, "bot_token"),
            JsonRead.Bool(saved.Data, "enabled"),
            JsonRead.String(saved.Data, "default_agent") ?? normalized.AgentId,
            JsonRead.String(saved.Data, "session_quality_mode") ?? normalized.SessionQualityMode,
            JsonRead.String(saved.Data, "audience_profile") ?? normalized.AudienceProfile,
            JsonRead.String(saved.Data, "avatar_asset_id") ?? "",
            JsonRead.StringSet(saved.Data, "dm_allowlist"),
            JsonRead.StringSet(saved.Data, "clear_allowlist"),
            Math.Clamp(JsonRead.Long(saved.Data, "max_attachment_bytes", normalized.MaxAttachmentBytes),
                1, 25 * 1024 * 1024))
        {
            PresenceContextMode = DiscordPresenceContextModes.Normalize(
                JsonRead.String(saved.Data, "presence_context_mode")),
            PresenceExtensionFeedEnabled = JsonRead.Bool(
                saved.Data, "presence_extension_feed_enabled"),
            ShowToolActivity = JsonRead.Bool(saved.Data, "show_tool_activity", true),
            AvatarManaged = JsonRead.Bool(saved.Data, "avatar_managed"),
            AvatarFingerprint = JsonRead.String(saved.Data, "avatar_fingerprint"),
            GuildAvatarAssetIds = JsonRead.StringDictionary(
                saved.Data, "guild_avatar_asset_ids"),
            GuildAvatarFingerprints = JsonRead.StringDictionary(
                saved.Data, "guild_avatar_fingerprints"),
        });
    }

    internal static LeafDiscordConfig NormalizeUpdate(
        LeafDiscordConfig existing, LeafDiscordConfigUpdate update)
    {
        var applicationId = RequiredSnowflake(update.ApplicationId, "Application ID");
        var publicKey = (update.InteractionPublicKey ?? "").Trim().ToLowerInvariant();
        if (publicKey.Length != 64 || publicKey.Any(ch => !Uri.IsHexDigit(ch)))
            throw new ArgumentException("Interaction public key must contain exactly 64 hexadecimal characters.");
        var token = update.ClearBotToken
            ? null
            : string.IsNullOrWhiteSpace(update.BotToken)
                ? existing.BotToken
                : update.BotToken.Trim();
        if (update.Enabled && string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A Discord bot token is required before the bridge can be enabled.");
        var dmAllowlist = ValidateSnowflakes(update.DmAllowlist, "DM allowlist");
        var clearAllowlist = ValidateSnowflakes(update.ClearAllowlist, "/clear allowlist");
        return existing with
        {
            ApplicationId = applicationId,
            InteractionPublicKey = publicKey,
            BotToken = token,
            Enabled = update.Enabled,
            AgentId = string.IsNullOrWhiteSpace(update.AgentId)
                ? existing.AgentId : update.AgentId.Trim(),
            SessionQualityMode = string.IsNullOrWhiteSpace(update.SessionQualityMode)
                ? existing.SessionQualityMode : update.SessionQualityMode.Trim(),
            DmAllowlist = dmAllowlist.ToHashSet(StringComparer.Ordinal),
            ClearAllowlist = clearAllowlist.ToHashSet(StringComparer.Ordinal),
            MaxAttachmentBytes = Math.Clamp(update.MaxAttachmentBytes, 1, 25 * 1024 * 1024),
            AvatarAssetId = existing.AvatarAssetId,
            PresenceContextMode = DiscordPresenceContextModes.Normalize(
                update.PresenceContextMode, existing.PresenceContextMode),
            PresenceExtensionFeedEnabled = update.PresenceExtensionFeedEnabled
                ?? existing.PresenceExtensionFeedEnabled,
            ShowToolActivity = update.ShowToolActivity ?? existing.ShowToolActivity,
        };
    }

    internal static LeafDiscordConfigView View(LeafDiscordConfig config) => new(
        config.ApplicationId,
        config.InteractionPublicKey,
        !string.IsNullOrWhiteSpace(config.BotToken),
        config.Enabled,
        config.AgentId,
        config.SessionQualityMode,
        config.AudienceProfile,
        config.AvatarAssetId,
        config.GuildAvatarAssetIds,
        config.DmAllowlist.Order().ToArray(),
        config.ClearAllowlist.Order().ToArray(),
        config.MaxAttachmentBytes)
    {
        BotId = config.EntityId,
        PresenceContextMode = config.PresenceContextMode,
        PresenceExtensionFeedEnabled = config.PresenceExtensionFeedEnabled,
        ShowToolActivity = config.ShowToolActivity,
    };

    private static string RequiredSnowflake(string? value, string label)
    {
        value = value?.Trim();
        if (value is null || !ulong.TryParse(value, out var parsed) || parsed == 0)
            throw new ArgumentException($"{label} must be a Discord snowflake.");
        return value;
    }

    private static string[] ValidateSnowflakes(IEnumerable<string>? values, string label)
        => (values ?? []).Select(value => RequiredSnowflake(value, label))
            .Distinct(StringComparer.Ordinal).ToArray();
}

public sealed record LeafDiscordConfigView(
    string ApplicationId,
    string InteractionPublicKey,
    bool HasBotToken,
    bool Enabled,
    string AgentId,
    string SessionQualityMode,
    string AudienceProfile,
    string AvatarAssetId,
    IReadOnlyDictionary<string, string> GuildAvatarAssetIds,
    IReadOnlyList<string> DmAllowlist,
    IReadOnlyList<string> ClearAllowlist,
    long MaxAttachmentBytes)
{
    public Guid BotId { get; init; }
    public string? AgentName { get; init; }
    public string? AgentAvatarUrl { get; init; }
    public string PresenceContextMode { get; init; } = DiscordPresenceContextModes.Off;
    public bool PresenceExtensionFeedEnabled { get; init; }
    public bool ShowToolActivity { get; init; } = true;
}

public sealed record LeafDiscordConfigUpdate(
    string ApplicationId,
    string InteractionPublicKey,
    string? BotToken,
    bool ClearBotToken,
    bool Enabled,
    IReadOnlyList<string>? DmAllowlist,
    IReadOnlyList<string>? ClearAllowlist,
    long MaxAttachmentBytes = 10 * 1024 * 1024,
    string? AgentId = null,
    string? SessionQualityMode = null,
    string? PresenceContextMode = null,
    bool? PresenceExtensionFeedEnabled = null,
    bool? ShowToolActivity = null);

internal static class DiscordPresenceContextModes
{
    public const string Off = "off";
    public const string Active = "active";
    public const string Roster = "roster";

    public static string Normalize(string? value, string fallback = Off)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback.Trim().ToLowerInvariant()
            : value.Trim().ToLowerInvariant();
        return normalized is Off or Active or Roster
            ? normalized
            : throw new ArgumentException(
                "Presence context mode must be 'off', 'active', or 'roster'.");
    }
}

public sealed record DiscordBinding(
    Guid EntityId,
    string BindingId,
    string ApplicationId,
    string ConversationId,
    string Kind,
    string? GuildId,
    string? ParentConversationId,
    string AgentId,
    string OwnerId,
    string AudienceProfile,
    string ResponseMode,
    long Generation,
    string? ConversationHandleId,
    string? SessionId,
    string? Epoch,
    long Sequence,
    string? LastReceivedSnowflake,
    string? LastClearRequestId,
    bool Enabled,
    string ConversationName,
    string? GuildName)
{
    public string? CounterpartyUserId { get; init; }
    public string? IconUrl { get; init; }
    public string SessionQualityMode { get; init; } = "deep-codex";
    public string? ChannelAvatarAssetId { get; init; }
    public string DeliveryMode { get; init; } = "bot";
    public string? WebhookId { get; init; }
    public IReadOnlySet<string> AuthorizedAutomationIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public DiscordServerMapDisclosureState? ServerMapDisclosure { get; init; }
    /// <summary>
    /// The newest Discord message this binding delivered to its Agent session, valid only for
    /// <see cref="LastDeliveredGeneration"/>. Unlike <see cref="LastReceivedSnowflake"/>, it
    /// never advances for a message that was recorded but not admitted.
    /// </summary>
    public string? LastDeliveredSnowflake { get; init; }
    public long? LastDeliveredGeneration { get; init; }
    [JsonIgnore]
    public string? WebhookToken { get; init; }
}

public sealed record CreateDiscordBinding(
    string ApplicationId,
    string ConversationId,
    string Kind,
    string? GuildId,
    string? ParentConversationId,
    string AgentId,
    string OwnerId,
    string AudienceProfile,
    string ResponseMode = "participate",
    string? ConversationName = null,
    string? GuildName = null,
    string? CounterpartyUserId = null,
    string SessionQualityMode = "deep-codex");

public sealed record WebhookTokenProtectionResult(
    int Rewritten, IReadOnlyList<Guid> Failed);

public sealed class DiscordBindingStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-binding";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> creationGates =
        new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<DiscordBinding>> ListAsync(CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery { TypeSlug = TypeSlug, Limit = 500 }, ct))
            .Select(Map).ToList();

    public async Task<DiscordBinding?> GetAsync(Guid entityId, CancellationToken ct = default)
        => await store.GetAsync(entityId, ct) is { TypeSlug: TypeSlug } entity ? Map(entity) : null;

    public async Task<DiscordBinding?> FindAsync(
        string applicationId, string conversationId, CancellationToken ct = default)
    {
        var slug = BindingSlug(applicationId, conversationId);
        var entity = await store.GetBySlugAsync(TypeSlug, slug, ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<DiscordBinding?> FindByBindingIdAsync(
        string bindingId, CancellationToken ct = default)
    {
        var entities = await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?> { ["binding_id"] = bindingId },
            Limit = 2,
        }, ct);
        return entities.Count == 1 ? Map(entities[0]) : null;
    }

    public async Task<DiscordBinding> CreateAsync(CreateDiscordBinding request, CancellationToken ct = default)
    {
        ValidateKind(request.Kind);
        ValidateMode(request.ResponseMode);
        var bindingId = BindingKey(request.ApplicationId, request.ConversationId);
        var entity = await store.UpsertBySlugAsync(TypeSlug,
            BindingSlug(request.ApplicationId, request.ConversationId),
            $"Discord {request.ConversationName ?? request.ConversationId}", new JsonObject
            {
                ["binding_id"] = bindingId,
                ["application_id"] = request.ApplicationId,
                ["conversation_id"] = request.ConversationId,
                ["kind"] = request.Kind,
                ["guild_id"] = request.GuildId,
                ["parent_conversation_id"] = request.ParentConversationId,
                ["agent"] = request.AgentId,
                ["session_quality_mode"] = request.SessionQualityMode,
                ["owner_id"] = request.OwnerId,
                ["audience_profile"] = request.AudienceProfile,
                ["response_mode"] = request.ResponseMode,
                ["generation"] = 1,
                ["sequence"] = 0,
                ["enabled"] = true,
                ["conversation_name"] = request.ConversationName,
                ["guild_name"] = request.GuildName,
                ["counterparty_user_id"] = request.CounterpartyUserId,
            }, ct);
        return Map(entity);
    }

    public async Task<DiscordBinding> GetOrCreateAsync(
        CreateDiscordBinding request, CancellationToken ct = default)
    {
        var key = BindingKey(request.ApplicationId, request.ConversationId);
        var gate = creationGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var existing = await FindAsync(request.ApplicationId, request.ConversationId, ct);
            if (existing is null) return await CreateAsync(request, ct);
            var patch = new JsonObject();
            string? entityName = null;
            if (!string.IsNullOrWhiteSpace(request.ConversationName)
                && request.ConversationName != existing.ConversationName)
            {
                patch["conversation_name"] = request.ConversationName;
                entityName = $"Discord {request.ConversationName}";
            }
            if (!string.IsNullOrWhiteSpace(request.GuildName)
                && request.GuildName != existing.GuildName)
                patch["guild_name"] = request.GuildName;
            if (!string.IsNullOrWhiteSpace(request.CounterpartyUserId)
                && request.CounterpartyUserId != existing.CounterpartyUserId)
                patch["counterparty_user_id"] = request.CounterpartyUserId;
            if (IsLegacyLocalOwner(existing.OwnerId)
                && !IsLegacyLocalOwner(request.OwnerId)
                && !string.IsNullOrWhiteSpace(request.OwnerId))
                patch["owner_id"] = request.OwnerId;
            if (patch.Count > 0)
                existing = Map(await store.PatchAsync(
                    existing.EntityId, patch, name: entityName, ct: ct));
            return existing;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordBinding> UpdateConversationMetadataAsync(
        DiscordBinding binding,
        string conversationName,
        string? guildName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conversationName))
            throw new ArgumentException("Discord conversation name is required.");
        var current = await GetAsync(binding.EntityId, ct)
            ?? throw new KeyNotFoundException("Discord binding was not found.");
        var patch = new JsonObject();
        if (!string.Equals(current.ConversationName, conversationName,
                StringComparison.Ordinal))
            patch["conversation_name"] = conversationName;
        if (!string.IsNullOrWhiteSpace(guildName)
            && !string.Equals(current.GuildName, guildName,
                StringComparison.Ordinal))
            patch["guild_name"] = guildName;
        return patch.Count == 0
            ? current
            : Map(await store.PatchAsync(
                current.EntityId,
                patch,
                name: $"Discord {conversationName}",
                ct: ct));
    }

    public async Task<DiscordBinding> RepairLegacyOwnerAsync(
        DiscordBinding binding,
        string canonicalOwnerId,
        CancellationToken ct = default)
    {
        if (!IsLegacyLocalOwner(binding.OwnerId)
            || IsLegacyLocalOwner(canonicalOwnerId)
            || string.IsNullOrWhiteSpace(canonicalOwnerId))
            return binding;
        var current = await GetAsync(binding.EntityId, ct)
            ?? throw new InvalidOperationException("Discord binding disappeared during owner migration");
        if (!IsLegacyLocalOwner(current.OwnerId)) return current;
        return Map(await store.PatchAsync(current.EntityId, new JsonObject
        {
            ["owner_id"] = canonicalOwnerId,
        }, ct: ct));
    }

    public async Task<DiscordBinding> AttachConversationAsync(
        DiscordBinding binding, ExternalConversationHandle handle, CancellationToken ct = default)
    {
        if (handle.Generation != binding.Generation || handle.BindingId != binding.BindingId)
            throw new InvalidOperationException("External conversation handle does not match the binding generation");
        return Map(await store.PatchAsync(binding.EntityId, new JsonObject
        {
            ["conversation_handle_id"] = handle.ConversationId,
            ["session_id"] = handle.SessionId,
            ["epoch"] = null,
            ["sequence"] = 0,
            ["server_map_disclosure"] = null,
        }, ct: ct));
    }

    public async Task<DiscordBinding> AdvanceGenerationAndAttachAsync(
        DiscordBinding binding, ExternalConversationHandle handle, string requestId,
        CancellationToken ct = default,
        string? agentId = null, string? sessionQualityMode = null)
    {
        if (handle.Generation != checked(binding.Generation + 1)
            || handle.BindingId != binding.BindingId)
            throw new InvalidOperationException("External conversation handle does not match the next binding generation");
        return Map(await store.PatchAsync(binding.EntityId, new JsonObject
        {
            ["generation"] = handle.Generation,
            ["conversation_handle_id"] = handle.ConversationId,
            ["session_id"] = handle.SessionId,
            ["epoch"] = null,
            ["sequence"] = 0,
            ["server_map_disclosure"] = null,
            ["last_clear_request_id"] = requestId,
            ["agent"] = agentId ?? binding.AgentId,
            ["session_quality_mode"] = sessionQualityMode ?? binding.SessionQualityMode,
        }, ct: ct));
    }

    public async Task<DiscordBinding> AdvanceCursorAsync(
        DiscordBinding binding, ExternalConversationCursor cursor, CancellationToken ct = default)
    {
        var current = await GetAsync(binding.EntityId, ct)
            ?? throw new InvalidOperationException(
                "Discord binding disappeared during transcript reconciliation");
        if (current.Generation != binding.Generation
            || !string.Equals(current.SessionId, binding.SessionId, StringComparison.Ordinal)
            || !string.Equals(current.ConversationHandleId, binding.ConversationHandleId,
                StringComparison.Ordinal))
            throw new DiscordBindingSessionChangedException(
                "The Discord binding session changed during transcript reconciliation");

        if (!string.Equals(current.Epoch, binding.Epoch, StringComparison.Ordinal)
            || current.Sequence != binding.Sequence)
        {
            if (string.Equals(current.Epoch, cursor.Epoch, StringComparison.Ordinal)
                && current.Sequence >= cursor.Sequence)
                return current;
            throw new DiscordBindingSessionChangedException(
                "The Discord binding cursor changed during transcript reconciliation");
        }

        if (string.Equals(current.Epoch, cursor.Epoch, StringComparison.Ordinal)
            && cursor.Sequence <= current.Sequence)
            return current;

        return Map(await store.PatchAsync(binding.EntityId, new JsonObject
        {
            ["epoch"] = cursor.Epoch,
            ["sequence"] = cursor.Sequence,
        }, ct: ct));
    }

    public async Task MarkReceivedAsync(DiscordBinding binding, string snowflake, CancellationToken ct = default)
        => _ = await store.PatchAsync(binding.EntityId,
            new JsonObject { ["last_received_snowflake"] = snowflake }, ct: ct);

    /// <summary>
    /// Advances the delivered-to-session cursor for the binding's current generation. The
    /// cursor only moves forward within a generation; a new generation starts it afresh.
    /// </summary>
    public async Task<DiscordBinding> MarkDeliveredAsync(
        DiscordBinding binding, string snowflake, CancellationToken ct = default)
    {
        if (!ulong.TryParse(snowflake, out var delivered)) return binding;
        var current = await GetAsync(binding.EntityId, ct) ?? binding;
        if (current.LastDeliveredGeneration == binding.Generation
            && ulong.TryParse(current.LastDeliveredSnowflake, out var existing)
            && existing >= delivered)
            return current;
        return Map(await store.PatchAsync(binding.EntityId, new JsonObject
        {
            ["last_delivered_snowflake"] = snowflake,
            ["last_delivered_generation"] = binding.Generation,
        }, ct: ct));
    }

    /// <summary>
    /// Records which server map the bound session now holds. Call only after the turn was
    /// handed to the session, so a filtered or failed turn can never hide a map change.
    /// </summary>
    public async Task<DiscordBinding> RecordServerMapDisclosureAsync(
        DiscordBinding binding, DiscordServerMapDisclosureState state,
        CancellationToken ct = default)
        => Map(await store.PatchAsync(binding.EntityId, new JsonObject
        {
            ["server_map_disclosure"] = DiscordServerMapDisclosure.ToJson(state),
        }, ct: ct));

    public async Task<DiscordBinding> SetChannelAvatarAsync(
        DiscordBinding binding, string avatarAssetId,
        string webhookId, string webhookToken,
        CancellationToken ct = default)
        => Map(await store.PatchAsync(binding.EntityId, new JsonObject
        {
            ["channel_avatar_asset_id"] = avatarAssetId,
            ["delivery_mode"] = "webhook",
            ["webhook_id"] = webhookId,
            ["webhook_token"] = webhookToken,
        }, ct: ct));

    public async Task<DiscordBinding> ClearChannelAvatarAsync(
        DiscordBinding binding, CancellationToken ct = default)
        => Map(await store.PatchAsync(binding.EntityId, new JsonObject
        {
            ["channel_avatar_asset_id"] = null,
            ["delivery_mode"] = "bot",
            ["webhook_id"] = null,
            ["webhook_token"] = null,
        }, ct: ct));

    /// <summary>
    /// Moves webhook tokens stored before <c>webhook_token</c> was a declared secret field into
    /// the RedLeaf vault. Reads hydrate vaulted and plaintext tokens alike, so the plugin cannot
    /// tell them apart: every webhook binding is rewritten with its unchanged <c>webhook_id</c>.
    /// The kernel write boundary vaults any declared secret still in the stored document, and an
    /// unchanged value records no version, so this is safe to run on every start.
    /// </summary>
    public async Task<WebhookTokenProtectionResult> ProtectStoredWebhookTokensAsync(
        CancellationToken ct = default)
    {
        const int pageSize = 500;
        var candidates = new Dictionary<Guid, string>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await store.QueryAsync(new EntityQuery
            {
                TypeSlug = TypeSlug,
                DataEquals = new Dictionary<string, object?> { ["delivery_mode"] = "webhook" },
                Offset = offset,
                Limit = pageSize,
            }, ct);
            foreach (var binding in page.Select(Map))
                if (!string.IsNullOrWhiteSpace(binding.WebhookToken)
                    && !string.IsNullOrWhiteSpace(binding.WebhookId))
                    candidates[binding.EntityId] = binding.WebhookId;
            if (page.Count < pageSize) break;
        }

        var failed = new List<Guid>();
        foreach (var (entityId, webhookId) in candidates)
        {
            try
            {
                await store.PatchAsync(entityId, new JsonObject
                {
                    ["webhook_id"] = webhookId,
                }, ct: ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                failed.Add(entityId);
            }
        }
        return new WebhookTokenProtectionResult(candidates.Count - failed.Count, failed);
    }

    public static string BindingKey(string applicationId, string conversationId)
        => $"discord:{applicationId}:{conversationId}";

    private static string BindingSlug(string applicationId, string conversationId)
        => $"discord-binding-{Hash($"{applicationId}:{conversationId}")}";

    internal static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static bool IsLegacyLocalOwner(string? ownerId)
        => string.Equals(ownerId, "local-user", StringComparison.OrdinalIgnoreCase);

    private static void ValidateKind(string value)
    {
        if (value is not ("dm" or "guild_channel" or "thread"))
            throw new ArgumentException("Binding kind must be dm, guild_channel, or thread", nameof(value));
    }

    private static void ValidateMode(string value)
    {
        if (!DiscordResponseModes.IsValid(value))
            throw new ArgumentException(
                "Response mode must be direct, selective, participate, or context_only",
                nameof(value));
    }

    private static DiscordBinding Map(LeafEntity entity)
    {
        var data = entity.Data;
        return new DiscordBinding(
            entity.Id,
            JsonRead.String(data, "binding_id") ?? BindingKey(
                JsonRead.String(data, "application_id") ?? "",
                JsonRead.String(data, "conversation_id") ?? ""),
            JsonRead.String(data, "application_id") ?? "",
            JsonRead.String(data, "conversation_id") ?? "",
            JsonRead.String(data, "kind") ?? "guild_channel",
            JsonRead.String(data, "guild_id"),
            JsonRead.String(data, "parent_conversation_id"),
            JsonRead.String(data, "agent") ?? "nova",
            JsonRead.String(data, "owner_id") ?? "",
            JsonRead.String(data, "audience_profile") ?? "nova-discord-external",
            JsonRead.String(data, "response_mode") ?? "direct",
            JsonRead.Long(data, "generation", 1),
            JsonRead.String(data, "conversation_handle_id"),
            JsonRead.String(data, "session_id"),
            JsonRead.String(data, "epoch"),
            JsonRead.Long(data, "sequence", 0),
            JsonRead.String(data, "last_received_snowflake"),
            JsonRead.String(data, "last_clear_request_id"),
            JsonRead.Bool(data, "enabled", true),
            JsonRead.String(data, "conversation_name")
                ?? JsonRead.String(data, "conversation_id") ?? entity.Name,
            JsonRead.String(data, "guild_name"))
        {
            CounterpartyUserId = JsonRead.String(data, "counterparty_user_id"),
            SessionQualityMode = JsonRead.String(data, "session_quality_mode") ?? "deep-codex",
            ChannelAvatarAssetId = JsonRead.String(data, "channel_avatar_asset_id"),
            DeliveryMode = JsonRead.String(data, "delivery_mode") ?? "bot",
            WebhookId = JsonRead.String(data, "webhook_id"),
            AuthorizedAutomationIds = JsonRead.StringSet(
                data, "authorized_automation_ids"),
            ServerMapDisclosure = DiscordServerMapDisclosure.FromJson(
                data["server_map_disclosure"]),
            LastDeliveredSnowflake = JsonRead.String(data, "last_delivered_snowflake"),
            LastDeliveredGeneration = data["last_delivered_generation"] is null
                ? null
                : JsonRead.Long(data, "last_delivered_generation", 0),
            WebhookToken = JsonRead.String(data, "webhook_token"),
        };
    }
}

internal static class JsonRead
{
    public static string? String(JsonObject data, string key)
        => data.TryGetPropertyValue(key, out var node) && node is JsonValue value
           && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)
            ? result : null;

    public static bool Bool(JsonObject data, string key, bool fallback = false)
        => data.TryGetPropertyValue(key, out var node) && node is JsonValue value
           && value.TryGetValue<bool>(out var result) ? result : fallback;

    public static long Long(JsonObject data, string key, long fallback)
    {
        if (!data.TryGetPropertyValue(key, out var node) || node is not JsonValue value) return fallback;
        if (value.TryGetValue<long>(out var result)) return result;
        if (value.TryGetValue<int>(out var integer)) return integer;
        return fallback;
    }

    public static IReadOnlySet<string> StringSet(JsonObject data, string key)
        => data[key] is JsonArray array
            ? array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text : null)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> StringDictionary(
        JsonObject data, string key)
    {
        if (data[key] is not JsonObject value)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        return value
            .Where(item => item.Value is JsonValue scalar
                && scalar.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            .ToDictionary(
                item => item.Key,
                item => item.Value!.GetValue<string>(),
                StringComparer.Ordinal);
    }

    public static JsonObject StringObject(IReadOnlyDictionary<string, string> values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values.OrderBy(item => item.Key, StringComparer.Ordinal))
            result[key] = value;
        return result;
    }
}
