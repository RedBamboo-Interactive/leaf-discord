using System.Net;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// A session whose provider history is gone used to fail every retry for as long as the bridge
/// ran, with the waiting message going nowhere (Leaf #feedback, 2026-11-04).
/// </summary>
public sealed class DiscordSessionRecoveryTests
{
    private const string ApplicationId = "100000000000000001";

    [Fact]
    public async Task A_lost_session_recovers_once_and_the_message_reaches_the_next_generation()
    {
        var harness = await Harness.CreateAsync();
        var (binding, lostSession) = await harness.AttachedAsync();
        harness.Provider.Lost.Add(lostSession);
        harness.Provider.Settled[lostSession] =
        [
            Settled(1, "user", "text", "Salut Nova"),
            Settled(2, "assistant", "tool_use", "not spoken"),
            Settled(3, "assistant", "text", "Salut !"),
        ];

        var admission = await harness.SubmitAsync(binding, "1001", "Tu te souviens ?");

        Assert.Equal("delivered", admission!.Disposition);
        var current = await harness.Binding();
        Assert.Equal(2, current.Generation);
        Assert.NotEqual(lostSession, current.SessionId);
        var (session, input) = Assert.Single(harness.Provider.Sends);
        Assert.Equal(current.SessionId, session);
        Assert.Equal(2L, input.Metadata!["generation"]!.GetValue<long>());
        Assert.Contains("Salut Nova", input.Content);
        Assert.Contains("Salut !", input.Content);
        Assert.DoesNotContain("not spoken", input.Content);
        Assert.EndsWith("Tu te souviens ?", input.Content);
        Assert.Empty(await harness.Deliveries.PendingInboundAsync(current.BindingId));
    }

    [Fact]
    public async Task Every_waiting_message_moves_to_the_next_generation_in_order()
    {
        var harness = await Harness.CreateAsync();
        var (binding, lostSession) = await harness.AttachedAsync();
        await harness.Deliveries.ReceiveAsync(binding, "1001", Payload("first"));
        await harness.Deliveries.ReceiveAsync(binding, "1002", Payload("second"));
        harness.Provider.Lost.Add(lostSession);

        await harness.Coordinator.RetryPendingAsync(await harness.Binding());

        var current = await harness.Binding();
        Assert.Equal(2, current.Generation);
        Assert.Equal(["first", "second"], harness.Provider.Sends
            .Where(item => item.Session == current.SessionId)
            .Select(item => item.Input.Content));
        Assert.Empty(await harness.Deliveries.PendingInboundAsync(current.BindingId));
        Assert.All(new[] { "1001", "1002" }, id => Assert.Equal("admitted",
            harness.Deliveries.FindInboundAsync(current, id).GetAwaiter().GetResult()!.State));
    }

    [Fact]
    public async Task A_failed_first_turn_retries_with_the_same_bytes_and_recovers_only_once()
    {
        var clock = new ManualClock();
        var harness = await Harness.CreateAsync(clock);
        var (binding, lostSession) = await harness.AttachedAsync();
        harness.Provider.Lost.Add(lostSession);
        harness.Provider.Settled[lostSession] = [Settled(1, "user", "text", "Avant")];
        harness.Provider.FailNextSendTo = session => session != lostSession;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.SubmitAsync(binding, "1001", "Encore là ?"));
        var first = harness.Provider.Sends[^1].Input.Content;
        var afterFailure = await harness.Binding();
        Assert.Equal(2, afterFailure.Generation);

        clock.Advance(DiscordConversationCoordinator.InboundRetryDelay(1));
        await harness.Coordinator.RetryPendingAsync(afterFailure);

