using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordCommonsBroadcastTests
{
    private const string GuildId = "600000000000000006";
    private const string OtherGuildId = "600000000000000016";
    private const string NovasRoleId = "777000000000000001";
    private const string AtlasRoleId = "777000000000000002";

    private static readonly DiscordCommonsRelayEnvoy Atlas = new(
        "envoy-1", "100000000000000001", "Ann", "agent-1", "Atlas", "Ann", "Atlas · Ann",
        "active", AtlasRoleId);
    private static readonly DiscordCommonsRelayEnvoy Sleeper = new(
        "envoy-2", "100000000000000002", "Bea", "agent-2", "Orion", "Bea", "Orion · Bea",
        "suspended", null);

    [Fact]
    public void Broadcast_role_reconciliation_creates_once_then_reuses_the_recorded_role()
    {
        var first = DiscordCommonsBroadcast.Plan([], null);
        Assert.Null(first.Role);

        var created = new DiscordManagedRole(NovasRoleId, "Novas", "0", true, false);
        var second = DiscordCommonsBroadcast.Plan([created], NovasRoleId);
        Assert.Same(created, second.Role);
        Assert.False(second.Update);

        // A lost record (or an earlier run) adopts the existing Novas role instead of a duplicate.
        var adopted = DiscordCommonsBroadcast.Plan(
            [new DiscordManagedRole("777000000000000009", "Atlas Ann", "0", true, false), created],
            null);
        Assert.Same(created, adopted.Role);

        // An edited role is repaired in place: renamed back, permissionless, mentionable.
        var edited = created with { Name = "novas-old", Mentionable = false };
        var repaired = DiscordCommonsBroadcast.Plan([edited], NovasRoleId);
        Assert.Same(edited, repaired.Role);
        Assert.True(repaired.Update);

        // Another integration's managed role is never taken over.
        Assert.Null(DiscordCommonsBroadcast.Plan(
            [new DiscordManagedRole("777000000000000010", "Novas", "0", true, true)], null).Role);
    }

    [Fact]
    public async Task Broadcast_role_ids_are_recorded_per_guild_and_survive_saves_and_restarts()
    {
        var store = new MemoryEntityStore();
        var botId = Guid.NewGuid();
        var configurations = new DiscordCommonsConfigurationStore(store);
        await configurations.SaveAsync(botId, new DiscordCommonsConfigurationUpdate("", null, false, null,
        [
            new DiscordCommonsGuildScopeUpdate(
                GuildId, true, [], [], "https://relay.example.test", "host-secret-for-test"),
        ]));

        await configurations.SetBroadcastRoleIdAsync(botId, GuildId, NovasRoleId);
        await configurations.SaveAsync(botId, new DiscordCommonsConfigurationUpdate("", null, false, null,
        [
            new DiscordCommonsGuildScopeUpdate(
                GuildId, true, [], [], "https://relay.example.test", null),
        ]));
        var restarted = await new DiscordCommonsConfigurationStore(store).GetAsync(botId);

        Assert.Equal(NovasRoleId, restarted!.BroadcastRoleId(GuildId));
        Assert.Null(restarted.BroadcastRoleId(OtherGuildId));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            configurations.SetBroadcastRoleIdAsync(botId, GuildId, "Novas"));
    }

    [Fact]
    public async Task Broadcast_role_is_registered_with_the_relay_as_the_host()
    {
        var store = new MemoryEntityStore();
        var configurations = new DiscordCommonsConfigurationStore(store);
        var botId = Guid.NewGuid();
        await configurations.SaveAsync(botId, new DiscordCommonsConfigurationUpdate("", null, false, null,
        [
            new DiscordCommonsGuildScopeUpdate(
                GuildId, true, [], [], "https://relay.example.test", "host-secret-for-test"),
        ]));
        var handler = new CaptureHandler();
        await using var relay = new DiscordCommonsRelayService(
            configurations, new DiscordCommonsIngestStore(store),
            NullLogger<DiscordCommonsRelayService>.Instance, handler);

        await relay.SetBroadcastRoleAsync(botId, GuildId, NovasRoleId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://relay.example.test/v1/admin/broadcast-role", request.Uri);
        Assert.Equal("Bearer host-secret-for-test", request.Authorization);
        Assert.Equal(NovasRoleId, JsonNode.Parse(request.Body!)!["discordRoleId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("hello", new[] { NovasRoleId }, NovasRoleId, true)]
    [InlineData("<@&777000000000000001> hello", new string[0], NovasRoleId, true)]
    [InlineData("<@&777000000000000002> hello", new[] { AtlasRoleId }, NovasRoleId, false)]
    [InlineData("<@&777000000000000001> hello", new[] { NovasRoleId }, null, false)]
    public void Broadcast_mentions_are_read_from_discord_or_raw_content(
        string content, string[] roles, string? broadcastRoleId, bool expected)
        => Assert.Equal(expected, DiscordCommonsBroadcast.Mentions(content, roles, broadcastRoleId));

    [Theory]
    [InlineData("guild_channel", "direct", true)]
    [InlineData("thread", "direct", true)]
    [InlineData("guild_channel", "selective", true)]
    [InlineData("guild_channel", "participate", true)]
    [InlineData("guild_channel", "context_only", false)]
    public void An_open_invitation_is_admitted_even_in_direct_mode_but_never_in_context_only(
        string kind, string mode, bool expected)
    {
        Assert.Equal(expected, DiscordBotRuntime.ShouldAdmitMessage(
            kind, mode, false, false, openInvitation: true));
        // Without the invitation, direct mode still waits for a mention or reply.
        if (mode == "direct")
            Assert.False(DiscordBotRuntime.ShouldAdmitMessage(kind, mode, false, false));
    }

    [Theory]
    [InlineData("direct", false, false)]
    [InlineData("selective", false, false)]
    [InlineData("participate", false, false)]
    [InlineData("direct", true, true)]
    public void An_open_invitation_alone_promises_no_immediate_typing(
        string mode, bool mentioned, bool expected)
        => Assert.Equal(expected, DiscordBotRuntime.ShouldStartTypingImmediately(
            "guild_channel", mode, mentioned, false, openInvitation: true));

    [Fact]
    public void Invitation_envelope_tells_the_agent_answering_is_optional()
    {
        var behavior = new DiscordBehavior(
            null, Guid.NewGuid(), DiscordBehaviorScopes.Guild, GuildId, null,
            DiscordResponseModes.Direct, null, "Be brief.", true);

        var invited = DiscordBotRuntime.InboundBehaviorContext(behavior, false, false, true);

        Assert.True(invited["open_invitation"]!.GetValue<bool>());
        // The selective mode is the one under which the Agent may settle with <discord-no-reply/>.
        Assert.Equal(DiscordResponseModes.Selective, invited["response_mode"]!.GetValue<string>());
        Assert.Equal(DiscordResponseModes.Direct, invited["owner_response_mode"]!.GetValue<string>());
        Assert.Equal("leaf-discord-behavior/v1", invited["schema"]!.GetValue<string>());
        Assert.Equal("trusted_owner_configuration", invited["trust"]!.GetValue<string>());
        Assert.Equal("Be brief.", invited["instructions"]!.GetValue<string>());

        // Mentioning the bot as well addresses it: the turn is a normal one.
        var addressed = DiscordBotRuntime.InboundBehaviorContext(behavior, true, false, true);
        Assert.False(addressed.ContainsKey("open_invitation"));
        Assert.Equal(DiscordResponseModes.Direct, addressed["response_mode"]!.GetValue<string>());
        Assert.False(DiscordBotRuntime.InboundBehaviorContext(behavior, false, false, false)
            .ContainsKey("open_invitation"));
    }

    [Fact]
    public void Unprompted_thread_posts_are_still_reported_into_their_thread()
    {
        var payload = DiscordBotRuntime.UnpromptedCommonsPostPayload(
            true, "agent-consult", "700000000000000007", "800000000000000008", "host:nova",
            "900000000000000009", "Morning, nothing to add.", null, NovasRoleId, [], 1234);

        Assert.NotNull(payload);
        Assert.Equal("800000000000000008", payload["threadRef"]!.GetValue<string>());
        Assert.True(payload["unprompted"]!.GetValue<bool>());
        Assert.Empty(payload["addresses"]!.AsArray());
        Assert.Equal("host-post:900000000000000009", payload["eventId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("<@&777000000000000001> has anyone tried the new cache?", true)]
    [InlineData("<@&777000000000000002> a question for you", true)]
    [InlineData("Atlas · Ann, what do you think?", true)]
    [InlineData("atlas ann, what do you think?", true)]
    [InlineData("Atlas, and every Nova, what do you think?", false)]
    [InlineData("Orion · Bea is switched off.", false)]
    [InlineData("<@&777000000000000099> some other role", false)]
    [InlineData("Good morning everyone.", false)]
    public void Unprompted_text_channel_posts_are_reported_as_a_chain_only_when_they_invite_or_address_envoys(
        string content, bool reported)
    {
        var payload = DiscordBotRuntime.UnpromptedCommonsPostPayload(
            false, "general", "700000000000000007", null, "host:nova",
            "900000000000000009", content, null, NovasRoleId, [Atlas, Sleeper], 1234);

        if (!reported)
        {
            Assert.Null(payload);
            return;
        }
        Assert.NotNull(payload);
        Assert.Equal("message:900000000000000009", payload["threadRef"]!.GetValue<string>());
        Assert.Equal("700000000000000007", payload["channelId"]!.GetValue<string>());
        Assert.Equal("general", payload["channel"]!.GetValue<string>());
        Assert.True(payload["unprompted"]!.GetValue<bool>());
        Assert.Equal(content, payload["content"]!.GetValue<string>());
    }

    [Fact]
    public void Native_announcements_are_never_reported_as_a_chain()
        => Assert.Null(DiscordBotRuntime.UnpromptedCommonsPostPayload(
            false, "announcements", "700000000000000007", null, "host:nova",
            "900000000000000009", $"<@&{NovasRoleId}> release notes", null, NovasRoleId, [Atlas], 1234));

    [Fact]
    public void Outbound_egress_keeps_the_broadcast_role_mention()
    {
        var content = $"<@&{NovasRoleId}> has anyone tried the new cache?";

        var assessment = new DiscordEgressGuard().Inspect(content);

        Assert.True(assessment.Allowed);
        Assert.Equal(content, assessment.Content);
        Assert.Empty(assessment.Findings);
        Assert.Equal([content], DiscordMessageChunker.Split(content));
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? Authorization, string? Body);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"discordRoleId\":\"{NovasRoleId}\"}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
