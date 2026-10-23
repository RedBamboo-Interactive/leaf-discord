using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordRecoveryAuthorityTests
{
    private const string ApplicationId = "100000000000000001";
    private const string Counterparty = "300000000000000003";
    private const string Owner = "owner-1";

    [Fact]
    public void Claims_only_discord_conversations_for_this_plugin()
    {
        var authority = new DiscordRecoveryAuthority(null!, null!, null!);

        Assert.Equal("leaf-discord", authority.PluginId);
        Assert.True(authority.Handles("discord:1:2"));
        Assert.False(authority.Handles("slack:1:2"));
        Assert.False(authority.Handles("Discord:1:2"));
    }

    [Fact]
    public async Task Current_binding_for_the_same_session_owner_and_agent_is_vouched_for()
    {
        var f = await Fixture.Create();

        var decision = await f.Authority.ValidateAsync(f.Request());

        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData("generation", "recovery_origin_superseded")]
    [InlineData("session", "recovery_origin_superseded")]
    [InlineData("owner", "recovery_origin_authority_changed")]
    [InlineData("agent", "recovery_origin_authority_changed")]
    [InlineData("missing", "recovery_origin_missing")]
    [InlineData("binding-disabled", "recovery_origin_revoked")]
    [InlineData("bot-disabled", "recovery_origin_revoked")]
    [InlineData("allowlist", "recovery_origin_revoked")]
    public async Task Changed_binding_state_refuses_recovery(string change, string expected)
    {
        var f = await Fixture.Create(
            botEnabled: change != "bot-disabled",
            allowlisted: change != "allowlist");
        if (change == "binding-disabled")
            await f.Entities.PatchAsync(f.Binding.EntityId, new JsonObject { ["enabled"] = false });
        var request = change switch
        {
            "generation" => f.Request() with { Generation = f.Binding.Generation + 1 },
            "session" => f.Request() with { SessionId = "other-session" },
            "owner" => f.Request() with { OwnerUserId = "owner-2" },
            "agent" => f.Request() with { AgentEntityId = Guid.NewGuid().ToString() },
            "missing" => f.Request() with { ConversationId = "discord:1:missing" },
            _ => f.Request(),
        };

        var decision = await f.Authority.ValidateAsync(request);

        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.BlockCode);
    }

    private sealed record Fixture(
        MemoryEntityStore Entities, DiscordRecoveryAuthority Authority,
        DiscordBinding Binding, string AgentEntityId)
    {
        public ExternalConversationRecoveryRequest Request() => new(
            Binding.BindingId, Binding.Generation, Binding.SessionId!, Owner, AgentEntityId, "nova");

        public static async Task<Fixture> Create(bool botEnabled = true, bool allowlisted = true)
        {
            var entities = new MemoryEntityStore();
            var agent = await entities.UpsertBySlugAsync("agent", "nova", "Nova");
            var configurations = new DiscordBotConfigurationStore(
                entities, new LeafDiscordConfigStore(entities));
            await configurations.CreateAsync(new LeafDiscordConfigUpdate(
                ApplicationId, new string('a', 64), "token", false, botEnabled,
                allowlisted ? [Counterparty] : [], [], AgentId: "nova"));
            var bindings = new DiscordBindingStore(entities);
            var created = await bindings.GetOrCreateAsync(new CreateDiscordBinding(
                ApplicationId, "400000000000000004", "dm", null, null, "nova", Owner,
                "nova-discord-external", "direct", "Alice", null)
            {
                CounterpartyUserId = Counterparty,
            });
            var attached = await bindings.AttachConversationAsync(created,
                new ExternalConversationHandle("nova", created.BindingId, created.Generation,
                    "handle-1", "session-1"));
            return new Fixture(entities,
                new DiscordRecoveryAuthority(bindings, configurations, new DiscordAgentResolver(entities)),
                attached, agent.Id.ToString());
        }
    }
}
