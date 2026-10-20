using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordGuildContextTests
{
    [Fact]
    public void Off_context_still_carries_a_bounded_server_map()
    {
        var serverMap = DiscordGuildNavigationBuilder.Build(
            "guild-1", "Leaf", "post-1",
            [new DiscordGuildCategorySnapshot("category-1", "workstreams", 1)],
            [new DiscordGuildChannelSnapshot(
                "forum-1", "workstreams", "forum", "category-1",
                "Long-running work", 1, true, true, false,
                [new DiscordGuildForumTagSnapshot("tag-1", "architecture", null, false)]),
             new DiscordGuildChannelSnapshot(
                "post-1", "Forum support", "forum_post", "forum-1",
                null, 2, true, false, true, [])],
            DateTimeOffset.Parse("2026-09-12T11:10:00+02:00"));

        var context = DiscordGuildContextBuilder.Build(
            "off", "guild-1", "Leaf", "post-1", "Forum support",
            [], false, DateTimeOffset.Parse("2026-09-12T11:10:00+02:00"),
            serverMap);

        Assert.NotNull(context);
        Assert.Equal("off", context["mode"]!.GetValue<string>());
        Assert.Empty(context["members"]!.AsArray());
        var map = context["server_map"]!.AsObject();
        Assert.Equal("leaf-discord-guild-map/v1", map["schema"]!.GetValue<string>());
        Assert.Equal(2, map["channels"]!.AsArray().Count);
        Assert.Equal("forum", map["channels"]![0]!["kind"]!.GetValue<string>());
        Assert.True(map["channels"]![0]!["can_read_history"]!.GetValue<bool>());
    }

    [Fact]
    public void Server_map_is_bounded_and_marks_omissions()
    {
        var channels = Enumerable.Range(1, DiscordGuildNavigationBuilder.MaxChannels + 2)
            .Select(index => new DiscordGuildChannelSnapshot(
                index.ToString(), $"Channel {index:000}", "text", null, null,
                index, true, true, true, []))
            .ToArray();

        var map = DiscordGuildNavigationBuilder.Build(
            "guild", "Leaf", "1", [], channels,
            DateTimeOffset.Parse("2026-09-12T11:10:00+02:00"));

        Assert.Equal(DiscordGuildNavigationBuilder.MaxChannels,
            map["channels"]!.AsArray().Count);
        Assert.Equal(2, map["summary"]!["channels_omitted"]!.GetValue<int>());
        Assert.True(map["summary"]!["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Active_context_excludes_offline_humans_and_all_bots()
    {
        var context = DiscordGuildContextBuilder.Build(
            "active",
            "guild-1",
            "Odyssees",
            "channel-1",
            "disponibilites",
            [
                Member("1", "Carol", "Online", "Playing", "Puzzle Quest"),
                Member("2", "Bob", "Offline"),
                Member("3", "MusicBot", "Online", isBot: true),
                Member("4", "Nikfox", "Offline", voice: "Gaming"),
            ],
            false,
            DateTimeOffset.Parse("2026-09-10T19:54:00+02:00"));

        Assert.NotNull(context);
        var members = context["members"]!.AsArray();
        Assert.Equal(2, members.Count);
        Assert.Equal("Carol", members[0]!["display_name"]!.GetValue<string>());
        Assert.Equal("Nikfox", members[1]!["display_name"]!.GetValue<string>());
        Assert.False(context["channel"]!["reader_presence_available"]!.GetValue<bool>());
        Assert.False(context["summary"]!["roster_complete"]!.GetValue<bool>());
    }

    [Fact]
    public void Roster_context_includes_offline_humans_and_reports_truncation()
    {
        var members = Enumerable.Range(1, DiscordGuildContextBuilder.MaxMembers + 2)
            .Select(index => Member(index.ToString(), $"Member {index:000}", "Offline"))
            .ToArray();

        var context = DiscordGuildContextBuilder.Build(
            "roster", "guild", "Guild", "channel", "general", members,
            true,
            DateTimeOffset.Parse("2026-09-10T19:54:00+02:00"));

        Assert.NotNull(context);
        Assert.Equal(DiscordGuildContextBuilder.MaxMembers,
            context["members"]!.AsArray().Count);
        Assert.True(context["summary"]!["truncated"]!.GetValue<bool>());
        Assert.Equal(2, context["summary"]!["omitted"]!.GetValue<int>());
        Assert.True(context["summary"]!["roster_complete"]!.GetValue<bool>());
    }

    [Fact]
    public void Owner_presence_prefers_voice_and_bounds_social_companions()
    {
        var owner = Member("1", "Owner", "Online", "Playing", "Puzzle Quest", "Gaming");
        var payload = DiscordOwnerPresenceBuilder.Build(
            Guid.NewGuid().ToString(),
            "100000000000000001",
            [
                new DiscordOwnerPresenceGuild(
                    "guild", "Odyssees", owner,
                    [
                        owner,
                        Member("2", "Bob", "Online", "Playing", "Puzzle Quest", "Gaming"),
                        Member("3", "Carol", "Online", "Playing", "Puzzle Quest"),
                        Member("4", "MusicBot", "Online", "Playing", "Puzzle Quest",
                            isBot: true),
                    ]),
            ],
            DateTimeOffset.Parse("2026-09-10T19:54:00+02:00"));

        Assert.Equal("active", payload["status"]!.GetValue<string>());
        Assert.Equal("gaming", payload["category"]!.GetValue<string>());
        Assert.Equal("Odyssees / Gaming", payload["voice_channel"]!.GetValue<string>());
        Assert.Equal(300, payload["priority"]!.GetValue<int>());
        var participants = payload["participants"]!.AsArray();
        Assert.Equal(2, participants.Count);
        Assert.Equal("shared-session",
            participants[0]!["relationship"]!.GetValue<string>());
        Assert.Equal("same-game",
            participants[1]!["relationship"]!.GetValue<string>());
        Assert.DoesNotContain(participants,
            item => item!["name"]!.GetValue<string>() == "MusicBot");
    }

    private static DiscordGuildContextMember Member(
        string id,
        string name,
        string status,
        string? activityType = null,
        string? activityName = null,
        string? voice = null,
        bool isBot = false)
        => new(
            id,
            name,
            status,
            activityType is null || activityName is null
                ? []
                : [new DiscordGuildContextActivity(activityType, activityName)],
            voice,
            isBot);
}
