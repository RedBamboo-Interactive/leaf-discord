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

    [Fact]
    public async Task Pending_envoy_of_a_current_member_is_approved()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-takit", MemberId, "pending"));
        var reconciler = Reconciler(gateway);

        await reconciler.ReconcileGuildAsync(GuildId, CancellationToken.None);

        Assert.Equal([(GuildId, "envoy-takit", "approve")], gateway.Calls);
        Assert.Equal("active", gateway.Envoys.Single().Status);
    }

    [Fact]
    public async Task Active_envoy_of_a_former_member_is_suspended()
    {
        var gateway = new FakeGateway { Members = [MemberId] };
        gateway.Envoys.Add(Envoy("envoy-member", MemberId, "active"));
        gateway.Envoys.Add(Envoy("envoy-gone", FormerMemberId, "active"));
        var reconciler = Reconciler(gateway);

        await reconciler.ReconcileGuildAsync(GuildId, CancellationToken.None);

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

        await reconciler.ReconcileGuildAsync(GuildId, CancellationToken.None);
        await reconciler.ReconcileGuildAsync(GuildId, CancellationToken.None);

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

        reconciler.NotifyDeparture(GuildId, FormerMemberId);
        reconciler.NotifyDeparture(GuildId, FormerMemberId);
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

        reconciler.NotifyDeparture(GuildId, MemberId);
        reconciler.NotifyDeparture("900000000000000009", MemberId);
        await reconciler.RunOnceAsync(full: false, CancellationToken.None);

        Assert.Empty(gateway.Calls);
        Assert.Equal("active", gateway.Envoys.Single().Status);
    }

    [Fact]
    public async Task Relay_failures_are_contained_and_retried_on_the_next_cycle()
    {
        var gateway = new FakeGateway
        {
            Members = [MemberId],
            StatusFailure = new HttpRequestException("relay down", null, HttpStatusCode.BadGateway),
        };
        gateway.Envoys.Add(Envoy("envoy-takit", MemberId, "pending"));
        var reconciler = Reconciler(gateway);

        var failed = await reconciler.RunOnceAsync(full: true, CancellationToken.None);
        Assert.False(failed.Healthy);
        Assert.Null(failed.Backoff);
        Assert.Empty(gateway.Calls);

        gateway.StatusFailure = null;
        var recovered = await reconciler.RunOnceAsync(full: true, CancellationToken.None);
        Assert.True(recovered.Healthy);
        Assert.Equal([(GuildId, "envoy-takit", "approve")], gateway.Calls);
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
        gateway.Envoys.Add(Envoy("envoy-takit", MemberId, "pending"));

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

        reconciler.NotifyDeparture(GuildId, FormerMemberId);
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
        gateway.Envoys.Add(Envoy("envoy-takit", MemberId, "pending"));
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

        Assert.Equal([(GuildId, "envoy-takit", "approve")], gateway.Calls);
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

        Assert.Equal(["400000000000000004"], guilds);
        Assert.Equal(["500000000000000005", "600000000000000006"], shared);
    }

    [Fact]
    public async Task Relay_status_change_posts_to_the_admin_route_with_the_host_credential()
    {
        var handler = new RelayHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var (relay, botId) = await RelayAsync(handler);

        await relay.SetEnvoyStatusAsync(botId, GuildId, "envoy-1", "approve");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            relay.SetEnvoyStatusAsync(botId, GuildId, "envoy-1", "unsuspend"));

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
        => new(gateway, NullLogger.Instance, TimeSpan.FromMinutes(5));

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
        public List<(string Guild, string Envoy, string Action)> Calls { get; } = [];
        public SemaphoreSlim EnvoyReads { get; } = new(0);
        public int MemberListings { get; private set; }
        public Exception? GuildsFailure { get; set; }
        public Exception? EnvoysFailure { get; set; }
        public Exception? MembersFailure { get; init; }
        public Exception? StatusFailure { get; set; }

        public Task<IReadOnlyList<string>> GuildIdsAsync(CancellationToken ct)
            => GuildsFailure is { } failure
                ? Task.FromException<IReadOnlyList<string>>(failure)
                : Task.FromResult<IReadOnlyList<string>>([GuildId]);

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
            Envoys[index] = Envoys[index] with { Status = action == "approve" ? "active" : "suspended" };
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
