using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordCommonsConfiguration(
    Guid EntityId,
    Guid BotId,
    string RelayUrl,
    string? HostCredential,
    IReadOnlyDictionary<string, DiscordCommonsGuildScope> GuildScopes,
    IReadOnlyDictionary<string, string> WebhookUrls,
    DiscordCommonsRoleReconciliation RoleReconciliation)
{
    public IReadOnlySet<string> GuildAllowlist => GuildScopes.Keys.ToHashSet(StringComparer.Ordinal);
    public bool Ready => GuildScopes.Values.Any(scope => scope.Ready);

    public DiscordCommonsGuildScope? Scope(string? guildId)
        => !string.IsNullOrWhiteSpace(guildId)
           && GuildScopes.TryGetValue(guildId, out var scope) ? scope : null;
}

public sealed record DiscordCommonsGuildScope(
    string GuildId,
    bool WholeGuild,
    IReadOnlySet<string> CategoryIds,
    IReadOnlySet<string> ChannelIds,
    IReadOnlySet<string> ExcludedChannelIds,
    string NativeAgentMode,
    string RelayUrl,
    string? HostCredential)
{
    public bool Ready => Uri.TryCreate(RelayUrl, UriKind.Absolute, out var relay)
                         && relay.Scheme == Uri.UriSchemeHttps
                         && !string.IsNullOrWhiteSpace(HostCredential);

    public bool Contains(string? channelId, string? categoryId)
        => (string.IsNullOrWhiteSpace(channelId) || !ExcludedChannelIds.Contains(channelId))
           && (WholeGuild
               || (!string.IsNullOrWhiteSpace(categoryId) && CategoryIds.Contains(categoryId))
               || (!string.IsNullOrWhiteSpace(channelId) && ChannelIds.Contains(channelId)));

    public bool SuppressesNativeAgent => string.Equals(
        NativeAgentMode, DiscordCommonsNativeAgentModes.SilentHost,
        StringComparison.Ordinal);
}

public static class DiscordCommonsNativeAgentModes
{
    public const string SilentHost = "silent-host";
    public const string Participant = "participant";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Participant => Participant,
            null or "" or SilentHost => SilentHost,
            _ => throw new ArgumentException(
                "Leaf Community nativeAgentMode must be silent-host or participant."),
        };
}

public sealed record DiscordCommonsGuildScopeUpdate(
    string GuildId,
    bool WholeGuild,
    IReadOnlyList<string>? CategoryIds,
    IReadOnlyList<string>? ChannelIds,
    string RelayUrl,
    string? HostCredential,
    bool ClearHostCredential = false,
    IReadOnlyList<string>? ExcludedChannelIds = null,
    string? NativeAgentMode = null);

public sealed record DiscordCommonsConfigurationUpdate(
    string RelayUrl,
    string? HostCredential,
    bool ClearHostCredential,
    IReadOnlyList<string>? GuildAllowlist,
    IReadOnlyList<DiscordCommonsGuildScopeUpdate>? GuildScopes = null);

public sealed record DiscordCommonsGuildScopeView(
    string GuildId,
    bool WholeGuild,
    IReadOnlyList<string> CategoryIds,
    IReadOnlyList<string> ChannelIds,
    IReadOnlyList<string> ExcludedChannelIds,
    string NativeAgentMode,
    string RelayUrl,
    bool HostCredentialConfigured);

public sealed record DiscordCommonsConfigurationView(
    string RelayUrl,
    bool HostCredentialConfigured,
    IReadOnlyList<string> GuildAllowlist,
    IReadOnlyList<DiscordCommonsGuildScopeView> GuildScopes,
    IReadOnlyList<string> WebhookChannelIds,
    DiscordCommonsRoleReconciliation RoleReconciliation);

public sealed record DiscordCommonsRoleReconciliation(
    string State,
    string? Error,
    DateTimeOffset? At,
    int EnvoyCount);

public sealed record DiscordCommonsRelayEnvoy(
    string Id,
    string OwnerDiscordUserId,
    string OwnerDisplayName,
    string AgentEntityId,
    string AgentName,
    string PublicName,
    string PersonaName,
    string Status,
    string? DiscordRoleId);

public sealed record DiscordCommonsGuildMember(
    string UserId,
    string DisplayName,
    bool IsBot);

public sealed record DiscordCommonsHostMessage(
    string MessageId,
    string EnvoyId,
    string PersonaName,
    string OwnerPublicName,
    string Content,
    string ThreadRef,
    string Scenario,
    string Channel,
    string? ChannelId);

public sealed record DiscordCommonsHostPostAuthorization(
    bool Allowed,
    string? Reason,
    string? Scenario);

public static class DiscordCommonsRolePolicy
{
    public const int MaximumRolesPerGuild = 50;

    public static string RoleName(DiscordCommonsRelayEnvoy envoy)
    {
        var agentName = string.IsNullOrWhiteSpace(envoy.AgentName)
            ? envoy.PersonaName.Split('·', 2)[0].Trim()
            : envoy.AgentName.Trim();
        var publicName = string.IsNullOrWhiteSpace(envoy.PublicName)
            ? envoy.PersonaName.Split('·', 2).Last().Trim()
            : envoy.PublicName.Trim();
        return $"{agentName} {publicName}";
    }

    public static void EnsureCanReconcile(
        bool manageRoles, IReadOnlyCollection<DiscordCommonsRelayEnvoy> envoys)
    {
        if (!manageRoles)
            throw new UnauthorizedAccessException(
                "The Discord bot needs Manage Roles to reconcile Leaf Community Agent roles.");
        if (envoys.Count(envoy => envoy.Status == "active") > MaximumRolesPerGuild)
            throw new InvalidOperationException(
                $"Leaf Community Agent role cap of {MaximumRolesPerGuild} was exceeded.");
    }

