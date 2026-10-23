using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// The outbound Sentinel pass: a reply is enqueued in review, reviewed once, then queued (deliver)
/// or held, and the session hears what happened. The review is durable in the outbox, so a
/// restart neither loses a reply nor posts it twice.
/// </summary>
public sealed class DiscordOutboundReviewTests
{
    private static readonly UntrustedContentVerdict Clean =
        new("none", [], "normal", true, UntrustedContentIsolation.VerifiedToolFree);

    private static readonly UntrustedContentVerdict Leak =
        new("high", ["personal-data"], "require_private_approval", true, UntrustedContentIsolation.VerifiedToolFree);

    [Fact]
    public async Task A_clean_reply_waits_in_review_then_is_queued_once_with_its_verdict()
    {
        var h = await Harness.CreateAsync(_ => Clean);
        h.Reply(1, "reply-1", "The bridge retries a 429 with Retry-After.");

        await h.ReconcileAsync();

        var pending = await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId);
        var row = Assert.Single(pending);
        Assert.Equal(DiscordDeliveryStore.ReviewingState, row.State);
        Assert.True(await h.Deliveries.HasPendingOutboundMessageAsync(h.Binding.BindingId));
        Assert.Empty(h.Sentinel.Requests);

        Assert.True(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));

        var queued = Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.Equal("queued", queued.State);
        Assert.Equal("delivered", queued.OutboundReview?["outcome"]?.GetValue<string>());
        Assert.Equal("none", queued.OutboundReview?["risk"]?.GetValue<string>());
        var request = Assert.Single(h.Sentinel.Requests);
        Assert.Equal(UntrustedContentDirection.Outbound, request.Direction);
        Assert.Equal("The bridge retries a 429 with Retry-After.", request.Content);
        Assert.Equal(UntrustedContentSources.Discord, request.Origin.Source);
        Assert.Equal(LeafDiscordPlugin.PluginId, request.Origin.CallerApp);
        Assert.Equal("owner-1", request.Origin.OwnerUserId);
        Assert.Equal("nova", request.AgentRef);
        Assert.Equal("a channel in a Discord server, readable by every member who can see it", request.Audience);
        Assert.Equal("codex-default", request.Compute?.Provider);
        Assert.Equal("deep", request.Compute?.QualityTier);
        Assert.Equal($"sentinel-outbound:{h.Binding.BindingId}:1:reply-1", request.IdempotencyKey);
        Assert.Empty(h.Provider.Inputs);

        // Reviewed once: nothing is left, and a second pass asks nothing.
        Assert.False(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));
        Assert.Single(h.Sentinel.Requests);
    }

    [Fact]
    public async Task The_egress_guard_still_redacts_before_the_sentinel_sees_the_reply()
    {
        var h = await Harness.CreateAsync(_ => Clean);
        h.Reply(1, "reply-1", "Use api_key = 9f3c2e1d7b6a5f4e3d2c1b0a and the JWT eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXZhbHVl");

        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        var reviewed = Assert.Single(h.Sentinel.Requests).Content;
        Assert.DoesNotContain("9f3c2e1d7b6a5f4e3d2c1b0a", reviewed, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", reviewed, StringComparison.Ordinal);
        Assert.Contains("[redacted credential]", reviewed, StringComparison.Ordinal);
        Assert.Contains("[redacted token]", reviewed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_leaking_reply_is_held_and_the_session_is_told_why_and_asked_to_rewrite()
    {
        var h = await Harness.CreateAsync(_ => Leak);
        h.Reply(1, "reply-1", "He lives at 14 rue des Lilas, 84000 Avignon.");

        await h.ReconcileAsync();
        Assert.True(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));

        Assert.Empty(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.False(await h.Deliveries.HasPendingOutboundMessageAsync(h.Binding.BindingId));
        var held = Assert.Single(await h.Rows("reply-1"));
        Assert.Equal(DiscordDeliveryStore.HeldState, held.State);
        Assert.Equal("held", held.OutboundReview?["outcome"]?.GetValue<string>());

        var notice = Assert.Single(h.Provider.Inputs);
        Assert.Equal($"sentinel-outbound-{DiscordBindingStore.Hash("reply-1")}", notice.RequestId);
        Assert.Contains("personal-data", notice.Content, StringComparison.Ordinal);
        var metadata = notice.Metadata!;
        Assert.Equal("discord", metadata["transport"]?.GetValue<string>());
        Assert.Equal("nova", metadata["agent_id"]?.GetValue<string>());
        var review = Assert.IsType<JsonObject>(metadata["outbound_sentinel_notice"]);
        Assert.Equal("reply-1", review["message_uid"]?.GetValue<string>());
        Assert.Equal("held", review["outcome"]?.GetValue<string>());
        Assert.Equal("high", review["risk"]?.GetValue<string>());
        Assert.Equal("require_private_approval", review["handling"]?.GetValue<string>());
        Assert.True(review["available"]?.GetValue<bool>());
        Assert.Equal(["personal-data"], review["signals"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task A_rewrite_that_passes_is_delivered()
    {
        var h = await Harness.CreateAsync(request =>
            request.Content.Contains("rue des Lilas", StringComparison.Ordinal) ? Leak : Clean);
        h.Reply(1, "reply-1", "He lives at 14 rue des Lilas.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        h.Reply(3, "reply-2", "I can't share where Laurent lives, but I can pass on your question.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        var queued = Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.Equal("reply-2", queued.MessageUid);
        Assert.Equal("delivered", queued.OutboundReview?["outcome"]?.GetValue<string>());
        Assert.Single(h.Provider.Inputs);
    }

    [Fact]
    public async Task A_rewrite_held_again_is_withheld_with_a_short_notice_and_no_further_rewrite()
    {
        var h = await Harness.CreateAsync(_ => Leak);
        h.Reply(1, "reply-1", "He lives at 14 rue des Lilas.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        h.Reply(3, "reply-2", "Fine: 14 rue des Lilas, second floor.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        Assert.Single(h.Provider.Inputs);
        var withheld = Assert.Single(await h.Rows("reply-2"));
        Assert.Equal(DiscordDeliveryStore.HeldState, withheld.State);
        Assert.Equal("withheld", withheld.OutboundReview?["outcome"]?.GetValue<string>());
        var notice = Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.Equal(DiscordConversationCoordinator.WithheldReplyNotice,
            notice.Payload?["content"]?.GetValue<string>());

        // Replaying the same decision (a restart) does not add a second notice.
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);
        Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
    }

    [Fact]
    public async Task A_new_participant_message_renews_the_single_rewrite()
    {
        var h = await Harness.CreateAsync(_ => Leak);
        h.Reply(1, "reply-1", "He lives at 14 rue des Lilas.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        await h.Coordinator.SubmitAsync(h.Binding, "300000000000000003",
            new ExternalRequestor("400000000000000004", "Participant"), "Where does he live?");
        await h.RefreshAsync();
        h.Reply(5, "reply-2", "At 14 rue des Lilas.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        Assert.Equal("held", (await h.Rows("reply-2"))[0].OutboundReview?["outcome"]?.GetValue<string>());
        Assert.Equal(2, h.Provider.Inputs.Count(input => input.Metadata?["outbound_sentinel_notice"] is not null));
    }

    [Fact]
    public async Task An_unavailable_review_is_retried_a_bounded_number_of_times_then_withheld()
    {
        var h = await Harness.CreateAsync(_ => UntrustedContentVerdict.Unavailable());
        h.Reply(1, "reply-1", "Here is the fix.");
        await h.ReconcileAsync();

        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);
        var waiting = Assert.Single(await h.Rows("reply-1"));
        Assert.Equal(DiscordDeliveryStore.ReviewingState, waiting.State);
        Assert.Equal(1, waiting.ReviewAttempts);
        Assert.Null(waiting.OutboundReview);
        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(30), Assert.Single(h.RetryDue));
        Assert.DoesNotContain(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId),
            item => item.State == "queued");

        // Not due yet: nothing is asked.
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);
        Assert.Single(h.Sentinel.Requests);

        foreach (var wait in new[] { 30, 60, 120 })
        {
            h.Clock.Advance(TimeSpan.FromSeconds(wait + 1));
            await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);
        }

        Assert.Equal(DiscordConversationCoordinator.MaxOutboundReviewAttempts, h.Sentinel.Requests.Count);
        var withheld = Assert.Single(await h.Rows("reply-1"));
        Assert.Equal(DiscordDeliveryStore.HeldState, withheld.State);
        Assert.Equal("withheld", withheld.OutboundReview?["outcome"]?.GetValue<string>());
        Assert.False(withheld.OutboundReview?["available"]?.GetValue<bool>());
        var notice = Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.Equal(DiscordConversationCoordinator.OutboundUnavailableNotice,
            notice.Payload?["content"]?.GetValue<string>());
        Assert.Empty(h.Provider.Inputs);

        // The session hears it on the next admitted participant message.
        await h.Coordinator.SubmitAsync(h.Binding, "300000000000000003",
            new ExternalRequestor("400000000000000004", "Participant"), "Thanks!");
        var reported = Assert.IsType<JsonArray>(Assert.Single(h.Provider.Inputs).Metadata?["outbound_sentinel_reviews"]);
        var review = Assert.IsType<JsonObject>(Assert.Single(reported));
        Assert.Equal("reply-1", review["message_uid"]?.GetValue<string>());
        Assert.Equal("withheld", review["outcome"]?.GetValue<string>());
        Assert.False(review["available"]?.GetValue<bool>());
        Assert.Equal(["verifier-unavailable"], review["signals"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task An_unavailable_review_that_recovers_delivers_the_reply()
    {
        var calls = 0;
        var h = await Harness.CreateAsync(_ => ++calls == 1 ? UntrustedContentVerdict.Unavailable() : Clean);
        h.Reply(1, "reply-1", "Here is the fix.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        h.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));

        var queued = Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.Equal("reply-1", queued.MessageUid);
        Assert.Equal("delivered", queued.OutboundReview?["outcome"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_reply_waiting_on_an_unavailable_review_holds_every_later_reply()
    {
        var h = await Harness.CreateAsync(request =>
            request.Content == "First." ? UntrustedContentVerdict.Unavailable() : Clean);
        h.Reply(1, "reply-1", "First.");
        h.Reply(2, "reply-2", "Second.");
        await h.ReconcileAsync();

        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        Assert.Equal(["First."], h.Sentinel.Requests.Select(request => request.Content));
        Assert.All(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId),
            item => Assert.Equal(DiscordDeliveryStore.ReviewingState, item.State));
    }

    [Fact]
    public async Task A_throwing_sentinel_fails_closed_like_an_unavailable_review()
    {
        var h = await Harness.CreateAsync(_ => throw new InvalidOperationException("compute down"));
        h.Reply(1, "reply-1", "Here is the fix.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        var waiting = Assert.Single(await h.Rows("reply-1"));
        Assert.Equal(DiscordDeliveryStore.ReviewingState, waiting.State);
        Assert.Equal(1, waiting.ReviewAttempts);
        Assert.DoesNotContain(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId),
            item => item.State == "queued");
    }

    [Fact]
    public async Task Without_an_installed_sentinel_a_reply_is_withheld_at_once_with_a_notice()
    {
        var h = await Harness.CreateAsync(null);
        h.Reply(1, "reply-1", "Here is the fix.");
        await h.ReconcileAsync();
        Assert.Equal(DiscordDeliveryStore.ReviewingState, Assert.Single(await h.Rows("reply-1")).State);

        Assert.True(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));

        var withheld = Assert.Single(await h.Rows("reply-1"));
        Assert.Equal(DiscordDeliveryStore.HeldState, withheld.State);
        Assert.Equal("withheld", withheld.OutboundReview?["outcome"]?.GetValue<string>());
        Assert.Equal(DiscordConversationCoordinator.OutboundUnavailableNotice,
            Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId))
                .Payload?["content"]?.GetValue<string>());
        Assert.Empty(h.RetryDue);
    }

    [Fact]
    public async Task A_refused_message_stays_durable_and_is_admitted_when_the_sentinel_recovers()
    {
        var h = await Harness.CreateAsync(_ => Clean);
        h.Provider.Refuse = true;

        var refused = await h.Coordinator.SubmitAsync(h.Binding, "300000000000000003",
            new ExternalRequestor("400000000000000004", "Participant"), "Hello?");

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, refused?.Disposition);
        var inbound = Assert.Single(await h.Deliveries.PendingInboundAsync(h.Binding.BindingId));
        Assert.True(inbound.SentinelRefused);
        Assert.Equal(1, inbound.Attempts);
        Assert.Equal("sentinel_unavailable", inbound.LastError);
        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(30), inbound.NextAttemptAt);
        Assert.Equal(inbound.NextAttemptAt, Assert.Single(h.RetryDue));
        await h.RefreshAsync();
        Assert.Null(h.Binding.LastDeliveredSnowflake);
        var notice = Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));
        Assert.Equal(DiscordConversationCoordinator.InboundUnavailableNotice,
            notice.Payload?["content"]?.GetValue<string>());

        // Not due: the session is not asked again.
        await h.Coordinator.RetryPendingAsync(h.Binding);
        Assert.Single(h.Provider.Inputs);

        // Still down when due: it waits longer, and no second notice is posted.
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        await h.Coordinator.RetryPendingAsync(h.Binding);
        Assert.Equal(2, h.Provider.Inputs.Count);
        Assert.Equal(2, Assert.Single(await h.Deliveries.PendingInboundAsync(h.Binding.BindingId)).Attempts);
        Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));

        // Recovered, after a restart: the stored message is admitted once.
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var restarted = await h.RestartAsync(_ => Clean);
        await restarted.Coordinator.RetryPendingAsync(restarted.Binding);

        Assert.Equal("Hello?", Assert.Single(restarted.Provider.Inputs).Content);
        Assert.Empty(await restarted.Deliveries.PendingInboundAsync(restarted.Binding.BindingId));
        await restarted.RefreshAsync();
        Assert.Equal("300000000000000003", restarted.Binding.LastDeliveredSnowflake);
        await restarted.Coordinator.RetryPendingAsync(restarted.Binding);
        Assert.Single(restarted.Provider.Inputs);
    }

    [Fact]
    public async Task Later_messages_wait_behind_a_refused_one_and_are_admitted_in_order()
    {
        var h = await Harness.CreateAsync(_ => Clean);
        h.Provider.Refuse = true;
        await h.Coordinator.SubmitAsync(h.Binding, "300000000000000003",
            new ExternalRequestor("400000000000000004", "Participant"), "First");
        h.Provider.Refuse = false;

        var second = await h.Coordinator.SubmitAsync(h.Binding, "300000000000000005",
            new ExternalRequestor("400000000000000004", "Participant"), "Second");

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, second?.Disposition);
        Assert.Single(h.Provider.Inputs);
        Assert.Single(await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId));

        h.Clock.Advance(TimeSpan.FromSeconds(31));
        await h.Coordinator.RetryPendingAsync(h.Binding);

        Assert.Equal(["First", "First", "Second"], h.Provider.Inputs.Select(input => input.Content));
        Assert.Empty(await h.Deliveries.PendingInboundAsync(h.Binding.BindingId));
    }

    [Fact]
    public async Task A_message_refused_for_too_long_is_dropped_and_no_longer_holds_the_binding()
    {
        var h = await Harness.CreateAsync(_ => Clean);
        h.Provider.Refuse = true;
        await h.Coordinator.SubmitAsync(h.Binding, "300000000000000003",
            new ExternalRequestor("400000000000000004", "Participant"), "Old");
        h.Provider.Refuse = false;
        h.Clock.Advance(DiscordConversationCoordinator.InboundRefusalWindow + TimeSpan.FromMinutes(1));

        await h.Coordinator.RetryPendingAsync(h.Binding);
        var admitted = await h.Coordinator.SubmitAsync(h.Binding, "300000000000000005",
            new ExternalRequestor("400000000000000004", "Participant"), "New");

        Assert.Equal(["Old", "New"], h.Provider.Inputs.Select(input => input.Content));
        Assert.Equal("delivered", admitted?.Disposition);
        Assert.Empty(await h.Deliveries.PendingInboundAsync(h.Binding.BindingId));
        var stored = Assert.Single(await h.Entities.QueryAsync(new Leaf.Sdk.EntityQuery
        {
            TypeSlug = DiscordDeliveryStore.TypeSlug,
            DataEquals = new Dictionary<string, object?> { ["discord_id"] = "300000000000000003" },
        }));
        Assert.Equal("refused", JsonRead.String(stored.Data, "state"));
    }

    [Fact]
    public async Task A_turn_without_earlier_reviews_sends_no_outbound_report()
    {
        var h = await Harness.CreateAsync(_ => Clean);

        await h.Coordinator.SubmitAsync(h.Binding, "300000000000000003",
            new ExternalRequestor("400000000000000004", "Participant"), "Hello");

        Assert.Null(Assert.Single(h.Provider.Inputs).Metadata?["outbound_sentinel_reviews"]);
    }

    [Fact]
    public async Task A_restart_during_review_reviews_again_and_queues_exactly_once()
    {
        var h = await Harness.CreateAsync(_ => Clean);
        h.Reply(1, "reply-1", new string('a', 2_500));
        await h.ReconcileAsync();
        Assert.Equal(2, (await h.Rows("reply-1")).Count);

        // Restart: a new coordinator over the same durable store, and the settled read
        // starts again from scratch (cursor lost). The same rows are found, none added.
        var restarted = await h.RestartAsync(_ => Clean, resetCursor: true);
        await restarted.ReconcileAsync();
        Assert.Equal(2, (await restarted.Rows("reply-1")).Count);
        await restarted.Coordinator.ReviewPendingOutboundAsync(restarted.Binding);

        var queued = await restarted.Deliveries.PendingOutboundAsync(restarted.Binding.BindingId);
        Assert.Equal([0, 1], queued.Select(item => item.ChunkIndex));
        Assert.All(queued, item => Assert.Equal("queued", item.State));
        Assert.Single(restarted.Sentinel.Requests);

        // Delivered rows are never re-enqueued by another re-read.
        foreach (var item in queued) await restarted.Deliveries.MarkSentAsync(item, $"90000000000000000{item.ChunkIndex}");
        var again = await restarted.RestartAsync(_ => Clean, resetCursor: true);
        await again.ReconcileAsync();
        Assert.Empty(await again.Deliveries.PendingOutboundAsync(again.Binding.BindingId));
        Assert.All(await again.Rows("reply-1"), item => Assert.Equal("sent", item.State));
    }

    [Fact]
    public async Task A_restart_after_the_verdict_replays_it_without_a_second_review()
    {
        var h = await Harness.CreateAsync(_ => Leak);
        h.Reply(1, "reply-1", "He lives at 14 rue des Lilas.");
        await h.ReconcileAsync();
        // The verdict was recorded, then the process stopped before the hold was carried out.
        var rows = await h.Rows("reply-1");
        await h.Deliveries.RecordOutboundReviewAsync(rows,
            DiscordConversationCoordinator.OutboundReviewRecord(Leak, "held",
                DiscordConversationCoordinator.TurnKey(h.Binding), "job-1", "trace-1", 1200),
            DiscordConversationCoordinator.TurnKey(h.Binding));

        var restarted = await h.RestartAsync(_ => Clean);
        await restarted.Coordinator.ReviewPendingOutboundAsync(restarted.Binding);

        Assert.Empty(restarted.Sentinel.Requests);
        Assert.Equal(DiscordDeliveryStore.HeldState, Assert.Single(await restarted.Rows("reply-1")).State);
        var notice = Assert.Single(restarted.Provider.Inputs);
        Assert.Equal($"sentinel-outbound-{DiscordBindingStore.Hash("reply-1")}", notice.RequestId);
    }

    [Fact]
    public async Task A_notice_replayed_after_a_restart_reuses_its_request_id()
    {
        var h = await Harness.CreateAsync(_ => Leak);
        h.Reply(1, "reply-1", "He lives at 14 rue des Lilas.");
        await h.ReconcileAsync();
        // The notice reached the session, then the process stopped before the rows left review.
        h.Provider.FailAfterSend = true;
        await Assert.ThrowsAsync<IOException>(() => h.Coordinator.ReviewPendingOutboundAsync(h.Binding));
        Assert.Equal(DiscordDeliveryStore.ReviewingState, Assert.Single(await h.Rows("reply-1")).State);

        var restarted = await h.RestartAsync(_ => Clean);
        await restarted.Coordinator.ReviewPendingOutboundAsync(restarted.Binding);

        // RedCompute admits a message once per (transport, binding, generation, request ID).
        Assert.Equal(h.Provider.Inputs[0].RequestId, Assert.Single(restarted.Provider.Inputs).RequestId);
        Assert.Empty(restarted.Sentinel.Requests);
        Assert.Equal(DiscordDeliveryStore.HeldState, Assert.Single(await restarted.Rows("reply-1")).State);
    }

    [Fact]
    public async Task Replies_leave_review_in_order_and_a_later_reply_never_overtakes_an_earlier_one()
    {
        var h = await Harness.CreateAsync(_ => Clean);
        h.Reply(1, "reply-1", "First.");
        h.Reply(2, "reply-2", "Second.");
        await h.ReconcileAsync();

        var pending = await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId);
        Assert.Equal(["reply-1", "reply-2"], pending.Select(item => item.MessageUid));
        Assert.All(pending, item => Assert.Equal(DiscordDeliveryStore.ReviewingState, item.State));

        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        Assert.Equal(["First.", "Second."], h.Sentinel.Requests.Select(request => request.Content));
        var queued = await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId);
        Assert.Equal(["reply-1", "reply-2"], queued.Select(item => item.MessageUid));
    }

    [Fact]
    public async Task Clear_leaves_the_old_generation_in_review_inert()
    {
        var h = await Harness.CreateAsync(_ => Clean);
        h.Reply(1, "reply-1", "Old generation.");
        await h.ReconcileAsync();
        await h.SeedBotConfigurationAsync();

        var cleared = await h.Coordinator.ClearAsync(h.Binding, "clear-1");

        Assert.False(await h.Coordinator.ReviewPendingOutboundAsync(cleared));
        Assert.Empty(h.Sentinel.Requests);
        Assert.Equal(DiscordDeliveryStore.ReviewingState, Assert.Single(await h.Rows("reply-1")).State);
    }

    [Fact]
    public async Task Commentary_and_outreach_copies_are_not_held_for_review()
    {
        var h = await Harness.CreateAsync(_ => Leak);
        h.Provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 1), "turn-1", "assistant", "commentary",
            "Looking at the logs.", DateTimeOffset.UtcNow));
        h.Reply(2, "discord-outreach-abc", "Proactive hello.");
        await h.ReconcileAsync();

        var pending = await h.Deliveries.PendingOutboundAsync(h.Binding.BindingId);
        Assert.Equal(2, pending.Count);
        Assert.All(pending, item => Assert.Equal("queued", item.State));
        Assert.False(await h.Coordinator.ReviewPendingOutboundAsync(h.Binding));
        Assert.Empty(h.Sentinel.Requests);
    }

    [Fact]
    public async Task A_direct_message_audience_names_the_verified_owner()
    {
        var entities = new MemoryEntityStore();
        var owner = await entities.UpsertBySlugAsync("user", "owner", "Owner Example", new JsonObject
        {
            [DiscordUserIdentityResolver.UserField] = "200000000000000002",
        });
        var h = await Harness.CreateAsync(_ => Clean, entities, kind: "dm",
            ownerId: owner.Id.ToString(), counterparty: "200000000000000002");
        h.Reply(1, "reply-1", "Your meeting moved.");
        await h.ReconcileAsync();
        await h.Coordinator.ReviewPendingOutboundAsync(h.Binding);

        Assert.Equal(
            "a Discord direct message with the verified owner (Discord is still an external service)",
            Assert.Single(h.Sentinel.Requests).Audience);
    }

    [Fact]
    public async Task The_transcript_shows_the_outbound_verdict_on_the_reply()
    {
        var messages = new[]
        {
            new ExternalConversationMessage(new ExternalConversationCursor("e", 1), "reply-1",
                "assistant", "text", "Withheld text.", DateTimeOffset.UtcNow),
            new ExternalConversationMessage(new ExternalConversationCursor("e", 2), "reply-2",
                "assistant", "text", "Plain text.", DateTimeOffset.UtcNow),
        };
        var review = DiscordConversationCoordinator.OutboundReviewRecord(Leak, "withheld", "1:-", "job", "trace", 900);

        var projected = await DiscordTranscriptProjection.ProjectAsync(messages,
            (_, _) => Task.FromResult<DiscordUserProfile?>(null), null, default,
            (uid, _) => Task.FromResult(uid == "reply-1" ? review : null));

        var outbound = projected[0].Metadata?["discord"]?["outboundSentinel"];
        Assert.Equal("withheld", outbound?["outcome"]?.GetValue<string>());
        Assert.Equal("high", outbound?["risk"]?.GetValue<string>());
        Assert.Null(outbound?["turn"]);
        Assert.Null(projected[1].Metadata?["discord"]);
    }

    private sealed class Harness
    {
        public required MemoryEntityStore Entities { get; init; }
        public required DiscordBindingStore Bindings { get; init; }
        public required DiscordDeliveryStore Deliveries { get; init; }
        public required ScriptedProvider Provider { get; init; }
        public required FakeSentinel Sentinel { get; init; }
        public required DiscordConversationCoordinator Coordinator { get; init; }
        public required DiscordBinding Binding { get; set; }
        public required ManualClock Clock { get; init; }
        public List<DateTimeOffset> RetryDue { get; } = [];

        public static async Task<Harness> CreateAsync(
            Func<UntrustedContentReviewRequest, UntrustedContentVerdict>? decide,
            MemoryEntityStore? entities = null,
            string kind = "guild_channel",
            string ownerId = "owner-1",
            string? counterparty = null)
        {
            entities ??= new MemoryEntityStore();
            _ = await entities.UpsertBySlugAsync("quality-mode", "deep-codex", "Deep (Codex)", new JsonObject
            {
                ["provider"] = "codex-default",
                ["quality_tier"] = "deep",
                ["model"] = "gpt-5.6-sol",
                ["effort"] = "high",
            });
            var bindings = new DiscordBindingStore(entities);
            var binding = await bindings.CreateAsync(new CreateDiscordBinding(
                "100000000000000001", kind == "dm" ? "323456789012345678" : "123456789012345678", kind,
                kind == "dm" ? null : "223456789012345678", null, "nova", ownerId,
                "nova-discord-external", "participate", CounterpartyUserId: counterparty));
            var harness = Build(entities, bindings, binding, decide, new ScriptedProvider(), new ManualClock());
            harness.Binding = await harness.Coordinator.EnsureReadyAsync(binding);
            return harness;
        }

        public async Task<Harness> RestartAsync(
            Func<UntrustedContentReviewRequest, UntrustedContentVerdict> decide, bool resetCursor = false)
        {
            var provider = new ScriptedProvider();
            provider.Messages.AddRange(Provider.Messages);
            var binding = await Bindings.GetAsync(Binding.EntityId) ?? throw new InvalidOperationException();
            if (resetCursor)
                binding = await Bindings.AdvanceCursorAsync(binding, new ExternalConversationCursor("epoch-1", 0));
            return Build(Entities, Bindings, binding, decide, provider, Clock);
        }

        private static Harness Build(
            MemoryEntityStore entities, DiscordBindingStore bindings, DiscordBinding binding,
            Func<UntrustedContentReviewRequest, UntrustedContentVerdict>? decide, ScriptedProvider provider,
            ManualClock clock)
        {
            var deliveries = new DiscordDeliveryStore(entities);
            var sentinel = new FakeSentinel(decide ?? (_ => Clean));
            var configurations = new DiscordBotConfigurationStore(new LeafDiscordConfigStore(entities));
            var coordinator = new DiscordConversationCoordinator(
                bindings, deliveries, configurations, new DiscordBehaviorStore(entities),
                new DiscordRuntimeOptionsResolver(entities),
                new DiscordUserIdentityResolver(entities, NullLogger<DiscordUserIdentityResolver>.Instance),
                new NoComputeGateway(), [provider], new PromptInjectionDetector(),
                new DiscordEgressGuard(), new DiscordTypingLeases(),
                NullLogger<DiscordConversationCoordinator>.Instance,
                decide is null ? null : sentinel, clock);
            var harness = new Harness
            {
                Entities = entities,
                Bindings = bindings,
                Deliveries = deliveries,
                Provider = provider,
                Sentinel = sentinel,
                Coordinator = coordinator,
                Binding = binding,
                Clock = clock,
            };
            coordinator.RetryDue += (_, dueAt) => harness.RetryDue.Add(dueAt);
            return harness;
        }

        public void Reply(long sequence, string messageUid, string text)
            => Provider.Messages.Add(new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", sequence), messageUid,
                "assistant", "text", text, DateTimeOffset.UtcNow));

        public async Task ReconcileAsync()
        {
            await RefreshAsync();
            await Coordinator.ReconcileAsync(Binding);
            await RefreshAsync();
        }

        public async Task RefreshAsync()
            => Binding = await Bindings.GetAsync(Binding.EntityId) ?? throw new InvalidOperationException();

        public async Task<IReadOnlyList<DiscordDelivery>> Rows(string messageUid)
            => (await Entities.QueryAsync(new Leaf.Sdk.EntityQuery
            {
                TypeSlug = DiscordDeliveryStore.TypeSlug,
                DataEquals = new Dictionary<string, object?>
                {
                    ["direction"] = "outbound",
                    ["message_uid"] = messageUid,
                },
            })).Select(entity => new DiscordDelivery(
                entity.Id, "outbound", "message", "", 1,
                JsonRead.String(entity.Data, "state") ?? "", null, messageUid,
                (int)JsonRead.Long(entity.Data, "chunk_index", 0), null, null, 0, null, null, entity.CreatedAt)
            {
                OutboundReview = entity.Data["outbound_review"] as JsonObject,
                ReviewAttempts = (int)JsonRead.Long(entity.Data, "review_attempts", 0),
            }).OrderBy(item => item.ChunkIndex).ToList();

        public async Task SeedBotConfigurationAsync()
            => await new DiscordBotConfigurationStore(new LeafDiscordConfigStore(Entities)).CreateAsync(
                new LeafDiscordConfigUpdate("100000000000000001", new string('a', 64), null, false, false,
                    "nova", [], [], 10 * 1024 * 1024, "nova", "deep-codex", "deep-codex"),
                ownerUserId: "owner-1");
    }

    private sealed class FakeSentinel(Func<UntrustedContentReviewRequest, UntrustedContentVerdict> decide)
        : IUntrustedContentReview
    {
        public List<UntrustedContentReviewRequest> Requests { get; } = [];

        public Task<UntrustedContentReviewResult> ReviewAsync(
            UntrustedContentReviewRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new UntrustedContentReviewResult(
                decide(request), null, [], new UntrustedContentScreening(0, [], [], request.Content, []),
                "job-1", "trace-1"));
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
        public bool FailAfterSend { get; set; }
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
            Inputs.Add(input);
            if (FailAfterSend) throw new IOException("process stopped");
            if (Refuse && input.Metadata?["outbound_sentinel_notice"] is null)
                return Task.FromResult(new ExternalConversationAdmission(
                    $"uid-{input.RequestId}", ExternalConversationDispositions.RefusedSentinelUnavailable));
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

    private sealed class NoComputeGateway : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
            => throw new InvalidOperationException("No compute call is expected");
    }
}
