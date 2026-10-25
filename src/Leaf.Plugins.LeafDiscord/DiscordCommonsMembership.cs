using System.Collections.Concurrent;
using System.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

public static class DiscordCommonsEnvoyActions
{
    public const string Approve = "approve";
    public const string Suspend = "suspend";
}

public sealed record DiscordCommonsMembershipAction(
    DiscordCommonsRelayEnvoy Envoy,
    string Action);

/// <summary>
/// Membership of a Leaf Community guild is the permission for its relay. A member's pending
/// Envoy is approved and a former member's active Envoy is suspended. A suspension is never
/// lifted here: the relay records no reason, so it may be a host moderation decision.
/// </summary>
public static class DiscordCommonsMembershipPolicy
{
    public static IReadOnlyList<DiscordCommonsMembershipAction> Plan(
        IEnumerable<DiscordCommonsRelayEnvoy> envoys,
        IReadOnlySet<string> memberIds)
        => envoys.Where(envoy => !string.IsNullOrWhiteSpace(envoy.OwnerDiscordUserId))
            .Select(envoy => envoy.Status switch
            {
                "pending" when memberIds.Contains(envoy.OwnerDiscordUserId)
                    => new DiscordCommonsMembershipAction(envoy, DiscordCommonsEnvoyActions.Approve),
                "active" when !memberIds.Contains(envoy.OwnerDiscordUserId)
                    => new DiscordCommonsMembershipAction(envoy, DiscordCommonsEnvoyActions.Suspend),
                _ => null,
            })
            .OfType<DiscordCommonsMembershipAction>()
            .ToArray();

