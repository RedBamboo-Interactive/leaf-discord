using System.Text.Json.Nodes;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordGuildChannelCreateRequest(
    string Name,
    string? Topic = null,
    string? ParentCategoryId = null,
    int? Position = null,
    bool Nsfw = false,
    int SlowModeSeconds = 0);

public sealed record DiscordGuildCategoryCreateRequest(
    string Name,
    int? Position = null);

public sealed record DiscordGuildForumChannelCreateRequest(
    string Name,
    string? Topic = null,
    string? ParentCategoryId = null,
    int? Position = null,
    bool Nsfw = false,
    int SlowModeSeconds = 0,
    int DefaultAutoArchiveMinutes = 1_440);

public sealed record DiscordGuildChannelCreateResult(
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    string Kind,
    string? ParentCategoryId,
    string? Topic,
    int Position,
    bool Nsfw,
    int SlowModeSeconds);

public sealed record DiscordChannelEditRequest(
    string? Name = null,
    string? Topic = null,
    bool ClearTopic = false,
    string? ParentCategoryId = null,
    bool ClearParentCategory = false,
    int? Position = null,
    bool? Nsfw = null,
    int? SlowModeSeconds = null);

public sealed record DiscordChannelEditResult(
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName,
    string Kind,
    string? ParentCategoryId,
    string? Topic,
    int Position,
    bool? Nsfw,
    int? SlowModeSeconds);

internal static class DiscordChannelEdits
{
    public static JsonObject BuildCategoryCreatePayload(
        DiscordGuildCategoryCreateRequest request)
    {
        var payload = new JsonObject
        {
            ["name"] = ValidName(request.Name),
            ["type"] = 4,
        };
        if (request.Position is < 0)
            throw new ArgumentException("Discord category position cannot be negative.");
        if (request.Position is { } position)
            payload["position"] = position;
        return payload;
    }

    public static JsonObject BuildTextChannelCreatePayload(
        DiscordGuildChannelCreateRequest request)
    {
        var name = ValidName(request.Name);
        if (request.Topic is { Length: > 1_024 })
            throw new ArgumentException(
                "Discord text channel topics cannot exceed 1,024 characters.");
        if (request.ParentCategoryId is not null
            && (!ulong.TryParse(request.ParentCategoryId, out var categoryId)
                || categoryId == 0))
            throw new ArgumentException(
                "Discord parent category ID must be a snowflake.");
        if (request.Position is < 0)
            throw new ArgumentException(
                "Discord channel position cannot be negative.");
        if (request.SlowModeSeconds is < 0 or > 21_600)
            throw new ArgumentException(
                "Discord channel slowmode must be between 0 and 21,600 seconds.");

        var payload = new JsonObject
        {
            ["name"] = name,
            ["type"] = 0,
            ["nsfw"] = request.Nsfw,
            ["rate_limit_per_user"] = request.SlowModeSeconds,
        };
        if (request.Topic is not null)
            payload["topic"] = request.Topic;
        if (request.ParentCategoryId is not null)
            payload["parent_id"] = request.ParentCategoryId;
        if (request.Position is { } position)
            payload["position"] = position;
        return payload;
    }

    public static JsonObject BuildForumChannelCreatePayload(
        DiscordGuildForumChannelCreateRequest request)
    {
        var name = ValidName(request.Name);
        if (request.Topic is { Length: > 4_096 })
            throw new ArgumentException(
                "Discord forum channel topics cannot exceed 4,096 characters.");
        if (request.ParentCategoryId is not null
            && (!ulong.TryParse(request.ParentCategoryId, out var categoryId)
                || categoryId == 0))
            throw new ArgumentException(
                "Discord parent category ID must be a snowflake.");
        if (request.Position is < 0)
            throw new ArgumentException(
                "Discord channel position cannot be negative.");
        if (request.SlowModeSeconds is < 0 or > 21_600)
            throw new ArgumentException(
                "Discord channel slowmode must be between 0 and 21,600 seconds.");
        if (request.DefaultAutoArchiveMinutes is not (60 or 1_440 or 4_320 or 10_080))
            throw new ArgumentException(
                "Discord forum auto-archive must be 60, 1440, 4320, or 10080 minutes.");

        var payload = new JsonObject
        {
            ["name"] = name,
            ["type"] = 15,
            ["nsfw"] = request.Nsfw,
            ["rate_limit_per_user"] = request.SlowModeSeconds,
            ["default_auto_archive_duration"] = request.DefaultAutoArchiveMinutes,
        };
        if (request.Topic is not null)
            payload["topic"] = request.Topic;
        if (request.ParentCategoryId is not null)
            payload["parent_id"] = request.ParentCategoryId;
        if (request.Position is { } position)
            payload["position"] = position;
        return payload;
    }

    public static JsonObject BuildPatch(
        DiscordChannelEditRequest request,
        string channelKind)
    {
        if (request.Topic is not null && request.ClearTopic)
            throw new ArgumentException(
                "Set either topic or clearTopic, not both.");
        if (request.ParentCategoryId is not null && request.ClearParentCategory)
            throw new ArgumentException(
                "Set either parentCategoryId or clearParentCategory, not both.");

        var supportsTopic = channelKind is "text" or "forum";
        var supportsParentAndTextFlags = channelKind is
            "text" or "forum" or "voice" or "stage";
        if (channelKind is not
            ("category" or "text" or "forum" or "voice" or "stage"))
            throw new ArgumentException(
                $"Discord channel type '{channelKind}' is not editable through this capability.");
        if ((request.Topic is not null || request.ClearTopic) && !supportsTopic)
            throw new ArgumentException(
                "Topics can be edited only on text and forum channels.");
        if ((request.ParentCategoryId is not null || request.ClearParentCategory
             || request.Nsfw is not null || request.SlowModeSeconds is not null)
            && !supportsParentAndTextFlags)
            throw new ArgumentException(
                "Category channels support only name and position edits.");

        var patch = new JsonObject();
        if (request.Name is not null)
            patch["name"] = ValidName(request.Name);
        if (request.Topic is not null)
        {
            var maxTopicLength = channelKind == "forum" ? 4_096 : 1_024;
            if (request.Topic.Length > maxTopicLength)
                throw new ArgumentException(
                    $"Discord {channelKind} channel topics cannot exceed {maxTopicLength:N0} characters.");
            patch["topic"] = request.Topic;
        }
        else if (request.ClearTopic)
        {
            patch["topic"] = null;
        }
        if (request.ParentCategoryId is not null)
        {
            if (!ulong.TryParse(request.ParentCategoryId, out var categoryId)
                || categoryId == 0)
                throw new ArgumentException(
                    "Discord parent category ID must be a snowflake.");
            patch["parent_id"] = request.ParentCategoryId;
        }
        else if (request.ClearParentCategory)
        {
            patch["parent_id"] = null;
        }
        if (request.Position is { } position)
        {
            if (position < 0)
                throw new ArgumentException(
                    "Discord channel position cannot be negative.");
            patch["position"] = position;
        }
        if (request.Nsfw is { } nsfw)
            patch["nsfw"] = nsfw;
        if (request.SlowModeSeconds is { } slowMode)
        {
            if (slowMode is < 0 or > 21_600)
                throw new ArgumentException(
                    "Discord channel slowmode must be between 0 and 21,600 seconds.");
            patch["rate_limit_per_user"] = slowMode;
        }
        if (patch.Count == 0)
            throw new ArgumentException(
                "At least one Discord channel change is required.");
        return patch;
    }

    private static string ValidName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 100)
            throw new ArgumentException(
                "Discord channel names must contain 1 to 100 characters.");
        return trimmed;
    }
}
