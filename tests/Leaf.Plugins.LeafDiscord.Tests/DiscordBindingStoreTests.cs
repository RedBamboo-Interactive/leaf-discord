using System.Text.Json;
using Leaf.Plugins.LeafDiscord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordBindingStoreTests
{
    [Fact]
    public async Task Automatic_binding_creation_is_idempotent_per_discord_conversation()
    {
        var store = new MemoryEntityStore();
        var bindings = new DiscordBindingStore(store);
        var request = new CreateDiscordBinding(
            "100000000000000001",
            "123456789012345678",
            "guild_channel",
            "223456789012345678",
            null,
            "nova",
            "owner-1",
            "nova-discord-external",
            "participate",
            "support",
            "Dogfooders");

        var created = await Task.WhenAll(
            bindings.GetOrCreateAsync(request),
            bindings.GetOrCreateAsync(request));

        Assert.Equal(created[0].EntityId, created[1].EntityId);
        Assert.Equal("support", created[0].ConversationName);
        Assert.Equal("Dogfooders", created[0].GuildName);
        Assert.Single(await bindings.ListAsync());
    }

    [Fact]
    public async Task Existing_dm_binding_backfills_its_authenticated_counterparty()
    {
        var store = new MemoryEntityStore();
        var bindings = new DiscordBindingStore(store);
        var request = new CreateDiscordBinding(
            "100000000000000001",
            "323456789012345678",
            "dm",
            null,
            null,
            "nova",
            "owner-1",
            "nova-discord-external",
            "direct",
            "Alice",
            null);
        var created = await bindings.GetOrCreateAsync(request);

        var updated = await bindings.GetOrCreateAsync(
            request with { CounterpartyUserId = "300000000000000003" });

        Assert.Equal(created.EntityId, updated.EntityId);
        Assert.Equal("300000000000000003", updated.CounterpartyUserId);
        Assert.Equal(
            "300000000000000003",
            (await bindings.GetAsync(created.EntityId))?.CounterpartyUserId);
    }

    [Fact]
    public async Task Legacy_local_owner_is_repaired_without_replacing_the_binding()
    {
        var store = new MemoryEntityStore();
        var bindings = new DiscordBindingStore(store);
        var created = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "523456789012345678", "guild_channel",
            "223456789012345678", null, "agent-alpha", "local-user",
            "discord-external", "participate", "disponibilités", "Odyssées"));
        var owner = Guid.NewGuid().ToString();

        var repaired = await bindings.RepairLegacyOwnerAsync(created, owner);
        var repeated = await bindings.RepairLegacyOwnerAsync(repaired, Guid.NewGuid().ToString());

        Assert.Equal(created.EntityId, repaired.EntityId);
        Assert.Equal(created.BindingId, repaired.BindingId);
        Assert.Equal(created.Generation, repaired.Generation);
        Assert.Equal(owner, repaired.OwnerId);
        Assert.Equal(owner, repeated.OwnerId);
    }

    [Fact]
    public async Task Binding_pins_agent_and_session_compute_and_no_sentinel_compute()
    {
        var store = new MemoryEntityStore();
        var bindings = new DiscordBindingStore(store);

        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "423456789012345678", "guild_channel",
            "223456789012345678", null, "agent-alpha", "owner-1",
            "discord-external", "participate", "ideas", "Dogfooders", null,
            "standard-opencode"));

        var persisted = await bindings.GetAsync(binding.EntityId);
        Assert.NotNull(persisted);
        Assert.Equal("agent-alpha", persisted.AgentId);
        Assert.Equal("standard-opencode", persisted.SessionQualityMode);
        // The Sentinel chooses its own compute; bindings no longer store one.
        var data = (await store.GetAsync(binding.EntityId))!.Data;
        Assert.False(data.ContainsKey("sentinel_quality_mode"));
    }

    [Fact]
    public async Task Channel_avatar_switches_only_that_binding_and_never_serializes_webhook_secret()
    {
        var store = new MemoryEntityStore();
        var bindings = new DiscordBindingStore(store);
        var created = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "623456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "discord-external", "participate", "announcements", "Leaf"));

        var overridden = await bindings.SetChannelAvatarAsync(
            created, "channel-nova.png", "777", "webhook-secret");

        Assert.Equal(created.BindingId, overridden.BindingId);
        Assert.Equal(created.Generation, overridden.Generation);
        Assert.Equal("channel-nova.png", overridden.ChannelAvatarAssetId);
        Assert.Equal("webhook", overridden.DeliveryMode);
        Assert.Equal("777", overridden.WebhookId);
        Assert.Equal("webhook-secret", overridden.WebhookToken);
        var projected = JsonSerializer.Serialize(overridden);
        Assert.DoesNotContain("webhook-secret", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("WebhookToken", projected, StringComparison.Ordinal);

        var cleared = await bindings.ClearChannelAvatarAsync(overridden);
        Assert.Equal(created.BindingId, cleared.BindingId);
        Assert.Equal(created.Generation, cleared.Generation);
        Assert.Null(cleared.ChannelAvatarAssetId);
        Assert.Equal("bot", cleared.DeliveryMode);
        Assert.Null(cleared.WebhookId);
        Assert.Null(cleared.WebhookToken);
    }

    [Fact]
    public async Task Webhook_token_is_a_vaulted_secret_that_reads_redacted_and_sends_hydrated()
    {
        var store = MemoryEntityStore.WithSeedSecretFields();
        var bindings = new DiscordBindingStore(store);
        var created = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "623456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "discord-external", "participate", "announcements", "Leaf"));

        var overridden = await bindings.SetChannelAvatarAsync(
            created, "channel-nova.png", "777", "webhook-secret");

        Assert.Equal("webhook-secret", store.VaultValue(created.EntityId, "webhook_token"));
        Assert.False(store.StoredData(created.EntityId).ContainsKey("webhook_token"));
        Assert.Equal("777", store.StoredData(created.EntityId)["webhook_id"]?.GetValue<string>());
        var redacted = store.Redacted(created.EntityId);
        Assert.Equal(MemoryEntityStore.RedactedSentinel,
            redacted["webhook_token"]?.GetValue<string>());
        Assert.DoesNotContain("webhook-secret", redacted.ToJsonString(), StringComparison.Ordinal);

        // Every plugin read path the send and relay code uses still gets the token.
        Assert.Equal("webhook-secret", overridden.WebhookToken);
        Assert.Equal("webhook-secret", (await bindings.GetAsync(created.EntityId))?.WebhookToken);
        Assert.Equal("webhook-secret", (await bindings.FindAsync(
            "100000000000000001", "623456789012345678"))?.WebhookToken);
        Assert.Equal("webhook-secret", Assert.Single(await bindings.ListAsync()).WebhookToken);

        // An unrelated write keeps the vaulted token.
        await bindings.MarkReceivedAsync(overridden, "923456789012345678");
        Assert.Equal("webhook-secret", (await bindings.GetAsync(created.EntityId))?.WebhookToken);

        var cleared = await bindings.ClearChannelAvatarAsync(overridden);
        Assert.Null(cleared.WebhookToken);
        Assert.Null(store.VaultValue(created.EntityId, "webhook_token"));
    }
}
