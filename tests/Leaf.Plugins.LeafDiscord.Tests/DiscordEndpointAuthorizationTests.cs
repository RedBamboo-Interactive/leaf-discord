using System.Security.Claims;
using System.Text.Json;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordEndpointAuthorizationTests
{
    [Fact]
    public void Discord_agent_context_requires_a_signed_external_conversation_session()
    {
        var identity = JsonSerializer.Serialize(new
        {
            actor = new
            {
                kind = "agent",
                entityId = "00000000-0000-4000-8000-000000000004",
            },
            beneficiary = new
            {
                kind = "user",
                id = "00000000-0000-4000-8000-000000000001",
            },
            context = new object[]
            {
                new { kind = "external-conversation", id = "binding-1" },
                new { kind = "session", id = "session-1" },
            },
        });
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", "execution"),
                new Claim("execution_identity", identity),
            ], "test")),
        };

        var caller = DiscordEndpoints.ReadDiscordAgentCaller(http);

        Assert.NotNull(caller);
        Assert.Equal("binding-1", caller.BindingId);
        Assert.Equal("session-1", caller.SessionId);
    }

    [Fact]
    public void Ordinary_user_or_malformed_execution_context_cannot_send_outreach()
    {
        var ordinary = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "owner")], "test")),
        };
        var malformed = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", "execution"),
                new Claim("execution_identity", "{}"),
            ], "test")),
        };

        Assert.Null(DiscordEndpoints.ReadDiscordAgentCaller(ordinary));
        Assert.Null(DiscordEndpoints.ReadDiscordAgentCaller(malformed));
        Assert.Null(DiscordEndpoints.ReadPrivateLeafAgentCaller(ordinary));
        Assert.Null(DiscordEndpoints.ReadPrivateLeafAgentCaller(malformed));
    }
    [Fact]
    public void Private_leaf_agent_context_can_address_an_owned_binding()
    {
        var identity = JsonSerializer.Serialize(new
        {
            app = new { id = "nova" },
            actor = new
            {
                kind = "agent",
                entityId = "00000000-0000-4000-8000-000000000004",
            },
            beneficiary = new
            {
                kind = "user",
                id = "00000000-0000-4000-8000-000000000001",
            },
            context = new object[]
            {
                new { kind = "discussion", id = "2d670529" },
            },
        });
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", "execution"),
                new Claim("execution_identity", identity),
            ], "test")),
        };

        var caller = DiscordEndpoints.ReadPrivateLeafAgentCaller(http);

        Assert.NotNull(caller);
        Assert.Equal("00000000-0000-4000-8000-000000000004", caller.ActorEntityId);
        Assert.Equal("00000000-0000-4000-8000-000000000001", caller.BeneficiaryId);
        Assert.Equal("nova", caller.AppId);
        Assert.Equal("agent", caller.ActorKind);
    }

    [Fact]
    public void Automation_can_address_only_a_binding_that_explicitly_authorizes_it()
    {
        var identity = JsonSerializer.Serialize(new
        {
            app = new { id = "nova" },
            actor = new { kind = "agent", entityId = "agent-entity-1" },
            beneficiary = new { kind = "user", id = "owner-1" },
            context = new object[]
            {
                new { kind = "automation", id = "automation-1" },
            },
        });
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", "execution"),
                new Claim("execution_identity", identity),
            ], "test")),
        };
        var binding = new DiscordBinding(
            Guid.NewGuid(), "binding-1", "app-1", "conversation-1", "guild_channel",
            "guild-1", null, "nova", "owner-1", "nova-discord-external",
            "participate", 1, "conversation-handle", "session-1", null, 0,
            null, null, true, "announcements", "Leaf")
        {
            AuthorizedAutomationIds = new HashSet<string>(
                ["automation-1"], StringComparer.OrdinalIgnoreCase),
        };

        var caller = DiscordEndpoints.ReadBindingMessageCaller(http);
        Assert.NotNull(caller);
        Assert.Equal("automation-1", caller.AutomationId);
        Assert.Null(DiscordEndpoints.ReadPrivateLeafAgentCaller(http));
        Assert.True(DiscordEndpoints.CanAddressBinding(
            caller, binding, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBinding(
            caller with { AutomationId = "automation-2" }, binding, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBinding(
            caller, binding with { AuthorizedAutomationIds = new HashSet<string>() },
            "agent-entity-1"));
    }

    [Theory]
    [InlineData("discord", false)]
    [InlineData("nova", true)]
    public void Discord_origin_or_wrong_app_cannot_address_another_binding(
        string appId,
        bool includeExternalConversation)
    {
        var context = new List<object>
        {
            new { kind = "discussion", id = "2d670529" },
        };
        if (includeExternalConversation)
            context.Add(new { kind = "external-conversation", id = "binding-1" });
        var identity = JsonSerializer.Serialize(new
        {
            app = new { id = appId },
            actor = new
            {
                kind = "agent",
                entityId = "00000000-0000-4000-8000-000000000004",
            },
            beneficiary = new
            {
                kind = "user",
                id = "00000000-0000-4000-8000-000000000001",
            },
            context,
        });
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", "execution"),
                new Claim("execution_identity", identity),
            ], "test")),
        };

        Assert.Null(DiscordEndpoints.ReadPrivateLeafAgentCaller(http));
    }
    [Fact]
    public void Private_leaf_agent_must_own_the_binding_and_match_its_agent()
    {
        var caller = new LeafAgentCaller("agent-entity-1", "owner-1");
        var binding = new DiscordBinding(
            Guid.NewGuid(), "binding-1", "app-1", "conversation-1", "guild_channel",
            "guild-1", null, "nova", "owner-1", "nova-discord-external",
            "participate", 1, "conversation-handle", "session-1", null, 0,
            null, null, true, "general", "Leaf");

        Assert.True(DiscordEndpoints.CanAddressBinding(
            caller, binding, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBinding(
            caller with { BeneficiaryId = "owner-2" }, binding, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBinding(
            caller with { ActorEntityId = "agent-entity-2" }, binding, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBinding(caller, binding, null));
    }

    [Fact]
    public void Message_edit_and_delete_use_the_existing_binding_message_authority()
    {
        var binding = new DiscordBinding(
            Guid.NewGuid(), "binding-1", "app-1", "conversation-1", "guild_channel",
            "guild-1", null, "nova", "owner-1", "nova-discord-external",
            "participate", 1, "conversation-handle", "session-1", null, 0,
            null, null, true, "general", "Leaf")
        {
            AuthorizedAutomationIds = new HashSet<string>(
                ["automation-1"], StringComparer.OrdinalIgnoreCase),
        };

        Assert.True(DiscordEndpoints.CanAddressBinding(
            new LeafAgentCaller("agent-entity-1", "owner-1"),
            binding, "agent-entity-1"));
        Assert.True(DiscordEndpoints.CanAddressBinding(
            new LeafAgentCaller("agent-entity-1", "owner-1", "automation-1"),
            binding, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBinding(
            new LeafAgentCaller("agent-entity-1", "owner-1", "automation-2"),
            binding, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBinding(
            new LeafAgentCaller("agent-entity-1", "owner-2"),
            binding, "agent-entity-1"));
    }

    [Fact]
    public void Private_leaf_agent_must_own_and_match_the_enabled_bot()
    {
        var caller = new LeafAgentCaller("agent-entity-1", "owner-1");
        var config = DiscordBotConfigurationStore.EmptyDefaults() with
        {
            Enabled = true,
            OwnerUserId = "owner-1",
        };

        Assert.True(DiscordEndpoints.CanAddressBot(
            caller, config, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBot(
            caller with { BeneficiaryId = "owner-2" }, config, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBot(
            caller with { ActorEntityId = "agent-entity-2" }, config, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanAddressBot(
            caller, config with { Enabled = false }, "agent-entity-1"));
    }

    [Fact]
    public void Guild_reads_require_the_current_enabled_guild_session_owner_and_agent()
    {
        var caller = new DiscordAgentCaller(
            "agent-entity-1", "owner-1", "binding-1", "session-1");
        var source = new DiscordBinding(
            Guid.NewGuid(), "binding-1", "app-1", "conversation-1", "thread",
            "guild-1", "forum-1", "nova", "owner-1", "nova-discord-external",
            "participate", 1, "conversation-handle", "session-1", null, 0,
            null, null, true, "Forum post", "Leaf");

        Assert.True(DiscordEndpoints.CanReadGuildScope(
            caller, source, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanReadGuildScope(
            caller with { SessionId = "session-2" }, source, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanReadGuildScope(
            caller with { BeneficiaryId = "owner-2" }, source, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanReadGuildScope(
            caller with { ActorEntityId = "agent-entity-2" }, source, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanReadGuildScope(
            caller, source with { Enabled = false }, "agent-entity-1"));
        Assert.False(DiscordEndpoints.CanReadGuildScope(
            caller, source with { Kind = "dm", GuildId = null }, "agent-entity-1"));
    }

    [Fact]
    public void Bot_management_allows_its_owner_or_the_trusted_local_administrator()
    {
        var owner = new FakeAuthContext("owner-1");
        var other = new FakeAuthContext("owner-2");
        var localAdministrator = new FakeAuthContext("local-user", ["admin"]);
        var unprivilegedLocalSubject = new FakeAuthContext("local-user", []);
        var unclaimed = DiscordBotConfigurationStore.EmptyDefaults();
        var claimed = unclaimed with { OwnerUserId = "owner-1" };

        Assert.True(DiscordEndpoints.CanManageBot(owner, unclaimed));
        Assert.True(DiscordEndpoints.CanManageBot(owner, claimed));
        Assert.True(DiscordEndpoints.CanManageBot(localAdministrator, claimed));
        Assert.False(DiscordEndpoints.CanManageBot(unprivilegedLocalSubject, claimed));
        Assert.False(DiscordEndpoints.CanManageBot(other, claimed));
        Assert.False(DiscordEndpoints.CanManageBot(new FakeAuthContext(null), unclaimed));
    }

    [Fact]
    public void Discord_forbidden_response_is_an_explicit_http_403()
    {
        var result = DiscordEndpoints.Forbidden("Denied for this test.");

        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private sealed class FakeAuthContext(
        string? userId,
        IReadOnlyList<string>? roles = null) : IAuthContext
    {
        public string? UserId { get; } = userId;
        public string? Email => null;
        public IReadOnlyList<string> Roles { get; } = roles ?? ["owner"];
        public bool IsInRole(string role)
            => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }
}