    /// <summary>
    /// Ready guild scopes whose relay serves exactly one guild of this bot. A relay shared by
    /// several guilds has no single membership to judge, so it is left alone.
    /// </summary>
    public static (IReadOnlyList<string> Guilds, IReadOnlyList<string> SharedRelayGuilds) MembershipGuilds(
        IEnumerable<DiscordCommonsGuildScope> scopes)
    {
        var groups = scopes.Where(scope => scope.Ready)
            .GroupBy(scope => scope.RelayUrl, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (
            groups.Where(group => group.Count() == 1)
                .Select(group => group.Single().GuildId)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            groups.Where(group => group.Count() > 1)
                .SelectMany(group => group.Select(scope => scope.GuildId))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    internal static TimeSpan? RateLimitDelay(Exception exception) => exception switch
    {
        DiscordRestException { StatusCode: HttpStatusCode.TooManyRequests } discord
            => discord.RetryAfter ?? TimeSpan.FromMinutes(1),
        DiscordCommonsRelayException { StatusCode: HttpStatusCode.TooManyRequests } relay
            => relay.RetryAfter ?? TimeSpan.FromMinutes(1),
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
            => TimeSpan.FromMinutes(1),
        _ => null,
    };

    internal static string SafeError(Exception exception) => exception switch
    {
        DiscordRestException discord => $"discord_http_{(int)discord.StatusCode}",
        DiscordCommonsRelayException relay => $"relay_http_{(int)relay.StatusCode}",
        HttpRequestException { StatusCode: { } status } => $"relay_http_{(int)status}",
        _ => exception.GetType().Name,
    };
}

public sealed class DiscordCommonsRelayException(HttpStatusCode statusCode, TimeSpan? retryAfter)
    : Exception($"Leaf Community relay answered HTTP {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>What the membership reconciler needs from Discord and the relay.</summary>
public interface IDiscordCommonsMembershipGateway
{
    Task<IReadOnlyList<string>> GuildIdsAsync(CancellationToken ct);
    Task<IReadOnlyList<DiscordCommonsRelayEnvoy>> GetEnvoysAsync(string guildId, CancellationToken ct);

    /// <summary>Every human member's user ID; throws rather than return a partial list.</summary>
    Task<IReadOnlySet<string>> ListMemberIdsAsync(string guildId, CancellationToken ct);

    Task<bool> IsMemberAsync(string guildId, string userId, CancellationToken ct);
    Task SetEnvoyStatusAsync(string guildId, string envoyId, string action, CancellationToken ct);
}

public sealed record DiscordCommonsMembershipCycle(bool Healthy, TimeSpan? Backoff);

/// <summary>
/// Reconciles Leaf Community Envoy status with guild membership. Gateway handlers only queue a
/// departure and wake the loop; every Discord and relay call runs on the loop, so a slow or
/// failing relay never blocks the gateway. A failed cycle is retried sooner, a rate limit
/// waits for its Retry-After, and a departure whose suspend failed is caught by the next
/// full reconcile.
/// </summary>
public sealed class DiscordCommonsMembershipReconciler(
    IDiscordCommonsMembershipGateway gateway,
    ILogger logger,
    TimeSpan? interval = null) : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(1);
    private readonly TimeSpan fullInterval = interval ?? DefaultInterval;
    private readonly ConcurrentQueue<(string GuildId, string UserId)> departures = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private int fullRequested = 1;

    public void NotifyDeparture(string guildId, string userId)
    {
        departures.Enqueue((guildId, userId));
        Wake();
    }

    public void RequestFullReconcile()
    {
        Interlocked.Exchange(ref fullRequested, 1);
        Wake();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var nextFullAt = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var full = Interlocked.Exchange(ref fullRequested, 0) == 1
                           || DateTimeOffset.UtcNow >= nextFullAt;
                var cycle = await RunOnceAsync(full, ct);
                if (full || !cycle.Healthy)
                    nextFullAt = DateTimeOffset.UtcNow + (cycle.Healthy ? fullInterval : FailureRetry);
                if (cycle.Backoff is { } backoff)
                {
                    await Task.Delay(backoff, ct);
                    continue;
                }
                var wait = nextFullAt - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await signal.WaitAsync(wait, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task<DiscordCommonsMembershipCycle> RunOnceAsync(
        bool full, CancellationToken ct)
    {
        var healthy = true;
        IReadOnlyList<string> guilds;
        try
        {
            guilds = await gateway.GuildIdsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Leaf Community membership reconciliation could not read its guild scopes: {Error}",
                DiscordCommonsMembershipPolicy.SafeError(ex));
            return new(false, null);
        }

        var seen = new HashSet<(string, string)>();
        while (departures.TryDequeue(out var departure))
        {
            if (!seen.Add(departure) || !guilds.Contains(departure.GuildId)) continue;
            var outcome = await GuardAsync(departure.GuildId, "departure",
                () => HandleDepartureAsync(departure.GuildId, departure.UserId, ct), ct);
            healthy &= outcome.Healthy;
            if (outcome.Backoff is not null) return outcome;
        }
        if (!full) return new(healthy, null);

        foreach (var guildId in guilds)
        {
            var outcome = await GuardAsync(guildId, "reconcile",
                () => ReconcileGuildAsync(guildId, ct), ct);
            healthy &= outcome.Healthy;
            if (outcome.Backoff is not null) return outcome;
        }
        return new(healthy, null);
    }

    public async Task ReconcileGuildAsync(string guildId, CancellationToken ct)
    {
        var envoys = await gateway.GetEnvoysAsync(guildId, ct);
        if (!envoys.Any(envoy => envoy.Status is "pending" or "active")) return;
        var members = await gateway.ListMemberIdsAsync(guildId, ct);
        foreach (var action in DiscordCommonsMembershipPolicy.Plan(envoys, members))
            await ApplyAsync(guildId, action, "membership-reconcile", ct);
    }

    public async Task HandleDepartureAsync(string guildId, string userId, CancellationToken ct)
    {
        var owned = (await gateway.GetEnvoysAsync(guildId, ct))
            .Where(envoy => envoy.Status == "active"
                            && string.Equals(envoy.OwnerDiscordUserId, userId, StringComparison.Ordinal))
            .ToArray();
        if (owned.Length == 0) return;
        // The event may be stale: a member who left and rejoined keeps their Envoy.
        if (await gateway.IsMemberAsync(guildId, userId, ct)) return;
        foreach (var envoy in owned)
            await ApplyAsync(guildId,
                new DiscordCommonsMembershipAction(envoy, DiscordCommonsEnvoyActions.Suspend),
                "member-departed", ct);
    }

    private async Task ApplyAsync(
        string guildId, DiscordCommonsMembershipAction action, string trigger, CancellationToken ct)
    {
        await gateway.SetEnvoyStatusAsync(guildId, action.Envoy.Id, action.Action, ct);
        logger.LogInformation(
            "Leaf Community Envoy {EnvoyId} in guild {GuildId}: {Action} ({Trigger})",
            action.Envoy.Id, guildId, action.Action, trigger);
    }

    private async Task<DiscordCommonsMembershipCycle> GuardAsync(
        string guildId, string phase, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
            return new(true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var backoff = DiscordCommonsMembershipPolicy.RateLimitDelay(ex);
            logger.LogWarning(
                "Leaf Community membership {Phase} failed for guild {GuildId}: {Error}; retry {Retry}",
                phase, guildId, DiscordCommonsMembershipPolicy.SafeError(ex),
                backoff is null ? "next cycle" : $"after {backoff.Value.TotalSeconds:0}s");
            return new(false, backoff);
        }
    }

    private void Wake()
    {
        try { signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    public void Dispose() => signal.Dispose();
}

/// <summary>The bot runtime's view: its own Commons scopes, its REST token, its gateway client.</summary>
internal sealed class DiscordCommonsMembershipGateway(
    Guid botId,
    DiscordCommonsConfigurationStore configurations,
    DiscordCommonsRelayService relay,
    DiscordRestTransport rest,
    DiscordSocketClient source,
    Func<bool> isCurrent,
    Func<CancellationToken, Task<string>> botToken,
    ILogger logger) : IDiscordCommonsMembershipGateway
{
    public async Task<IReadOnlyList<string>> GuildIdsAsync(CancellationToken ct)
    {
        if (!isCurrent()) return [];
        var config = await configurations.GetAsync(botId, ct);
        if (config is null) return [];
        var (guilds, shared) = DiscordCommonsMembershipPolicy.MembershipGuilds(config.GuildScopes.Values);
        if (shared.Count > 0)
            logger.LogWarning(
                "Leaf Community guilds {GuildIds} share one relay; membership is not reconciled for them",
                string.Join(",", shared));
        return guilds.Where(guildId => ulong.TryParse(guildId, out var id) && source.GetGuild(id) is not null)
            .ToArray();
    }

    public Task<IReadOnlyList<DiscordCommonsRelayEnvoy>> GetEnvoysAsync(string guildId, CancellationToken ct)
        => relay.GetEnvoysAsync(botId, guildId, ct);

    public async Task<IReadOnlySet<string>> ListMemberIdsAsync(string guildId, CancellationToken ct)
    {
        var members = await rest.ListGuildMembersAsync(await botToken(ct), guildId, ct);
        // The bot is always a member; without itself the listing is not trustworthy.
        var self = source.CurrentUser?.Id.ToString();
        if (self is null || !members.Any(member => member.UserId == self))
            throw new InvalidOperationException("The guild member listing was incomplete.");
        return members.Where(member => !member.IsBot)
            .Select(member => member.UserId)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> IsMemberAsync(string guildId, string userId, CancellationToken ct)
        => await rest.IsGuildMemberAsync(await botToken(ct), guildId, userId, ct);

    public Task SetEnvoyStatusAsync(string guildId, string envoyId, string action, CancellationToken ct)
        => relay.SetEnvoyStatusAsync(botId, guildId, envoyId, action, ct);
}
