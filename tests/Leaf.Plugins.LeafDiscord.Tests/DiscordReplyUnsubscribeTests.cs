using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// A private discussion can stop the Discord reply notices a send subscribed it to; ending the
/// subscription of the discussion that leads the conversation also ends that lead.
/// </summary>
public sealed class DiscordReplyUnsubscribeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private const string AgentId = "agent-entity-1";

    private static readonly LeafAgentCaller D1 = new(AgentId, "owner-1") { DiscussionId = "d1" };
    private static readonly LeafAgentCaller D2 = new(AgentId, "owner-1") { DiscussionId = "d2" };

    private sealed class Fixture
    {
        public MemoryEntityStore Entities { get; } = new();
        public DiscordReplySubscriptionStore Subscriptions { get; }
        public DiscordConversationLeadStore Leads { get; }
        public DiscordBinding Binding { get; } = new(
            Guid.NewGuid(), "discord:app-1:conversation-1", "app-1", "conversation-1", "guild_channel",
            "guild-1", null, "nova", "owner-1", "nova-discord-external",
            "direct", 1, "handle-1", "session-1", null, 0, null, null, true, "Room", null);

        public Fixture()
        {
            Subscriptions = new DiscordReplySubscriptionStore(Entities);
            Leads = new DiscordConversationLeadStore(Entities);
        }

        public Task SubscribeAsync(string discussionId)
            => Subscriptions.SubscribeAsync(Binding, discussionId, "owner-1", AgentId, "m1", Now);

        public Task<IResult> UnsubscribeAsync(
            LeafAgentCaller? caller, bool admin = false, string? discussionId = null)
            => DiscordEndpoints.UnsubscribeRepliesAsync(
                caller, admin, Binding, AgentId, discussionId, Subscriptions, Leads, Now, default);

        public Task<IResult> ReadAsync(
            LeafAgentCaller? caller, bool admin = false, string? discussionId = null)
            => DiscordEndpoints.ReadSubscriptionAsync(
                caller, admin, Binding, AgentId, discussionId, Subscriptions, Now, default);

        public async Task<bool> IsSubscribedAsync(string discussionId)
            => (await Subscriptions.ActiveAsync(Binding.BindingId, Now))
                .Any(item => item.DiscussionId == discussionId);
    }

    private static int Status(IResult result)
        => ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;

    private static JsonNode Body(IResult result)
        => JsonSerializer.SerializeToNode(((IValueHttpResult)result).Value)!;

    [Fact]
    public async Task A_discussion_removes_its_own_subscription_and_only_its_own()
    {
        var f = new Fixture();
        await f.SubscribeAsync("d1");
        await f.SubscribeAsync("d2");

        var result = await f.UnsubscribeAsync(D1);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.True(Body(result)["ok"]!.GetValue<bool>());
        Assert.Equal(1, Body(result)["removed"]!.GetValue<int>());
        Assert.False(await f.IsSubscribedAsync("d1"));
        Assert.True(await f.IsSubscribedAsync("d2"));
    }

    [Fact]
    public async Task An_agent_discussion_cannot_remove_another_discussions_subscription()
    {
        var f = new Fixture();
        await f.SubscribeAsync("d2");

        var result = await f.UnsubscribeAsync(D1, discussionId: "d2");

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.True(await f.IsSubscribedAsync("d2"));
        // Naming itself in the query is harmless; the signed identity is what counts.
        Assert.Equal(StatusCodes.Status200OK, Status(await f.UnsubscribeAsync(D2, discussionId: "d2")));
        Assert.False(await f.IsSubscribedAsync("d2"));
    }

    [Fact]
    public async Task An_agent_that_cannot_address_the_binding_or_has_no_discussion_is_refused()
    {
        var f = new Fixture();
        await f.SubscribeAsync("d1");

        var stranger = new LeafAgentCaller(AgentId, "someone-else") { DiscussionId = "d1" };
        var automation = new LeafAgentCaller(AgentId, "owner-1", "automation-1");

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await f.UnsubscribeAsync(stranger)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await f.UnsubscribeAsync(automation)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await f.UnsubscribeAsync(null)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await f.ReadAsync(null)));
        Assert.True(await f.IsSubscribedAsync("d1"));
    }

    [Fact]
    public async Task An_owner_removes_another_discussions_subscription_with_discussion_id()
    {
        var f = new Fixture();
        await f.SubscribeAsync("d1");
        await f.SubscribeAsync("d2");

        Assert.Equal(StatusCodes.Status400BadRequest, Status(await f.UnsubscribeAsync(null, admin: true)));
        var result = await f.UnsubscribeAsync(null, admin: true, discussionId: "d2");

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal(1, Body(result)["removed"]!.GetValue<int>());
        Assert.False(await f.IsSubscribedAsync("d2"));
        Assert.True(await f.IsSubscribedAsync("d1"));
    }

    [Fact]
    public async Task A_second_unsubscribe_is_idempotent_and_removes_nothing()
    {
        var f = new Fixture();
        await f.SubscribeAsync("d1");

        Assert.Equal(1, Body(await f.UnsubscribeAsync(D1))["removed"]!.GetValue<int>());
        var again = await f.UnsubscribeAsync(D1);

        Assert.Equal(StatusCodes.Status200OK, Status(again));
        Assert.True(Body(again)["ok"]!.GetValue<bool>());
        Assert.Equal(0, Body(again)["removed"]!.GetValue<int>());
    }

    [Fact]
    public async Task Unsubscribing_the_leading_discussion_ends_its_lead()
    {
        var f = new Fixture();
        await f.SubscribeAsync("d1");
        await f.Leads.ClaimAsync(f.Binding, "d1", "owner-1", AgentId, TimeSpan.FromMinutes(30), Now);
        Assert.True((await f.Leads.GetAsync(f.Binding))!.IsActive(Now));

        var result = await f.UnsubscribeAsync(D1);

        Assert.True(Body(result)["leadEnded"]!.GetValue<bool>());
        var lead = (await f.Leads.GetAsync(f.Binding))!;
        Assert.False(lead.IsActive(Now));
        Assert.Equal("unsubscribed", lead.ReleaseReason);
    }

    [Fact]
    public async Task Unsubscribing_a_discussion_that_does_not_lead_leaves_the_lead_alone()
    {
        var f = new Fixture();
        await f.SubscribeAsync("d1");
        await f.SubscribeAsync("d2");
        await f.Leads.ClaimAsync(f.Binding, "d1", "owner-1", AgentId, TimeSpan.FromMinutes(30), Now);

        var result = await f.UnsubscribeAsync(D2);

        Assert.False(Body(result)["leadEnded"]!.GetValue<bool>());
        Assert.True((await f.Leads.GetAsync(f.Binding))!.IsActive(Now));
        Assert.True(await f.IsSubscribedAsync("d1"));
    }

    [Fact]
    public async Task No_notice_is_published_after_the_subscription_is_removed()
    {
        var f = new Fixture();
        var events = new RecordingEvents();
        var notifier = new DiscordReplyNotifier(
            f.Subscriptions, events, new PromptInjectionDetector(),
            NullLogger<DiscordReplyNotifier>.Instance);
        await f.SubscribeAsync("d1");
        var requestor = new ExternalRequestor("700000000000000007", "Alice", "alice");

        Assert.Equal(1, await notifier.NotifyAsync(
            f.Binding, "1", requestor, "hello", 0, "a1", true, false, Now.AddMinutes(1)));
        await f.UnsubscribeAsync(D1);
        Assert.Equal(0, await notifier.NotifyAsync(
            f.Binding, "2", requestor, "hello again", 0, "a2", true, false, Now.AddMinutes(2)));

        Assert.Single(events.Published);
    }

    [Fact]
    public async Task Reading_reports_whether_the_calling_discussion_is_subscribed_and_until_when()
    {
        var f = new Fixture();
        var before = await f.ReadAsync(D1);
        Assert.False(Body(before)["subscribed"]!.GetValue<bool>());
        Assert.Null(Body(before)["expiresAt"]);

        await f.SubscribeAsync("d1");
        var after = await f.ReadAsync(D1);
        Assert.True(Body(after)["subscribed"]!.GetValue<bool>());
        Assert.Equal(Now.Add(DiscordReplySubscriptionStore.Lifetime),
            Body(after)["expiresAt"]!.GetValue<DateTimeOffset>());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await f.ReadAsync(D2, discussionId: "d1")));
        Assert.True(Body(await f.ReadAsync(null, admin: true, discussionId: "d1"))["subscribed"]!
            .GetValue<bool>());
    }

    private sealed class RecordingEvents : IPluginEvents
    {
        public List<(string Type, JsonObject Payload)> Published { get; } = [];

        public Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default)
        {
            Published.Add((eventType, payload));
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler)
            => throw new NotSupportedException();
    }
}
