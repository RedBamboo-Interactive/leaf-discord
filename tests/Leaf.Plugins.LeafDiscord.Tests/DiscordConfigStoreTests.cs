using Leaf.Plugins.LeafDiscord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordConfigStoreTests
{
    private const string ApplicationId = "100000000000000001";
    private static readonly string PublicKey = new('a', 64);

    [Fact]
    public async Task Fresh_legacy_seed_contains_no_application_credentials_or_avatar()
    {
        var config = await new LeafDiscordConfigStore(new MemoryEntityStore()).GetAsync();

        Assert.Empty(config.ApplicationId);
        Assert.Empty(config.InteractionPublicKey);
        Assert.Empty(config.AvatarAssetId);
        Assert.False(config.Enabled);
    }

    [Fact]
    public async Task Existing_legacy_entity_is_not_overwritten_by_seed_defaults()
    {
        var entities = new MemoryEntityStore();
        var existing = await entities.UpsertBySlugAsync(
            LeafDiscordConfigStore.TypeSlug,
            LeafDiscordConfigStore.Slug,
            "Discord",
            new System.Text.Json.Nodes.JsonObject
            {
                ["application_id"] = ApplicationId,
                ["interaction_public_key"] = PublicKey,
                ["avatar_asset_id"] = "existing-avatar.png",
            });
        var config = new LeafDiscordConfigStore(entities);

        var ensured = await config.EnsureAsync();

        Assert.Equal(existing.Id, ensured.Id);
        Assert.Equal(ApplicationId, ensured.Data["application_id"]?.GetValue<string>());
        Assert.Equal(PublicKey, ensured.Data["interaction_public_key"]?.GetValue<string>());
        Assert.Equal("existing-avatar.png", ensured.Data["avatar_asset_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Agent_and_compute_profiles_are_persisted_independently()
    {
        var config = new LeafDiscordConfigStore(new MemoryEntityStore());
        var current = await config.GetAsync();

        await config.SaveAsync(new LeafDiscordConfigUpdate(
            ApplicationId,
            PublicKey,
            null,
            false,
            false,
            current.SentinelAgentId,
            [],
            [],
            current.MaxAttachmentBytes,
            "agent-alpha",
            "standard-opencode",
            "fast-codex"));

        var persisted = await config.GetAsync();
        Assert.Equal("agent-alpha", persisted.AgentId);
        Assert.Equal("standard-opencode", persisted.SessionQualityMode);
        Assert.Equal("fast-codex", persisted.SentinelQualityMode);
    }

    [Fact]
    public async Task Presence_context_is_off_by_default_and_validated_on_write()
    {
        var config = new LeafDiscordConfigStore(new MemoryEntityStore());
        var current = await config.GetAsync();
        Assert.Equal("off", current.PresenceContextMode);

        await config.SaveAsync(new LeafDiscordConfigUpdate(
            ApplicationId,
            PublicKey,
            null,
            false,
            false,
            current.SentinelAgentId,
            [],
            [],
            current.MaxAttachmentBytes,
            current.AgentId,
            current.SessionQualityMode,
            current.SentinelQualityMode,
            "roster"));

        Assert.Equal("roster", (await config.GetAsync()).PresenceContextMode);
        await config.SaveAsync(new LeafDiscordConfigUpdate(
            ApplicationId,
            PublicKey,
            null,
            false,
            false,
            current.SentinelAgentId,
            [],
            [],
            current.MaxAttachmentBytes,
            current.AgentId,
            current.SessionQualityMode,
            current.SentinelQualityMode,
            "active",
            true));
        Assert.True((await config.GetAsync()).PresenceExtensionFeedEnabled);
        Assert.Throws<ArgumentException>(() => LeafDiscordConfigStore.NormalizeUpdate(
            current,
            new LeafDiscordConfigUpdate(
                ApplicationId,
                PublicKey,
                null,
                false,
                false,
                current.SentinelAgentId,
                [],
                [],
                current.MaxAttachmentBytes,
                current.AgentId,
                current.SessionQualityMode,
                current.SentinelQualityMode,
                "everyone")));
    }
}
