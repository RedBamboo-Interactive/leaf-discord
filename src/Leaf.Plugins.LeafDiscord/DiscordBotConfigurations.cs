using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Durable per-Agent Discord bot definitions. The old singleton remains a read-only
/// migration source so existing installations retain their token and binding identity.
/// </summary>
public sealed class DiscordBotConfigurationStore(
    IEntityStore store,
    LeafDiscordConfigStore legacy)
{
    public DiscordBotConfigurationStore(LeafDiscordConfigStore legacy)
        : this(legacy.EntityStore, legacy)
    {
    }

    public const string TypeSlug = "leaf-discord-bot";
    internal IEntityStore EntityStore => store;
    private readonly SemaphoreSlim migrationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> applicationGates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> avatarGates = new();

    public async Task<IReadOnlyList<LeafDiscordConfig>> ListAsync(CancellationToken ct = default)
    {
        await EnsureLegacyMigratedAsync(ct);
        return (await store.QueryAsync(new EntityQuery { TypeSlug = TypeSlug, Limit = 200 }, ct))
            .Select(Map)
            .OrderBy(config => config.AgentId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(config => config.ApplicationId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<LeafDiscordConfig?> GetAsync(Guid entityId, CancellationToken ct = default)
        => await store.GetAsync(entityId, ct) is { TypeSlug: TypeSlug } entity ? Map(entity) : null;

    public async Task<LeafDiscordConfig?> FindByApplicationIdAsync(
        string applicationId, CancellationToken ct = default)
    {
        await EnsureLegacyMigratedAsync(ct);
        var entities = await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?> { ["application_id"] = applicationId },
            Limit = 2,
        }, ct);
        if (entities.Count > 1)
            throw new InvalidOperationException(
                $"More than one Discord bot configuration uses application '{applicationId}'.");
        return entities.Count == 1 ? Map(entities[0]) : null;
    }

    public async Task<LeafDiscordConfig> CreateAsync(
        LeafDiscordConfigUpdate update, CancellationToken ct = default,
        string? ownerUserId = null)
    {
        var normalized = LeafDiscordConfigStore.NormalizeUpdate(EmptyDefaults(), update) with
        {
            OwnerUserId = NormalizeOwnerUserId(ownerUserId),
        };
        var gate = applicationGates.GetOrAdd(normalized.ApplicationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await FindByApplicationIdAsync(normalized.ApplicationId, ct) is not null)
                throw new ArgumentException(
                    $"Discord application '{normalized.ApplicationId}' is already configured.");
            var entity = await store.UpsertBySlugAsync(
                TypeSlug,
                Slug(normalized.ApplicationId),
                $"Discord bot for {normalized.AgentId}",
                Data(normalized),
                ct);
            return Map(entity);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LeafDiscordConfig> SaveAsync(
        Guid entityId, LeafDiscordConfigUpdate update, CancellationToken ct = default)
    {
        var entity = await store.GetAsync(entityId, ct);
        if (entity is not { TypeSlug: TypeSlug })
            throw new KeyNotFoundException("Discord bot configuration was not found.");
        var existing = Map(entity);
        var normalized = LeafDiscordConfigStore.NormalizeUpdate(existing, update);
        if (!string.Equals(
                normalized.ApplicationId, existing.ApplicationId, StringComparison.Ordinal))
            throw new ArgumentException(
                "Discord application ID is immutable. Add a separate bot configuration instead.");
        return Map(await store.PatchAsync(
            entityId,
            Data(normalized),
            name: $"Discord bot for {normalized.AgentId}",
            ct: ct));
    }

    public async Task MarkAvatarSynchronizedAsync(
        Guid entityId, string fingerprint, CancellationToken ct = default)
        => _ = await store.PatchAsync(entityId, new JsonObject
        {
            ["avatar_fingerprint"] = fingerprint,
        }, ct: ct);

    public async Task<LeafDiscordConfig> SetAvatarAsync(
        Guid entityId, string? avatarAssetId, CancellationToken ct = default)
    {
        var entity = await store.GetAsync(entityId, ct);
        if (entity is not { TypeSlug: TypeSlug })
            throw new KeyNotFoundException("Discord bot configuration was not found.");
        return Map(await store.PatchAsync(entityId, new JsonObject
        {
            ["avatar_asset_id"] = avatarAssetId,
            ["avatar_managed"] = true,
            ["avatar_fingerprint"] = null,
        }, ct: ct));
    }

    public async Task<LeafDiscordConfig> SetGuildAvatarAsync(
        Guid entityId, string guildId, string? avatarAssetId,
        CancellationToken ct = default)
    {
        var gate = avatarGates.GetOrAdd(entityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var entity = await store.GetAsync(entityId, ct);
            if (entity is not { TypeSlug: TypeSlug })
                throw new KeyNotFoundException("Discord bot configuration was not found.");
            var current = Map(entity);
            var desired = new Dictionary<string, string>(
                current.GuildAvatarAssetIds, StringComparer.Ordinal);
            var fingerprints = new Dictionary<string, string>(
                current.GuildAvatarFingerprints, StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(avatarAssetId))
                desired.Remove(guildId);
            else
                desired[guildId] = avatarAssetId;
            fingerprints.Remove(guildId);
            return Map(await store.PatchAsync(entityId, new JsonObject
            {
                ["guild_avatar_asset_ids"] = JsonRead.StringObject(desired),
                ["guild_avatar_fingerprints"] = JsonRead.StringObject(fingerprints),
            }, ct: ct));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkGuildAvatarSynchronizedAsync(
        Guid entityId, string guildId, string fingerprint,
        CancellationToken ct = default)
    {
        var gate = avatarGates.GetOrAdd(entityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var entity = await store.GetAsync(entityId, ct);
            if (entity is not { TypeSlug: TypeSlug })
                throw new KeyNotFoundException("Discord bot configuration was not found.");
            var current = Map(entity);
            var fingerprints = new Dictionary<string, string>(
                current.GuildAvatarFingerprints, StringComparer.Ordinal)
            {
                [guildId] = fingerprint,
            };
            _ = await store.PatchAsync(entityId, new JsonObject
            {
                ["guild_avatar_fingerprints"] = JsonRead.StringObject(fingerprints),
            }, ct: ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LeafDiscordConfig> EnsureOwnerAsync(
        Guid entityId, string ownerUserId, CancellationToken ct = default)
    {
        var entity = await store.GetAsync(entityId, ct);
        if (entity is not { TypeSlug: TypeSlug })
            throw new KeyNotFoundException("Discord bot configuration was not found.");
        var current = Map(entity);
        if (!string.IsNullOrWhiteSpace(current.OwnerUserId)) return current;
        var owner = NormalizeOwnerUserId(ownerUserId)
            ?? throw new ArgumentException("An authenticated owner user ID is required.");
        return Map(await store.PatchAsync(entityId, new JsonObject
        {
            ["owner_user_id"] = owner,
        }, ct: ct));
    }

    public async Task EnsureLegacyMigratedAsync(CancellationToken ct = default)
    {
        await migrationGate.WaitAsync(ct);
        try
        {
            var legacyConfig = await legacy.GetExistingAsync(ct);
            if (legacyConfig is null) return;
            var migrated = await store.QueryAsync(new EntityQuery
            {
                TypeSlug = TypeSlug,
                DataEquals = new Dictionary<string, object?>
                {
                    ["application_id"] = legacyConfig.ApplicationId,
                },
                Limit = 1,
            }, ct);
            if (migrated.Count > 0) return;
            await store.UpsertBySlugAsync(
                TypeSlug,
                Slug(legacyConfig.ApplicationId),
                $"Discord bot for {legacyConfig.AgentId}",
                Data(legacyConfig),
                ct);
        }
        finally
        {
            migrationGate.Release();
        }
    }

    internal static LeafDiscordConfig EmptyDefaults() => new(
        "",
        "",
        null,
        false,
        "",
        "",
        "deep-codex",
        "deep-codex",
        "leaf-discord-external",
        "",
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        10 * 1024 * 1024);

    private static JsonObject Data(LeafDiscordConfig config) => new()
    {
        ["application_id"] = config.ApplicationId,
        ["interaction_public_key"] = config.InteractionPublicKey,
        ["bot_token"] = config.BotToken,
        ["enabled"] = config.Enabled,
        ["default_agent"] = config.AgentId,
        ["sentinel_agent"] = config.SentinelAgentId,
        ["session_quality_mode"] = config.SessionQualityMode,
        ["sentinel_quality_mode"] = config.SentinelQualityMode,
        ["audience_profile"] = string.IsNullOrWhiteSpace(config.AudienceProfile)
            ? "leaf-discord-external" : config.AudienceProfile,
        ["avatar_asset_id"] = config.AvatarAssetId,
        ["avatar_managed"] = config.AvatarManaged,
        ["avatar_fingerprint"] = config.AvatarFingerprint,
        ["guild_avatar_asset_ids"] = JsonRead.StringObject(config.GuildAvatarAssetIds),
        ["guild_avatar_fingerprints"] = JsonRead.StringObject(
            config.GuildAvatarFingerprints),
        ["dm_allowlist"] = new JsonArray(config.DmAllowlist
            .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["clear_allowlist"] = new JsonArray(config.ClearAllowlist
            .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["max_attachment_bytes"] = config.MaxAttachmentBytes,
        ["presence_context_mode"] = config.PresenceContextMode,
        ["presence_extension_feed_enabled"] = config.PresenceExtensionFeedEnabled,
        ["owner_user_id"] = config.OwnerUserId,
    };

    private static LeafDiscordConfig Map(LeafEntity entity)
    {
        var data = entity.Data;
        return new LeafDiscordConfig(
            JsonRead.String(data, "application_id") ?? "",
            JsonRead.String(data, "interaction_public_key") ?? "",
            JsonRead.String(data, "bot_token"),
            JsonRead.Bool(data, "enabled"),
            JsonRead.String(data, "default_agent") ?? "",
            JsonRead.String(data, "sentinel_agent")
                ?? JsonRead.String(data, "default_agent") ?? "",
            JsonRead.String(data, "session_quality_mode") ?? "deep-codex",
            JsonRead.String(data, "sentinel_quality_mode") ?? "deep-codex",
            JsonRead.String(data, "audience_profile") ?? "leaf-discord-external",
            JsonRead.String(data, "avatar_asset_id") ?? "",
            JsonRead.StringSet(data, "dm_allowlist"),
            JsonRead.StringSet(data, "clear_allowlist"),
            Math.Clamp(
                JsonRead.Long(data, "max_attachment_bytes", 10 * 1024 * 1024),
                1,
                25 * 1024 * 1024))
        {
            EntityId = entity.Id,
            EntitySlug = entity.Slug,
            AvatarFingerprint = JsonRead.String(data, "avatar_fingerprint"),
            AvatarManaged = JsonRead.Bool(data, "avatar_managed"),
            GuildAvatarAssetIds = JsonRead.StringDictionary(data, "guild_avatar_asset_ids"),
            GuildAvatarFingerprints = JsonRead.StringDictionary(
                data, "guild_avatar_fingerprints"),
            OwnerUserId = JsonRead.String(data, "owner_user_id"),
            PresenceContextMode = DiscordPresenceContextModes.Normalize(
                JsonRead.String(data, "presence_context_mode")),
            PresenceExtensionFeedEnabled = JsonRead.Bool(
                data, "presence_extension_feed_enabled"),
        };
    }

    private static string? NormalizeOwnerUserId(string? ownerUserId)
        => Guid.TryParse(ownerUserId?.Trim(), out var parsed)
            ? parsed.ToString()
            : null;

    private static string Slug(string applicationId)
        => $"leaf-discord-bot-{Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(applicationId))).ToLowerInvariant()[..24]}";
}
