using Leaf.Plugins.LeafDiscord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordTypingLeasesTests
{
    [Fact]
    public void Typing_remains_active_until_every_admitted_turn_settles()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var indicator = new FakeIndicator();

        var first = leases.Begin("binding", () => indicator);
        leases.MarkAdmitted(first);
        var second = leases.Begin(
            "binding", () => throw new InvalidOperationException("must reuse"));
        leases.MarkAdmitted(second);
        var snapshot = Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding"));

        leases.CompleteQuiescent(snapshot);
        leases.CompleteQuiescent(snapshot);

        Assert.Equal(1, indicator.DisposeCount);
    }

    [Fact]
    public void Quiescent_snapshot_does_not_release_a_later_admitted_turn()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var indicator = new FakeIndicator();
        var first = leases.Begin("binding", () => indicator);
        leases.MarkAdmitted(first);
        var firstSnapshot = Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding"));

        var second = leases.Begin(
            "binding", () => throw new InvalidOperationException("must reuse"));
        leases.MarkAdmitted(second);
        leases.CompleteQuiescent(firstSnapshot);

        Assert.Equal(0, indicator.DisposeCount);

        var secondSnapshot = Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding"));
        leases.CompleteQuiescent(secondSnapshot);

        Assert.Equal(1, indicator.DisposeCount);
    }

    [Fact]
    public void Quiescent_snapshot_does_not_release_an_admission_still_in_progress()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var indicator = new FakeIndicator();
        var admission = leases.Begin("binding", () => indicator);
        var snapshot = Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding"));

        leases.CompleteQuiescent(snapshot);
        Assert.Equal(0, indicator.DisposeCount);

        leases.MarkAdmitted(admission);
        leases.CompleteQuiescent(
            Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding")));

        Assert.Equal(1, indicator.DisposeCount);
    }

    [Fact]
    public void Cancelling_every_unadmitted_turn_releases_typing_once()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var indicator = new FakeIndicator();
        var first = leases.Begin("binding", () => indicator);
        var second = leases.Begin(
            "binding", () => throw new InvalidOperationException("must reuse"));

        leases.Cancel(first);
        Assert.Equal(0, indicator.DisposeCount);

        leases.Cancel(second);
        leases.Cancel(second);

        Assert.Equal(1, indicator.DisposeCount);
    }

    [Fact]
    public void Deferred_typing_activates_only_for_its_admitted_message_uid()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var indicator = new FakeIndicator();
        var createCount = 0;
        var admission = leases.Begin("binding", () =>
        {
            createCount++;
            return indicator;
        }, startImmediately: false);
        leases.MarkAdmitted(admission, "turn-1");

        leases.Activate("binding", "older-turn");
        Assert.Equal(0, createCount);

        leases.Activate("binding", "turn-1");
        leases.Activate("binding", "turn-1");
        Assert.Equal(1, createCount);

        leases.CompleteQuiescent(
            Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding")));
        Assert.Equal(1, indicator.DisposeCount);
    }

    [Fact]
    public void Deferred_no_reply_settles_without_creating_an_indicator()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var createCount = 0;
        var admission = leases.Begin("binding", () =>
        {
            createCount++;
            return new FakeIndicator();
        }, startImmediately: false);
        leases.MarkAdmitted(admission, "turn-1");

        leases.CompleteQuiescent(
            Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding")));

        Assert.Equal(0, createCount);
        Assert.Null(leases.Capture("binding"));
    }

    [Fact]
    public async Task Deferred_selective_turn_starts_typing_after_grace_period()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var createCount = 0;
        var admission = leases.Begin("binding", () =>
        {
            Interlocked.Increment(ref createCount);
            return new FakeIndicator();
        }, startImmediately: false);
        leases.ActivateAfter(admission, TimeSpan.FromMilliseconds(10));
        await WaitUntilAsync(() => Volatile.Read(ref createCount) == 1);

        Assert.Equal(1, createCount);
    }

    [Fact]
    public async Task Deferred_no_reply_cancels_scheduled_typing_before_it_appears()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var createCount = 0;
        var admission = leases.Begin("binding", () =>
        {
            Interlocked.Increment(ref createCount);
            return new FakeIndicator();
        }, startImmediately: false);
        leases.MarkAdmitted(admission, "turn-1");
        leases.ActivateAfter(admission, TimeSpan.FromMilliseconds(30));

        leases.CompleteQuiescent(
            Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding")));
        await Task.Delay(80);

        Assert.Equal(0, createCount);
    }

    [Fact]
    public void Refresh_restarts_a_visible_indicator_without_ending_the_lease()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var created = new List<FakeIndicator>();
        var admission = leases.Begin("binding", () =>
        {
            var indicator = new FakeIndicator();
            created.Add(indicator);
            return indicator;
        });
        leases.MarkAdmitted(admission, "turn-1");

        leases.Refresh("binding");

        Assert.Equal(2, created.Count);
        Assert.Equal(1, created[0].DisposeCount);
        Assert.Equal(0, created[1].DisposeCount);

        leases.CompleteQuiescent(Assert.IsType<DiscordTypingSnapshot>(leases.Capture("binding")));
        Assert.Equal(1, created[1].DisposeCount);
    }

    [Fact]
    public void Refresh_does_not_start_typing_that_is_not_showing()
    {
        using var leases = new DiscordTypingLeases(TimeSpan.FromMinutes(1));
        var createCount = 0;
        leases.Begin("binding", () =>
        {
            createCount++;
            return new FakeIndicator();
        }, startImmediately: false);

        leases.Refresh("binding");
        leases.Refresh("unknown");

        Assert.Equal(0, createCount);
    }

    private sealed class FakeIndicator : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
