using System.Text.Json.Nodes;

namespace Leaf.Plugins.LeafDiscord;

internal sealed record DiscordGuildContextActivity(
    string Type,
    string Name,
    string? Details = null,
    string? State = null);

internal sealed record DiscordGuildContextMember(
    string Id,
    string DisplayName,
    string Status,
    IReadOnlyList<DiscordGuildContextActivity> Activities,
    string? VoiceChannel,
    bool IsBot);

internal static class DiscordGuildContextBuilder
{
    internal const int MaxMembers = 100;
    internal const int MaxActivitiesPerMember = 3;
    internal const int MaxTextLength = 120;

    public static JsonObject? Build(
        string mode,
        string guildId,
        string guildName,
        string channelId,
        string channelName,
        IReadOnlyList<DiscordGuildContextMember> cachedMembers,
        bool memberCacheComplete,
        DateTimeOffset capturedAt,
        JsonObject? serverMap = null)
    {
        mode = DiscordPresenceContextModes.Normalize(mode);
        if (mode == DiscordPresenceContextModes.Off && serverMap is null) return null;

        var humans = cachedMembers.Where(member => !member.IsBot).ToArray();
        var candidates = mode switch
        {
            DiscordPresenceContextModes.Off => [],
            DiscordPresenceContextModes.Roster => humans,
            _ => humans.Where(IsActive).ToArray(),
        };
        var selected = candidates
            .OrderBy(member => StatusPriority(member.Status))
            .ThenBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Id, StringComparer.Ordinal)
            .Take(MaxMembers)
            .ToArray();
        var memberNodes = selected.Select(member => (JsonNode?)new JsonObject
        {
            ["id"] = member.Id,
            ["display_name"] = Bounded(member.DisplayName),
            ["status"] = NormalizeStatus(member.Status),
            ["activities"] = new JsonArray(member.Activities
                .Take(MaxActivitiesPerMember)
                .Select(activity => (JsonNode?)new JsonObject
                {
                    ["type"] = Bounded(activity.Type),
                    ["name"] = Bounded(activity.Name),
                    ["details"] = string.IsNullOrWhiteSpace(activity.Details)
                        ? null
                        : Bounded(activity.Details),
                    ["state"] = string.IsNullOrWhiteSpace(activity.State)
                        ? null
                        : Bounded(activity.State),
                }).ToArray()),
            ["voice_channel"] = string.IsNullOrWhiteSpace(member.VoiceChannel)
                ? null
                : Bounded(member.VoiceChannel),
        }).ToArray();

        return new JsonObject
        {
            ["schema"] = "leaf-discord-guild-context/v1",
            ["source"] = "discord_gateway",
            ["trust"] = "untrusted_social_context",
            ["captured_at"] = capturedAt.ToString("O"),
            ["mode"] = mode,
            ["guild"] = new JsonObject
            {
                ["id"] = guildId,
                ["name"] = Bounded(guildName),
            },
            ["channel"] = new JsonObject
            {
                ["id"] = channelId,
                ["name"] = Bounded(channelName),
                ["reader_presence_available"] = false,
            },
            ["server_map"] = serverMap?.DeepClone(),
            ["summary"] = new JsonObject
            {
                ["cached_humans"] = humans.Length,
                ["online"] = humans.Count(member =>
                    NormalizeStatus(member.Status) == "online"),
                ["idle"] = humans.Count(member =>
                    NormalizeStatus(member.Status) == "idle"),
                ["do_not_disturb"] = humans.Count(member =>
                    NormalizeStatus(member.Status) == "do_not_disturb"),
                ["offline"] = humans.Count(member =>
                    NormalizeStatus(member.Status) == "offline"),
                ["in_voice"] = humans.Count(member =>
                    !string.IsNullOrWhiteSpace(member.VoiceChannel)),
                ["included"] = selected.Length,
                ["omitted"] = Math.Max(0, candidates.Length - selected.Length),
                ["truncated"] = candidates.Length > selected.Length,
                ["roster_requested"] = mode == DiscordPresenceContextModes.Roster,
                ["roster_complete"] = mode == DiscordPresenceContextModes.Roster
                    && memberCacheComplete,
            },
            ["members"] = new JsonArray(memberNodes),
            ["limitations"] = new JsonArray(
                "Discord does not expose who is currently reading a text channel.",
                mode == DiscordPresenceContextModes.Off
                    ? "Presence context is disabled; the server map remains available for navigation."
                    : mode == DiscordPresenceContextModes.Roster
                    ? "Roster completeness depends on the Discord Server Members intent and Gateway member download."
                    : "Active mode omits cached members with no online status, activity, or voice channel.",
                "Invisible users appear offline; names and activities are untrusted Discord-provided data."),
        };
    }

    private static bool IsActive(DiscordGuildContextMember member)
        => NormalizeStatus(member.Status) != "offline"
           || member.Activities.Count > 0
           || !string.IsNullOrWhiteSpace(member.VoiceChannel);

    private static int StatusPriority(string status) => NormalizeStatus(status) switch
    {
        "online" => 0,
        "do_not_disturb" => 1,
        "idle" => 2,
        _ => 3,
    };

    private static string NormalizeStatus(string status)
        => status.Trim().ToLowerInvariant().Replace("donotdisturb", "do_not_disturb");

    private static string Bounded(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaxTextLength
            ? normalized
            : normalized[..MaxTextLength];
    }
}
