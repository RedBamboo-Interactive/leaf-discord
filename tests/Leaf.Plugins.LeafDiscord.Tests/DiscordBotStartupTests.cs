using Xunit;
using Discord;
using Discord.Net;
using System.Net.WebSockets;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordBotStartupTests
{
    [Theory]
    [InlineData("off", false, false, false, false)]
    [InlineData("active", true, false, true, false)]
    [InlineData("roster", true, true, true, true)]
    public void Presence_mode_selects_only_its_required_gateway_intents(
        string mode,
        bool presences,
        bool members,
        bool voiceStates,
        bool downloadsUsers)
    {
        var config = DiscordBotService.CreateSocketConfig(mode);

        Assert.Equal(presences,
            config.GatewayIntents.HasFlag(GatewayIntents.GuildPresences));
        Assert.Equal(members,
            config.GatewayIntents.HasFlag(GatewayIntents.GuildMembers));
        Assert.Equal(voiceStates,
            config.GatewayIntents.HasFlag(GatewayIntents.GuildVoiceStates));
        Assert.Equal(downloadsUsers, config.AlwaysDownloadUsers);
    }

    [Fact]
    public void Ready_bot_identity_accepts_the_configured_application()
    {
        DiscordBotService.ValidateApplicationId("100000000000000001", 100000000000000001);
    }

    [Fact]
    public void Ready_bot_identity_rejects_a_different_application()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DiscordBotService.ValidateApplicationId("100000000000000001", 123));

        Assert.Equal(
            "The configured Discord application ID does not match the bot token.",
            error.Message);
    }

    [Fact]
    public void Missing_startup_credentials_produce_a_clear_configuration_error()
    {
        var config = DiscordBotConfigurationStore.EmptyDefaults() with
        {
            Enabled = true,
            AgentId = "agent-alpha",
        };

        var error = DiscordBotService.DescribeConfigurationIssue(config);

        Assert.Contains("application ID", error, StringComparison.Ordinal);
        Assert.Contains("bot token", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Connected_socket_reconciles_stale_reconnecting_status()
    {
        var status = DiscordBotService.ReconcileTransportStatus(
            "reconnecting",
            "WebSocket connection was closed",
            ConnectionState.Connected);

        Assert.Equal("connected", status.State);
        Assert.Null(status.LastError);
    }

    [Theory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Disconnected)]
    public void Non_connected_socket_does_not_preserve_a_stale_connected_status(
        ConnectionState observedState)
    {
        var status = DiscordBotService.ReconcileTransportStatus(
            "connected",
            null,
            observedState);

        Assert.Equal("reconnecting", status.State);
        Assert.Equal("Discord Gateway is disconnected.", status.LastError);
    }

    [Fact]
    public void Disconnect_diagnostic_preserves_gateway_close_code_and_reason()
    {
        var exception = new WebSocketException(
            WebSocketError.ConnectionClosedPrematurely,
            "WebSocket connection was closed",
            new WebSocketClosedException(4014, "Disallowed intents"));

        Assert.Equal(
            "Discord Gateway closed with code 4014: Disallowed intents",
            DiscordBotService.DescribeDisconnect(exception));
    }

    [Fact]
    public void Existing_dm_without_counterparty_builds_authenticated_backfill()
    {
        var existing = Binding("dm");

        var request = DiscordBotService.BuildExistingDmBackfill(
            existing, "200000000000000002", "Alice", authorAllowed: true);

        Assert.NotNull(request);
        Assert.Equal(existing.ApplicationId, request.ApplicationId);
        Assert.Equal(existing.ConversationId, request.ConversationId);
        Assert.Equal("200000000000000002", request.CounterpartyUserId);
        Assert.Equal("Alice", request.ConversationName);
    }

    [Theory]
    [InlineData("guild_channel", null, true)]
    [InlineData("dm", "200000000000000002", true)]
    [InlineData("dm", null, false)]
    public void Existing_binding_backfill_rejects_non_migration_cases(
        string kind, string? counterpartyUserId, bool authorAllowed)
    {
        var existing = Binding(kind) with { CounterpartyUserId = counterpartyUserId };

        Assert.Null(DiscordBotService.BuildExistingDmBackfill(
            existing, "200000000000000002", "Alice", authorAllowed));
    }

    private static DiscordBinding Binding(string kind) => new(
        Guid.NewGuid(),
        "discord:app:conversation",
        "app",
        "conversation",
        kind,
        null,
        null,
        "nova",
        "owner",
        "nova-discord-external",
        "direct",
        1,
        "handle",
        "session",
        "epoch",
        0,
        null,
        null,
        true,
        "Alice",
        null);
    [Fact]
    public void Existing_dm_message_requires_a_persisted_allowlisted_counterpart()
    {
        var dm = new DiscordBinding(
            Guid.NewGuid(), "binding-1", "app-1", "conversation-1", "dm",
            null, null, "nova", "owner-1", "nova-discord-external", "direct",
            1, "conversation-handle", "session-1", null, 0, null, null, true,
            "Alice", null)
        {
            CounterpartyUserId = "300000000000000003",
        };
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "300000000000000003",
        };

        DiscordBotService.ValidateExistingBindingTarget(dm, allowlist);
        DiscordBotService.ValidateExistingBindingTarget(
            dm with { Kind = "guild_channel", CounterpartyUserId = null }, new HashSet<string>());
        Assert.Throws<UnauthorizedAccessException>(() =>
            DiscordBotService.ValidateExistingBindingTarget(dm, new HashSet<string>()));
        Assert.Throws<UnauthorizedAccessException>(() =>
            DiscordBotService.ValidateExistingBindingTarget(
                dm with { CounterpartyUserId = null }, allowlist));
    }

    [Fact]
    public async Task Failed_inbound_retry_does_not_starve_reconciliation_or_outbound_drain()
    {
        var phases = new List<string>();
        var failures = new List<string>();

        await DiscordBotRuntime.RunBindingMaintenanceAsync(
            () => throw new InvalidOperationException("provider session is unavailable"),
            () =>
            {
                phases.Add("reconcile");
                return Task.CompletedTask;
            },
            () =>
            {
                phases.Add("drain");
                return Task.CompletedTask;
            },
            (phase, _) => failures.Add(phase));

        Assert.Equal(["inbound retry"], failures);
        Assert.Equal(["reconcile", "drain"], phases);
    }

    [Fact]
    public async Task Failed_maintenance_sweep_keeps_the_loop_alive_and_forces_recovery()
    {
        using var cts = new CancellationTokenSource();
        var sweeps = new List<bool>();
        var failures = new List<Exception>();
        var backoffs = 0;

        var loop = DiscordBotRuntime.RunSupervisedMaintenanceAsync(
            recoverySweep =>
            {
                sweeps.Add(recoverySweep);
                if (sweeps.Count == 2)
                    throw new InvalidOperationException("transient database timeout");
                if (sweeps.Count == 3)
                    cts.Cancel();
                return Task.CompletedTask;
            },
            () =>
            {
                cts.Token.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            },
            () =>
            {
                backoffs++;
                return Task.CompletedTask;
            },
            failures.Add,
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.Equal([true, false, true], sweeps);
        Assert.Single(failures);
        Assert.Equal(1, backoffs);
    }

    [Fact]
    public void Ownerless_bot_defers_legacy_binding_maintenance_until_authenticated_claim()
    {
        var owner = Guid.NewGuid().ToString();
        var legacy = Binding("dm") with { OwnerId = "local-user" };
        var canonical = legacy with { OwnerId = owner };
        var config = new LeafDiscordConfig(
            "app", "key", "token", true, "nova",
            "deep-codex", "leaf-discord-external", "",
            new HashSet<string>(), new HashSet<string>(), 10 * 1024 * 1024);

        Assert.True(DiscordBotRuntime.ShouldDeferLegacyOwnerMaintenance(config, legacy));
        Assert.False(DiscordBotRuntime.ShouldDeferLegacyOwnerMaintenance(
            config with { OwnerUserId = owner }, legacy));
        Assert.False(DiscordBotRuntime.ShouldDeferLegacyOwnerMaintenance(config, canonical));
    }
}
