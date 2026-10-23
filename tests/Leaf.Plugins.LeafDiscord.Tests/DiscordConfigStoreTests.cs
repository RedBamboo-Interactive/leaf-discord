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
    public async Task Agent_and_session_compute_are_persisted_and_no_sentinel_setting_is_written()
    {
        var store = new MemoryEntityStore();
        var config = new LeafDiscordConfigStore(store);
        var current = await config.GetAsync();

        await config.SaveAsync(new LeafDiscordConfigUpdate(
            ApplicationId,
            PublicKey,
            null,
            false,
            false,
            [],
            [],
            current.MaxAttachmentBytes,
            "agent-alpha",
            "standard-opencode"));

        var persisted = await config.GetAsync();
        Assert.Equal("agent-alpha", persisted.AgentId);
        Assert.Equal("standard-opencode", persisted.SessionQualityMode);
        var data = (await store.GetBySlugAsync(LeafDiscordConfigStore.TypeSlug, LeafDiscordConfigStore.Slug))!.Data;
        Assert.False(data.ContainsKey("sentinel_agent"));
        Assert.False(data.ContainsKey("sentinel_quality_mode"));
    }

    [Fact]
    public async Task Stored_sentinel_settings_from_earlier_versions_are_ignored()
    {
        var store = new MemoryEntityStore();
        await store.UpsertBySlugAsync(LeafDiscordConfigStore.TypeSlug, LeafDiscordConfigStore.Slug, "Discord",
            new System.Text.Json.Nodes.JsonObject
            {
                ["application_id"] = ApplicationId,
                ["interaction_public_key"] = PublicKey,
                ["default_agent"] = "agent-alpha",
                ["sentinel_agent"] = "retired-sentinel-agent",
                ["session_quality_mode"] = "standard-opencode",
                ["sentinel_quality_mode"] = "deep-claude",
            });
        var config = new LeafDiscordConfigStore(store);

        var current = await config.GetAsync();
        Assert.Equal("agent-alpha", current.AgentId);
        Assert.Equal("standard-opencode", current.SessionQualityMode);

        // Saving after an upgrade keeps working; the retired keys are simply never read.
        await config.SaveAsync(new LeafDiscordConfigUpdate(
            ApplicationId, PublicKey, null, false, false, [], [], current.MaxAttachmentBytes,
            current.AgentId, current.SessionQualityMode));
        Assert.Equal("agent-alpha", (await config.GetAsync()).AgentId);
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
            [],
            [],
            current.MaxAttachmentBytes,
            current.AgentId,
            current.SessionQualityMode,
            "roster"));

        Assert.Equal("roster", (await config.GetAsync()).PresenceContextMode);
        await config.SaveAsync(new LeafDiscordConfigUpdate(
            ApplicationId,
            PublicKey,
            null,
            false,
            false,
            [],
            [],
            current.MaxAttachmentBytes,
            current.AgentId,
            current.SessionQualityMode,
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
                [],
                [],
                current.MaxAttachmentBytes,
                current.AgentId,
                current.SessionQualityMode,
                "everyone")));
    }
}
