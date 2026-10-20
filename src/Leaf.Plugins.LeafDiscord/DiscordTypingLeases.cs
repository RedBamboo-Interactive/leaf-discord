namespace Leaf.Plugins.LeafDiscord;

internal readonly record struct DiscordTypingAdmission(string BindingId, long Sequence);
internal readonly record struct DiscordTypingSnapshot(string BindingId, long ThroughSequence);

public sealed class DiscordTypingLeases : IDisposable
{
    private sealed record AdmissionState(bool Admitted, string? MessageUid);

    private sealed class Lease(
        Func<IDisposable> createIndicator,
        CancellationTokenSource timeout)
    {
        public Func<IDisposable> CreateIndicator { get; } = createIndicator;
        public IDisposable? Indicator { get; set; }
        public CancellationTokenSource Timeout { get; } = timeout;
        public Dictionary<long, AdmissionState> Admissions { get; } = [];
        public long NextSequence { get; set; }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private readonly TimeSpan maxLifetime;

    public DiscordTypingLeases() : this(TimeSpan.FromMinutes(10)) { }

    internal DiscordTypingLeases(TimeSpan maxLifetime)
    {
        if (maxLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxLifetime));
        this.maxLifetime = maxLifetime;
    }

    internal DiscordTypingAdmission Begin(
        string bindingId,
        Func<IDisposable> createIndicator,
        bool startImmediately = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        ArgumentNullException.ThrowIfNull(createIndicator);
        lock (gate)
        {
            if (!leases.TryGetValue(bindingId, out var lease))
            {
                var timeout = new CancellationTokenSource();
                lease = new Lease(createIndicator, timeout);
                if (startImmediately) Activate(lease);
                leases[bindingId] = lease;
                _ = ExpireAsync(bindingId, lease, timeout.Token);
            }
            else if (startImmediately)
            {
                Activate(lease);
            }

            var sequence = checked(++lease.NextSequence);
            lease.Admissions[sequence] = new AdmissionState(false, null);
            return new DiscordTypingAdmission(bindingId, sequence);
        }
    }

    internal void MarkAdmitted(
        DiscordTypingAdmission admission,
        string? messageUid = null)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(admission.BindingId, out var lease)
                || !lease.Admissions.ContainsKey(admission.Sequence))
                return;
            lease.Admissions[admission.Sequence] = new AdmissionState(true, messageUid);
        }
    }

    internal void Activate(string bindingId, string? messageUid)
    {
        if (string.IsNullOrWhiteSpace(messageUid)) return;
        lock (gate)
        {
            if (!leases.TryGetValue(bindingId, out var lease)
                || !lease.Admissions.Values.Any(item =>
                    item.Admitted
                    && string.Equals(item.MessageUid, messageUid, StringComparison.Ordinal)))
                return;
            Activate(lease);
        }
    }

    internal void Cancel(DiscordTypingAdmission admission)
    {
        Lease? completed = null;
        lock (gate)
        {
            if (!leases.TryGetValue(admission.BindingId, out var lease)
                || !lease.Admissions.Remove(admission.Sequence))
                return;
            if (lease.Admissions.Count > 0) return;
            leases.Remove(admission.BindingId);
            completed = lease;
        }
        DisposeLease(completed);
    }

    internal DiscordTypingSnapshot? Capture(string bindingId)
    {
        lock (gate)
        {
            return leases.TryGetValue(bindingId, out var lease)
                ? new DiscordTypingSnapshot(bindingId, lease.NextSequence)
                : null;
        }
    }

    internal void CompleteQuiescent(DiscordTypingSnapshot snapshot)
    {
        Lease? completed = null;
        lock (gate)
        {
            if (!leases.TryGetValue(snapshot.BindingId, out var lease)) return;
            foreach (var sequence in lease.Admissions
                         .Where(item => item.Key <= snapshot.ThroughSequence && item.Value.Admitted)
                         .Select(item => item.Key)
                         .ToArray())
                lease.Admissions.Remove(sequence);
            if (lease.Admissions.Count > 0) return;
            leases.Remove(snapshot.BindingId);
            completed = lease;
        }
        DisposeLease(completed);
    }

    private async Task ExpireAsync(string bindingId, Lease expected, CancellationToken ct)
    {
        try
        {
            await Task.Delay(maxLifetime, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        Lease? expired = null;
        lock (gate)
        {
            if (leases.TryGetValue(bindingId, out var current)
                && ReferenceEquals(current, expected))
            {
                leases.Remove(bindingId);
                expired = current;
            }
        }
        DisposeLease(expired);
    }

    public void Dispose()
    {
        Lease[] pending;
        lock (gate)
        {
            pending = leases.Values.ToArray();
            leases.Clear();
        }
        foreach (var lease in pending) DisposeLease(lease);
    }

    private static void DisposeLease(Lease? lease)
    {
        if (lease is null) return;
        lease.Timeout.Cancel();
        lease.Timeout.Dispose();
        lease.Indicator?.Dispose();
    }

    private static void Activate(Lease lease)
        => lease.Indicator ??= lease.CreateIndicator();
}
