using System.Collections.Concurrent;

namespace Leaf.Plugins.LeafDiscord;

internal sealed record DiscordMaintenanceRequest(
    string BindingId,
    bool FullReconciliation,
    bool DrainOutbound);

/// <summary>
/// Deduplicates maintenance work by binding. Retry methods deliberately do not wake
/// the loop, so an unsettled session is revisited by the recovery watchdog instead
/// of becoming a busy loop.
/// </summary>
internal sealed class DiscordMaintenanceQueue : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> full = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> drain = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0, 1);

    public void RequestFull(string bindingId)
    {
        full[bindingId] = 0;
        Signal();
    }

    public void RequestDrain(string bindingId)
    {
        drain[bindingId] = 0;
        Signal();
    }

    public void RetryFull(string bindingId) => full[bindingId] = 0;

    public void RetryDrain(string bindingId) => drain[bindingId] = 0;

    public async Task<bool> WaitAsync(TimeSpan recoveryInterval, CancellationToken ct)
        => await wake.WaitAsync(recoveryInterval, ct);

    public IReadOnlyList<DiscordMaintenanceRequest> TakePending()
    {
        var bindingIds = full.Keys.Concat(drain.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = new List<DiscordMaintenanceRequest>(bindingIds.Length);
        foreach (var bindingId in bindingIds)
        {
            var reconcile = full.TryRemove(bindingId, out _);
            var flush = drain.TryRemove(bindingId, out _);
            if (reconcile || flush)
                result.Add(new DiscordMaintenanceRequest(bindingId, reconcile, flush));
        }
        return result;
    }

    public void Dispose() => wake.Dispose();

    private void Signal()
    {
        if (wake.CurrentCount == 0)
            wake.Release();
    }
}
