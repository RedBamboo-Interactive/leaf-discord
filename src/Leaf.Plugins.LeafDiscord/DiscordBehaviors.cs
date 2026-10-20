using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public static class DiscordBehaviorScopes
{
    public const string Default = "default";
    public const string Guild = "guild";
    public const string Channel = "channel";
    public const string DmDefault = "dm_default";
    public const string Dm = "dm";

    public static bool IsValid(string value)
        => value is Default or Guild or Channel or DmDefault or Dm;
}

public static class DiscordResponseModes
{
    public const string Direct = "direct";
    public const string Selective = "selective";
    public const string Participate = "participate";
    public const string ContextOnly = "context_only";

    public static bool IsValid(string value)
        => value is Direct or Selective or Participate or ContextOnly;
}

public sealed record DiscordBehavior(
    Guid? EntityId,
    Guid BotEntityId,
    string Scope,
    string? GuildId,
    string? ConversationId,
    string ResponseMode,
    Guid? PromptEntityId,
    string Instructions,
    bool Enabled,
    bool Persisted = true)
{
    public JsonObject ToTrustedMetadata() => new()
    {
        ["schema"] = "leaf-discord-behavior/v1",
        ["source"] = "leaf_owner_configuration",
        ["trust"] = "trusted_owner_configuration",
        ["behavior_entity_id"] = EntityId?.ToString(),
        ["prompt_entity_id"] = PromptEntityId?.ToString(),
        ["scope"] = Scope,
        ["response_mode"] = ResponseMode,
        ["instructions"] = Instructions,
    };
}

public sealed record DiscordBehaviorUpdate(
    string Scope,
    string ResponseMode,
    string? Instructions = null,
    string? GuildId = null,
    string? ConversationId = null,
    bool Enabled = true);

/// <summary>
/// Versioned, owner-authored Discord behavior configuration. Prompt text lives in a
/// separate Entity and behavior rows only reference it; no policy is persisted as a
/// nested JSON document.
/// </summary>
public sealed class DiscordBehaviorStore(IEntityStore store)
{
    public const string BehaviorTypeSlug = "leaf-discord-behavior";
    public const string PromptTypeSlug = "leaf-discord-prompt";
    private const int MaxInstructions = 12_000;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> migrationGates = new();

