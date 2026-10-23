using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordBotConfigurationStoreTests
{
    [Fact]
    public async Task Fresh_install_does_not_create_a_Nova_singleton()
    {
        var entities = new MemoryEntityStore();
        var legacy = new LeafDiscordConfigStore(entities);
        var bots = new DiscordBotConfigurationStore(entities, legacy);

        Assert.Empty(await bots.ListAsync());
        Assert.Null(await legacy.GetExistingAsync());
    }

    [Fact]
    public async Task Existing_singleton_migrates_once_without_changing_application_identity()
    {
        var entities = new MemoryEntityStore();
        var legacy = new LeafDiscordConfigStore(entities);
        await legacy.SaveAsync(Update(
            "100000000000000001", "nova", enabled: true, token: "secret-token"));
        var bots = new DiscordBotConfigurationStore(entities, legacy);

        var first = Assert.Single(await bots.ListAsync());
        var second = Assert.Single(await bots.ListAsync());

        Assert.NotEqual(Guid.Empty, first.EntityId);
        Assert.Equal("100000000000000001", first.ApplicationId);
        Assert.Equal("nova", first.AgentId);
        Assert.Equal("secret-token", first.BotToken);
        Assert.Equal(first.EntityId, second.EntityId);
    }

    [Fact]
    public async Task Multiple_Agent_bots_keep_credentials_and_application_ids_isolated()
    {
        var entities = new MemoryEntityStore();
        var bots = new DiscordBotConfigurationStore(
            entities, new LeafDiscordConfigStore(entities));

        var nova = await bots.CreateAsync(Update(
            "100000000000000001", "nova", token: "nova-token"));
        var agent = await bots.CreateAsync(Update(
            "100000000000000002", "agent-alpha", token: "agent-token"));

        Assert.Equal(2, (await bots.ListAsync()).Count);
        Assert.Equal("nova-token",
            (await bots.FindByApplicationIdAsync(nova.ApplicationId))?.BotToken);
        Assert.Equal("agent-token",
            (await bots.FindByApplicationIdAsync(agent.ApplicationId))?.BotToken);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            bots.CreateAsync(Update(nova.ApplicationId, "other")));
    }

    [Fact]
    public async Task Explicit_bot_owner_survives_reads_and_is_not_reclaimed_by_another_admin()
    {
        var entities = new MemoryEntityStore();
        var bots = new DiscordBotConfigurationStore(
            entities, new LeafDiscordConfigStore(entities));
        var owner = Guid.NewGuid().ToString();
        var otherAdmin = Guid.NewGuid().ToString();

        var created = await bots.CreateAsync(
            Update("100000000000000003", "agent-alpha"),
            ownerUserId: owner);
        var read = await bots.FindByApplicationIdAsync(created.ApplicationId);
        var retained = await bots.EnsureOwnerAsync(created.EntityId, otherAdmin);

        Assert.Equal(owner, read?.OwnerUserId);
        Assert.Equal(owner, retained.OwnerUserId);
    }

    [Fact]
    public async Task Legacy_bot_without_owner_is_claimed_once_by_authenticated_admin()
    {
        var entities = new MemoryEntityStore();
        var bots = new DiscordBotConfigurationStore(
            entities, new LeafDiscordConfigStore(entities));
        var created = await bots.CreateAsync(
            Update("100000000000000004", "agent-alpha"));
        var owner = Guid.NewGuid().ToString();

        var claimed = await bots.EnsureOwnerAsync(created.EntityId, owner);

        Assert.Equal(owner, claimed.OwnerUserId);
        Assert.Equal(owner, (await bots.GetAsync(created.EntityId))?.OwnerUserId);
    }

    [Fact]
    public void Canonical_avatar_accepts_only_local_RedLeaf_Assets()
    {
        Assert.Equal("avatar.png",
            DiscordBotRuntime.CanonicalAssetId("/api/assets/avatar.png"));
        Assert.Equal("avatar.webp",
            DiscordBotRuntime.CanonicalAssetId("avatar.webp"));
        Assert.Null(DiscordBotRuntime.CanonicalAssetId(
            "https://example.test/private-outfit.png"));
        Assert.Null(DiscordBotRuntime.CanonicalAssetId("../avatar.png"));
    }

    [Fact]
    public async Task Explicit_bot_and_guild_avatars_survive_unrelated_configuration_updates()
    {
        var entities = new MemoryEntityStore();
        var bots = new DiscordBotConfigurationStore(
            entities, new LeafDiscordConfigStore(entities));
        var created = await bots.CreateAsync(Update(
            "100000000000000005", "nova", token: "nova-token"));

        await bots.SetAvatarAsync(created.EntityId, "discord-nova.png");
        await bots.MarkAvatarSynchronizedAsync(created.EntityId, "global-fingerprint");
        await bots.SetGuildAvatarAsync(
            created.EntityId, "223456789012345678", "leaf-nova.png");
        await bots.MarkGuildAvatarSynchronizedAsync(
            created.EntityId, "223456789012345678", "guild-fingerprint");
        await bots.SaveAsync(created.EntityId, Update(
            created.ApplicationId, "nova", enabled: true, token: null));

        var persisted = await bots.GetAsync(created.EntityId);
        Assert.NotNull(persisted);
        Assert.True(persisted.AvatarManaged);
        Assert.Equal("discord-nova.png", persisted.AvatarAssetId);
        Assert.Equal("global-fingerprint", persisted.AvatarFingerprint);
        Assert.Equal("leaf-nova.png",
            persisted.GuildAvatarAssetIds["223456789012345678"]);
        Assert.Equal("guild-fingerprint",
            persisted.GuildAvatarFingerprints["223456789012345678"]);
    }

    private static LeafDiscordConfigUpdate Update(
        string applicationId,
        string agentId,
        bool enabled = false,
        string? token = null) => new(
        applicationId,
        new string('a', 64),
        token,
        false,
        enabled,
        [],
        [],
        10 * 1024 * 1024,
        agentId,
        "deep-codex");
}
