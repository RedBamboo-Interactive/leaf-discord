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
}
