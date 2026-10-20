using System.Text.Json.Nodes;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordServerMapDisclosureTests
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-10-16T09:00:00+00:00");

    [Fact]
    public void First_turn_of_a_session_carries_the_full_map_with_its_hash()
    {
        var binding = Binding();

        var plan = DiscordServerMapDisclosure.Plan(binding, Context(Map("general")));

        var map = plan.GuildContext!["server_map"]!.AsObject();
        Assert.Equal("leaf-discord-guild-map/v1", map["schema"]!.GetValue<string>());
        Assert.Single(map["channels"]!.AsArray());
        Assert.Equal(plan.NextState!.Hash, map["hash"]!.GetValue<string>());
        Assert.Equal(DiscordServerMapDisclosure.HashLength, plan.NextState.Hash.Length);
        Assert.Equal(0, plan.NextState.TurnsSinceFull);
        Assert.Equal(binding.Generation, plan.NextState.Generation);
        Assert.Equal(binding.SessionId, plan.NextState.SessionId);
    }

    [Fact]
    public void Unchanged_map_is_replaced_by_a_compact_reference()
    {
        var first = DiscordServerMapDisclosure.Plan(Binding(), Context(Map("general")));
        var context = Context(Map("general", CapturedAt.AddMinutes(5)));

        var plan = DiscordServerMapDisclosure.Plan(
            Binding() with { ServerMapDisclosure = first.NextState }, context);

        var reference = plan.GuildContext!["server_map"]!.AsObject();
        Assert.Equal(DiscordServerMapDisclosure.ReferenceSchema,
            reference["schema"]!.GetValue<string>());
        Assert.True(reference["unchanged"]!.GetValue<bool>());
        Assert.Equal(first.NextState!.Hash, reference["hash"]!.GetValue<string>());
        Assert.Equal(1, reference["turns_since_full"]!.GetValue<long>());
        Assert.Equal(DiscordServerMapDisclosure.RefreshRoute,
            reference["refresh"]!.GetValue<string>());
        Assert.Null(reference["channels"]);
        Assert.Equal(1, plan.NextState!.TurnsSinceFull);
        // Everything outside server_map is untouched, and the caller's copy keeps the full map.
        Assert.Equal("active", plan.GuildContext["mode"]!.GetValue<string>());
        Assert.Equal("Owner", plan.GuildContext["members"]![0]!["display_name"]!.GetValue<string>());
        Assert.NotNull(context["server_map"]!["channels"]);
    }

    [Fact]
    public void Changed_map_is_sent_in_full_again()
    {
        var first = DiscordServerMapDisclosure.Plan(Binding(), Context(Map("general")));

        var plan = DiscordServerMapDisclosure.Plan(
            Binding() with { ServerMapDisclosure = first.NextState with { TurnsSinceFull = 3 } },
            Context(Map("renamed-general")));

        var map = plan.GuildContext!["server_map"]!.AsObject();
        Assert.Equal("renamed-general", map["channels"]![0]!["name"]!.GetValue<string>());
        Assert.NotEqual(first.NextState!.Hash, plan.NextState!.Hash);
        Assert.Equal(0, plan.NextState.TurnsSinceFull);
    }

    [Fact]
    public void Full_map_is_refreshed_after_the_turn_budget()
    {
        var state = DiscordServerMapDisclosure.Plan(Binding(), Context(Map("general"))).NextState!;
        for (var turn = 1; turn < DiscordServerMapDisclosure.RefreshTurns; turn++)
        {
            var plan = DiscordServerMapDisclosure.Plan(
                Binding() with { ServerMapDisclosure = state }, Context(Map("general")));
            Assert.True(plan.GuildContext!["server_map"]!["unchanged"]!.GetValue<bool>());
            state = plan.NextState!;
            Assert.Equal(turn, state.TurnsSinceFull);
        }

        var refreshed = DiscordServerMapDisclosure.Plan(
            Binding() with { ServerMapDisclosure = state }, Context(Map("general")));

        Assert.NotNull(refreshed.GuildContext!["server_map"]!["channels"]);
        Assert.Equal(0, refreshed.NextState!.TurnsSinceFull);
    }

    [Fact]
    public void New_generation_or_session_resets_the_disclosure()
    {
        var state = DiscordServerMapDisclosure.Plan(Binding(), Context(Map("general"))).NextState!;

        var cleared = DiscordServerMapDisclosure.Plan(
            Binding() with { Generation = 2, ServerMapDisclosure = state },
            Context(Map("general")));
        var reopened = DiscordServerMapDisclosure.Plan(
            Binding() with { SessionId = "session-2", ServerMapDisclosure = state },
            Context(Map("general")));

        Assert.NotNull(cleared.GuildContext!["server_map"]!["channels"]);
        Assert.Equal(2, cleared.NextState!.Generation);
        Assert.NotNull(reopened.GuildContext!["server_map"]!["channels"]);
        Assert.Equal("session-2", reopened.NextState!.SessionId);
    }

    [Fact]
    public void Hash_ignores_capture_time_and_key_order()
    {
        var map = Map("general");
        var reordered = new JsonObject(map.Reverse()
            .Select(item => KeyValuePair.Create(item.Key, item.Value?.DeepClone())));
        reordered["captured_at"] = CapturedAt.AddHours(1).ToString("O");

        Assert.Equal(DiscordServerMapDisclosure.ContentHash(map),
            DiscordServerMapDisclosure.ContentHash(reordered));
        Assert.NotEqual(DiscordServerMapDisclosure.ContentHash(map),
            DiscordServerMapDisclosure.ContentHash(Map("general", topic: "new topic")));
    }

    [Fact]
    public void Context_without_a_server_map_passes_through_without_state()
    {
        var context = new JsonObject { ["schema"] = "leaf-discord-guild-context/v1" };

        var plan = DiscordServerMapDisclosure.Plan(Binding(), context);
        var none = DiscordServerMapDisclosure.Plan(Binding(), null);

        Assert.Same(context, plan.GuildContext);
        Assert.Null(plan.NextState);
        Assert.Null(none.GuildContext);
        Assert.Null(none.NextState);
    }

    [Fact]
    public void Disclosure_state_round_trips_through_entity_json()
    {
        var state = new DiscordServerMapDisclosureState(3, "session-1", "abcdef0123456789", 7);

        Assert.Equal(state, DiscordServerMapDisclosure.FromJson(
            DiscordServerMapDisclosure.ToJson(state)));
        Assert.Null(DiscordServerMapDisclosure.FromJson(null));
        Assert.Null(DiscordServerMapDisclosure.FromJson(new JsonObject()));
    }

    internal static JsonObject Map(
        string channelName, DateTimeOffset? capturedAt = null, string? topic = null)
        => DiscordGuildNavigationBuilder.Build(
            "guild-1", "Leaf", "channel-1",
            [new DiscordGuildCategorySnapshot("category-1", "general", 1)],
            [new DiscordGuildChannelSnapshot(
                "channel-1", channelName, "text", "category-1", topic, 1,
                true, true, true, [])],
            capturedAt ?? CapturedAt);

    internal static JsonObject Context(JsonObject serverMap)
        => DiscordGuildContextBuilder.Build(
            "active", "guild-1", "Leaf", "channel-1", "general",
            [new DiscordGuildContextMember("42", "Owner", "online", [], null, false)],
            true, CapturedAt, serverMap)!;

    private static DiscordBinding Binding() => new(
        Guid.NewGuid(), "discord:app:channel-1", "app", "channel-1", "guild_channel",
        "guild-1", null, "nova", "owner-1", "nova-discord-external", "participate",
        1, "conversation-1", "session-1", null, 0, null, null, true, "general", "Leaf");
}
