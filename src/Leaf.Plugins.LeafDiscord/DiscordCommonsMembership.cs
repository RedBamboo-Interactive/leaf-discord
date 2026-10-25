using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Discord.WebSocket;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

public static class DiscordCommonsEnvoyActions
{
    public const string Approve = "approve";
    public const string Suspend = "suspend";
    public const string Unsuspend = "unsuspend";
}

public sealed record DiscordCommonsMembershipAction(
    DiscordCommonsRelayEnvoy Envoy,
    string Action);

/// <summary>A Leaf Community guild whose relay serves only that guild.</summary>
public sealed record DiscordCommonsMembershipGuild(string GuildId, string RelayUrl);

/// <summary>An Envoy this host suspended because its owner was no longer a guild member.</summary>
public sealed record DiscordCommonsMembershipSuspension(
    Guid EntityId,
    string RelayUrl,
    string EnvoyId,
    string OwnerDiscordUserId,
    string GuildId);

/// <summary>
/// Membership of a Leaf Community guild is the permission for its relay. A member's pending
/// Envoy is approved and a former member's active Envoy is suspended. When the owner returns,
/// only the suspensions this host recorded are lifted; the relay records no reason, so any
/// other suspension may be a host moderation decision and stays manual.
/// </summary>
public static class DiscordCommonsMembershipPolicy
{
    /// <summary>
    /// Only an Envoy owned by a real Discord user is governed by membership. Host-enrolled
    /// Envoys carry a synthetic owner (the relay mints <c>lab-owner_…</c>) that is never a member.
    /// </summary>
    public static bool IsDiscordSnowflake(string? value)
        => value is { Length: >= 17 and <= 20 } && value.All(char.IsAsciiDigit);

    public static IReadOnlyList<DiscordCommonsMembershipAction> Plan(
        IEnumerable<DiscordCommonsRelayEnvoy> envoys,
        IReadOnlySet<string> memberIds,
        IReadOnlySet<string> membershipSuspendedEnvoyIds)
        => envoys.Where(envoy => IsDiscordSnowflake(envoy.OwnerDiscordUserId))
            .Select(envoy => (envoy, member: memberIds.Contains(envoy.OwnerDiscordUserId)) switch
            {
                ({ Status: "pending" }, true)
                    => new DiscordCommonsMembershipAction(envoy, DiscordCommonsEnvoyActions.Approve),
                ({ Status: "active" }, false)
                    => new DiscordCommonsMembershipAction(envoy, DiscordCommonsEnvoyActions.Suspend),
                ({ Status: "suspended" }, true) when membershipSuspendedEnvoyIds.Contains(envoy.Id)
                    => new DiscordCommonsMembershipAction(envoy, DiscordCommonsEnvoyActions.Unsuspend),
                _ => null,
            })
            .OfType<DiscordCommonsMembershipAction>()
            .ToArray();

    /// <summary>
    /// A record only stands for an Envoy that is still suspended under the same owner. Anything
    /// else means the host (or the relay) changed it since, so the record must not act.
    /// </summary>
    public static bool IsStale(
        DiscordCommonsMembershipSuspension record,
        IReadOnlyDictionary<string, DiscordCommonsRelayEnvoy> envoysById)
        => !envoysById.TryGetValue(record.EnvoyId, out var envoy)
           || envoy.Status != "suspended"
           || !string.Equals(envoy.OwnerDiscordUserId, record.OwnerDiscordUserId, StringComparison.Ordinal);

