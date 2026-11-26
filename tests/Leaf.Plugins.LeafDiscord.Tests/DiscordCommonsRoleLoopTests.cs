using Leaf.Plugins.LeafDiscord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// 2026-11-26 (ad7278d0): Public Agent role reconciliation read its Commons configuration, and wrote
/// its error state, outside any guard, so one transient database failure ended the loop for good
/// (the N17 shape). Every tick is now contained; only cancellation ends it.
/// </summary>
public sealed class DiscordCommonsRoleLoopTests
{
    [Fact]
    public async Task A_transient_failure_is_reported_and_the_next_tick_reconciles_again()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reconciles = 0;
        var failures = new List<Exception>();

        await DiscordBotRuntime.RunPeriodicReconciliationAsync(
            _ =>
            {
                reconciles++;
                if (reconciles == 1)
                    throw new InvalidOperationException(
                        "An exception has been raised that is likely due to a transient failure.",
                        new TimeoutException("The operation has timed out."));
                return Task.CompletedTask;
            },
            _ => ValueTask.FromResult(reconciles < 3),
            failures.Add,
            cts.Token);

        Assert.Equal(3, reconciles);
        var failure = Assert.Single(failures);
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public async Task Cancellation_ends_the_loop_and_is_not_reported_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var failures = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiscordBotRuntime.RunPeriodicReconciliationAsync(
                token =>
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                _ => ValueTask.FromResult(true),
                _ => failures++,
                cts.Token));

        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task A_failure_every_tick_never_ends_the_loop_before_it_is_stopped()
    {
        using var cts = new CancellationTokenSource();
        var failures = 0;
        var ticks = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiscordBotRuntime.RunPeriodicReconciliationAsync(
                _ => throw new TimeoutException("database unavailable"),
                token =>
                {
                    if (++ticks == 5) cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(true);
                },
                _ => failures++,
                cts.Token));

        Assert.Equal(5, failures);
    }
}
