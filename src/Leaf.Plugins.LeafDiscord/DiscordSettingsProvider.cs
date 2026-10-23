using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Hosting;

namespace Leaf.Plugins.LeafDiscord;

public sealed class DiscordSettingsProvider(
    DiscordBotConfigurationStore configurations,
    DiscordAgentResolver agents,
    DiscordRuntimeOptionsResolver runtimeOptions,
    DiscordBotService bot,
    IHostApplicationLifetime lifetime) : IPluginSettingsProvider
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> Allowed =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["connection"] =
                ["application_id", "interaction_public_key", "bot_token", "clear_bot_token", "enabled", "restart_bridge"],
            // sentinel_quality_mode is retired (the Sentinel chooses its own compute). It is still
            // accepted from a stale form and ignored, so an old page saves instead of failing.
            ["access"] =
                ["default_agent", "session_quality_mode", "sentinel_quality_mode",
                 "dm_allowlist", "clear_allowlist"],
            ["attachments"] = ["max_attachment_bytes"],
            ["replies"] = ["show_tool_activity"],
            ["presence"] = ["presence_context_mode", "presence_extension_feed_enabled"],
        };

    public string PluginId => LeafDiscordPlugin.PluginId;

    public async Task<JsonObject> DescribeAsync(string? userId, CancellationToken ct = default)
    {
        var config = (await configurations.ListAsync(ct)).FirstOrDefault()
            ?? DiscordBotConfigurationStore.EmptyDefaults();
        var status = await bot.GetStatusAsync(ct);
        var agentOptions = await runtimeOptions.ListAgentsAsync(ct);
        var qualityModes = await runtimeOptions.ListQualityModesAsync(ct);
        return BuildDescriptor(config, status, agentOptions, qualityModes);
    }

    internal static JsonObject BuildDescriptor(
        LeafDiscordConfig config,
        DiscordBotStatus status,
        IReadOnlyList<DiscordSettingOption> agentOptions,
        IReadOnlyList<DiscordQualityMode> qualityModes)
    {
        JsonArray AgentOptions() => new(agentOptions.Select(Option).ToArray());
        JsonArray QualityOptions() => new(qualityModes.Select(mode => Option(
            new DiscordSettingOption(mode.Id,
                mode.Name,
                string.Join(" / ", new[] { mode.Provider, mode.Model, mode.Effort }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                mode.EntityId, "quality-mode"))).ToArray());

        return new JsonObject
        {
            ["id"] = LeafDiscordPlugin.PluginId,
            ["status"] = Status(status),
            ["sourceOfTruth"] = "entities",
            ["sections"] = new JsonArray
            {
                Section("connection", "Connection", new JsonArray
                {
                    Field("application_id", "string", config.ApplicationId,
                        "Discord application ID."),
                    Field("interaction_public_key", "string", config.InteractionPublicKey,
                        "Discord interaction verification public key."),
                    SecretField("bot_token", !string.IsNullOrWhiteSpace(config.BotToken),
                        status.State == "connected" ? "verified" : "unverified",
                        "Bot credential protected by RedLeaf encrypted entity secrets."),
                    Field("enabled", "boolean", config.Enabled,
                        "Connect this bridge when RedLeaf is running."),
                    Action("restart_bridge",
                        "Reconnect only the Discord transport without restarting RedLeaf."),
                }),
                Section("access", "Access and safety", new JsonArray
                {
                    EntityField("default_agent", config.AgentId,
                        "Agent identity used for new Discord conversations.",
                        "agent", AgentOptions()),
                    EntityField("session_quality_mode", config.SessionQualityMode,
                        "Provider, model, and reasoning effort for new Agent sessions.",
                        "quality-mode", QualityOptions()),
                    Field("dm_allowlist", "string_list", null,
                        "Discord user IDs allowed to talk to the selected Agent in direct messages.",
                        new JsonArray(config.DmAllowlist.Order()
                            .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())),
                    Field("clear_allowlist", "string_list", null,
                        "Discord user IDs allowed to clear shared conversation context.",
                        new JsonArray(config.ClearAllowlist.Order()
                            .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())),
                }),
                Section("attachments", "Attachments", new JsonArray
                {
                    Field("max_attachment_bytes", "number", config.MaxAttachmentBytes,
                        "Maximum accepted attachment size in bytes (1 to 25 MiB)."),
                }),
                Section("replies", "Replies", new JsonArray
                {
                    Field("show_tool_activity", "boolean", config.ShowToolActivity,
                        "Show tool-call squares in the progress message while the Agent works."),
                }),
                Section("presence", "Guild context", new JsonArray
                {
                    Field("presence_context_mode", "select", config.PresenceContextMode,
                        "Optional bounded guild presence context. Enable matching privileged intents in the Discord Developer Portal before selecting active or roster.",
                        options: new JsonArray
                        {
                            Option(new DiscordSettingOption("off", "Off",
                                "No presence or member context is requested.")),
                            Option(new DiscordSettingOption("active", "Active members",
                                "Online/activity and voice context; requires Presence Intent.")),
                            Option(new DiscordSettingOption("roster", "Full roster",
                                "Also includes offline cached members; requires Presence and Server Members intents.")),
                        }),
                    Field("presence_extension_feed_enabled", "boolean",
                        config.PresenceExtensionFeedEnabled,
                        "Privately publish the linked owner's current Discord game, music, and voice context to the optional Presence extension."),
                }),
            },
            ["secretPolicy"] = new JsonObject
            {
                ["fieldType"] = "secret",
                ["protection"] = "encrypted",
                ["readable"] = false,
            },
        };
    }

    public async Task<JsonObject> WriteAsync(
        string? userId,
        string section,
        JsonObject values,
        bool validateOnly,
        CancellationToken ct = default)
    {
        if (!Allowed.TryGetValue(section, out var allowed))
            throw new ArgumentException($"Unknown Discord settings section '{section}'");
        foreach (var key in values.Select(pair => pair.Key))
            if (!allowed.Contains(key))
                throw new ArgumentException($"Unknown setting '{key}'");

        if (section == "connection" && ReadBool(values, "restart_bridge") == true)
        {
            if (!validateOnly) await bot.RestartAsync(lifetime.ApplicationStopping);
            return Result(section, validateOnly,
                validateOnly ? null : await bot.GetStatusAsync(ct));
        }

        var current = (await configurations.ListAsync(ct)).FirstOrDefault()
            ?? throw new ArgumentException(
                "Add a Discord bot configuration before editing its settings.");
        var update = new LeafDiscordConfigUpdate(
            ReadString(values, "application_id") ?? current.ApplicationId,
            ReadString(values, "interaction_public_key") ?? current.InteractionPublicKey,
            ReadString(values, "bot_token"),
            ReadBool(values, "clear_bot_token") ?? false,
            ReadBool(values, "enabled") ?? current.Enabled,
            ReadStringList(values, "dm_allowlist") ?? current.DmAllowlist.ToArray(),
            ReadStringList(values, "clear_allowlist") ?? current.ClearAllowlist.ToArray(),
            ReadLong(values, "max_attachment_bytes") ?? current.MaxAttachmentBytes,
            ReadString(values, "default_agent") ?? current.AgentId,
            ReadString(values, "session_quality_mode") ?? current.SessionQualityMode,
            ReadString(values, "presence_context_mode") ?? current.PresenceContextMode,
            ReadBool(values, "presence_extension_feed_enabled")
                ?? current.PresenceExtensionFeedEnabled,
            ReadBool(values, "show_tool_activity") ?? current.ShowToolActivity);
        var normalized = LeafDiscordConfigStore.NormalizeUpdate(current, update);
        if (await agents.ResolveAsync(normalized.AgentId, ct) is null)
            throw new ArgumentException(
                $"Agent '{normalized.AgentId}' was not found");
        if (await runtimeOptions.ResolveQualityModeAsync(normalized.SessionQualityMode, ct) is null)
            throw new ArgumentException(
                $"Session quality mode '{normalized.SessionQualityMode}' was not found");

        if (!validateOnly)
        {
            await configurations.SaveAsync(current.EntityId, update, ct);
            if (section is "connection" or "presence")
                await bot.RestartAsync(lifetime.ApplicationStopping);
        }

        return Result(section, validateOnly,
            validateOnly ? null : await bot.GetStatusAsync(ct));
    }

    private static JsonObject Result(
        string section, bool validateOnly, DiscordBotStatus? status) => new()
    {
        ["ok"] = true,
        ["validatedOnly"] = validateOnly,
        ["section"] = section,
        ["status"] = status is null ? null : Status(status),
    };

    private static JsonObject Status(DiscordBotStatus status) => new()
    {
        ["state"] = status.State,
        ["configured"] = status.Configured,
        ["enabled"] = status.Enabled,
        ["botUser"] = status.BotUser,
        ["guilds"] = status.Guilds,
        ["connectedAt"] = status.ConnectedAt?.ToString("O"),
        ["lastError"] = status.LastError,
    };

    private static JsonObject Section(string id, string label, JsonArray fields)
        => new() { ["id"] = id, ["label"] = label, ["kind"] = "entity-settings", ["fields"] = fields };

    private static JsonObject Field(
        string key, string type, object? value, string description,
        JsonArray? values = null, JsonArray? options = null)
    {
        var field = new JsonObject
        {
            ["key"] = key,
            ["type"] = type,
            ["value"] = JsonValue.Create(value),
            ["description"] = description,
        };
        if (values is not null) field["value"] = values;
        if (options is not null) field["options"] = options;
        return field;
    }

    private static JsonObject Option(DiscordSettingOption option)
    {
        var result = new JsonObject
        {
            ["value"] = option.Value,
            ["label"] = option.Label,
            ["description"] = option.Description,
        };
        if (!string.IsNullOrWhiteSpace(option.EntityId)
            && !string.IsNullOrWhiteSpace(option.EntityTypeSlug))
        {
            result["entity"] = new JsonObject
            {
                ["id"] = option.EntityId,
                ["typeSlug"] = option.EntityTypeSlug,
                ["name"] = option.Label,
            };
        }
        return result;
    }

    private static JsonObject EntityField(
        string key, object? value, string description,
        string targetType, JsonArray options) => new()
    {
        ["key"] = key,
        ["type"] = "entity_ref",
        ["value"] = JsonValue.Create(value),
        ["description"] = description,
        ["targetType"] = targetType,
        ["options"] = options,
    };

    private static JsonObject SecretField(
        string key, bool configured, string verification, string description) => new()
    {
        ["key"] = key,
        ["type"] = "secret",
        ["configured"] = configured,
        ["protection"] = "encrypted",
        ["verification"] = verification,
        ["description"] = description,
    };

    private static JsonObject Action(string key, string description) => new()
    {
        ["key"] = key,
        ["type"] = "action",
        ["description"] = description,
    };

    private static string? ReadString(JsonObject values, string key)
        => values[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result : null;

    private static bool? ReadBool(JsonObject values, string key)
        => values[key] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result : null;

    private static long? ReadLong(JsonObject values, string key)
    {
        if (values[key] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var result)) return result;
        return value.TryGetValue<int>(out var integer) ? integer : null;
    }

    private static IReadOnlyList<string>? ReadStringList(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node)) return null;
        if (node is not JsonArray array)
            throw new ArgumentException($"{key} must be an array of Discord user IDs");
        return array.Select(item => item?.GetValue<string>()
            ?? throw new ArgumentException($"{key} must contain only strings")).ToArray();
    }
}
