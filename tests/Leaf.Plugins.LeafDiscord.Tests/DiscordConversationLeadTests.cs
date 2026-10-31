using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// A private discussion that leads a Discord conversation through outreach is its only voice:
/// the binding's session still receives every message, in order and through the same inbound
/// review, but nothing it writes is posted until the lead ends. Then the session answers again,
/// told what the discussion said for it. Without a lead nothing changes.
/// </summary>
public sealed class DiscordConversationLeadTests
{
    private static readonly UntrustedContentVerdict Clean =
        new("none", [], "normal", true, UntrustedContentIsolation.VerifiedToolFree);

    private static readonly UntrustedContentVerdict Leak =
        new("high", ["personal-data"], "require_private_approval", true, UntrustedContentIsolation.VerifiedToolFree);

    private static readonly ExternalRequestor Tester = new("700000000000000007", "Tester", "tester");

    [Fact]
    public async Task While_the_discussion_leads_the_session_hears_everything_and_posts_nothing()
    {
        var h = await Harness.CreateAsync();
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));

        var owner = await h.Coordinator.SendExistingProactiveAsync(
            h.Binding, "approve-1", "Accord pour la correction: désactive la tâche planifiée.");
        await h.SubmitAsync("1001", "ok je lance");

        // The turn is the ordinary one, through the provider (Nova runs its inbound review there).
        var input = Assert.Single(h.Provider.Inputs);
        Assert.Equal("ok je lance", input.Content);
        Assert.Equal(Tester, input.Requestor);
        Assert.True(input.Metadata!["untrusted"]!.GetValue<bool>());
        var lead = input.Metadata["discord_conversation_lead"]!.AsObject();
        Assert.Equal(DiscordConversationLeadStore.ContextSchema, lead["schema"]!.GetValue<string>());
        Assert.Equal("trusted_bridge_state", lead["trust"]!.GetValue<string>());
        Assert.Equal("owner_leads", lead["state"]!.GetValue<string>());
        Assert.Equal("observe_only", lead["reply_policy"]!.GetValue<string>());
        // The session never sees a transcript injection, so the owner's message rides on its turn.
        var relayed = Assert.Single(lead["owner_messages"]!.AsArray())!.AsObject();
        Assert.Equal(owner.MessageUid, relayed["message_uid"]!.GetValue<string>());
        Assert.Equal("Accord pour la correction: désactive la tâche planifiée.",
            relayed["content"]!.GetValue<string>());

        // The session answers anyway ("Non, pas sous cette forme"), with a tool call on the way.
        h.Reply(1, owner.MessageUid, "Accord pour la correction: désactive la tâche planifiée.");
        h.Tool(2, "tool-1");
        h.Reply(3, "reply-1", "Non, pas sous cette forme.");
        await h.ReconcileAsync();

        Assert.Empty(await h.Rows("reply-1"));
        Assert.Empty(await h.Rows("tool-1"));
        // The owner's own message keeps its single reviewed outbox row, and only it was reviewed.
        Assert.Single(await h.Rows(owner.MessageUid));
        var review = Assert.Single(h.Sentinel.Requests);
        Assert.Equal(UntrustedContentDirection.Outbound, review.Direction);
        Assert.Contains("Accord pour la correction", review.Content, StringComparison.Ordinal);
        Assert.False(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));
        Assert.Single(h.Sentinel.Requests);

        var stored = (await h.Leads.GetAsync(h.Binding))!;
        Assert.Equal(["reply-1"], stored.UnpostedReplies);
        Assert.Empty(stored.OwnerMessages);
        Assert.Equal(["1001"], stored.LedTurns);
    }

    [Fact]
    public async Task The_leading_discussion_hears_the_reply_and_learns_it_leads()
    {
        var h = await Harness.CreateAsync();
        var subscriptions = new DiscordReplySubscriptionStore(h.Entities);
        await subscriptions.SubscribeAsync(h.Binding, "d1", "owner-1", "agent-1", "m1", DateTimeOffset.UtcNow);
        await subscriptions.SubscribeAsync(h.Binding, "d2", "owner-1", "agent-1", "m2", DateTimeOffset.UtcNow);
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);
        var events = new RecordingEvents();
        var notifier = new DiscordReplyNotifier(subscriptions, events, new PromptInjectionDetector(),
            NullLogger<DiscordReplyNotifier>.Instance);
        var admission = await h.SubmitAsync("1001", "2 / Smart Home");
        var sentAt = new DateTimeOffset(2026, 10, 29, 19, 22, 3, TimeSpan.Zero);

        // As the gateway ingress does it: after admission, with the lead as it stands.
        var published = await notifier.NotifyAsync(
            h.Binding, "1001", Tester, "2 / Smart Home", 0, admission!.MessageUid,
            false, false, DateTimeOffset.UtcNow, sentAt, await h.Coordinator.ActiveLeadAsync(h.Binding));

        Assert.Equal(2, published);
        var byDiscussion = events.Published.ToDictionary(
            item => item.Payload["discussion_id"]!.GetValue<string>(), item => item.Payload);
        var leader = byDiscussion["d1"]["conversation_lead"]!.AsObject();
        Assert.Equal("owner", leader["state"]!.GetValue<string>());
        Assert.True(leader["this_discussion_leads"]!.GetValue<bool>());
        Assert.Equal("d1", leader["lead_discussion_id"]!.GetValue<string>());
        Assert.False(byDiscussion["d2"]["conversation_lead"]!["this_discussion_leads"]!.GetValue<bool>());
        Assert.Equal("2026-10-29 19:22:03 UTC", byDiscussion["d1"]["discord_sent_at_label"]!.GetValue<string>());
        Assert.DoesNotContain("Smart Home", events.Published[0].Payload.ToJsonString());
    }

    [Fact]
    public async Task After_the_window_the_session_answers_again_with_what_it_missed()
    {
        var h = await Harness.CreateAsync();
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));
        var first = await h.Coordinator.SendExistingProactiveAsync(h.Binding, "approve-1", "Accord pour la correction.");
        await h.SubmitAsync("1001", "ok je lance");
        h.Reply(1, "reply-1", "Non, pas sous cette forme.");
        await h.ReconcileAsync();
        // Said after the session's last turn: it has to hear this one at hand-back.
        var second = await h.Coordinator.SendExistingProactiveAsync(h.Binding, "rerun-1", "Relance le launcher une fois.");

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(DiscordConversationLeadStore.HandingBackState,
            DiscordConversationLeadStore.Project(await h.Leads.GetAsync(h.Binding), h.Clock.GetUtcNow()).State);
        // Sent after the lead ended: ordinary outreach, which the session must still hear.
        var late = await h.Coordinator.SendExistingProactiveAsync(h.Binding, "late-1", "Merci !");
        await h.SubmitAsync("1002", "c'est bon, ça marche");

        var handback = h.Provider.Inputs[^1].Metadata!["discord_conversation_lead"]!.AsObject();
        Assert.Equal("handed_back", handback["state"]!.GetValue<string>());
        Assert.Equal("answer", handback["reply_policy"]!.GetValue<string>());
        Assert.Equal("expired", handback["end_reason"]!.GetValue<string>());
        Assert.Equal([second.MessageUid, late.MessageUid], handback["owner_messages"]!.AsArray()
            .Select(item => item!["message_uid"]!.GetValue<string>()));
        Assert.Equal(["reply-1"], handback["unposted_replies"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.DoesNotContain(first.MessageUid, handback.ToJsonString());

        h.Reply(2, "reply-2", "Parfait, content que ça marche.");
        await h.ReconcileAsync();
        Assert.Equal(DiscordDeliveryStore.ReviewingState, Assert.Single(await h.Rows("reply-2")).State);
        Assert.True(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));
        Assert.Equal("queued", Assert.Single(await h.Rows("reply-2")).State);

        // Handed back: later turns carry nothing about the lead, exactly as before it.
        await h.SubmitAsync("1003", "merci");
        Assert.Null(h.Provider.Inputs[^1].Metadata!["discord_conversation_lead"]);
        Assert.Equal(DiscordConversationLeadStore.SessionState,
            DiscordConversationLeadStore.Project(await h.Leads.GetAsync(h.Binding), h.Clock.GetUtcNow()).State);
    }

    [Fact]
    public async Task A_reply_to_a_led_turn_stays_unposted_even_when_it_settles_after_the_lead()
    {
        var h = await Harness.CreateAsync();
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));
        await h.SubmitAsync("1001", "ok je lance");
        await h.Leads.ReleaseAsync(h.Binding, "released", h.Clock.GetUtcNow());

        h.Reply(1, "reply-1", "Non, pas sous cette forme.");
        await h.ReconcileAsync();

        Assert.Empty(await h.Rows("reply-1"));
        Assert.Equal(["reply-1"], (await h.Leads.GetAsync(h.Binding))!.UnpostedReplies);
    }

    [Fact]
    public async Task A_lead_taken_mid_turn_holds_the_reply_that_turn_was_writing()
    {
        var h = await Harness.CreateAsync();
        await h.SubmitAsync("1001", "rapport #2");
        // The owner approves a plan while the session is still answering report #2.
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));
        await h.Coordinator.SendExistingProactiveAsync(h.Binding, "approve-1", "Accord pour la correction.");
        h.Reply(1, "reply-1", "Non, pas sous cette forme.");

        await h.ReconcileAsync();

        Assert.Empty(await h.Rows("reply-1"));
    }

    [Fact]
    public async Task Outside_a_lead_the_next_turn_carries_the_owner_messages_and_replies_are_unchanged()
    {
        var h = await Harness.CreateAsync();
        // No outreach yet: the turn is exactly what it was before conversation leads existed.
        await h.SubmitAsync("1000", "salut");
        Assert.DoesNotContain(h.Provider.Inputs[^1].Metadata!, pair => pair.Key == "discord_conversation_lead");

        // 28 Oct: the owner approves a plan from a private discussion, without leading.
        var approve = await h.Coordinator.SendExistingProactiveAsync(
            h.Binding, "approve-1", "Accord pour la correction: désactive la tâche planifiée.");
        var steps = await h.Coordinator.SendExistingProactiveAsync(
            h.Binding, "steps-1", "Ensuite lance le launcher une fois.");
        Assert.Equal(DiscordConversationLeadStore.SessionState,
            DiscordConversationLeadStore.Project(await h.Leads.GetAsync(h.Binding), h.Clock.GetUtcNow()).State);
        Assert.Equal(2, DiscordConversationLeadStore.Project(
            await h.Leads.GetAsync(h.Binding), h.Clock.GetUtcNow()).PendingOwnerMessages);
        await h.SubmitAsync("1001", "ok je lance");

        var context = h.Provider.Inputs[^1].Metadata!["discord_conversation_lead"]!.AsObject();
        Assert.Equal("trusted_bridge_state", context["trust"]!.GetValue<string>());
        Assert.Equal("session_leads", context["state"]!.GetValue<string>());
        Assert.Equal("answer", context["reply_policy"]!.GetValue<string>());
        Assert.Null(context["lead_started_at"]);
        Assert.Null(context["lead_expires_at"]);
        Assert.Null(context["end_reason"]);
        Assert.Empty(context["unposted_replies"]!.AsArray());
        var relayed = context["owner_messages"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        Assert.Equal([approve.MessageUid, steps.MessageUid],
            relayed.Select(item => item["message_uid"]!.GetValue<string>()));
        Assert.Equal("Accord pour la correction: désactive la tâche planifiée.",
            relayed[0]["content"]!.GetValue<string>());
        // The participant's message is unchanged and still untrusted.
        Assert.Equal("ok je lance", h.Provider.Inputs[^1].Content);
        Assert.True(h.Provider.Inputs[^1].Metadata!["untrusted"]!.GetValue<bool>());

        // Posting is unchanged: the session's reply goes through its outbound review and out;
        // the owner's transcript copies keep their one reviewed row each.
        h.Reply(1, approve.MessageUid, "Accord pour la correction: désactive la tâche planifiée.");
        h.Reply(2, steps.MessageUid, "Ensuite lance le launcher une fois.");
        h.Tool(3, "tool-1");
        h.Reply(4, "reply-1", "Parfait, je te laisse faire.");
        await h.ReconcileAsync();
        Assert.Equal(DiscordDeliveryStore.ReviewingState, Assert.Single(await h.Rows("reply-1")).State);
        Assert.NotEmpty(await h.Rows("tool-1"));
        Assert.Single(await h.Rows(approve.MessageUid));
        Assert.Single(await h.Rows(steps.MessageUid));
        Assert.True(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));
        Assert.Equal("queued", Assert.Single(await h.Rows("reply-1")).State);
        Assert.Contains(h.Sentinel.Requests, request => request.Content == "Parfait, je te laisse faire.");
        Assert.Empty((await h.Leads.GetAsync(h.Binding))!.UnpostedReplies);
        Assert.Null(await h.Coordinator.ActiveLeadAsync(h.Binding));
        Assert.Null(await DiscordEndpoints.RejectWhileDiscussionLeadsAsync(
            h.Binding, h.Leads, h.Clock.GetUtcNow(), default));

        // Relayed once: the following turn carries nothing.
        await h.SubmitAsync("1002", "merci");
        Assert.DoesNotContain(h.Provider.Inputs[^1].Metadata!, pair => pair.Key == "discord_conversation_lead");
        var status = DiscordConversationLeadStore.Project(await h.Leads.GetAsync(h.Binding), h.Clock.GetUtcNow());
        Assert.Equal(DiscordConversationLeadStore.SessionState, status.State);
        Assert.Equal(0, status.PendingOwnerMessages);
        Assert.Null(status.LeadDiscussionId);

        var subscriptions = new DiscordReplySubscriptionStore(h.Entities);
        await subscriptions.SubscribeAsync(h.Binding, "d1", "owner-1", "agent-1", "m1", DateTimeOffset.UtcNow);
        var events = new RecordingEvents();
        await new DiscordReplyNotifier(subscriptions, events, new PromptInjectionDetector(),
                NullLogger<DiscordReplyNotifier>.Instance)
            .NotifyAsync(h.Binding, "1001", Tester, "salut", 0, "uid-1001", false, false, DateTimeOffset.UtcNow);
        Assert.Equal("session", Assert.Single(events.Published).Payload["conversation_lead"]!["state"]!
            .GetValue<string>());
    }

    [Fact]
    public async Task Outside_a_lead_owner_messages_wait_for_a_turn_the_inbound_sentinel_admits()
    {
        var h = await Harness.CreateAsync();
        var owner = await h.Coordinator.SendExistingProactiveAsync(h.Binding, "approve-1", "Accord.");
        h.Provider.Refuse = true;

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable,
            (await h.SubmitAsync("1001", "ok"))!.Disposition);
        Assert.Equal([owner.MessageUid],
            (await h.Leads.GetAsync(h.Binding))!.OwnerMessages.Select(item => item.MessageUid));

        h.Provider.Refuse = false;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Coordinator.RetryPendingAsync(h.Binding);
        var context = h.Provider.Inputs[^1].Metadata!["discord_conversation_lead"]!;
        Assert.Equal("session_leads", context["state"]!.GetValue<string>());
        Assert.Equal(owner.MessageUid, context["owner_messages"]![0]!["message_uid"]!.GetValue<string>());
        Assert.Empty((await h.Leads.GetAsync(h.Binding))!.OwnerMessages);
    }

    [Fact]
    public async Task Owner_messages_sent_before_a_lead_ride_on_its_first_observed_turn()
    {
        var h = await Harness.CreateAsync();
        var before = await h.Coordinator.SendExistingProactiveAsync(h.Binding, "hello-1", "Bonjour !");
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));
        var during = await h.Coordinator.SendExistingProactiveAsync(h.Binding, "approve-1", "Accord.");

        await h.SubmitAsync("1001", "ok");

        var context = h.Provider.Inputs[^1].Metadata!["discord_conversation_lead"]!;
        Assert.Equal("owner_leads", context["state"]!.GetValue<string>());
        Assert.Equal([before.MessageUid, during.MessageUid], context["owner_messages"]!.AsArray()
            .Select(item => item!["message_uid"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_lead_lasts_an_hour_by_default_and_at_most_the_reply_callback_window()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), DiscordConversationLeadStore.DefaultWindow);
        Assert.Equal(DiscordReplySubscriptionStore.Lifetime, DiscordConversationLeadStore.MaxWindow);
        var h = await Harness.CreateAsync();
        var now = h.Clock.GetUtcNow();

        var (lead, _) = await h.Leads.ClaimAsync(h.Binding, "d1", "owner-1", "agent-1",
            DiscordConversationLeadStore.DefaultWindow, now);
        Assert.Equal(now.AddHours(1), lead.ExpiresAt);
        // Each leader message renews the hour from that message.
        var renewed = await h.Leads.RenewAsync(h.Binding, "d1", null, now.AddMinutes(50));
        Assert.Equal(now.AddMinutes(110), renewed!.ExpiresAt);
        Assert.False(renewed.IsActive(now.AddMinutes(111)));

        // An explicit window up to the maximum still works; beyond it is refused.
        var (longest, _) = await h.Leads.ClaimAsync(h.Binding, "d1", "owner-1", "agent-1",
            DiscordConversationLeadStore.MaxWindow, now.AddMinutes(200));
        Assert.Equal(now.AddMinutes(200).AddHours(48), longest.ExpiresAt);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Leads.ClaimAsync(
            h.Binding, "d1", "owner-1", "agent-1",
            DiscordConversationLeadStore.MaxWindow + TimeSpan.FromMinutes(1), now));
    }

    [Fact]
    public async Task The_inbound_sentinel_still_gates_a_led_turn_and_what_it_carried_waits_for_the_retry()
    {
        var h = await Harness.CreateAsync();
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));
        var owner = await h.Coordinator.SendExistingProactiveAsync(h.Binding, "approve-1", "Accord.");
        h.Provider.Refuse = true;

        var refused = await h.SubmitAsync("1001", "ok");

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, refused!.Disposition);
        Assert.Single(await h.Deliveries.PendingInboundAsync(h.Binding.BindingId));
        // The same static notice as without a lead: the bridge, not the session, says it.
        var notice = Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId),
            item => item.MessageUid != owner.MessageUid);
        Assert.Equal(DiscordConversationCoordinator.InboundUnavailableNotice,
            notice.Payload?["content"]?.GetValue<string>());
        var stored = (await h.Leads.GetAsync(h.Binding))!;
        Assert.Equal([owner.MessageUid], stored.OwnerMessages.Select(item => item.MessageUid));
        Assert.Empty(stored.LedTurns);

        h.Provider.Refuse = false;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Coordinator.RetryPendingAsync(h.Binding);
        Assert.Equal(owner.MessageUid, h.Provider.Inputs[^1].Metadata!["discord_conversation_lead"]!
            ["owner_messages"]![0]!["message_uid"]!.GetValue<string>());
        Assert.Empty((await h.Leads.GetAsync(h.Binding))!.OwnerMessages);
    }

    [Fact]
    public async Task An_owner_message_the_outbound_sentinel_holds_is_neither_sent_nor_relayed()
    {
        var h = await Harness.CreateAsync();
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));
        h.Sentinel.Decide = _ => Leak;

        await Assert.ThrowsAsync<DiscordSentinelRefusedException>(() =>
            h.Coordinator.SendExistingProactiveAsync(h.Binding, "leak-1", "Il habite au 14 rue des Lilas."));

        Assert.Empty(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.Empty((await h.Leads.GetAsync(h.Binding))!.OwnerMessages);
    }

    [Fact]
    public async Task One_discussion_leads_at_a_time_and_a_failed_claim_is_put_back()
    {
        var h = await Harness.CreateAsync();
        var now = h.Clock.GetUtcNow();
        var (first, previous) = await h.Leads.ClaimAsync(
            h.Binding, "d1", "owner-1", "agent-1", TimeSpan.FromMinutes(30), now);
        Assert.Null(previous);

        var conflict = await Assert.ThrowsAsync<DiscordConversationLedElsewhereException>(() =>
            h.Leads.ClaimAsync(h.Binding, "d2", "owner-1", "agent-1", TimeSpan.FromMinutes(30), now));
        Assert.Equal("d1", conflict.Lead.DiscussionId);

        // Each owner message from the leader moves the window; another discussion's does not.
        Assert.Null(await h.Leads.RenewAsync(h.Binding, "d2", null, now.AddMinutes(20)));
        var renewed = await h.Leads.RenewAsync(h.Binding, "d1", null, now.AddMinutes(20));
        Assert.Equal(now.AddMinutes(50), renewed!.ExpiresAt);
        Assert.Equal(first.StartedAt, renewed.StartedAt);

        // Once expired, another discussion may take it.
        var (taken, before) = await h.Leads.ClaimAsync(
            h.Binding, "d2", "owner-1", "agent-1", TimeSpan.FromMinutes(10), now.AddMinutes(51));
        Assert.Equal("d2", taken.DiscussionId);
        await h.Leads.RevertClaimAsync(h.Binding, before);
        Assert.Equal("d1", (await h.Leads.GetAsync(h.Binding))!.DiscussionId);

        // A first claim that is reverted leaves no row at all.
        var other = await Harness.CreateAsync(conversationId: "423456789012345678");
        var (_, none) = await other.Leads.ClaimAsync(
            other.Binding, "d1", "owner-1", "agent-1", TimeSpan.FromMinutes(5), now);
        await other.Leads.RevertClaimAsync(other.Binding, none);
        Assert.Null(await other.Leads.GetAsync(other.Binding));
    }

    [Fact]
    public async Task A_cleared_binding_does_not_inherit_the_lead()
    {
        var h = await Harness.CreateAsync();
        await new DiscordBotConfigurationStore(new LeafDiscordConfigStore(h.Entities)).CreateAsync(
            new LeafDiscordConfigUpdate("100000000000000001", new string('a', 64), null, false, false,
                [], [], 10 * 1024 * 1024, "nova", "deep-codex"),
            ownerUserId: "owner-1");
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));

        var cleared = await h.Coordinator.ClearAsync(h.Binding, "clear-1");

        Assert.NotEqual(h.Binding.Generation, cleared.Generation);

        Assert.Null(await h.Leads.GetAsync(cleared));
        Assert.Null(await h.Coordinator.ActiveLeadAsync(cleared));
    }

    [Fact]
    public async Task Owner_messages_kept_for_the_session_are_bounded()
    {
        var h = await Harness.CreateAsync();
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));
        var now = h.Clock.GetUtcNow();
        for (var i = 0; i < DiscordConversationLeadStore.MaxOwnerMessages + 3; i++)
            await h.Leads.RecordOwnerMessageAsync(h.Binding, $"m{i}", new string('x', 5000), now);

        var lead = (await h.Leads.GetAsync(h.Binding))!;
        Assert.Equal(DiscordConversationLeadStore.MaxOwnerMessages, lead.OwnerMessages.Count);
        Assert.Equal(3, lead.OmittedOwnerMessages);
        Assert.Equal("m3", lead.OwnerMessages[0].MessageUid);
        Assert.All(lead.OwnerMessages, item =>
            Assert.Equal(DiscordConversationLeadStore.MaxOwnerMessageLength, item.Content.Length));
        Assert.Equal(3, DiscordConversationLeadStore.SessionContext(lead, now)!["omitted_owner_messages"]!
            .GetValue<int>());
    }

    [Fact]
    public async Task Status_shows_who_answers_for_the_owner()
    {
        var h = await Harness.CreateAsync();
        var now = h.Clock.GetUtcNow();
        await h.ClaimAsync("d1", TimeSpan.FromMinutes(30));

        var leading = DiscordConversationLeadStore.Project(await h.Leads.GetAsync(h.Binding), now);
        Assert.Equal("owner", leading.State);
        Assert.Equal("d1", leading.LeadDiscussionId);
        Assert.Equal(now.AddMinutes(30), leading.ExpiresAt);
        var json = JsonSerializer.SerializeToNode(h.Binding with { ConversationLead = leading },
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("owner", json["conversationLead"]!["state"]!.GetValue<string>());

        await h.Leads.ReleaseAsync(h.Binding, "released", now.AddMinutes(5));
        var released = DiscordConversationLeadStore.Project(await h.Leads.GetAsync(h.Binding), now.AddMinutes(6));
        Assert.Equal("handing_back", released.State);
        Assert.Equal("released", released.EndReason);
        Assert.Null(released.ExpiresAt);
        Assert.Equal(now.AddMinutes(5), released.EndedAt);
    }

    [Fact]
    public void Only_a_signed_discussion_that_hears_the_replies_can_lead()
    {
        var discussion = new LeafAgentCaller("agent-1", "owner-1") { DiscussionId = "d1" };
        var automation = new LeafAgentCaller("agent-1", "owner-1", "automation-1");

        Assert.Null(DiscordEndpoints.ValidateLeadRequest(Request(lead: true), discussion));
        Assert.Null(DiscordEndpoints.ValidateLeadRequest(Request(), automation));
        Assert.Null(DiscordEndpoints.ValidateLeadRequest(Request(lead: true, minutes: 2880), discussion));
        Assert.Equal("lead_requires_discussion",
            Error(DiscordEndpoints.ValidateLeadRequest(Request(lead: true), automation)));
        Assert.Equal("lead_requires_reply_callback",
            Error(DiscordEndpoints.ValidateLeadRequest(Request(lead: true, notify: false), discussion)));
        Assert.Equal("invalid_lead_window",
            Error(DiscordEndpoints.ValidateLeadRequest(Request(lead: true, minutes: 0), discussion)));
        Assert.Equal("invalid_lead_window",
            Error(DiscordEndpoints.ValidateLeadRequest(Request(lead: true, minutes: 2881), discussion)));
        Assert.Equal("invalid_lead_window",
            Error(DiscordEndpoints.ValidateLeadRequest(Request(minutes: 10), discussion)));
    }

    [Fact]
    public async Task The_leader_renews_with_each_message_and_hands_back_with_lead_false()
    {
        var h = await Harness.CreateAsync();
        var leader = new LeafAgentCaller("agent-1", "owner-1") { DiscussionId = "d1" };
        var other = new LeafAgentCaller("agent-1", "owner-1") { DiscussionId = "d2" };
        var subscription = new DiscordReplySubscription(Guid.NewGuid(), h.Binding.BindingId,
            h.Binding.EntityId, "d1", "owner-1", "agent-1", null, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(48));
        await h.Leads.ClaimAsync(h.Binding, "d1", "owner-1", "agent-1",
            TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow.AddMinutes(-5));

        // Without the flag, a leader message still renews: "each owner message renews it".
        var renewed = await DiscordEndpoints.SettleLeadAsync(
            Request(), leader, h.Binding, subscription, h.Leads, NullLogger.Instance, default);
        Assert.Equal("owner", renewed!.State);
        Assert.True(renewed.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(9));

        // Another discussion's plain message leaves the lead alone.
        var untouched = await DiscordEndpoints.SettleLeadAsync(
            Request(lead: false), other, h.Binding, null, h.Leads, NullLogger.Instance, default);
        Assert.Equal("owner", untouched!.State);

        var handedBack = await DiscordEndpoints.SettleLeadAsync(
            Request(lead: false), leader, h.Binding, subscription, h.Leads, NullLogger.Instance, default);
        Assert.Equal("handing_back", handedBack!.State);
        Assert.Equal("released", handedBack.EndReason);
    }

    [Fact]
    public async Task A_lead_whose_discussion_cannot_hear_the_replies_is_released_at_once()
    {
        var h = await Harness.CreateAsync();
        var leader = new LeafAgentCaller("agent-1", "owner-1") { DiscussionId = "d1" };
        await h.Leads.ClaimAsync(h.Binding, "d1", "owner-1", "agent-1",
            TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);

        var settled = await DiscordEndpoints.SettleLeadAsync(
            Request(lead: true), leader, h.Binding, replyCallback: null, h.Leads, NullLogger.Instance, default);

        Assert.Equal("handing_back", settled!.State);
        Assert.Equal("reply_callback_unavailable", settled.EndReason);
    }

    [Fact]
    public async Task The_session_cannot_react_while_a_discussion_leads()
    {
        var h = await Harness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(await DiscordEndpoints.RejectWhileDiscussionLeadsAsync(h.Binding, h.Leads, now, default));

        await h.Leads.ClaimAsync(h.Binding, "d1", "owner-1", "agent-1", TimeSpan.FromMinutes(10), now);
        var rejected = await DiscordEndpoints.RejectWhileDiscussionLeadsAsync(h.Binding, h.Leads, now, default);

        Assert.Equal(StatusCodes.Status409Conflict, (rejected as IStatusCodeHttpResult)?.StatusCode);
        Assert.Null(await DiscordEndpoints.RejectWhileDiscussionLeadsAsync(
            h.Binding, h.Leads, now.AddMinutes(11), default));
    }

    [Fact]
    public async Task The_transcript_marks_replies_the_lead_kept_off_discord()
    {
        var messages = new[]
        {
            new ExternalConversationMessage(new ExternalConversationCursor("e", 1), "reply-1",
                "assistant", "text", "Non, pas sous cette forme.", DateTimeOffset.UtcNow),
            new ExternalConversationMessage(new ExternalConversationCursor("e", 2), "reply-2",
                "assistant", "text", "Parfait.", DateTimeOffset.UtcNow),
        };

        var projected = await DiscordTranscriptProjection.ProjectAsync(
            messages, (_, _) => Task.FromResult<DiscordUserProfile?>(null),
            unpostedReplies: new HashSet<string>(["reply-1"]));

        var unposted = projected[0].Metadata!["discord"]!["conversationLead"]!;
        Assert.False(unposted["posted"]!.GetValue<bool>());
        Assert.Equal("owner_discussion_led", unposted["reason"]!.GetValue<string>());
        Assert.Null(projected[1].Metadata?["discord"]);
    }

    private static readonly LeafAgentCaller Leader =
        new("agent-1", "owner-1") { DiscussionId = "d1" };

    private static LeafDiscordConfig BotConfig(params string[] dmAllowlist)
        => new("100000000000000001", new string('a', 64), "token", true, "nova", "deep-codex",
            "nova-discord-external", "avatar", new HashSet<string>(dmAllowlist),
            new HashSet<string>(), 1024);

    private static Task<IResult> ClaimLeadAsync(
        Harness h, LeafAgentCaller caller, int? minutes = null,
        LeafDiscordConfig? config = null, DiscordBinding? binding = null,
        DiscordReplySubscriptionStore? subscriptions = null, string agentEntityId = "agent-1")
        => DiscordEndpoints.ClaimLeadAsync(
            caller, binding ?? h.Binding, agentEntityId, config ?? BotConfig(Tester.ExternalId),
            minutes, h.Leads, subscriptions ?? new DiscordReplySubscriptionStore(h.Entities),
            NullLogger.Instance, h.Clock.GetUtcNow(), default);

    private static int Status(IResult result)
        => ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;

    private static JsonNode Body(IResult result)
        => JsonSerializer.SerializeToNode(((IValueHttpResult)result).Value)!;

    [Fact]
    public async Task A_discussion_takes_the_lead_without_posting_anything()
    {
        var h = await Harness.CreateAsync();
        var subscriptions = new DiscordReplySubscriptionStore(h.Entities);

        var result = await ClaimLeadAsync(h, Leader, subscriptions: subscriptions);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var body = Body(result);
        Assert.False(body["renewed"]!.GetValue<bool>());
        Assert.Equal("owner", body["conversationLead"]!["State"]!.GetValue<string>());
        Assert.Equal("d1", body["replyCallback"]!["discussionId"]!.GetValue<string>());
        var lead = (await h.Leads.GetAsync(h.Binding))!;
        Assert.True(lead.IsActive(h.Clock.GetUtcNow()));
        Assert.Equal("d1", lead.DiscussionId);
        Assert.Equal(DiscordConversationLeadStore.DefaultWindow, lead.Window);
        Assert.Empty(lead.OwnerMessages);
        // Nothing was sent: no outbox row, no turn, no review.
        Assert.Empty(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.Empty(h.Provider.Inputs);
        Assert.Empty(h.Sentinel.Requests);
        // The leader hears the replies.
        Assert.Contains(await subscriptions.ActiveAsync(h.Binding.BindingId, DateTimeOffset.UtcNow),
            subscription => subscription.DiscussionId == "d1");

        // A session reply written now stays off Discord, as with a lead taken by a message.
        await h.SubmitAsync("1001", "ça marche toujours pas");
        h.Reply(1, "reply-1", "Mets à jour et relance.");
        await h.ReconcileAsync();
        Assert.Empty(await h.Rows("reply-1"));
    }

    [Fact]
    public async Task The_leader_renews_idempotently_and_another_discussion_gets_the_current_lead()
    {
        var h = await Harness.CreateAsync();
        Assert.Equal(StatusCodes.Status200OK, Status(await ClaimLeadAsync(h, Leader, minutes: 10)));
        var started = (await h.Leads.GetAsync(h.Binding))!.StartedAt;
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        // Again without a window: renewed with the window it already had.
        var renewed = await ClaimLeadAsync(h, Leader);
        Assert.Equal(StatusCodes.Status200OK, Status(renewed));
        Assert.True(Body(renewed)["renewed"]!.GetValue<bool>());
        var lead = (await h.Leads.GetAsync(h.Binding))!;
        Assert.Equal(TimeSpan.FromMinutes(10), lead.Window);
        Assert.Equal(h.Clock.GetUtcNow().AddMinutes(10), lead.ExpiresAt);
        Assert.Equal(started, lead.StartedAt);

        // With a window, that window replaces it.
        await ClaimLeadAsync(h, Leader, minutes: 30);
        Assert.Equal(TimeSpan.FromMinutes(30), (await h.Leads.GetAsync(h.Binding))!.Window);

        var other = new LeafAgentCaller("agent-1", "owner-1") { DiscussionId = "d2" };
        var conflict = await ClaimLeadAsync(h, other);
        Assert.Equal(StatusCodes.Status409Conflict, Status(conflict));
        Assert.Equal("discord_conversation_led_elsewhere", Body(conflict)["error"]!.GetValue<string>());
        Assert.Equal("d1", Body(conflict)["conversationLead"]!["LeadDiscussionId"]!.GetValue<string>());
        Assert.Equal("d1", (await h.Leads.GetAsync(h.Binding))!.DiscussionId);
    }

    [Fact]
    public async Task The_lead_route_applies_the_messages_route_authorization()
    {
        var h = await Harness.CreateAsync();
        var automation = new LeafAgentCaller("agent-1", "owner-1", "automation-1");
        var stranger = new LeafAgentCaller("agent-1", "owner-2") { DiscussionId = "d1" };

        // An automation must be authorized on the binding, and even then cannot lead.
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await ClaimLeadAsync(h, automation)));
        Assert.Equal("lead_requires_discussion", Error(await ClaimLeadAsync(h, automation,
            binding: h.Binding with { AuthorizedAutomationIds = new HashSet<string> { "automation-1" } })));
        Assert.Equal("invalid_lead_window", Error(await ClaimLeadAsync(h, Leader, minutes: 0)));
        Assert.Equal("invalid_lead_window", Error(await ClaimLeadAsync(
            h, Leader, minutes: (int)DiscordConversationLeadStore.MaxWindow.TotalMinutes + 1)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await ClaimLeadAsync(h, stranger)));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Status(await ClaimLeadAsync(h, Leader, agentEntityId: "agent-2")));
        // A DM counterpart outside the allowlist cannot be led, exactly as it cannot be messaged.
        var notAllowed = await ClaimLeadAsync(h, Leader, config: BotConfig());
        Assert.Equal(StatusCodes.Status403Forbidden, Status(notAllowed));
        Assert.Equal("binding_not_allowed", Error(notAllowed));
        Assert.Equal(StatusCodes.Status409Conflict,
            Status(await ClaimLeadAsync(h, Leader, binding: h.Binding with { Enabled = false })));

        Assert.Null(await h.Leads.GetAsync(h.Binding));
    }

    [Fact]
    public async Task A_claim_whose_discussion_cannot_hear_the_replies_is_put_back()
    {
        var h = await Harness.CreateAsync();
        var broken = new DiscordReplySubscriptionStore(
            FailingWritesStore.Create(h.Entities, DiscordReplySubscriptionStore.TypeSlug));

        var result = await ClaimLeadAsync(h, Leader, subscriptions: broken);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(result));
        Assert.Equal("reply_callback_unavailable", Error(result));
        Assert.Null(await h.Leads.GetAsync(h.Binding));

        // A leader already in place that loses its callback hands back rather than going deaf.
        await ClaimLeadAsync(h, Leader);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable,
            Status(await ClaimLeadAsync(h, Leader, subscriptions: broken)));
        var lead = (await h.Leads.GetAsync(h.Binding))!;
        Assert.False(lead.IsActive(h.Clock.GetUtcNow()));
        Assert.Equal("reply_callback_unavailable", lead.ReleaseReason);
    }

    [Fact]
    public async Task A_reply_in_review_when_the_lead_is_claimed_is_never_posted()
    {
        var h = await Harness.CreateAsync();
        await h.SubmitAsync("1001", "ça marche toujours pas");
        // The session's turn settles and its reply reaches the outbox before the claim...
        h.Reply(1, "reply-1", "Mets à jour et relance.");
        await h.ReconcileAsync();
        Assert.NotEmpty(await h.Rows("reply-1"));

        // ...then the discussion that is fixing the problem takes the lead.
        await ClaimLeadAsync(h, Leader);
        Assert.True(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));

        Assert.Empty(await h.Rows("reply-1"));
        Assert.Empty(h.Sentinel.Requests);
        Assert.Equal(["reply-1"], (await h.Leads.GetAsync(h.Binding))!.UnpostedReplies);
    }

    [Fact]
    public async Task A_reviewed_reply_queued_when_the_lead_is_claimed_is_withheld_at_send()
    {
        var h = await Harness.CreateAsync();
        await h.SubmitAsync("1001", "ça marche toujours pas");
        h.Reply(1, "reply-1", "Mets à jour et relance.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);
        var queued = Assert.Single(await h.Rows("reply-1"));
        Assert.Equal("queued", queued.State);
        Assert.False(await h.Coordinator.WithholdForLeadAsync(h.Binding, queued));

        await ClaimLeadAsync(h, Leader);

        // What the outbox drain asks just before sending.
        Assert.True(await h.Coordinator.WithholdForLeadAsync(h.Binding, queued));
        Assert.Empty(await h.Rows("reply-1"));
        var lead = (await h.Leads.GetAsync(h.Binding))!;
        Assert.Equal(["reply-1"], lead.UnpostedReplies);
        // The session hears on its next turn that the reply was never posted.
        await h.SubmitAsync("1002", "alors ?");
        Assert.Equal("reply-1", h.Provider.Inputs[^1].Metadata!["discord_conversation_lead"]!
            ["unposted_replies"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Owner_outreach_notices_and_partly_posted_replies_are_not_withheld()
    {
        var h = await Harness.CreateAsync();
        var outreach = await h.Coordinator.SendExistingProactiveAsync(
            h.Binding, "approve-1", "Je regarde, ne touche à rien pour l'instant.");
        await h.Coordinator.EnqueueNoticeAsync(h.Binding, "1001", "I could not admit that message.");
        var partial = new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 9), "reply-2", "assistant", "text",
            "first half / second half", DateTimeOffset.UtcNow);
        var sentChunk = await h.Deliveries.EnqueueOutboundAsync(h.Binding, partial, 0, "first half");
        var nextChunk = await h.Deliveries.EnqueueOutboundAsync(h.Binding, partial, 1, "second half");
        await h.Deliveries.MarkSentAsync(sentChunk, "300000000000002000");

        await ClaimLeadAsync(h, Leader);

        var pending = await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId);
        foreach (var row in pending)
            Assert.False(await h.Coordinator.WithholdForLeadAsync(h.Binding, row));
        Assert.Contains(pending, row => row.MessageUid == outreach.MessageUid);
        Assert.Contains(pending, row => row.ReviewExempt == DiscordDeliveryStore.BridgeNoticeExemption);
        Assert.Contains(pending, row => row.EntityId == nextChunk.EntityId);
        Assert.Empty((await h.Leads.GetAsync(h.Binding))!.UnpostedReplies);
    }

    private static DiscordBindingMessageRequest Request(
        bool? lead = null, int? minutes = null, bool notify = true)
        => new("hello", "key-1", NotifyReplies: notify, LeadConversation: lead, LeadForMinutes: minutes);

    private static string? Error(IResult? result)
        => result is IValueHttpResult { Value: { } value }
            ? JsonSerializer.SerializeToNode(value)?["error"]?.GetValue<string>()
            : null;

    private sealed class Harness
    {
        public required MemoryEntityStore Entities { get; init; }
        public required DiscordBindingStore Bindings { get; init; }
        public required DiscordDeliveryStore Deliveries { get; init; }
        public required DiscordConversationLeadStore Leads { get; init; }
        public required ScriptedProvider Provider { get; init; }
        public required RecordingSentinel Sentinel { get; init; }
        public required DiscordConversationCoordinator Coordinator { get; init; }
        public required ManualClock Clock { get; init; }
        public required DiscordBinding Binding { get; set; }

        public static async Task<Harness> CreateAsync(string conversationId = "323456789012345678")
        {
            var entities = new MemoryEntityStore();
            _ = await entities.UpsertBySlugAsync("quality-mode", "deep-codex", "Deep (Codex)", new JsonObject
            {
                ["provider"] = "codex-default",
                ["quality_tier"] = "deep",
                ["model"] = "gpt-5.6-sol",
                ["effort"] = "high",
            });
            var bindings = new DiscordBindingStore(entities);
            var binding = await bindings.CreateAsync(new CreateDiscordBinding(
                "100000000000000001", conversationId, "dm", null, null, "nova", "owner-1",
                "nova-discord-external", "direct", "Tester", CounterpartyUserId: Tester.ExternalId));
            var deliveries = new DiscordDeliveryStore(entities);
            var leads = new DiscordConversationLeadStore(entities);
            var provider = new ScriptedProvider();
            var sentinel = new RecordingSentinel();
            var clock = new ManualClock();
            var coordinator = new DiscordConversationCoordinator(
                bindings, deliveries,
                new DiscordBotConfigurationStore(new LeafDiscordConfigStore(entities)),
                new DiscordBehaviorStore(entities),
                new DiscordRuntimeOptionsResolver(entities),
                new DiscordUserIdentityResolver(entities, NullLogger<DiscordUserIdentityResolver>.Instance),
                new AcceptingComputeGateway(), [provider], new PromptInjectionDetector(),
                new DiscordEgressGuard(), new DiscordTypingLeases(),
                NullLogger<DiscordConversationCoordinator>.Instance, sentinel, clock, leads);
            var harness = new Harness
            {
                Entities = entities,
                Bindings = bindings,
                Deliveries = deliveries,
                Leads = leads,
                Provider = provider,
                Sentinel = sentinel,
                Coordinator = coordinator,
                Clock = clock,
                Binding = binding,
            };
            harness.Binding = await coordinator.EnsureReadyAsync(binding);
            return harness;
        }

        public async Task ClaimAsync(string discussionId, TimeSpan window, DateTimeOffset? now = null)
        {
            await RefreshAsync();
            await Leads.ClaimAsync(Binding, discussionId, "owner-1", "agent-1", window,
                now ?? Clock.GetUtcNow());
        }

        public async Task<ExternalConversationAdmission?> SubmitAsync(string messageId, string content)
        {
            await RefreshAsync();
            var admission = await Coordinator.SubmitAsync(
                Binding, messageId, Tester, content, behaviorContext: new JsonObject());
            await RefreshAsync();
            return admission;
        }

        public void Reply(long sequence, string messageUid, string text)
            => Provider.Messages.Add(new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", sequence), messageUid,
                "assistant", "text", text, DateTimeOffset.UtcNow));

        public void Tool(long sequence, string messageUid)
            => Provider.Messages.Add(new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", sequence), messageUid,
                "assistant", "tool_use", null, DateTimeOffset.UtcNow, ToolName: "Bash", ToolInput: "{}"));

        public async Task ReconcileAsync()
        {
            await RefreshAsync();
            await Coordinator.ReconcileAsync(Binding);
            await RefreshAsync();
        }

        public async Task RefreshAsync()
            => Binding = await Bindings.GetAsync(Binding.EntityId) ?? throw new InvalidOperationException();

        public async Task<IReadOnlyList<DiscordDelivery>> Rows(string messageUid)
            => (await Deliveries.PendingOutboundAsync(Binding.BindingId))
                .Where(item => item.MessageUid == messageUid)
                .Concat((await Entities.QueryAsync(new Leaf.Sdk.EntityQuery
                {
                    TypeSlug = DiscordDeliveryStore.TypeSlug,
                    DataEquals = new Dictionary<string, object?>
                    {
                        ["direction"] = "outbound",
                        ["message_uid"] = messageUid,
                        ["state"] = "sent",
                    },
                })).Select(entity => new DiscordDelivery(
                    entity.Id, "outbound", "message", "", 1, "sent", null, messageUid,
                    0, null, null, 0, null, null, entity.CreatedAt)))
                .ToList();
    }

    private sealed class RecordingSentinel : IUntrustedContentReview
    {
        public List<UntrustedContentReviewRequest> Requests { get; } = [];
        public Func<UntrustedContentReviewRequest, UntrustedContentVerdict> Decide { get; set; } = _ => Clean;

        public Task<UntrustedContentReviewResult> ReviewAsync(
            UntrustedContentReviewRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var verdict = Decide(request);
            return Task.FromResult(new UntrustedContentReviewResult(
                verdict, null, [], new UntrustedContentScreening(0, [], [], request.Content, []),
                "job-1", "trace-1")
            {
                Direction = request.Direction,
                ImagesReviewed = request.Images?.Count ?? 0,
            });
        }

        public UntrustedContentScreening Screen(
            string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
            string? ownerDisplayName = null)
            => new(0, [], [], content, []);
    }

    private sealed class ScriptedProvider : IExternalAgentConversationProvider
    {
        public List<ExternalConversationMessage> Messages { get; } = [];
        public List<ExternalConversationInput> Inputs { get; } = [];
        /// <summary>Answers like Nova when its inbound Sentinel review is unavailable.</summary>
        public bool Refuse { get; set; }
        public string ProviderId => "test";
        public bool CanHandle(string agentSlugOrId) => agentSlugOrId == "nova";

        public Task<ExternalConversationHandle> OpenAsync(
            ExternalConversationOpenRequest request, CancellationToken ct = default)
            => Task.FromResult(new ExternalConversationHandle(
                ProviderId, request.BindingId, request.Generation, $"conversation-{request.Generation}",
                $"session-{request.Generation}"));

        public Task<ExternalConversationAdmission> SendAsync(
            ExternalConversationHandle handle, ExternalConversationInput input, CancellationToken ct = default)
        {
            if (Refuse)
                return Task.FromResult(new ExternalConversationAdmission(
                    $"uid-{input.RequestId}", ExternalConversationDispositions.RefusedSentinelUnavailable));
            Inputs.Add(input);
            return Task.FromResult(new ExternalConversationAdmission($"uid-{input.RequestId}", "delivered"));
        }

        public Task<ExternalConversationPage> ReadSettledAsync(
            ExternalConversationHandle handle, ExternalConversationCursor? after = null,
            int limit = 100, CancellationToken ct = default)
        {
            var messages = Messages
                .Where(message => after is null || message.Cursor.Sequence > after.Sequence)
                .OrderBy(message => message.Cursor.Sequence)
                .ToList();
            return Task.FromResult(new ExternalConversationPage(
                messages, messages.Count == 0 ? after : messages[^1].Cursor, IsQuiescent: true));
        }

        public IDisposable SubscribeSettled(Func<ExternalConversationSettled, CancellationToken, Task> handler)
            => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>Accepts transcript injections the way RedCompute confirms them.</summary>
    private sealed class AcceptingComputeGateway : IComputeGateway
    {
        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["injected"] = true,
                    ["messageUid"] = body["messageUid"]?.GetValue<string>(),
                }.ToJsonString()),
            };
        }
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

/// <summary>An entity store whose writes of one type fail, like an unavailable database.</summary>
public class FailingWritesStore : System.Reflection.DispatchProxy
{
    private Leaf.Sdk.Services.IEntityStore inner = null!;
    private string failingType = "";

    public static Leaf.Sdk.Services.IEntityStore Create(
        Leaf.Sdk.Services.IEntityStore inner, string failingType)
    {
        var proxy = Create<Leaf.Sdk.Services.IEntityStore, FailingWritesStore>();
        var store = (FailingWritesStore)(object)proxy;
        store.inner = inner;
        store.failingType = failingType;
        return proxy;
    }

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "UpsertBySlugAsync" && Equals(args?[0], failingType))
            throw new InvalidOperationException("The entity store is unavailable.");
        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
