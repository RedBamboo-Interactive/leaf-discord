using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordCommonsTests
{
    private const string LabGuildId = "600000000000000006";

    [Fact]
    public async Task Fresh_configuration_has_no_relay_or_guild_default()
    {
        var configurations = new DiscordCommonsConfigurationStore(new MemoryEntityStore());

        var config = await configurations.GetAsync(Guid.NewGuid());

        Assert.NotNull(config);
        Assert.Empty(config.RelayUrl);
        Assert.Empty(config.GuildAllowlist);
        Assert.False(config.Ready);
    }

    [Fact]
    public void Commons_guild_messages_never_reach_a_native_agent_session()
    {
        var allowlist = new HashSet<string>([LabGuildId], StringComparer.Ordinal);

        Assert.False(DiscordCommonsRelayService.ShouldReachNativeAgent(
            LabGuildId, allowlist));
        Assert.True(DiscordCommonsRelayService.ShouldReachNativeAgent(
            "400000000000000004", allowlist));
        Assert.True(DiscordCommonsRelayService.ShouldReachNativeAgent(null, allowlist));
    }

    [Fact]
    public async Task Configuration_projection_never_returns_credentials_or_webhook_urls()
    {
        var store = new MemoryEntityStore();
        var configurations = new DiscordCommonsConfigurationStore(store);
        var botId = Guid.NewGuid();
        var saved = await configurations.SaveAsync(botId,
            new DiscordCommonsConfigurationUpdate(
                "https://relay.example.test",
                "host-secret-for-test",
                false,
                [LabGuildId]));
        _ = await configurations.SetWebhookUrlAsync(
            botId,
            "700000000000000007",
            "https://discord.com/api/webhooks/123456789012345678/test-token");

        var serialized = JsonSerializer.Serialize(
            DiscordCommonsConfigurationStore.View(
                (await configurations.GetAsync(botId))!));

        Assert.True(saved.Ready);
        Assert.DoesNotContain("host-secret-for-test", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", serialized, StringComparison.Ordinal);
        Assert.Contains("HostCredentialConfigured", serialized, StringComparison.Ordinal);
        Assert.Contains("700000000000000007", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingest_queue_is_durable_and_idempotent_by_event_id()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordCommonsIngestStore(store);
        var botId = Guid.NewGuid();
        var payload = new JsonObject
        {
            ["type"] = "human_message",
            ["eventId"] = "gateway-event-1",
            ["content"] = "hello",
        };

        var first = await deliveries.EnqueueAsync(botId, "gateway-event-1", payload);
        var duplicate = await deliveries.EnqueueAsync(botId, "gateway-event-1", payload);

        Assert.Equal(first.EntityId, duplicate.EntityId);
        Assert.Single(await deliveries.PendingAsync());
        await deliveries.MarkSentAsync(first);
        Assert.Empty(await deliveries.PendingAsync());
        Assert.Null((await store.GetAsync(first.EntityId))!.Data["payload"]);
    }

    [Theory]
    [InlineData("général", "general")]
    [InlineData("GENERAL", "general")]
    [InlineData("nova-consult", "nova-consult")]
    public void Discord_channel_names_map_to_protocol_channels(
        string source, string expected)
        => Assert.Equal(expected, DiscordCommonsRelayService.ChannelName(source));

    [Fact]
    public void Human_ingest_forwards_reply_reference_and_role_mentions_without_opening_floor()
    {
        var payload = DiscordCommonsRelayService.BuildHumanMessagePayload(
            "message-1", "human-1", "Lynial", "hello", [],
            ["900000000000000009"], "nova-consult", "thread-1",
            "800000000000000008", 1234);

        Assert.Equal("800000000000000008",
            payload["referencedMessageId"]!.GetValue<string>());
        Assert.Equal("900000000000000009",
            payload["mentionRoles"]![0]!.GetValue<string>());
        Assert.False(payload["opensFloor"]!.GetValue<bool>());
    }

    [Fact]
    public void Envoy_roles_are_mentionable_permissionless_and_follow_public_name()
    {
        var envoy = new DiscordCommonsRelayEnvoy(
            "envoy-1", "user-1", "Owner", "agent-1", "Atlas", "Lynial", "Atlas · Lynial",
            "active", "role-1");
        var stale = new DiscordManagedRole(
            "role-1", "Nova Old", "8", false, false);

        Assert.Equal("Atlas Lynial", DiscordCommonsRolePolicy.RoleName(envoy));
        Assert.True(DiscordCommonsRolePolicy.NeedsUpdate(stale, "Atlas Lynial"));
        Assert.False(DiscordCommonsRolePolicy.NeedsUpdate(
            stale with { Name = "Atlas Lynial", Permissions = "0", Mentionable = true },
            "Atlas Lynial"));
    }

    [Fact]
    public void Role_reconciliation_fails_clearly_without_manage_roles_and_caps_active_envoys()
    {
        var envoy = new DiscordCommonsRelayEnvoy(
            "envoy-1", "user-1", "Owner", "agent-1", "Atlas", "Owner", "Atlas · Owner",
            "active", null);
        var missing = Assert.Throws<UnauthorizedAccessException>(() =>
            DiscordCommonsRolePolicy.EnsureCanReconcile(false, [envoy]));
        Assert.Contains("Manage Roles", missing.Message, StringComparison.Ordinal);

        var tooMany = Enumerable.Range(0, DiscordCommonsRolePolicy.MaximumRolesPerGuild + 1)
            .Select(index => envoy with { Id = $"envoy-{index}" }).ToArray();
        var capped = Assert.Throws<InvalidOperationException>(() =>
            DiscordCommonsRolePolicy.EnsureCanReconcile(true, tooMany));
        Assert.Contains("cap", capped.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suspended_envoys_are_not_counted_as_active_role_targets()
    {
        var suspended = new DiscordCommonsRelayEnvoy(
            "envoy-1", "user-1", "Owner", "agent-1", "Atlas", "Owner", "Atlas · Owner",
            "suspended", "role-1");
        var manySuspended = Enumerable.Range(0, 100)
            .Select(index => suspended with { Id = $"envoy-{index}" }).ToArray();

        DiscordCommonsRolePolicy.EnsureCanReconcile(true, manySuspended);
    }
}