    public static bool NeedsUpdate(DiscordManagedRole role, string desiredName)
        => !string.Equals(role.Name, desiredName, StringComparison.Ordinal)
           || role.Permissions != "0"
           || !role.Mentionable;
}

public sealed class DiscordCommonsConfigurationStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-commons-config";

    public async Task<DiscordCommonsConfiguration?> GetAsync(
        Guid botId, CancellationToken ct = default)
    {
        var entity = await store.GetBySlugAsync(TypeSlug, Slug(botId), ct);
        entity ??= (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["bot_id"] = botId.ToString(),
            },
            Limit = 2,
        }, ct)).SingleOrDefault();
        entity ??= await store.UpsertBySlugAsync(
            TypeSlug,
            Slug(botId),
            "Leaf Community relay",
            new JsonObject
            {
                ["bot_id"] = botId.ToString(),
                ["relay_url"] = "",
                ["guild_allowlist"] = new JsonArray(),
                ["guild_scopes"] = "[]",
                ["relay_targets"] = "{}",
                ["webhook_urls"] = "{}",
                ["role_reconciliation_state"] = "not-run",
            },
            ct);
        return Map(entity);
    }

    public async Task<DiscordCommonsConfiguration> SaveAsync(
        Guid botId,
        DiscordCommonsConfigurationUpdate update,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(botId, ct);
        var relayUrl = string.IsNullOrWhiteSpace(update.RelayUrl)
            ? existing?.RelayUrl ?? ""
            : NormalizeRelayUrl(update.RelayUrl);
        var credential = update.ClearHostCredential
            ? null
            : string.IsNullOrWhiteSpace(update.HostCredential)
                ? existing?.HostCredential
                : update.HostCredential.Trim();
        var scopes = update.GuildScopes is null
            ? LegacyScopes(update.GuildAllowlist, relayUrl, credential)
            : NormalizeScopes(update.GuildScopes, existing);
        if (scopes.Count == 0)
            throw new ArgumentException("At least one Leaf Community guild scope is required.");
        var guilds = scopes.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        var data = new JsonObject
        {
            ["bot_id"] = botId.ToString(),
            ["relay_url"] = relayUrl,
            ["host_credential"] = credential,
            ["guild_allowlist"] = new JsonArray(guilds
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["guild_scopes"] = SerializeGuildScopes(scopes.Values),
            ["relay_targets"] = SerializeRelayTargets(scopes.Values),
            ["webhook_urls"] = SerializeWebhookUrls(
                existing?.WebhookUrls ?? new Dictionary<string, string>()),
            ["role_reconciliation_state"] = existing?.RoleReconciliation.State ?? "not-run",
            ["role_reconciliation_error"] = existing?.RoleReconciliation.Error,
            ["role_reconciliation_at"] = existing?.RoleReconciliation.At?.ToString("O"),
            ["role_reconciliation_envoy_count"] = existing?.RoleReconciliation.EnvoyCount ?? 0,
        };
        var entity = existing is null
            ? await store.UpsertBySlugAsync(
                TypeSlug, Slug(botId), "Leaf Community relay", data, ct)
            : await store.PatchAsync(
                existing.EntityId, data, name: "Leaf Community relay", ct: ct);
        return Map(entity);
    }

    public async Task<DiscordCommonsConfiguration> SetWebhookUrlAsync(
        Guid botId,
        string channelId,
        string webhookUrl,
        CancellationToken ct = default)
    {
        channelId = RequiredSnowflake(channelId);
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.Equals(parsed.Host, "discord.com", StringComparison.OrdinalIgnoreCase)
            || !parsed.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
            throw new ArgumentException("The managed webhook URL is invalid.");
        var existing = await GetAsync(botId, ct)
            ?? throw new InvalidOperationException("Discord Commons is not configured.");
        var urls = new Dictionary<string, string>(existing.WebhookUrls, StringComparer.Ordinal)
        {
            [channelId] = webhookUrl,
        };
        var entity = await store.PatchAsync(existing.EntityId, new JsonObject
        {
            ["webhook_urls"] = SerializeWebhookUrls(urls),
        }, ct: ct);
        return Map(entity);
    }

    public static DiscordCommonsConfigurationView View(DiscordCommonsConfiguration config)
        => new(
            config.RelayUrl,
            !string.IsNullOrWhiteSpace(config.HostCredential),
            config.GuildAllowlist.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            config.GuildScopes.Values.OrderBy(scope => scope.GuildId, StringComparer.Ordinal)
                .Select(scope => new DiscordCommonsGuildScopeView(
                    scope.GuildId,
                    scope.WholeGuild,
                    scope.CategoryIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    scope.ChannelIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    scope.ExcludedChannelIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    scope.NativeAgentMode,
                    scope.RelayUrl,
                    !string.IsNullOrWhiteSpace(scope.HostCredential)))
                .ToArray(),
            config.WebhookUrls.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            config.RoleReconciliation);

    public async Task SetRoleReconciliationAsync(
        Guid botId,
        DiscordCommonsRoleReconciliation status,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(botId, ct)
            ?? throw new InvalidOperationException("Discord Commons is not configured.");
        _ = await store.PatchAsync(existing.EntityId, new JsonObject
        {
            ["role_reconciliation_state"] = status.State,
            ["role_reconciliation_error"] = status.Error,
            ["role_reconciliation_at"] = status.At?.ToString("O"),
            ["role_reconciliation_envoy_count"] = status.EnvoyCount,
        }, ct: ct);
    }

    internal static string NormalizeRelayUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not ("" or "/"))
            throw new ArgumentException(
                "The Commons relay URL must be an HTTPS origin without credentials, path, query, or fragment.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static DiscordCommonsConfiguration Map(LeafEntity entity)
    {
        var data = entity.Data;
        var botId = Guid.TryParse(JsonRead.String(data, "bot_id"), out var parsed)
            ? parsed
            : Guid.Empty;
        _ = DateTimeOffset.TryParse(
            JsonRead.String(data, "role_reconciliation_at"), out var roleAt);
        var relayUrl = JsonRead.String(data, "relay_url") ?? "";
        var credential = JsonRead.String(data, "host_credential");
        var scopes = ParseGuildScopes(
            JsonRead.String(data, "guild_scopes"),
            JsonRead.String(data, "relay_targets"));
        if (scopes.Count == 0)
        {
            scopes = JsonRead.StringSet(data, "guild_allowlist").ToDictionary(
                guildId => guildId,
                guildId => new DiscordCommonsGuildScope(
                    guildId, true, new HashSet<string>(), new HashSet<string>(),
                    new HashSet<string>(), DiscordCommonsNativeAgentModes.SilentHost,
                    relayUrl, credential),
                StringComparer.Ordinal);
        }
        return new DiscordCommonsConfiguration(
            entity.Id,
            botId,
            relayUrl,
            credential,
            scopes,
            ParseWebhookUrls(JsonRead.String(data, "webhook_urls")),
            new DiscordCommonsRoleReconciliation(
                JsonRead.String(data, "role_reconciliation_state") ?? "not-run",
                JsonRead.String(data, "role_reconciliation_error"),
                roleAt == default ? null : roleAt,
                (int)JsonRead.Long(data, "role_reconciliation_envoy_count", 0)));
    }

    private static IReadOnlyDictionary<string, DiscordCommonsGuildScope> LegacyScopes(
        IReadOnlyList<string>? guildAllowlist,
        string relayUrl,
        string? credential)
        => (guildAllowlist ?? []).Select(RequiredSnowflake)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                guildId => guildId,
                guildId => new DiscordCommonsGuildScope(
                    guildId, true, new HashSet<string>(), new HashSet<string>(),
                    new HashSet<string>(), DiscordCommonsNativeAgentModes.SilentHost,
                    relayUrl, credential),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, DiscordCommonsGuildScope> NormalizeScopes(
        IReadOnlyList<DiscordCommonsGuildScopeUpdate> updates,
        DiscordCommonsConfiguration? existing)
    {
        var result = new Dictionary<string, DiscordCommonsGuildScope>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            var guildId = RequiredSnowflake(update.GuildId);
            if (!result.TryAdd(guildId, null!))
                throw new ArgumentException("Each Leaf Community guild may appear only once.");
            var categories = (update.CategoryIds ?? []).Select(RequiredSnowflake)
                .ToHashSet(StringComparer.Ordinal);
            var channels = (update.ChannelIds ?? []).Select(RequiredSnowflake)
                .ToHashSet(StringComparer.Ordinal);
            var excludedChannels = (update.ExcludedChannelIds ?? []).Select(RequiredSnowflake)
                .ToHashSet(StringComparer.Ordinal);
            if (!update.WholeGuild && categories.Count == 0 && channels.Count == 0)
                throw new ArgumentException(
                    "A scoped Leaf Community guild needs a category or channel ID.");
            var previous = existing?.Scope(guildId);
            var targetUrl = string.IsNullOrWhiteSpace(update.RelayUrl)
                ? previous?.RelayUrl ?? ""
                : NormalizeRelayUrl(update.RelayUrl);
            if (string.IsNullOrWhiteSpace(targetUrl))
                throw new ArgumentException("Each Leaf Community guild needs a relay URL.");
            var targetCredential = update.ClearHostCredential
                ? null
                : string.IsNullOrWhiteSpace(update.HostCredential)
                    ? previous?.HostCredential
                    : update.HostCredential.Trim();
            result[guildId] = new DiscordCommonsGuildScope(
                guildId, update.WholeGuild, categories, channels, excludedChannels,
                DiscordCommonsNativeAgentModes.Normalize(update.NativeAgentMode),
                targetUrl, targetCredential);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, DiscordCommonsGuildScope> ParseGuildScopes(
        string? scopeJson,
        string? targetJson)
    {
        try
        {
            var scopes = JsonNode.Parse(scopeJson ?? "[]") as JsonArray;
            var targets = JsonNode.Parse(targetJson ?? "{}") as JsonObject;
            if (scopes is null || targets is null)
                return new Dictionary<string, DiscordCommonsGuildScope>(StringComparer.Ordinal);
            var result = new Dictionary<string, DiscordCommonsGuildScope>(StringComparer.Ordinal);
            foreach (var item in scopes.OfType<JsonObject>())
            {
                var guildId = item["guildId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(guildId)) continue;
                var target = targets[guildId] as JsonObject;
                result[guildId] = new DiscordCommonsGuildScope(
                    guildId,
                    item["wholeGuild"]?.GetValue<bool>() ?? false,
                    StringSet(item["categoryIds"]),
                    StringSet(item["channelIds"]),
                    StringSet(item["excludedChannelIds"]),
                    DiscordCommonsNativeAgentModes.Normalize(
                        item["nativeAgentMode"]?.GetValue<string>()),
                    target?["relayUrl"]?.GetValue<string>() ?? "",
                    target?["hostCredential"]?.GetValue<string>());
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, DiscordCommonsGuildScope>(StringComparer.Ordinal);
        }
    }

    private static string SerializeGuildScopes(IEnumerable<DiscordCommonsGuildScope> scopes)
        => new JsonArray(scopes.OrderBy(scope => scope.GuildId, StringComparer.Ordinal)
            .Select(scope => (JsonNode?)new JsonObject
            {
                ["guildId"] = scope.GuildId,
                ["wholeGuild"] = scope.WholeGuild,
                ["categoryIds"] = new JsonArray(scope.CategoryIds.OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["channelIds"] = new JsonArray(scope.ChannelIds.OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["excludedChannelIds"] = new JsonArray(scope.ExcludedChannelIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["nativeAgentMode"] = scope.NativeAgentMode,
            }).ToArray()).ToJsonString();

    private static string SerializeRelayTargets(IEnumerable<DiscordCommonsGuildScope> scopes)
        => new JsonObject(scopes.Select(scope => KeyValuePair.Create<string, JsonNode?>(
            scope.GuildId,
            new JsonObject
            {
                ["relayUrl"] = scope.RelayUrl,
                ["hostCredential"] = scope.HostCredential,
            }))).ToJsonString();

    private static IReadOnlySet<string> StringSet(JsonNode? node)
        => node is JsonArray values
            ? values.OfType<JsonValue>().Select(value => value.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> ParseWebhookUrls(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonNode.Parse(value) is JsonObject parsed
                ? parsed.Where(pair => pair.Value is JsonValue)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value!.GetValue<string>(),
                        StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string SerializeWebhookUrls(IReadOnlyDictionary<string, string> values)
        => new JsonObject(values.Select(pair =>
            KeyValuePair.Create<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))))
            .ToJsonString();

    private static string RequiredSnowflake(string value)
        => ulong.TryParse(value?.Trim(), out var parsed) && parsed > 0
            ? parsed.ToString()
            : throw new ArgumentException("Commons guild and channel IDs must be Discord snowflakes.");

    private static string Slug(Guid botId) => $"leaf-discord-commons-{botId:N}";
}

public sealed record DiscordCommonsIngestDelivery(
    Guid EntityId,
    Guid BotId,
    string? GuildId,
    string EventId,
    JsonObject? Payload,
    string State,
    int Attempts,
    DateTimeOffset? NextAttemptAt);

public sealed record DiscordCommonsHostTurn(
    Guid EntityId,
    string BindingId,
    long Generation,
    string GuildId,
    string ChannelId,
    string Channel,
    string ThreadRef,
    string SourceMessageId,
    string SourceEnvoyId,
    string SourcePersonaName,
    string ParticipantId,
    string State);

public sealed class DiscordCommonsHostTurnStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-commons-host-turn";

    public async Task<DiscordCommonsHostTurn> SetPendingAsync(
        DiscordBinding binding, string channelId, string channel,
        DiscordCommonsHostMessage message, string participantId,
        CancellationToken ct = default)
    {
        var slug = $"commons-host-turn-{DiscordBindingStore.Hash(
            $"{binding.BindingId}:{message.MessageId}")}";
        var data = new JsonObject
        {
            ["binding_id"] = binding.BindingId,
            ["generation"] = binding.Generation,
            ["guild_id"] = binding.GuildId,
            ["channel_id"] = channelId,
            ["channel"] = channel,
            ["thread_ref"] = message.ThreadRef,
            ["source_message_id"] = message.MessageId,
            ["source_envoy_id"] = message.EnvoyId,
            ["source_persona_name"] = message.PersonaName,
            ["participant_id"] = participantId,
            ["state"] = "pending",
        };
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        var entity = existing is null
            ? await store.UpsertBySlugAsync(TypeSlug, slug, "Leaf Community host turn", data, ct)
            : existing;
        return Map(entity);
    }

    public async Task<DiscordCommonsHostTurn?> GetPendingAsync(
        DiscordBinding binding, CancellationToken ct = default)
    {
        var active = new List<LeafEntity>();
        foreach (var state in new[] { "authorized", "pending" })
            active.AddRange(await store.QueryAsync(new EntityQuery
            {
                TypeSlug = TypeSlug,
                DataEquals = new Dictionary<string, object?>
                {
                    ["binding_id"] = binding.BindingId,
                    ["state"] = state,
                },
                Limit = 50,
            }, ct));
        return active.OrderBy(entity => entity.CreatedAt)
            .Select(Map)
            .FirstOrDefault(turn => turn.Generation == binding.Generation
                && (turn.State is "pending" or "authorized"));
    }

    public async Task CompleteAsync(
        DiscordCommonsHostTurn turn, string state, CancellationToken ct = default)
        => _ = await store.PatchAsync(turn.EntityId,
            new JsonObject { ["state"] = state }, ct: ct);

    public async Task<bool> CompletePendingAsync(
        DiscordBinding binding, string state, CancellationToken ct = default)
    {
        var pending = await GetPendingAsync(binding, ct);
        if (pending is null) return false;
        await CompleteAsync(pending, state, ct);
        return true;
    }

    private static DiscordCommonsHostTurn Map(LeafEntity entity)
        => new(
            entity.Id,
            JsonRead.String(entity.Data, "binding_id") ?? "",
            JsonRead.Long(entity.Data, "generation", 0),
            JsonRead.String(entity.Data, "guild_id") ?? "",
            JsonRead.String(entity.Data, "channel_id") ?? "",
            JsonRead.String(entity.Data, "channel") ?? "",
            JsonRead.String(entity.Data, "thread_ref") ?? "",
            JsonRead.String(entity.Data, "source_message_id") ?? "",
            JsonRead.String(entity.Data, "source_envoy_id") ?? "",
            JsonRead.String(entity.Data, "source_persona_name") ?? "",
            JsonRead.String(entity.Data, "participant_id") ?? "",
            JsonRead.String(entity.Data, "state") ?? "pending");
}

public sealed class DiscordCommonsIngestStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-commons-ingest";

    public async Task<DiscordCommonsIngestDelivery> EnqueueAsync(
        Guid botId, string guildId, string eventId, JsonObject payload,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 200)
            throw new ArgumentException("A bounded Commons event ID is required.");
        var slug = $"discord-commons-{Hash($"{botId:N}:{eventId}")}";
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        if (existing is not null) return Map(existing);
        return Map(await store.UpsertBySlugAsync(TypeSlug, slug,
            $"Leaf Community ingest {eventId}", new JsonObject
            {
                ["bot_id"] = botId.ToString(),
                ["guild_id"] = guildId,
                ["event_id"] = eventId,
                ["payload"] = payload.DeepClone(),
                ["state"] = "queued",
                ["attempts"] = 0,
                ["next_attempt_at"] = DateTimeOffset.UtcNow.ToString("O"),
            }, ct));
    }

    public async Task<IReadOnlyList<DiscordCommonsIngestDelivery>> PendingAsync(
        CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            Limit = 500,
        }, ct)).Select(Map)
            .Where(item => item.State is "queued" or "retry")
            .Where(item => item.Payload is not null)
            .Where(item => item.NextAttemptAt is null || item.NextAttemptAt <= DateTimeOffset.UtcNow)
            .OrderBy(item => item.NextAttemptAt)
            .ToArray();

    public async Task MarkSentAsync(
        DiscordCommonsIngestDelivery delivery, CancellationToken ct = default)
        => _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "sent",
            ["payload"] = null,
            ["last_error"] = null,
            ["next_attempt_at"] = null,
        }, ct: ct);

    public async Task MarkRetryAsync(
        DiscordCommonsIngestDelivery delivery,
        string safeError,
        TimeSpan delay,
        CancellationToken ct = default)
        => _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "retry",
            ["attempts"] = delivery.Attempts + 1,
            ["last_error"] = safeError.Length <= 200 ? safeError : safeError[..200],
            ["next_attempt_at"] = DateTimeOffset.UtcNow.Add(delay).ToString("O"),
        }, ct: ct);

    private static DiscordCommonsIngestDelivery Map(LeafEntity entity)
    {
        var data = entity.Data;
        _ = Guid.TryParse(JsonRead.String(data, "bot_id"), out var botId);
        _ = DateTimeOffset.TryParse(JsonRead.String(data, "next_attempt_at"), out var next);
        return new DiscordCommonsIngestDelivery(
            entity.Id,
            botId,
            JsonRead.String(data, "guild_id"),
            JsonRead.String(data, "event_id") ?? "",
            data["payload"]?.DeepClone() as JsonObject,
            JsonRead.String(data, "state") ?? "queued",
            (int)JsonRead.Long(data, "attempts", 0),
            next == default ? null : next);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..32];
}