    public async Task EnsureDefaultsAsync(
        LeafDiscordConfig config,
        IReadOnlyList<DiscordBinding> existingBindings,
        CancellationToken ct = default)
    {
        var gate = migrationGates.GetOrAdd(config.EntityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var existingDefault = await FindAsync(
                config.EntityId, DiscordBehaviorScopes.Default, null, null, ct);
            if (existingDefault is not null)
            {
                if (await FindAsync(config.EntityId, DiscordBehaviorScopes.DmDefault,
                        null, null, ct) is null)
                    await SaveAsync(config, new DiscordBehaviorUpdate(
                        DiscordBehaviorScopes.DmDefault, DiscordResponseModes.Direct), ct);
                return;
            }

            // Existing bindings predate behavior Entities. Capture their exact admission mode
            // as explicit overrides before introducing safe defaults for future scopes.
            foreach (var binding in existingBindings.Where(binding =>
                         string.Equals(binding.ApplicationId, config.ApplicationId,
                             StringComparison.Ordinal)))
            {
                var scope = binding.Kind == "dm"
                    ? DiscordBehaviorScopes.Dm
                    : DiscordBehaviorScopes.Channel;
                await SaveAsync(config, new DiscordBehaviorUpdate(
                    scope,
                    NormalizeMode(binding.ResponseMode),
                    GuildId: binding.GuildId,
                    ConversationId: binding.ConversationId), ct);
            }

            await SaveAsync(config, new DiscordBehaviorUpdate(
                DiscordBehaviorScopes.Default, DiscordResponseModes.Direct), ct);
            await SaveAsync(config, new DiscordBehaviorUpdate(
                DiscordBehaviorScopes.DmDefault, DiscordResponseModes.Direct), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<DiscordBehavior>> ListAsync(
        Guid botEntityId, CancellationToken ct = default)
    {
        var entities = await store.QueryAsync(new EntityQuery
        {
            TypeSlug = BehaviorTypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["bot"] = botEntityId.ToString(),
            },
            Limit = 500,
        }, ct);
        var result = new List<DiscordBehavior>(entities.Count);
        foreach (var entity in entities)
            result.Add(await MapAsync(entity, ct));
        return result
            .OrderBy(item => ScopeOrder(item.Scope))
            .ThenBy(item => item.GuildId, StringComparer.Ordinal)
            .ThenBy(item => item.ConversationId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<DiscordBehavior> ResolveAsync(
        LeafDiscordConfig config,
        DiscordBinding binding,
        CancellationToken ct = default)
        => await ResolveAsync(config, binding.Kind, binding.GuildId,
            binding.ConversationId, binding.ParentConversationId,
            binding.ResponseMode, ct);

    public async Task<DiscordBehavior> ResolveAsync(
        LeafDiscordConfig config,
        string kind,
        string? guildId,
        string conversationId,
        string? parentConversationId,
        string fallbackResponseMode,
        CancellationToken ct = default)
    {
        var candidates = kind == "dm"
            ? new[]
            {
                (DiscordBehaviorScopes.Dm, (string?)null, (string?)conversationId),
                (DiscordBehaviorScopes.DmDefault, (string?)null, (string?)null),
                (DiscordBehaviorScopes.Default, (string?)null, (string?)null),
            }
            : new[]
            {
                (DiscordBehaviorScopes.Channel, guildId, (string?)conversationId),
                (DiscordBehaviorScopes.Channel, guildId, parentConversationId),
                (DiscordBehaviorScopes.Guild, guildId, (string?)null),
                (DiscordBehaviorScopes.Default, (string?)null, (string?)null),
            };

        foreach (var (scope, candidateGuild, candidateConversation) in candidates.Distinct())
        {
            if (scope == DiscordBehaviorScopes.Channel
                && string.IsNullOrWhiteSpace(candidateConversation))
                continue;
            var behavior = await FindAsync(
                config.EntityId, scope, candidateGuild, candidateConversation, ct);
            if (behavior is { Enabled: true }) return behavior;
        }

        return new DiscordBehavior(
            null, config.EntityId, kind == "dm"
                ? DiscordBehaviorScopes.DmDefault
                : DiscordBehaviorScopes.Default,
            guildId, conversationId, NormalizeMode(fallbackResponseMode),
            null, "", true, Persisted: false);
    }

    public async Task<DiscordBehavior> SaveAsync(
        LeafDiscordConfig config,
        DiscordBehaviorUpdate update,
        CancellationToken ct = default)
    {
        var normalized = Normalize(update);
        var key = ScopeKey(normalized.Scope, normalized.GuildId, normalized.ConversationId);
        var behaviorSlug = BehaviorSlug(config.EntityId, key);
        var promptSlug = PromptSlug(config.EntityId, key);
        var existing = await store.GetBySlugAsync(BehaviorTypeSlug, behaviorSlug, ct);
        Guid? promptId = existing is null
            ? null
            : Guid.TryParse(JsonRead.String(existing.Data, "prompt"), out var parsed)
                ? parsed : null;

        if (normalized.Instructions is not null || promptId is not null)
        {
            var prompt = await store.UpsertBySlugAsync(
                PromptTypeSlug,
                promptSlug,
                $"Discord prompt · {DisplayScope(normalized)}",
                new JsonObject { ["instructions"] = normalized.Instructions ?? "" },
                ct);
            promptId = prompt.Id;
        }

        var behavior = await store.UpsertBySlugAsync(
            BehaviorTypeSlug,
            behaviorSlug,
            $"Discord behavior · {DisplayScope(normalized)}",
            new JsonObject
            {
                ["bot"] = config.EntityId.ToString(),
                ["scope"] = normalized.Scope,
                ["guild_id"] = normalized.GuildId,
                ["conversation_id"] = normalized.ConversationId,
                ["response_mode"] = normalized.ResponseMode,
                ["prompt"] = promptId?.ToString(),
                ["enabled"] = normalized.Enabled,
            },
            ct);
        return await MapAsync(behavior, ct);
    }

    public async Task<DiscordBehavior?> DisableAsync(
        LeafDiscordConfig config,
        string scope,
        string? guildId,
        string? conversationId,
        CancellationToken ct = default)
    {
        var normalized = Normalize(new DiscordBehaviorUpdate(
            scope, DiscordResponseModes.Direct, GuildId: guildId,
            ConversationId: conversationId));
        if (normalized.Scope is DiscordBehaviorScopes.Default
            or DiscordBehaviorScopes.DmDefault)
            throw new ArgumentException("Default Discord behaviors cannot be removed.");
        var existing = await store.GetBySlugAsync(
            BehaviorTypeSlug,
            BehaviorSlug(config.EntityId,
                ScopeKey(normalized.Scope, normalized.GuildId, normalized.ConversationId)),
            ct);
        if (existing is null) return null;
        return await MapAsync(await store.PatchAsync(existing.Id,
            new JsonObject { ["enabled"] = false }, ct: ct), ct);
    }

    public async Task<DiscordBehavior?> FindAsync(
        Guid botEntityId,
        string scope,
        string? guildId,
        string? conversationId,
        CancellationToken ct = default)
    {
        var key = ScopeKey(scope, guildId, conversationId);
        var entity = await store.GetBySlugAsync(
            BehaviorTypeSlug, BehaviorSlug(botEntityId, key), ct);
        return entity is null ? null : await MapAsync(entity, ct);
    }

    private async Task<DiscordBehavior> MapAsync(LeafEntity entity, CancellationToken ct)
    {
        var data = entity.Data;
        if (!Guid.TryParse(JsonRead.String(data, "bot"), out var botId))
            throw new InvalidOperationException(
                $"Discord behavior '{entity.Id}' has no valid bot Entity reference.");
        Guid? promptId = Guid.TryParse(JsonRead.String(data, "prompt"), out var parsedPrompt)
            ? parsedPrompt : null;
        var instructions = "";
        if (promptId is not null
            && await store.GetAsync(promptId.Value, ct) is { TypeSlug: PromptTypeSlug } prompt)
            instructions = JsonRead.String(prompt.Data, "instructions") ?? "";
        return new DiscordBehavior(
            entity.Id,
            botId,
            JsonRead.String(data, "scope") ?? DiscordBehaviorScopes.Default,
            JsonRead.String(data, "guild_id"),
            JsonRead.String(data, "conversation_id"),
            NormalizeMode(JsonRead.String(data, "response_mode")),
            promptId,
            instructions,
            JsonRead.Bool(data, "enabled", true));
    }

    private static DiscordBehaviorUpdate Normalize(DiscordBehaviorUpdate update)
    {
        var scope = (update.Scope ?? "").Trim().ToLowerInvariant();
        if (!DiscordBehaviorScopes.IsValid(scope))
            throw new ArgumentException("Discord behavior scope is invalid.");
        var mode = NormalizeMode(update.ResponseMode);
        var guildId = SnowflakeOrNull(update.GuildId, "Guild ID");
        var conversationId = SnowflakeOrNull(update.ConversationId, "Conversation ID");
        if (scope is DiscordBehaviorScopes.Default or DiscordBehaviorScopes.DmDefault)
        {
            guildId = null;
            conversationId = null;
        }
        else if (scope == DiscordBehaviorScopes.Guild)
        {
            if (guildId is null) throw new ArgumentException("A guild behavior requires a guild ID.");
            conversationId = null;
        }
        else if (scope == DiscordBehaviorScopes.Channel)
        {
            if (guildId is null || conversationId is null)
                throw new ArgumentException("A channel behavior requires guild and channel IDs.");
        }
        else if (scope == DiscordBehaviorScopes.Dm)
        {
            guildId = null;
            if (conversationId is null)
                throw new ArgumentException("A DM behavior requires its conversation ID.");
        }

        var instructions = update.Instructions?.Trim();
        if (instructions?.Length > MaxInstructions || instructions?.Contains('\0') == true)
            throw new ArgumentException(
                $"Discord behavior instructions must be at most {MaxInstructions:N0} characters.");
        return update with
        {
            Scope = scope,
            ResponseMode = mode,
            Instructions = instructions,
            GuildId = guildId,
            ConversationId = conversationId,
        };
    }

    private static string NormalizeMode(string? value)
    {
        var mode = string.IsNullOrWhiteSpace(value)
            ? DiscordResponseModes.Direct
            : value.Trim().ToLowerInvariant();
        return DiscordResponseModes.IsValid(mode)
            ? mode
            : throw new ArgumentException("Discord response mode is invalid.");
    }

    private static string? SnowflakeOrNull(string? value, string label)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!ulong.TryParse(value, out var parsed) || parsed == 0)
            throw new ArgumentException($"{label} must be a Discord snowflake.");
        return value;
    }

    private static string ScopeKey(string scope, string? guildId, string? conversationId)
        => scope switch
        {
            DiscordBehaviorScopes.Default => "default",
            DiscordBehaviorScopes.DmDefault => "dm-default",
            DiscordBehaviorScopes.Guild => $"guild:{guildId}",
            DiscordBehaviorScopes.Channel => $"channel:{guildId}:{conversationId}",
            DiscordBehaviorScopes.Dm => $"dm:{conversationId}",
            _ => throw new ArgumentException("Discord behavior scope is invalid."),
        };

    private static string BehaviorSlug(Guid botEntityId, string key)
        => $"discord-behavior-{Hash($"{botEntityId:N}:{key}")}";

    private static string PromptSlug(Guid botEntityId, string key)
        => $"discord-prompt-{Hash($"{botEntityId:N}:{key}")}";

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..24];

    private static int ScopeOrder(string scope) => scope switch
    {
        DiscordBehaviorScopes.Default => 0,
        DiscordBehaviorScopes.DmDefault => 1,
        DiscordBehaviorScopes.Guild => 2,
        DiscordBehaviorScopes.Channel => 3,
        DiscordBehaviorScopes.Dm => 4,
        _ => 5,
    };

    private static string DisplayScope(DiscordBehaviorUpdate update) => update.Scope switch
    {
        DiscordBehaviorScopes.Default => "default",
        DiscordBehaviorScopes.DmDefault => "DM default",
        DiscordBehaviorScopes.Guild => $"guild {update.GuildId}",
        DiscordBehaviorScopes.Channel => $"channel {update.ConversationId}",
        DiscordBehaviorScopes.Dm => $"DM {update.ConversationId}",
        _ => update.Scope,
    };
}
