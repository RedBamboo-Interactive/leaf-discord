using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordMaintenanceQueueTests
{
    [Fact]
    public void Requests_are_deduplicated_and_merge_full_with_drain_work()
    {
        using var queue = new DiscordMaintenanceQueue();
        queue.RequestFull("binding-1");
        queue.RequestFull("binding-1");
        queue.RequestDrain("binding-1");

        var request = Assert.Single(queue.TakePending());

        Assert.Equal("binding-1", request.BindingId);
        Assert.True(request.FullReconciliation);
        Assert.True(request.DrainOutbound);
        Assert.Empty(queue.TakePending());
    }

    [Fact]
    public async Task A_request_arriving_while_work_is_running_survives_the_first_take()
    {
        using var queue = new DiscordMaintenanceQueue();
        queue.RequestFull("binding-1");
        Assert.Single(queue.TakePending());

        queue.RequestFull("binding-1");
        await queue.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Single(queue.TakePending());
    }

    [Fact]
    public async Task Retry_waits_for_the_recovery_watchdog_instead_of_busy_looping()
    {
        using var queue = new DiscordMaintenanceQueue();
        queue.RetryFull("binding-1");

        var signalled = await queue.WaitAsync(
            TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.False(signalled);
        Assert.True(Assert.Single(queue.TakePending()).FullReconciliation);
    }
}
