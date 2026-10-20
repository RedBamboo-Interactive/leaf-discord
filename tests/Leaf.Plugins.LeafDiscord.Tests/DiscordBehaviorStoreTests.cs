using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordBehaviorStoreTests
{
    [Fact]
    public async Task Migration_preserves_existing_binding_modes_as_explicit_overrides()
    {
        var entities = new MemoryEntityStore();
        var config = Config(Guid.NewGuid());
        var binding = await new DiscordBindingStore(entities).CreateAsync(
            new CreateDiscordBinding(
                config.ApplicationId, "200", "guild_channel", "100", null,
                config.AgentId, "owner", config.AudienceProfile,
                DiscordResponseModes.Participate));
        var store = new DiscordBehaviorStore(entities);

        await store.EnsureDefaultsAsync(config, [binding]);

        var all = await store.ListAsync(config.EntityId);
        Assert.Contains(all, item => item.Scope == DiscordBehaviorScopes.Default
            && item.ResponseMode == DiscordResponseModes.Direct);
        Assert.Contains(all, item => item.Scope == DiscordBehaviorScopes.DmDefault
            && item.ResponseMode == DiscordResponseModes.Direct);
        Assert.Contains(all, item => item.Scope == DiscordBehaviorScopes.Channel
            && item.ConversationId == "200"
            && item.ResponseMode == DiscordResponseModes.Participate);
        Assert.Equal(DiscordResponseModes.Participate,
            (await store.ResolveAsync(config, binding)).ResponseMode);
    }

    [Fact]
    public async Task Thread_inherits_parent_then_guild_then_default_by_specificity()
    {
        var entities = new MemoryEntityStore();
        var config = Config(Guid.NewGuid());
        var store = new DiscordBehaviorStore(entities);
        await store.EnsureDefaultsAsync(config, []);
        await store.SaveAsync(config, new DiscordBehaviorUpdate(
            DiscordBehaviorScopes.Guild,
            DiscordResponseModes.Selective,
            "Guild prompt",
            GuildId: "100"));
        await store.SaveAsync(config, new DiscordBehaviorUpdate(
            DiscordBehaviorScopes.Channel,
            DiscordResponseModes.Participate,
            "Forum prompt",
            GuildId: "100",
            ConversationId: "200"));

        var inherited = await store.ResolveAsync(
            config, "thread", "100", "300", "200",
            DiscordResponseModes.Direct);
        var guild = await store.ResolveAsync(
            config, "thread", "100", "301", "999",
            DiscordResponseModes.Direct);
        var fallback = await store.ResolveAsync(
            config, "guild_channel", "400", "401", null,
            DiscordResponseModes.Participate);

        Assert.Equal(DiscordBehaviorScopes.Channel, inherited.Scope);
        Assert.Equal("Forum prompt", inherited.Instructions);
        Assert.Equal(DiscordResponseModes.Participate, inherited.ResponseMode);
        Assert.Equal(DiscordBehaviorScopes.Guild, guild.Scope);
        Assert.Equal(DiscordResponseModes.Selective, guild.ResponseMode);
        Assert.Equal(DiscordBehaviorScopes.Default, fallback.Scope);
        Assert.Equal(DiscordResponseModes.Direct, fallback.ResponseMode);
    }

    [Fact]
    public async Task Prompt_is_a_separate_entity_referenced_by_behavior()
    {
        var entities = new MemoryEntityStore();
        var config = Config(Guid.NewGuid());
        var store = new DiscordBehaviorStore(entities);

        var behavior = await store.SaveAsync(config, new DiscordBehaviorUpdate(
            DiscordBehaviorScopes.Channel,
            DiscordResponseModes.Selective,
            "Cute, kind, and concise.",
            GuildId: "100",
            ConversationId: "200"));

        Assert.NotNull(behavior.EntityId);
        Assert.NotNull(behavior.PromptEntityId);
        var behaviorEntity = await entities.GetAsync(behavior.EntityId!.Value);
        var promptEntity = await entities.GetAsync(behavior.PromptEntityId!.Value);
        Assert.Equal(DiscordBehaviorStore.BehaviorTypeSlug, behaviorEntity!.TypeSlug);
        Assert.Equal(DiscordBehaviorStore.PromptTypeSlug, promptEntity!.TypeSlug);
        Assert.Equal(behavior.PromptEntityId.ToString(),
            behaviorEntity.Data["prompt"]!.GetValue<string>());
        Assert.Equal("Cute, kind, and concise.",
            promptEntity.Data["instructions"]!.GetValue<string>());
        Assert.DoesNotContain(behaviorEntity.Data, pair => pair.Value is JsonObject or JsonArray);
    }

    [Theory]
    [InlineData("dm", "direct", false, false, true)]
    [InlineData("guild_channel", "direct", false, false, false)]
    [InlineData("guild_channel", "direct", true, false, true)]
    [InlineData("thread", "direct", false, true, true)]
    [InlineData("guild_channel", "selective", false, false, true)]
    [InlineData("guild_channel", "participate", false, false, true)]
    [InlineData("guild_channel", "context_only", true, true, false)]
    public void Admission_policy_is_deterministic(
        string kind, string mode, bool mentioned, bool repliesToBot, bool expected)
        => Assert.Equal(expected, DiscordBotRuntime.ShouldAdmitMessage(
            kind, mode, mentioned, repliesToBot));

    [Theory]
    [InlineData("dm", "direct", false, false, true)]
    [InlineData("guild_channel", "direct", true, false, true)]
    [InlineData("thread", "direct", false, true, true)]
    [InlineData("guild_channel", "selective", false, false, false)]
    [InlineData("guild_channel", "selective", true, false, true)]
    [InlineData("guild_channel", "participate", false, false, true)]
    public void Typing_policy_distinguishes_deliberation_from_response_intent(
        string kind, string mode, bool mentioned, bool repliesToBot, bool expected)
        => Assert.Equal(expected, DiscordBotRuntime.ShouldStartTypingImmediately(
            kind, mode, mentioned, repliesToBot));

    [Fact]
    public void Startup_backfill_policy_prioritizes_dms_and_bounds_guild_history()
    {
        var guild = DiscordBotRuntime.GetStartupBackfillPolicy("guild_channel");
        var thread = DiscordBotRuntime.GetStartupBackfillPolicy("thread");
        var dm = DiscordBotRuntime.GetStartupBackfillPolicy("dm");

        Assert.Equal(25, guild.MessageLimit);
        Assert.Equal(TimeSpan.FromMinutes(15), guild.MaxAge);
        Assert.True(guild.RequiresDirectAddress);
        Assert.Equal(guild, thread);
        Assert.Equal(50, dm.MessageLimit);
        Assert.Equal(TimeSpan.FromHours(24), dm.MaxAge);
        Assert.False(dm.RequiresDirectAddress);
    }

    [Theory]
    [InlineData("guild_channel", -14, false, false, false)]
    [InlineData("guild_channel", -14, true, false, true)]
    [InlineData("guild_channel", -15, true, false, true)]
    [InlineData("thread", -14, false, true, true)]
    [InlineData("guild_channel", -16, true, true, false)]
    [InlineData("dm", -23 * 60, false, false, true)]
    [InlineData("dm", -24 * 60, false, false, true)]
    [InlineData("dm", -25 * 60, true, true, false)]
    public void Startup_backfill_admission_is_surface_and_age_aware(
        string kind,
        int minutesFromNow,
        bool mentioned,
        bool repliesToBot,
        bool expected)
    {
        var now = new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, DiscordBotRuntime.ShouldAdmitStartupBackfillMessage(
            kind, now.AddMinutes(minutesFromNow), now, mentioned, repliesToBot));
    }

    private static LeafDiscordConfig Config(Guid id) => new(
        "10", new string('a', 64), "token", true, "nova", "nova",
        "deep-codex", "deep-codex", "leaf-discord-external", "",
        new HashSet<string>(), new HashSet<string>(), 10 * 1024 * 1024)
    {
        EntityId = id,
        OwnerUserId = "owner",
    };
}