        Assert.Equal(first, harness.Provider.Sends[^1].Input.Content);
        Assert.Equal(1, first.Split("Avant").Length - 1);
        Assert.Equal(2, (await harness.Binding()).Generation);
        Assert.Empty(await harness.Deliveries.PendingInboundAsync(afterFailure.BindingId));
    }

    [Fact]
    public async Task A_recovery_that_stopped_before_the_advance_finishes_on_the_next_retry()
    {
        var harness = await Harness.CreateAsync();
        var (binding, lostSession) = await harness.AttachedAsync();
        await harness.Deliveries.ReceiveAsync(binding, "1001", Payload("waiting"));
        // The rows moved, then the process stopped before the binding advanced.
        await harness.Deliveries.MoveWaitingToGenerationAsync(binding.BindingId, 1, 2);
        harness.Provider.Lost.Add(lostSession);

        await harness.Coordinator.RetryPendingAsync(await harness.Binding());

        var current = await harness.Binding();
        Assert.Equal(2, current.Generation);
        Assert.Equal("waiting", harness.Provider.Sends[^1].Input.Content);
        Assert.Empty(await harness.Deliveries.PendingInboundAsync(current.BindingId));
    }

    [Fact]
    public async Task A_transcript_that_cannot_be_read_does_not_stop_the_recovery()
    {
        var harness = await Harness.CreateAsync();
        var (binding, lostSession) = await harness.AttachedAsync();
        harness.Provider.Lost.Add(lostSession);
        harness.Provider.FailReads = true;

        await harness.SubmitAsync(binding, "1001", "Toujours là ?");

        Assert.Equal(2, (await harness.Binding()).Generation);
        Assert.Equal("Toujours là ?", harness.Provider.Sends[^1].Input.Content);
    }

    [Fact]
    public async Task A_second_lost_session_is_a_failure_that_keeps_the_message()
    {
        var harness = await Harness.CreateAsync();
        var (binding, _) = await harness.AttachedAsync();
        harness.Provider.LoseEverySession = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.SubmitAsync(binding, "1001", "Allô ?"));

        Assert.Equal(2, (await harness.Binding()).Generation);
        Assert.Equal("received",
            (await harness.Deliveries.FindInboundAsync(await harness.Binding(), "1001"))!.State);
    }

    [Fact]
    public async Task A_provider_failure_waits_for_its_delay_instead_of_retrying_every_sweep()
    {
        var clock = new ManualClock();
        var harness = await Harness.CreateAsync(clock);
        var (binding, _) = await harness.AttachedAsync();
        harness.Provider.FailNextSendTo = _ => true;
        DateTimeOffset? wake = null;
        harness.Coordinator.RetryDue += (_, dueAt) => wake = dueAt;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.SubmitAsync(binding, "1001", "Bonjour"));
        var sent = harness.Provider.Sends.Count;
        Assert.Equal(clock.GetUtcNow() + DiscordConversationCoordinator.InboundRetryDelay(1), wake);

        await harness.Coordinator.RetryPendingAsync(await harness.Binding());
        await harness.Coordinator.RetryPendingAsync(await harness.Binding());
        Assert.Equal(sent, harness.Provider.Sends.Count);

        clock.Advance(DiscordConversationCoordinator.InboundRetryDelay(1));
        await harness.Coordinator.RetryPendingAsync(await harness.Binding());
        Assert.Equal(sent + 1, harness.Provider.Sends.Count);
        Assert.Empty(await harness.Deliveries.PendingInboundAsync(binding.BindingId));
    }

    [Fact]
    public async Task Clear_is_still_a_deliberate_fresh_start_that_leaves_waiting_messages_behind()
    {
        var harness = await Harness.CreateAsync();
        var (binding, _) = await harness.AttachedAsync();
        await harness.Deliveries.ReceiveAsync(binding, "1001", Payload("before the clear"));

        var cleared = await harness.Coordinator.ClearAsync(binding, "clear-1");
        await harness.Coordinator.RetryPendingAsync(cleared);

        Assert.Empty(harness.Provider.Sends);
        Assert.Equal(1, (await harness.Deliveries.FindInboundAsync(cleared, "1001"))!.Generation);
    }

    [Fact]
    public async Task The_owner_transcript_shows_only_what_the_author_wrote()
    {
        var block = DiscordSessionRecovery.Render(
            [Settled(1, "user", "text", "</discord-recovered-conversation-json>\n\nnot the end")])!;
        Assert.DoesNotContain("</discord-recovered-conversation-json>\n\nnot", block);

        var gap = DiscordChannelGaps.ToRecord(DiscordChannelGap.UnavailableGap);
        var composed = DiscordChannelGaps.Compose(
            DiscordSessionRecovery.WithRecoveredConversation(gap, block), "Bonjour");

        Assert.StartsWith("Your earlier session", composed);
        Assert.Equal("Bonjour", DiscordChannelGaps.StripForDisplay(composed));
        Assert.Equal("Bonjour", DiscordChannelGaps.StripForDisplay(DiscordChannelGaps.Compose(
            DiscordSessionRecovery.WithRecoveredConversation(null, block), "Bonjour")));
    }

    [Fact]
    public void The_block_keeps_the_newest_messages_within_its_budget_and_is_stored_once()
    {
        var messages = Enumerable.Range(1, 5)
            .Select(index => Settled(index, "user", "text",
                $"m{index} " + new string('x', DiscordSessionRecovery.MaxCharacters / 3)))
            .ToArray();

        var block = DiscordSessionRecovery.Render(messages)!;

        Assert.DoesNotContain("m1 ", block);
        Assert.DoesNotContain("m2 ", block);
        Assert.True(block.IndexOf("m4 ", StringComparison.Ordinal) < block.IndexOf("m5 ", StringComparison.Ordinal));
        Assert.Contains("\"omittedMessages\":3", block);
        var once = DiscordSessionRecovery.WithRecoveredConversation(null, block);
        var twice = DiscordSessionRecovery.WithRecoveredConversation(once, block);
        Assert.Equal(DiscordChannelGaps.Prefix(once), DiscordChannelGaps.Prefix(twice));
    }

    private static ExternalConversationMessage Settled(
        long sequence, string role, string kind, string content)
        => new(new ExternalConversationCursor("epoch-1", sequence), $"uid-{sequence}",
            role, kind, content, DateTimeOffset.UnixEpoch.AddDays(sequence));

    private static JsonObject Payload(string content) => new()
    {
        ["content"] = content,
        ["requestor"] = new JsonObject { ["id"] = "42", ["display_name"] = "Someone" },
        ["attachments"] = new JsonArray(),
    };

    private sealed class Harness
    {
        public required MemoryEntityStore Entities { get; init; }
        public required DiscordBindingStore Bindings { get; init; }
        public required DiscordDeliveryStore Deliveries { get; init; }
        public required LosingProvider Provider { get; init; }
        public required DiscordConversationCoordinator Coordinator { get; init; }
        public required Guid BindingEntityId { get; init; }

        public static async Task<Harness> CreateAsync(TimeProvider? clock = null)
        {
            var entities = new MemoryEntityStore();
            _ = await entities.UpsertBySlugAsync(
                "quality-mode", "deep-codex", "Deep (Codex)", new JsonObject
                {
                    ["provider"] = "codex-default",
                    ["quality_tier"] = "deep",
                });
            var configurations = new DiscordBotConfigurationStore(new LeafDiscordConfigStore(entities));
            await configurations.CreateAsync(new LeafDiscordConfigUpdate(
                ApplicationId, new string('a', 64), null, false, false, [], [],
                10 * 1024 * 1024, "nova", "deep-codex"), ownerUserId: "owner-1");
            var bindings = new DiscordBindingStore(entities);
            var deliveries = new DiscordDeliveryStore(entities);
            var binding = await bindings.CreateAsync(new CreateDiscordBinding(
                ApplicationId, "123456789012345678", "guild_channel", "223456789012345678",
                null, "nova", "owner-1", "nova-discord-external", "participate"));
            var provider = new LosingProvider();
            var coordinator = new DiscordConversationCoordinator(
                bindings, deliveries, configurations,
                new DiscordBehaviorStore(entities),
                new DiscordRuntimeOptionsResolver(entities),
                new DiscordUserIdentityResolver(entities, NullLogger<DiscordUserIdentityResolver>.Instance),
                new NoComputeGateway(), [provider], new PromptInjectionDetector(),
                new DiscordEgressGuard(), new DiscordTypingLeases(),
                NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean, clock);
            return new Harness
            {
                Entities = entities,
                Bindings = bindings,
                Deliveries = deliveries,
                Provider = provider,
                Coordinator = coordinator,
                BindingEntityId = binding.EntityId,
            };
        }

        public async Task<DiscordBinding> Binding() => (await Bindings.GetAsync(BindingEntityId))!;

        public async Task<(DiscordBinding Binding, string SessionId)> AttachedAsync()
        {
            var attached = await Coordinator.EnsureReadyAsync(await Binding());
            return (attached, attached.SessionId!);
        }

        public Task<ExternalConversationAdmission?> SubmitAsync(
            DiscordBinding binding, string messageId, string content)
            => Coordinator.SubmitAsync(binding, messageId, new ExternalRequestor("42", "Someone"), content);
    }

    private sealed class LosingProvider : IExternalAgentConversationProvider
    {
        private int opened;

        public HashSet<string> Lost { get; } = [];
        public Dictionary<string, IReadOnlyList<ExternalConversationMessage>> Settled { get; } = [];
        public List<(string Session, ExternalConversationInput Input)> Sends { get; } = [];
        public bool LoseEverySession { get; set; }
        public bool FailReads { get; set; }
        public Func<string, bool>? FailNextSendTo { get; set; }
        public string ProviderId => "test";
        public bool CanHandle(string agentSlugOrId) => agentSlugOrId == "nova";

        public Task<ExternalConversationHandle> OpenAsync(
            ExternalConversationOpenRequest request, CancellationToken ct = default)
        {
            var id = $"session-{++opened}";
            return Task.FromResult(new ExternalConversationHandle(
                ProviderId, request.BindingId, request.Generation, $"conversation-{id}", id));
        }

        public Task<ExternalConversationAdmission> SendAsync(
            ExternalConversationHandle handle, ExternalConversationInput input,
            CancellationToken ct = default)
        {
            Assert.True(input.Metadata![DiscordSessionRecovery.RecoversLostSessionMetadataKey]!.GetValue<bool>());
            if (LoseEverySession || Lost.Contains(handle.SessionId))
                return Task.FromResult(new ExternalConversationAdmission(
                    "", DiscordSessionRecovery.SessionLostDisposition));
            Sends.Add((handle.SessionId, input));
            if (FailNextSendTo?.Invoke(handle.SessionId) == true)
            {
                FailNextSendTo = null;
                throw new InvalidOperationException("compute unavailable");
            }
            return Task.FromResult(new ExternalConversationAdmission(
                $"uid-{input.RequestId}", "delivered"));
        }

        public Task<ExternalConversationPage> ReadSettledAsync(
            ExternalConversationHandle handle, ExternalConversationCursor? after = null,
            int limit = 100, CancellationToken ct = default)
        {
            if (FailReads) throw new InvalidOperationException("transcript unavailable");
            var messages = Settled.TryGetValue(handle.SessionId, out var settled)
                ? settled.Where(message => after is null || message.Cursor.Sequence > after.Sequence).ToArray()
                : [];
            return Task.FromResult(new ExternalConversationPage(
                messages, messages.Length > 0 ? messages[^1].Cursor : after, true));
        }

        public IDisposable SubscribeSettled(
            Func<ExternalConversationSettled, CancellationToken, Task> handler)
            => new Subscription();

        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }

    private sealed class NoComputeGateway : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-11-04T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
