using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordCommonsMembershipTests
{
    private const string GuildId = "400000000000000004";
    private const string MemberId = "100000000000000001";
    private const string FormerMemberId = "100000000000000002";
    private const string LabOwnerId = "lab-owner_ANdQ24GRCJZqQaEL";
    private const string RelayUrl = "https://relay.example.test";
    private static readonly DiscordCommonsMembershipGuild Guild = new(GuildId, RelayUrl);

    [Fact]
    public async Task Pending_envoy_of_a_current_member_is_approved()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-tester", MemberId, "pending"));
        var reconciler = Reconciler(gateway);

        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);

        Assert.Equal([(GuildId, "envoy-tester", "approve")], gateway.Calls);
        Assert.Equal("active", gateway.Envoys.Single().Status);
    }

    [Fact]
    public async Task Active_envoy_of_a_former_member_is_suspended()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-member", MemberId, "active"));
        gateway.Envoys.Add(Envoy("envoy-gone", FormerMemberId, "active"));
        var reconciler = Reconciler(gateway);

        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);

        Assert.Equal([(GuildId, "envoy-gone", "suspend")], gateway.Calls);
    }

    [Fact]
    public async Task Suspended_envoys_are_never_lifted_and_a_second_pass_does_nothing()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-suspended-member", MemberId, "suspended"));
        gateway.Envoys.Add(Envoy("envoy-suspended-gone", FormerMemberId, "suspended"));
        gateway.Envoys.Add(Envoy("envoy-pending-gone", FormerMemberId, "pending"));
        gateway.Envoys.Add(Envoy("envoy-active-gone", FormerMemberId, "active"));
        var reconciler = Reconciler(gateway);

        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);
        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);

        Assert.Equal([(GuildId, "envoy-active-gone", "suspend")], gateway.Calls);
        Assert.Equal("suspended", gateway.Envoys[0].Status);
        Assert.Equal("pending", gateway.Envoys[2].Status);
    }

    [Fact]
    public async Task Member_leave_or_ban_event_suspends_that_owners_active_envoys()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-gone-1", FormerMemberId, "active"));
        gateway.Envoys.Add(Envoy("envoy-gone-2", FormerMemberId, "active"));
        gateway.Envoys.Add(Envoy("envoy-member", MemberId, "active"));
        var reconciler = Reconciler(gateway);

        reconciler.NotifyMemberChanged(GuildId, FormerMemberId);
        reconciler.NotifyMemberChanged(GuildId, FormerMemberId);
        var cycle = await reconciler.RunOnceAsync(full: false, CancellationToken.None);

        Assert.True(cycle.Healthy);
        Assert.Equal(
            [(GuildId, "envoy-gone-1", "suspend"), (GuildId, "envoy-gone-2", "suspend")],
            gateway.Calls);
        Assert.Equal(0, gateway.MemberListings);
    }

    [Fact]
    public async Task Stale_departure_of_a_returned_member_and_other_guilds_are_ignored()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-member", MemberId, "active"));
        var reconciler = Reconciler(gateway);

        reconciler.NotifyMemberChanged(GuildId, MemberId);
        reconciler.NotifyMemberChanged("900000000000000009", MemberId);
        await reconciler.RunOnceAsync(full: false, CancellationToken.None);

        Assert.Empty(gateway.Calls);
        Assert.Equal("active", gateway.Envoys.Single().Status);
    }

    [Fact]
    public async Task Leave_suspends_and_records_then_rejoin_unsuspends_and_clears_the_record()
    {
        var gateway = new FakeGateway { Members = [MemberId, FormerMemberId] };
        gateway.Envoys.Add(Envoy("envoy-returning", FormerMemberId, "active"));
        var reconciler = Reconciler(gateway);

        gateway.Members.Remove(FormerMemberId);
        reconciler.NotifyMemberChanged(GuildId, FormerMemberId);
        await reconciler.RunOnceAsync(full: false, CancellationToken.None);

        Assert.Equal("suspended", gateway.Envoys.Single().Status);
        var record = Assert.Single(await gateway.Suspensions.ListAsync(RelayUrl));
        Assert.Equal("envoy-returning", record.EnvoyId);
        Assert.Equal(FormerMemberId, record.OwnerDiscordUserId);

        gateway.Members.Add(FormerMemberId);
        reconciler.NotifyMemberChanged(GuildId, FormerMemberId);
        await reconciler.RunOnceAsync(full: false, CancellationToken.None);

        Assert.Equal(
            [(GuildId, "envoy-returning", "suspend"), (GuildId, "envoy-returning", "unsuspend")],
            gateway.Calls);
        Assert.Equal("active", gateway.Envoys.Single().Status);
        Assert.Empty(await gateway.Suspensions.ListAsync(RelayUrl));
    }

    [Fact]
    public async Task Periodic_reconcile_lifts_a_recorded_suspension_of_a_returned_member()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-returning", FormerMemberId, "active"));
        var reconciler = Reconciler(gateway);

        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);
        Assert.Single(await gateway.Suspensions.ListAsync(RelayUrl));
        gateway.Members.Add(FormerMemberId);
        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);

        Assert.Equal("active", gateway.Envoys.Single().Status);
        Assert.Empty(await gateway.Suspensions.ListAsync(RelayUrl));
    }

    [Fact]
    public async Task Host_suspended_member_stays_suspended_on_rejoin()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-moderated", MemberId, "suspended"));
        var reconciler = Reconciler(gateway);

        reconciler.NotifyMemberChanged(GuildId, MemberId);
        await reconciler.RunOnceAsync(full: true, CancellationToken.None);

        Assert.Empty(gateway.Calls);
        Assert.Equal("suspended", gateway.Envoys.Single().Status);
    }

    [Fact]
    public async Task Stale_records_are_dropped_without_acting()
    {
        var gateway = new FakeGateway { Members = [MemberId, FormerMemberId] };
        // Host unsuspended this one by hand; the record no longer describes it.
        gateway.Envoys.Add(Envoy("envoy-host-lifted", MemberId, "active"));
        // Same ID now belongs to someone else's suspended Envoy.
        gateway.Envoys.Add(Envoy("envoy-other-owner", FormerMemberId, "suspended"));
        await gateway.Suspensions.RecordAsync(RelayUrl, GuildId,
            Envoy("envoy-host-lifted", MemberId, "active"));
        await gateway.Suspensions.RecordAsync(RelayUrl, GuildId,
            Envoy("envoy-other-owner", MemberId, "active"));
        await gateway.Suspensions.RecordAsync(RelayUrl, GuildId,
            Envoy("envoy-deleted", MemberId, "active"));
        var reconciler = Reconciler(gateway);

        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);

        Assert.Empty(gateway.Calls);
        Assert.Equal("suspended", gateway.Envoys[1].Status);
        Assert.Empty(await gateway.Suspensions.ListAsync(RelayUrl));
    }

    [Fact]
    public async Task Host_resuspension_after_a_lift_was_observed_is_not_undone()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-1", FormerMemberId, "active"));
        var reconciler = Reconciler(gateway);
        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);

        gateway.Envoys[0] = gateway.Envoys[0] with { Status = "active" };   // host unsuspends
        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None); // still gone: ours again
        gateway.Envoys[0] = gateway.Envoys[0] with { Status = "active" };   // host lifts it again
        gateway.Members.Add(FormerMemberId);
        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None); // record dropped
        gateway.Envoys[0] = gateway.Envoys[0] with { Status = "suspended" }; // host moderates
        reconciler.NotifyMemberChanged(GuildId, FormerMemberId);
        await reconciler.RunOnceAsync(full: true, CancellationToken.None);

        Assert.Equal("suspended", gateway.Envoys[0].Status);
        Assert.Empty(await gateway.Suspensions.ListAsync(RelayUrl));
        Assert.DoesNotContain(gateway.Calls, call => call.Action == "unsuspend");
    }

    [Fact]
    public async Task Records_are_keyed_by_relay_and_envoy()
    {
        var suspensions = new DiscordCommonsMembershipSuspensionStore(new MemoryEntityStore());
        var envoy = Envoy("envoy-1", MemberId, "active");

        await suspensions.RecordAsync(RelayUrl, GuildId, envoy);
        await suspensions.RecordAsync("https://RELAY.example.test/", GuildId, envoy);
        await suspensions.RecordAsync("https://lab.example.test", GuildId, envoy);

        Assert.Single(await suspensions.ListAsync(RelayUrl));
        Assert.Single(await suspensions.ListAsync("https://lab.example.test"));
    }

    [Theory]
    [InlineData("100000000000000001", true)]
    [InlineData("12345678901234567", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("1234567890123456", false)]
    [InlineData("123456789012345678901", false)]
    [InlineData("lab-owner_ANdQ24GRCJZqQaEL", false)]
    [InlineData("10000000000000000x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_discord_snowflakes_are_membership_owners(string? owner, bool expected)
        => Assert.Equal(expected, DiscordCommonsMembershipPolicy.IsDiscordSnowflake(owner));

    [Fact]
    public async Task Host_enrolled_envoys_are_outside_membership_reconcile()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-lab-active", LabOwnerId, "active"));
        gateway.Envoys.Add(Envoy("envoy-lab-pending", LabOwnerId, "pending"));
        gateway.Envoys.Add(Envoy("envoy-lab-suspended", LabOwnerId, "suspended"));
        var reconciler = Reconciler(gateway);

        await reconciler.ReconcileGuildAsync(Guild, CancellationToken.None);
        reconciler.NotifyMemberChanged(GuildId, LabOwnerId);
        await reconciler.RunOnceAsync(full: false, CancellationToken.None);

        Assert.Empty(gateway.Calls);
        Assert.Equal(["active", "pending", "suspended"], gateway.Envoys.Select(envoy => envoy.Status));
        Assert.Empty(await gateway.Suspensions.ListAsync(RelayUrl));
        // Nothing governed by membership, so Discord was not even asked for the member list.
        Assert.Equal(0, gateway.MemberListings);
    }

    [Fact]
    public async Task Repair_unsuspends_a_recorded_host_enrolled_suspension_and_clears_it()
    {
        var gateway = new FakeGateway
        {
            Members = [MemberId],
            MembersFailure = new DiscordRestException(HttpStatusCode.Forbidden, "Missing Access"),
        };
        gateway.Envoys.Add(Envoy("envoy_TCGc4YJc2z67hG2K", LabOwnerId, "suspended"));
        await gateway.Suspensions.RecordAsync(RelayUrl, GuildId,
            Envoy("envoy_TCGc4YJc2z67hG2K", LabOwnerId, "active"));

        await Reconciler(gateway).RunOnceAsync(full: true, CancellationToken.None);

        Assert.Equal([(GuildId, "envoy_TCGc4YJc2z67hG2K", "unsuspend")], gateway.Calls);
        Assert.Equal("active", gateway.Envoys.Single().Status);
        Assert.Empty(await gateway.Suspensions.ListAsync(RelayUrl));
        Assert.Equal(0, gateway.MemberListings);
    }

    [Fact]
    public async Task Repair_only_clears_a_host_enrolled_record_whose_envoy_moved_on()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-lab-lifted", LabOwnerId, "active"));
        gateway.Envoys.Add(Envoy("envoy-lab-reowned", "lab-owner_someone-else", "suspended"));
        foreach (var id in new[] { "envoy-lab-lifted", "envoy-lab-reowned", "envoy-lab-deleted" })
            await gateway.Suspensions.RecordAsync(RelayUrl, GuildId, Envoy(id, LabOwnerId, "active"));

        await Reconciler(gateway).ReconcileGuildAsync(Guild, CancellationToken.None);

        Assert.Empty(gateway.Calls);
        Assert.Equal("suspended", gateway.Envoys[1].Status);
        Assert.Empty(await gateway.Suspensions.ListAsync(RelayUrl));
    }

    [Fact]
    public async Task Failed_repair_keeps_the_record_for_the_next_cycle()
    {
        var gateway = new FakeGateway
        {
            Members = [MemberId],
            StatusFailure = new HttpRequestException("relay down", null, HttpStatusCode.BadGateway),
        };
        gateway.Envoys.Add(Envoy("envoy-lab", LabOwnerId, "suspended"));
        await gateway.Suspensions.RecordAsync(RelayUrl, GuildId, Envoy("envoy-lab", LabOwnerId, "active"));
        var reconciler = Reconciler(gateway);

        Assert.False((await reconciler.RunOnceAsync(full: true, CancellationToken.None)).Healthy);
        Assert.Single(await gateway.Suspensions.ListAsync(RelayUrl));
        gateway.StatusFailure = null;
        Assert.True((await reconciler.RunOnceAsync(full: true, CancellationToken.None)).Healthy);

        Assert.Equal([(GuildId, "envoy-lab", "unsuspend")], gateway.Calls);
        Assert.Empty(await gateway.Suspensions.ListAsync(RelayUrl));
    }

    [Fact]
    public async Task Relay_failures_are_contained_and_retried_on_the_next_cycle()
    {
        var gateway = new FakeGateway
        {
            Members = [MemberId],
            StatusFailure = new HttpRequestException("relay down", null, HttpStatusCode.BadGateway),
        };
        gateway.Envoys.Add(Envoy("envoy-tester", MemberId, "pending"));
        var reconciler = Reconciler(gateway);

        var failed = await reconciler.RunOnceAsync(full: true, CancellationToken.None);
        Assert.False(failed.Healthy);
        Assert.Null(failed.Backoff);
        Assert.Empty(gateway.Calls);

        gateway.StatusFailure = null;
        var recovered = await reconciler.RunOnceAsync(full: true, CancellationToken.None);
        Assert.True(recovered.Healthy);
        Assert.Equal([(GuildId, "envoy-tester", "approve")], gateway.Calls);
    }

    [Fact]
    public async Task Rate_limits_stop_the_cycle_and_honor_retry_after()
    {
        var gateway = new FakeGateway
        {
            Members = [MemberId],
            StatusFailure = new DiscordCommonsRelayException(
                HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)),
        };
        gateway.Envoys.Add(Envoy("envoy-tester", MemberId, "pending"));

        var cycle = await Reconciler(gateway).RunOnceAsync(full: true, CancellationToken.None);

        Assert.False(cycle.Healthy);
        Assert.Equal(TimeSpan.FromSeconds(7), cycle.Backoff);
    }

    [Fact]
    public async Task Unavailable_member_list_suspends_nobody()
    {
        var gateway = new FakeGateway
        {
            MembersFailure = new DiscordRestException(HttpStatusCode.Forbidden, "Missing Access"),
        };
        gateway.Envoys.Add(Envoy("envoy-active", MemberId, "active"));

        var cycle = await Reconciler(gateway).RunOnceAsync(full: true, CancellationToken.None);

        Assert.False(cycle.Healthy);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task Failing_scope_lookup_keeps_queued_departures_for_the_next_cycle()
    {
        var gateway = new FakeGateway { GuildsFailure = new InvalidOperationException("store") };
        gateway.Envoys.Add(Envoy("envoy-gone", FormerMemberId, "active"));
        var reconciler = Reconciler(gateway);

        reconciler.NotifyMemberChanged(GuildId, FormerMemberId);
        Assert.False((await reconciler.RunOnceAsync(full: false, CancellationToken.None)).Healthy);
        gateway.GuildsFailure = null;
        await reconciler.RunOnceAsync(full: false, CancellationToken.None);

        Assert.Equal([(GuildId, "envoy-gone", "suspend")], gateway.Calls);
    }

    [Fact]
    public async Task Run_loop_reconciles_on_start_and_survives_a_failing_relay()
    {
        var gateway = new FakeGateway
        {
            Members = [MemberId],
            EnvoysFailure = new HttpRequestException("relay down"),
        };
        gateway.Envoys.Add(Envoy("envoy-tester", MemberId, "pending"));
        using var reconciler = Reconciler(gateway);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var loop = reconciler.RunAsync(cts.Token);
        await gateway.EnvoyReads.WaitAsync(cts.Token);
        Assert.False(loop.IsFaulted);
        gateway.EnvoysFailure = null;
        reconciler.RequestFullReconcile();
        while (gateway.Calls.Count == 0) await Task.Delay(10, cts.Token);
        await cts.CancelAsync();
        await loop;

        Assert.Equal([(GuildId, "envoy-tester", "approve")], gateway.Calls);
    }

    [Fact]
    public void Only_ready_scopes_with_their_own_relay_are_reconciled()
    {
        var (guilds, shared) = DiscordCommonsMembershipPolicy.MembershipGuilds(
        [
            Scope("400000000000000004", "https://relay.example.test", "host"),
            Scope("500000000000000005", "https://lab.example.test", "lab"),
            Scope("600000000000000006", "https://LAB.example.test", "lab"),
            Scope("700000000000000007", "https://other.example.test", null),
        ]);

        Assert.Equal([new DiscordCommonsMembershipGuild("400000000000000004", RelayUrl)], guilds);
        Assert.Equal(["500000000000000005", "600000000000000006"], shared);
    }

    [Fact]
    public async Task Relay_status_change_posts_to_the_admin_route_with_the_host_credential()
    {
        var handler = new RelayHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var (relay, botId) = await RelayAsync(handler);

        await relay.SetEnvoyStatusAsync(botId, GuildId, "envoy-1", "approve");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            relay.SetEnvoyStatusAsync(botId, GuildId, "envoy-1", "delete"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://relay.example.test/v1/admin/envoys/envoy-1/approve", request.Uri);
        Assert.Equal("Bearer host-secret-for-test", request.Authorization);
    }

    [Fact]
    public async Task Relay_rate_limit_surfaces_status_and_retry_after_without_the_body()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"secret detail"}"""),
        };
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        var (relay, botId) = await RelayAsync(new RelayHandler(limited));

        var error = await Assert.ThrowsAsync<DiscordCommonsRelayException>(() =>
            relay.SetEnvoyStatusAsync(botId, GuildId, "envoy-1", "suspend"));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(12), error.RetryAfter);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(12), DiscordCommonsMembershipPolicy.RateLimitDelay(error));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Single_member_check_maps_unknown_member_to_not_a_member(
        HttpStatusCode status, bool expected)
    {
        var handler = new RelayHandler(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        using var transport = new DiscordRestTransport(handler);

        Assert.Equal(expected, await transport.IsGuildMemberAsync("secret", GuildId, MemberId));
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://discord.com/api/v10/guilds/{GuildId}/members/{MemberId}", request.Uri);
        Assert.Equal("Bot secret", request.Authorization);
    }

    [Fact]
    public async Task Single_member_check_does_not_guess_on_other_errors()
    {
        using var transport = new DiscordRestTransport(new RelayHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }));

        await Assert.ThrowsAsync<DiscordRestException>(() =>
            transport.IsGuildMemberAsync("secret", GuildId, MemberId));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("active")]
    public void Commons_membership_adds_member_and_ban_intents_without_downloading_users(string mode)
    {
        var config = DiscordBotService.CreateSocketConfig(mode, commonsMembershipEvents: true);

        Assert.True(config.GatewayIntents.HasFlag(GatewayIntents.GuildMembers));
        Assert.True(config.GatewayIntents.HasFlag(GatewayIntents.GuildBans));
        Assert.False(config.AlwaysDownloadUsers);
        Assert.False(DiscordBotService.CreateSocketConfig(mode)
            .GatewayIntents.HasFlag(GatewayIntents.GuildBans));
    }

    [Fact]
    public void Disallowed_intents_close_is_recognized_through_wrapping_exceptions()
    {
        var wrapped = new InvalidOperationException(
            "Discord Gateway disconnected before becoming ready.",
            new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "closed",
                new WebSocketClosedException(4014, "Disallowed intent(s).")));

        Assert.True(DiscordBotRuntime.DisallowsIntents(wrapped));
        Assert.False(DiscordBotRuntime.DisallowsIntents(
            new WebSocketClosedException(4004, "Authentication failed.")));
    }

    private static DiscordCommonsMembershipReconciler Reconciler(FakeGateway gateway)
        => new(gateway, gateway.Suspensions, NullLogger.Instance, TimeSpan.FromMinutes(5));

    private static DiscordCommonsRelayEnvoy Envoy(string id, string owner, string status)
        => new(id, owner, "Owner", "agent-1", "Atlas", "Owner", "Atlas · Owner", status, null);

    private static DiscordCommonsGuildScope Scope(string guildId, string relayUrl, string? credential)
        => new(guildId, true, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
            DiscordCommonsNativeAgentModes.Participant, relayUrl, credential);

    private static async Task<(DiscordCommonsRelayService Relay, Guid BotId)> RelayAsync(
        HttpMessageHandler handler)
    {
        var store = new MemoryEntityStore();
        var configurations = new DiscordCommonsConfigurationStore(store);
        var botId = Guid.NewGuid();
        await configurations.SaveAsync(botId, new DiscordCommonsConfigurationUpdate("", null, false, null,
        [
            new DiscordCommonsGuildScopeUpdate(
                GuildId, true, [], [], "https://relay.example.test", "host-secret-for-test"),
        ]));
        return (new DiscordCommonsRelayService(
            configurations, new DiscordCommonsIngestStore(store),
            NullLogger<DiscordCommonsRelayService>.Instance, handler), botId);
    }

    private sealed class FakeGateway : IDiscordCommonsMembershipGateway
    {
        public List<DiscordCommonsRelayEnvoy> Envoys { get; } = [];
        public HashSet<string> Members { get; init; } = [];
        public DiscordCommonsMembershipSuspensionStore Suspensions { get; } = new(new MemoryEntityStore());
        public List<(string Guild, string Envoy, string Action)> Calls { get; } = [];
        public SemaphoreSlim EnvoyReads { get; } = new(0);
        public int MemberListings { get; private set; }
        public Exception? GuildsFailure { get; set; }
        public Exception? EnvoysFailure { get; set; }
        public Exception? MembersFailure { get; init; }
        public Exception? StatusFailure { get; set; }

        public Task<IReadOnlyList<DiscordCommonsMembershipGuild>> GuildsAsync(CancellationToken ct)
            => GuildsFailure is { } failure
                ? Task.FromException<IReadOnlyList<DiscordCommonsMembershipGuild>>(failure)
                : Task.FromResult<IReadOnlyList<DiscordCommonsMembershipGuild>>([Guild]);

        public Task<IReadOnlyList<DiscordCommonsRelayEnvoy>> GetEnvoysAsync(string guildId, CancellationToken ct)
        {
            EnvoyReads.Release();
            return EnvoysFailure is { } failure
                ? Task.FromException<IReadOnlyList<DiscordCommonsRelayEnvoy>>(failure)
                : Task.FromResult<IReadOnlyList<DiscordCommonsRelayEnvoy>>(Envoys.ToArray());
        }

        public Task<IReadOnlySet<string>> ListMemberIdsAsync(string guildId, CancellationToken ct)
        {
            MemberListings++;
            return MembersFailure is { } failure
                ? Task.FromException<IReadOnlySet<string>>(failure)
                : Task.FromResult<IReadOnlySet<string>>(Members.ToHashSet());
        }

        public Task<bool> IsMemberAsync(string guildId, string userId, CancellationToken ct)
            => Task.FromResult(Members.Contains(userId));

        public Task SetEnvoyStatusAsync(string guildId, string envoyId, string action, CancellationToken ct)
        {
            if (StatusFailure is { } failure) return Task.FromException(failure);
            Calls.Add((guildId, envoyId, action));
            var index = Envoys.FindIndex(envoy => envoy.Id == envoyId);
            Envoys[index] = Envoys[index] with { Status = action == "suspend" ? "suspended" : "active" };
            return Task.CompletedTask;
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? Authorization);

    private sealed class RelayHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(response);
        }
    }
}