    /// <summary>
    /// Ready guild scopes whose relay serves exactly one guild of this bot. A relay shared by
    /// several guilds has no single membership to judge, so it is left alone.
    /// </summary>
    public static (IReadOnlyList<DiscordCommonsMembershipGuild> Guilds, IReadOnlyList<string> SharedRelayGuilds)
        MembershipGuilds(IEnumerable<DiscordCommonsGuildScope> scopes)
    {
        var groups = scopes.Where(scope => scope.Ready)
            .GroupBy(scope => DiscordCommonsMembershipSuspensionStore.RelayKey(scope.RelayUrl),
                StringComparer.Ordinal)
            .ToArray();
        return (
            groups.Where(group => group.Count() == 1)
                .Select(group => new DiscordCommonsMembershipGuild(group.Single().GuildId, group.Key))
                .OrderBy(guild => guild.GuildId, StringComparer.Ordinal).ToArray(),
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

/// <summary>
/// Durable record of the suspensions this host made for membership, keyed by relay and Envoy.
/// It is shared by every bot of the installation, so whichever bot sees the owner return can
/// lift a suspension another bot made.
/// </summary>
public sealed class DiscordCommonsMembershipSuspensionStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-commons-membership-suspension";

    public async Task RecordAsync(
        string relayUrl, string guildId, DiscordCommonsRelayEnvoy envoy, CancellationToken ct = default)
    {
        var relay = RelayKey(relayUrl);
        _ = await store.UpsertBySlugAsync(TypeSlug, Slug(relay, envoy.Id),
            "Leaf Community membership suspension", new JsonObject
            {
                ["relay_url"] = relay,
                ["envoy_id"] = envoy.Id,
                ["owner_discord_user_id"] = envoy.OwnerDiscordUserId,
                ["guild_id"] = guildId,
                ["suspended_at"] = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);
    }

    public async Task<IReadOnlyList<DiscordCommonsMembershipSuspension>> ListAsync(
        string relayUrl, CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?> { ["relay_url"] = RelayKey(relayUrl) },
            Limit = 500,
        }, ct)).Select(entity => new DiscordCommonsMembershipSuspension(
                entity.Id,
                JsonRead.String(entity.Data, "relay_url") ?? "",
                JsonRead.String(entity.Data, "envoy_id") ?? "",
                JsonRead.String(entity.Data, "owner_discord_user_id") ?? "",
                JsonRead.String(entity.Data, "guild_id") ?? ""))
            .Where(record => record.EnvoyId.Length > 0)
            .ToArray();

    public Task ClearAsync(DiscordCommonsMembershipSuspension record, CancellationToken ct = default)
        => store.DeleteAsync(record.EntityId, ct);

    public static string RelayKey(string relayUrl)
        => relayUrl.Trim().TrimEnd('/').ToLowerInvariant();

    private static string Slug(string relay, string envoyId)
        => $"commons-membership-suspension-{DiscordBindingStore.Hash($"{relay}\n{envoyId}")}";
}

/// <summary>What the membership reconciler needs from Discord and the relay.</summary>
public interface IDiscordCommonsMembershipGateway
{
    Task<IReadOnlyList<DiscordCommonsMembershipGuild>> GuildsAsync(CancellationToken ct);
    Task<IReadOnlyList<DiscordCommonsRelayEnvoy>> GetEnvoysAsync(string guildId, CancellationToken ct);

    /// <summary>Every human member's user ID; throws rather than return a partial list.</summary>
    Task<IReadOnlySet<string>> ListMemberIdsAsync(string guildId, CancellationToken ct);

    Task<bool> IsMemberAsync(string guildId, string userId, CancellationToken ct);
    Task SetEnvoyStatusAsync(string guildId, string envoyId, string action, CancellationToken ct);
}

public sealed record DiscordCommonsMembershipCycle(bool Healthy, TimeSpan? Backoff);

/// <summary>
/// Reconciles Leaf Community Envoy status with guild membership. Gateway handlers only queue a
/// member change and wake the loop; every Discord and relay call runs on the loop, so a slow or
/// failing relay never blocks the gateway. A failed cycle is retried sooner, a rate limit
/// waits for its Retry-After, and a member change whose action failed is caught by the next
/// full reconcile.
/// </summary>
public sealed class DiscordCommonsMembershipReconciler(
    IDiscordCommonsMembershipGateway gateway,
    DiscordCommonsMembershipSuspensionStore suspensions,
    ILogger logger,
    TimeSpan? interval = null) : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(1);
    private readonly TimeSpan fullInterval = interval ?? DefaultInterval;
    private readonly ConcurrentQueue<(string GuildId, string UserId)> memberChanges = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private int fullRequested = 1;

