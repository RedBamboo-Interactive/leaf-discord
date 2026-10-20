using Leaf.Plugins.LeafDiscord;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordAgentResolverTests
{
    [Fact]
    public async Task ResolveAsync_accepts_agent_slug_and_id_but_rejects_other_types()
    {
        var store = new MemoryEntityStore();
        var agent = await store.UpsertBySlugAsync("agent", "nova", "Nova");
        var other = await store.UpsertBySlugAsync("asset", "nova-avatar", "Nova Avatar");
        var resolver = new DiscordAgentResolver(store);

        Assert.Equal(agent, await resolver.ResolveAsync("nova"));
        Assert.Equal(agent, await resolver.ResolveAsync(agent.Id.ToString()));
        Assert.Null(await resolver.ResolveAsync(other.Id.ToString()));
        Assert.Null(await resolver.ResolveAsync("missing"));
    }

    [Fact]
    public async Task ResolveOwnerUserIdAsync_uses_the_authoritative_agent_creator()
    {
        var store = new MemoryEntityStore();
        var owner = store.Seed("user", "owner", "Owner", "system");
        _ = store.Seed("agent", "nova", "Nova", owner.Id.ToString());
        var resolver = new DiscordAgentResolver(store);

        Assert.Equal(owner.Id.ToString(), await resolver.ResolveOwnerUserIdAsync("nova"));
    }

    [Fact]
    public async Task Discord_identity_requires_one_exact_user_field_match()
    {
        var store = new MemoryEntityStore();
        var owner = await store.UpsertBySlugAsync("user", "owner", "Owner Example",
            new JsonObject { [DiscordUserIdentityResolver.UserField] = "200000000000000002" });
        var resolver = new DiscordUserIdentityResolver(
            store, NullLogger<DiscordUserIdentityResolver>.Instance);

        var resolved = await resolver.ResolveAsync(
            "200000000000000002", owner.Id.ToString());

        Assert.NotNull(resolved);
        Assert.Equal(owner.Id.ToString(), resolved.UserId);
        Assert.Equal("Owner Example", resolved.DisplayName);
        Assert.True(resolved.IsConversationOwner);
        Assert.Equal("Owner Example",
            await resolver.ResolveDisplayNameAsync(owner.Id.ToString()));
        Assert.Null(await resolver.ResolveAsync("not-a-snowflake", owner.Id.ToString()));
        Assert.Null(await resolver.ResolveAsync("42", owner.Id.ToString()));
    }

    [Fact]
    public async Task Discord_identity_fails_closed_when_the_link_is_ambiguous()
    {
        var store = new MemoryEntityStore();
        _ = await store.UpsertBySlugAsync("user", "one", "One",
            new JsonObject { [DiscordUserIdentityResolver.UserField] = "200000000000000002" });
        _ = await store.UpsertBySlugAsync("user", "two", "Two",
            new JsonObject { [DiscordUserIdentityResolver.UserField] = "200000000000000002" });
        var resolver = new DiscordUserIdentityResolver(
            store, NullLogger<DiscordUserIdentityResolver>.Instance);

        Assert.Null(await resolver.ResolveAsync("200000000000000002", "owner"));
    }
}