public sealed class DiscordCommonsRelayService(
    DiscordCommonsConfigurationStore configurations,
    DiscordCommonsIngestStore deliveries,
    ILogger<DiscordCommonsRelayService> logger,
    HttpMessageHandler? handler = null) : IAsyncDisposable
{
    private readonly HttpClient http = new(handler ?? new HttpClientHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private readonly SemaphoreSlim signal = new(0, 1);
    private CancellationTokenSource? runCts;
    private Task? loop;

    public async Task<bool> IsCommonsLocationAsync(
        Guid botId,
        string? guildId,
        string? channelId,
        string? categoryId,
        CancellationToken ct = default)
        => await configurations.GetAsync(botId, ct) is { } config
           && config.Scope(guildId) is { } scope
           && scope.Contains(channelId, categoryId);

    public async Task<bool> HasCommonsGuildAsync(
        Guid botId, string? guildId, CancellationToken ct = default)
        => (await configurations.GetAsync(botId, ct))?.Scope(guildId) is not null;

    public async Task<DiscordCommonsGuildScope?> GetScopeAsync(
        Guid botId, string? guildId, CancellationToken ct = default)
        => (await configurations.GetAsync(botId, ct))?.Scope(guildId);

    public Task<DiscordCommonsConfiguration?> GetConfigurationAsync(
        Guid botId, CancellationToken ct = default)
        => configurations.GetAsync(botId, ct);

    public async Task EnqueueAsync(
        Guid botId, string guildId, string eventId, JsonObject payload,
        CancellationToken ct = default)
    {
        _ = await deliveries.EnqueueAsync(botId, guildId, eventId, payload, ct);
        try { signal.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task<IReadOnlyList<DiscordCommonsRelayEnvoy>> GetEnvoysAsync(
        Guid botId, string guildId, CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Get,
            "/v1/admin/envoys", null, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("envoys").EnumerateArray()
            .Select(item => new DiscordCommonsRelayEnvoy(
                item.GetProperty("id").GetString() ?? "",
                item.GetProperty("ownerDiscordUserId").GetString() ?? "",
                item.GetProperty("ownerDisplayName").GetString() ?? "",
                item.TryGetProperty("agentEntityId", out var agentEntityId)
                    ? agentEntityId.GetString() ?? "" : "",
                item.TryGetProperty("agentName", out var agentName)
                    ? agentName.GetString() ?? "" : "",
                item.TryGetProperty("publicName", out var publicName)
                    ? publicName.GetString() ?? "" : "",
                item.GetProperty("personaName").GetString() ?? "",
                item.GetProperty("status").GetString() ?? "",
                item.TryGetProperty("discordRoleId", out var role)
                    && role.ValueKind == JsonValueKind.String ? role.GetString() : null))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();
    }

    public async Task PushReservedNamesAsync(
        Guid botId,
        string guildId,
        IReadOnlyList<DiscordCommonsGuildMember> members,
        CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Put,
            "/v1/admin/reserved-names", new JsonObject
            {
                ["members"] = new JsonArray(members.Where(member => !member.IsBot)
                    .Select(member => (JsonNode?)new JsonObject
                    {
                        ["userId"] = member.UserId,
                        ["displayName"] = member.DisplayName,
                    }).ToArray()),
            }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetDiscordRoleAsync(
        Guid botId, string guildId, string envoyId, string? roleId,
        CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Put,
            $"/v1/admin/envoys/{Uri.EscapeDataString(envoyId)}/discord-role",
            new JsonObject { ["discordRoleId"] = roleId }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetEnvoyStatusAsync(
        Guid botId, string guildId, string envoyId, string action,
        CancellationToken ct = default)
    {
        if (action is not (DiscordCommonsEnvoyActions.Approve or DiscordCommonsEnvoyActions.Suspend
                or DiscordCommonsEnvoyActions.Unsuspend))
            throw new ArgumentException(
                "Envoy status action must be approve, suspend or unsuspend.", nameof(action));
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Post,
            $"/v1/admin/envoys/{Uri.EscapeDataString(envoyId)}/{action}", null, ct);
        if (!response.IsSuccessStatusCode)
            throw new DiscordCommonsRelayException(
                response.StatusCode, response.Headers.RetryAfter?.Delta);
    }

    public async Task SyncOwnerDisplayNameAsync(
        Guid botId, string guildId, string envoyId, string displayName,
        CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Put,
            $"/v1/admin/envoys/{Uri.EscapeDataString(envoyId)}/owner-profile",
            new JsonObject { ["ownerDisplayName"] = displayName }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<DiscordCommonsHostMessage?> GetHostMessageAsync(
        Guid botId, string guildId, string messageId, CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Get,
            $"/v1/admin/host-participant/messages/{Uri.EscapeDataString(messageId)}",
            null, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("addressed", out var addressed)
            || !addressed.GetBoolean()
            || !document.RootElement.TryGetProperty("message", out var message))
            return null;
        return new DiscordCommonsHostMessage(
            messageId,
            message.GetProperty("envoyId").GetString() ?? "",
            message.GetProperty("personaName").GetString() ?? "Leaf Community Agent",
            message.GetProperty("ownerPublicName").GetString() ?? "Leaf member",
            message.GetProperty("content").GetString() ?? "",
            message.GetProperty("threadRef").GetString() ?? "",
            message.GetProperty("scenario").GetString() ?? "consult",
            message.GetProperty("channel").GetString() ?? "",
            message.TryGetProperty("channelId", out var channelId)
                && channelId.ValueKind == JsonValueKind.String ? channelId.GetString() : null);
    }

    public async Task<DiscordCommonsHostPostAuthorization> AuthorizeHostPostAsync(
        Guid botId, string guildId, string participantId, string threadRef,
        IReadOnlyList<string> addresses, string authorizationId,
        CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Post,
            "/v1/admin/host-participant/authorize-post", new JsonObject
            {
                ["participantId"] = participantId,
                ["threadRef"] = threadRef,
                ["addresses"] = new JsonArray(addresses.Select(value =>
                    (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["authorizationId"] = authorizationId,
            }, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new DiscordCommonsHostPostAuthorization(
            response.IsSuccessStatusCode
            && document.RootElement.TryGetProperty("allowed", out var allowed)
            && allowed.GetBoolean(),
            document.RootElement.TryGetProperty("reason", out var reason)
                ? reason.GetString() : null,
            document.RootElement.TryGetProperty("scenario", out var scenario)
                ? scenario.GetString() : null);
    }

    public async Task SetHostParticipantAsync(
        Guid botId, string guildId, string participantId, string displayName,
        string discordUserId, IReadOnlyList<string> roleIds, CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Put,
            "/v1/admin/host-participant", new JsonObject
            {
                ["id"] = participantId,
                ["displayName"] = displayName,
                ["discordUserId"] = discordUserId,
                ["roleIds"] = new JsonArray(roleIds.Select(value =>
                    (JsonNode?)JsonValue.Create(value)).ToArray()),
            }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task SyncChannelsAsync(
        Guid botId, string guildId, JsonArray channels, CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Put,
            "/v1/admin/channels", new JsonObject { ["channels"] = channels }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<string>> PendingWebhookChannelsAsync(
        Guid botId, string guildId, CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Get,
            "/v1/admin/webhooks/pending", null, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("channelIds").EnumerateArray()
            .Select(item => item.GetString() ?? "")
            .Where(item => item.Length > 0).ToArray();
    }

    public async Task RegisterChannelWebhookAsync(
        Guid botId, string guildId, string channelId, string webhookUrl,
        CancellationToken ct = default)
    {
        using var response = await SendHostAsync(botId, guildId, HttpMethod.Put,
            $"/v1/admin/channels/{Uri.EscapeDataString(channelId)}/webhook",
            new JsonObject { ["webhookUrl"] = webhookUrl }, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendHostAsync(
        Guid botId,
        string guildId,
        HttpMethod method,
        string path,
        JsonObject? body,
        CancellationToken ct)
    {
        var config = await configurations.GetAsync(botId, ct);
        var scope = config?.Scope(guildId);
        if (scope is not { Ready: true })
            throw new InvalidOperationException("Leaf Community is not configured for that guild.");
        var request = new HttpRequestMessage(method, new Uri(scope.RelayUrl + path));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", scope.HostCredential);
        if (body is not null)
            request.Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            return await http.SendAsync(request, ct);
        }
        finally
        {
            request.Dispose();
        }
    }

    public void Start(CancellationToken stoppingToken)
    {
        if (loop is not null) return;
        runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // Task.Run so the loop never captures the caller's context (the kernel's WPF
        // dispatcher); otherwise StopAsync waits on continuations a busy UI thread can't run.
        var token = runCts.Token;
        loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (runCts is null) return;
        await runCts.CancelAsync();
        if (loop is not null)
        {
            try { await loop; } catch (OperationCanceledException) { }
        }
        runCts.Dispose();
        runCts = null;
        loop = null;
    }

    internal static bool ShouldReachNativeAgent(
        DiscordCommonsGuildScope? scope, string? channelId, string? categoryId)
        => scope is null || !scope.Contains(channelId, categoryId)
           || !scope.SuppressesNativeAgent;

    internal static string ChannelName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "général" or "general" ? "general" : normalized;
    }

    /// <summary>A Discord attachment as the relay's human_message ingest describes it.</summary>
    internal sealed record DiscordCommonsAttachment(
        string Id, string Filename, string? ContentType, long Size, int? Width, int? Height,
        string Url, string? Description);

    internal static JsonObject BuildHumanMessagePayload(
        string eventId,
        string authorId,
        string displayName,
        string content,
        IReadOnlyList<string> mentions,
        IReadOnlyList<string> mentionRoles,
        string? channelId,
        string channel,
        string? threadRef,
        string? referencedMessageId,
        long timestamp,
        IReadOnlyList<DiscordCommonsAttachment>? attachments = null)
    {
        var listeningOnly = string.Equals(
            ChannelName(channel), "announcements", StringComparison.Ordinal);
        IReadOnlyList<string> forwardedMentions = listeningOnly ? [] : mentions;
        IReadOnlyList<string> forwardedRoles = listeningOnly ? [] : mentionRoles;
        var forwardedThread = listeningOnly ? null : threadRef;
        var forwardedReference = listeningOnly ? null : referencedMessageId;
        var payload = new JsonObject
        {
            ["type"] = "human_message",
            ["eventId"] = eventId,
            ["authorId"] = authorId,
            ["displayName"] = displayName,
            ["content"] = content,
            ["mentions"] = new JsonArray(forwardedMentions
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["mentionRoles"] = new JsonArray(forwardedRoles
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["referencedMessageId"] = forwardedReference,
            ["channelId"] = channelId,
            ["channel"] = channel,
            ["threadRef"] = forwardedThread,
            ["opensFloor"] = !listeningOnly && forwardedThread is not null
                && forwardedMentions.Count == 0
                && forwardedRoles.Count == 0
                && forwardedReference is null,
            ["timestamp"] = timestamp,
        };
        // Descriptors only: the relay forwards bounded images on Discord's CDN, and each Leaf
        // Community connector fetches, checks and reviews the bytes itself.
        if (!listeningOnly && attachments is { Count: > 0 })
            payload["attachments"] = new JsonArray(attachments.Select(item => (JsonNode?)new JsonObject
            {
                ["id"] = item.Id,
                ["filename"] = item.Filename,
                ["contentType"] = item.ContentType,
                ["size"] = item.Size,
                ["width"] = item.Width,
                ["height"] = item.Height,
                ["url"] = item.Url,
                ["description"] = item.Description,
            }).ToArray());
        return payload;
    }

    /// <summary>
    /// The native host's own post, reported after Discord accepted it. Its attachments are passed
    /// as Discord returned them; the relay forwards only bounded image descriptors to the Envoys
    /// it addresses, which fetch, check and review the bytes themselves.
    /// </summary>
    /// <summary>
    /// A native host post in a Leaf Community thread. An answer to an Envoy addresses that Envoy
    /// and was already authorized by the relay governor. An unprompted post (no source Envoy)
    /// lets the relay address the thread's Envoys it tags or names, and records it in history.
    /// </summary>
    internal static JsonObject BuildHostParticipantPostPayload(
        string messageId, string participantId, string content, string? sourceEnvoyId,
        string channel, string? channelId, string threadRef, long timestamp, JsonArray? attachments)
    {
        var payload = new JsonObject
        {
            ["type"] = "host_participant_post",
            ["eventId"] = $"host-post:{messageId}",
            ["participantId"] = participantId,
            ["content"] = content,
            ["addresses"] = sourceEnvoyId is null
                ? new JsonArray()
                : new JsonArray(JsonValue.Create(sourceEnvoyId)),
            ["channel"] = channel,
            ["channelId"] = channelId,
            ["threadRef"] = threadRef,
            ["messageId"] = messageId,
            ["timestamp"] = timestamp,
        };
        if (sourceEnvoyId is null) payload["unprompted"] = true;
        if (attachments is { Count: > 0 }) payload["attachments"] = attachments.DeepClone();
        return payload;
    }

    internal static JsonObject BuildAnnouncementPayload(
        string eventId,
        string messageId,
        string operation,
        string authorKind,
        string authorId,
        string displayName,
        string content,
        string? channelId,
        string channel,
        long timestamp)
    {
        if (operation is not ("create" or "update"))
            throw new ArgumentException("Announcement operation must be create or update.", nameof(operation));
        if (authorKind is not ("human" or "native_host"))
            throw new ArgumentException("Announcement author must be a human or native host.", nameof(authorKind));
        return new JsonObject
        {
            ["type"] = "announcement",
            ["operation"] = operation,
            ["eventId"] = eventId,
            ["messageId"] = messageId,
            ["authorKind"] = authorKind,
            ["authorId"] = authorId,
            ["displayName"] = displayName,
            ["content"] = content,
            ["channelId"] = channelId,
            ["channel"] = ChannelName(channel),
            ["timestamp"] = timestamp,
        };
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await DrainAsync(ct);
            var delay = Task.Delay(TimeSpan.FromSeconds(30), ct);
            var wake = signal.WaitAsync(ct);
            await Task.WhenAny(delay, wake);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        foreach (var delivery in await deliveries.PendingAsync(ct))
        {
            var config = await configurations.GetAsync(delivery.BotId, ct);
            var legacyCandidates = delivery.GuildId is null
                ? config?.GuildScopes.Values.Where(candidate => candidate.WholeGuild)
                    .Take(2).ToArray()
                : null;
            var scope = delivery.GuildId is null
                ? legacyCandidates is { Length: 1 } ? legacyCandidates[0] : null
                : config?.Scope(delivery.GuildId);
            if (scope is not { Ready: true } || delivery.Payload is null)
            {
                await deliveries.MarkRetryAsync(
                    delivery, "commons_not_configured", TimeSpan.FromMinutes(5), ct);
                continue;
            }
            try
            {
                var endpoint = new Uri(new Uri(scope.RelayUrl + "/"), "v1/ingest");
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", scope.HostCredential);
                request.Content = new StringContent(
                    delivery.Payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    await deliveries.MarkSentAsync(delivery, ct);
                    continue;
                }
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(delivery.Attempts + 1, 8)));
                await deliveries.MarkRetryAsync(
                    delivery,
                    $"relay_http_{(int)response.StatusCode}",
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(seconds),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(delivery.Attempts + 1, 8)));
                await deliveries.MarkRetryAsync(
                    delivery, $"relay_transport_{ex.GetType().Name}",
                    TimeSpan.FromSeconds(seconds), ct);
                logger.LogWarning(
                    "Commons ingest delivery {EventId} failed with {ErrorType}; retry scheduled",
                    delivery.EventId, ex.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        http.Dispose();
        signal.Dispose();
    }
}

public sealed record DiscordCommonsSecretInstallRequest(
    string SecretName,
    string WranglerProjectPath,
    string Environment = "lab");

public sealed record DiscordCommonsSecretInstallResult(
    string SecretName,
    string Environment,
    bool Installed);

public sealed class DiscordCommonsSecretInstaller(
    DiscordCommonsConfigurationStore configurations)
{
    private static readonly HashSet<string> AllowedSecretNames = new(
        ["WEBHOOK_SHOWCASE", "WEBHOOK_CONSULT", "WEBHOOK_BUGS", "WEBHOOK_GENERAL"],
        StringComparer.Ordinal);

    public async Task<DiscordCommonsSecretInstallResult> InstallAsync(
        Guid botId,
        string channelId,
        DiscordCommonsSecretInstallRequest request,
        CancellationToken ct = default)
    {
        if (!AllowedSecretNames.Contains(request.SecretName))
            throw new ArgumentException("The Wrangler secret name is not a Commons webhook slot.");
        if (request.Environment is not ("lab" or "production"))
            throw new ArgumentException(
                "Only the lab and production Wrangler environments are supported.");
        var projectPath = Path.GetFullPath(request.WranglerProjectPath);
        if (!File.Exists(Path.Combine(projectPath, "wrangler.toml"))
            || !File.Exists(Path.Combine(projectPath, "package.json")))
            throw new ArgumentException("The Wrangler project path is invalid.");
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new InvalidOperationException("Discord Commons is not configured.");
        if (!config.WebhookUrls.TryGetValue(channelId, out var webhookUrl))
            throw new KeyNotFoundException("No managed Commons webhook exists for that channel.");

        var wranglerEntrypoint = Path.Combine(
            projectPath, "node_modules", "wrangler", "bin", "wrangler.js");
        if (!File.Exists(wranglerEntrypoint))
            throw new ArgumentException(
                "The Wrangler project dependencies are not installed.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                // Launch the JavaScript entrypoint directly. Process.Start with
                // UseShellExecute=false cannot reliably execute Windows .cmd shims.
                FileName = OperatingSystem.IsWindows() ? "node.exe" : "node",
                WorkingDirectory = projectPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(wranglerEntrypoint);
        process.StartInfo.ArgumentList.Add("secret");
        process.StartInfo.ArgumentList.Add("put");
        process.StartInfo.ArgumentList.Add(request.SecretName);
        process.StartInfo.ArgumentList.Add("--env");
        process.StartInfo.ArgumentList.Add(request.Environment);
        if (!process.Start())
            throw new InvalidOperationException("Wrangler secret installation could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteLineAsync(webhookUrl.AsMemory(), ct);
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct);
        _ = await stdout;
        _ = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Wrangler secret installation failed.");
        return new DiscordCommonsSecretInstallResult(
            request.SecretName, request.Environment, true);
    }
}