    /// <summary>A user joined, left, was kicked or was banned; membership is re-checked.</summary>
    public void NotifyMemberChanged(string guildId, string userId)
    {
        memberChanges.Enqueue((guildId, userId));
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
        IReadOnlyDictionary<string, DiscordCommonsMembershipGuild> guilds;
        try
        {
            guilds = (await gateway.GuildsAsync(ct))
                .ToDictionary(guild => guild.GuildId, StringComparer.Ordinal);
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
        while (memberChanges.TryDequeue(out var change))
        {
            if (!seen.Add(change) || !guilds.TryGetValue(change.GuildId, out var guild)) continue;
            var outcome = await GuardAsync(guild.GuildId, "member change",
                () => HandleMemberChangeAsync(guild, change.UserId, ct), ct);
            healthy &= outcome.Healthy;
            if (outcome.Backoff is not null) return outcome;
        }
        if (!full) return new(healthy, null);

        foreach (var guild in guilds.Values)
        {
            var outcome = await GuardAsync(guild.GuildId, "reconcile",
                () => ReconcileGuildAsync(guild, ct), ct);
            healthy &= outcome.Healthy;
            if (outcome.Backoff is not null) return outcome;
        }
        return new(healthy, null);
    }

    public async Task ReconcileGuildAsync(DiscordCommonsMembershipGuild guild, CancellationToken ct)
    {
        var envoys = await gateway.GetEnvoysAsync(guild.GuildId, ct);
        var records = await LiveRecordsAsync(guild, envoys, ct);
        if (records.Count == 0 && !envoys.Any(envoy => envoy.Status is "pending" or "active"
                && DiscordCommonsMembershipPolicy.IsDiscordSnowflake(envoy.OwnerDiscordUserId)))
            return;
        var members = await gateway.ListMemberIdsAsync(guild.GuildId, ct);
        await ApplyPlanAsync(guild, envoys, members, records, "membership-reconcile", ct);
    }

    public async Task HandleMemberChangeAsync(
        DiscordCommonsMembershipGuild guild, string userId, CancellationToken ct)
    {
        if (!DiscordCommonsMembershipPolicy.IsDiscordSnowflake(userId)) return;
        var envoys = await gateway.GetEnvoysAsync(guild.GuildId, ct);
        var records = (await LiveRecordsAsync(guild, envoys, ct))
            .Where(pair => pair.Value.OwnerDiscordUserId == userId)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var owned = envoys.Where(envoy => string.Equals(
            envoy.OwnerDiscordUserId, userId, StringComparison.Ordinal)).ToArray();
        if (records.Count == 0 && !owned.Any(envoy => envoy.Status is "pending" or "active")) return;
        // Events can arrive late or out of order: Discord decides who is a member now.
        IReadOnlySet<string> members = await gateway.IsMemberAsync(guild.GuildId, userId, ct)
            ? new HashSet<string>([userId], StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        await ApplyPlanAsync(guild, owned, members, records, "member-changed", ct);
    }

    /// <summary>Records still describing a suspended Envoy; stale ones are dropped unused.</summary>
    private async Task<IReadOnlyDictionary<string, DiscordCommonsMembershipSuspension>> LiveRecordsAsync(
        DiscordCommonsMembershipGuild guild,
        IReadOnlyList<DiscordCommonsRelayEnvoy> envoys,
        CancellationToken ct)
    {
        var byId = envoys.GroupBy(envoy => envoy.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var live = new Dictionary<string, DiscordCommonsMembershipSuspension>(StringComparer.Ordinal);
        foreach (var record in await suspensions.ListAsync(guild.RelayUrl, ct))
        {
            if (!DiscordCommonsMembershipPolicy.IsDiscordSnowflake(record.OwnerDiscordUserId))
            {
                await RepairNonMemberSuspensionAsync(guild, record, byId, ct);
                continue;
            }
            if (DiscordCommonsMembershipPolicy.IsStale(record, byId) || !live.TryAdd(record.EnvoyId, record))
            {
                await suspensions.ClearAsync(record, ct);
                logger.LogInformation(
                    "Leaf Community membership suspension record for Envoy {EnvoyId} in guild {GuildId} "
                    + "is stale and was dropped", record.EnvoyId, guild.GuildId);
            }
        }
        return live;
    }

    /// <summary>
    /// A recorded suspension of a host-enrolled Envoy (synthetic owner) was made before such
    /// Envoys were excluded; membership never applied to it. Lift it if the relay still shows
    /// the suspension this host made, then forget it either way.
    /// </summary>
    private async Task RepairNonMemberSuspensionAsync(
        DiscordCommonsMembershipGuild guild,
        DiscordCommonsMembershipSuspension record,
        IReadOnlyDictionary<string, DiscordCommonsRelayEnvoy> envoysById,
        CancellationToken ct)
    {
        var lift = !DiscordCommonsMembershipPolicy.IsStale(record, envoysById);
        if (lift)
            await gateway.SetEnvoyStatusAsync(
                guild.GuildId, record.EnvoyId, DiscordCommonsEnvoyActions.Unsuspend, ct);
        await suspensions.ClearAsync(record, ct);
        logger.LogInformation(
            "Leaf Community Envoy {EnvoyId} in guild {GuildId}: host-enrolled Envoy outside membership; "
            + "{Repair} (membership-repair)",
            record.EnvoyId, guild.GuildId, lift ? "unsuspended and record cleared" : "record cleared");
    }

    private async Task ApplyPlanAsync(
        DiscordCommonsMembershipGuild guild,
        IEnumerable<DiscordCommonsRelayEnvoy> envoys,
        IReadOnlySet<string> members,
        IReadOnlyDictionary<string, DiscordCommonsMembershipSuspension> records,
        string trigger,
        CancellationToken ct)
    {
        var recorded = records.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var action in DiscordCommonsMembershipPolicy.Plan(envoys, members, recorded))
        {
            await gateway.SetEnvoyStatusAsync(guild.GuildId, action.Envoy.Id, action.Action, ct);
            logger.LogInformation(
                "Leaf Community Envoy {EnvoyId} in guild {GuildId}: {Action} ({Trigger})",
                action.Envoy.Id, guild.GuildId, action.Action, trigger);
            // The record follows the relay change: if it cannot be written, the Envoy stays
            // suspended until the host lifts it, never lifted by mistake.
            if (action.Action == DiscordCommonsEnvoyActions.Suspend)
                await suspensions.RecordAsync(guild.RelayUrl, guild.GuildId, action.Envoy, ct);
            else if (action.Action == DiscordCommonsEnvoyActions.Unsuspend)
                await suspensions.ClearAsync(records[action.Envoy.Id], ct);
        }
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
    public async Task<IReadOnlyList<DiscordCommonsMembershipGuild>> GuildsAsync(CancellationToken ct)
    {
        if (!isCurrent()) return [];
        var config = await configurations.GetAsync(botId, ct);
        if (config is null) return [];
        var (guilds, shared) = DiscordCommonsMembershipPolicy.MembershipGuilds(config.GuildScopes.Values);
        if (shared.Count > 0)
            logger.LogWarning(
                "Leaf Community guilds {GuildIds} share one relay; membership is not reconciled for them",
                string.Join(",", shared));
        return guilds.Where(guild => ulong.TryParse(guild.GuildId, out var id) && source.GetGuild(id) is not null)
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
