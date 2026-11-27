using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// Outage alerts (2026-11-27): the owner's one action makes a private #leaf-alerts and hands its webhook to the
/// kernel. The channel only the owner and the bot can read, who may ask for it, and the URL never coming back.
/// </summary>
public sealed class DiscordOutageAlertsTests
{
    private const string ApplicationId = "1546583557567287306";
    private const string GuildId = "1539243753775042630";
    private const string OwnerDiscordId = "301234567890123456";
    private const string BotUserId = "1425463973767151697";
    private const string WebhookUrl = "https://discord.com/api/webhooks/1580000000000000001/secret-token-value-0123456789";

    [Fact]
    public void The_channel_hides_from_everyone_but_the_owner_and_the_bot()
    {
        var payload = DiscordOutageAlerts.ChannelPayload(GuildId, OwnerDiscordId, BotUserId);

        Assert.Equal("leaf-alerts", payload["name"]!.GetValue<string>());
        Assert.Equal(0, payload["type"]!.GetValue<int>());
        var overwrites = payload["permission_overwrites"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        Assert.Equal(3, overwrites.Length);
        // @everyone is the guild's own role id: denied View Channel, granted nothing.
        Assert.Equal((GuildId, 0, "0", "1024"), Read(overwrites[0]));
        // The owner reads (View Channel, Read Message History); the bot also writes and keeps the webhook.
        Assert.Equal((OwnerDiscordId, 1, "66560", "0"), Read(overwrites[1]));
        Assert.Equal((BotUserId, 1, (1024UL | 2048 | 65536 | 536870912).ToString(), "0"), Read(overwrites[2]));

        static (string, int, string, string) Read(JsonObject item)
            => (item["id"]!.GetValue<string>(), item["type"]!.GetValue<int>(),
                item["allow"]!.GetValue<string>(), item["deny"]!.GetValue<string>());
    }

    [Fact]
    public void Only_a_channel_exactly_that_private_is_reused()
    {
        ulong guild = 1539243753775042630, owner = 301234567890123456, bot = 1425463973767151697, someone = 99999999999;
        DiscordOverwrite Everyone(ulong deny = DiscordOutageAlerts.ViewChannel) => new(guild, true, 0, deny);
        DiscordOverwrite Owner(ulong allow = DiscordOutageAlerts.OwnerAllow, ulong deny = 0) => new(owner, false, allow, deny);
        DiscordOverwrite Bot(ulong allow = DiscordOutageAlerts.BotAllow, ulong deny = 0) => new(bot, false, allow, deny);
        bool Private(params DiscordOverwrite[] overwrites) => DiscordOutageAlerts.IsPrivate(overwrites, guild, owner, bot);

        Assert.True(Private(Everyone(), Owner(), Bot()));
        // Hidden from everyone, the owner included: setup would succeed and the alerts be unreadable.
        Assert.False(Private(Everyone()));
        Assert.False(Private(Everyone(), Bot()));
        Assert.False(Private(Everyone(), Owner(allow: DiscordOutageAlerts.ViewChannel), Bot()));
        Assert.False(Private(Everyone(), Owner(deny: DiscordOutageAlerts.ReadMessageHistory), Bot()));
        // The bot could not post or keep its webhook.
        Assert.False(Private(Everyone(), Owner()));
        Assert.False(Private(Everyone(), Owner(), Bot(allow: DiscordOutageAlerts.ViewChannel | DiscordOutageAlerts.SendMessages)));
        // No @everyone deny: the channel follows the server and everyone sees it.
        Assert.False(Private(Owner(), Bot()));
        Assert.False(Private(Everyone(deny: 0), Owner(), Bot()));
        // A role or another member granted View Channel back.
        Assert.False(Private(Everyone(), Owner(), Bot(), new(12345, true, DiscordOutageAlerts.ViewChannel, 0)));
        Assert.False(Private(Everyone(), Owner(), Bot(), new(someone, false, DiscordOutageAlerts.ViewChannel, 0)));
        // Someone denied something is no reader.
        Assert.True(Private(Everyone(), Owner(), Bot(), new(someone, false, 0, DiscordOutageAlerts.SendMessages)));
    }

    [Fact]
    public void The_topic_says_server_admins_can_see_it()
        => Assert.Equal("Leaf tells you here when it stops answering. Private to you and the bot (server admins can see it).",
            DiscordOutageAlerts.Topic);

    [Fact]
    public async Task The_owners_private_discussion_sets_it_up_and_the_url_never_comes_back()
    {
        var harness = await Harness.CreateAsync();
        var alerts = new RecordingAlerts();
        string? ownerSeen = null;

        var (status, body) = await harness.SetUpAsync(harness.Discussion(), alerts, (_, owner) =>
        {
            ownerSeen = owner;
            return Task.FromResult(new DiscordOutageAlertsResult(GuildId, "1580000000000000000", "leaf-alerts", true, true, null, 0));
        });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(OwnerDiscordId, ownerSeen);
        Assert.Equal("leaf-alerts", body.GetProperty("channelName").GetString());
        Assert.True(body.GetProperty("scheduled").GetBoolean());
        Assert.DoesNotContain("webhooks", body.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("discord-conversation")]
    [InlineData("discord-session")]
    [InlineData("automation")]
    [InlineData("other-app")]
    public async Task A_discord_session_or_any_other_signed_session_is_refused(string kind)
    {
        var harness = await Harness.CreateAsync();

        var (status, _) = await harness.SetUpAsync(Signed(kind, harness.AgentId, harness.Owner), new RecordingAlerts(),
            (_, _) => throw new InvalidOperationException("Not expected."));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task Someone_who_is_not_the_owner_is_refused()
    {
        var harness = await Harness.CreateAsync();
        var stranger = "00000000-0000-4000-8000-000000000099";

        var (agent, _) = await harness.SetUpAsync(Signed("private", harness.AgentId, stranger), new RecordingAlerts(),
            (_, _) => throw new InvalidOperationException("Not expected."));
        var (user, _) = await harness.SetUpAsync(SignedIn(stranger), new RecordingAlerts(),
            (_, _) => throw new InvalidOperationException("Not expected."), new FakeAuthContext(stranger, ["member"]));

        Assert.Equal(StatusCodes.Status403Forbidden, agent);
        Assert.Equal(StatusCodes.Status403Forbidden, user);
    }

    [Fact]
    public async Task An_owner_without_a_linked_Discord_user_is_told_so_and_nothing_is_created()
    {
        var harness = await Harness.CreateAsync(linkDiscord: false);

        var (status, body) = await harness.SetUpAsync(harness.Discussion(), new RecordingAlerts(),
            (_, _) => throw new InvalidOperationException("Not expected."));

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("owner_discord_user_unlinked", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_kernel_without_outage_alerts_is_a_501_before_anything_is_created()
    {
        var harness = await Harness.CreateAsync();

        var (status, body) = await harness.SetUpAsync(harness.Discussion(), alerts: null,
            (_, _) => throw new InvalidOperationException("Not expected."));

        Assert.Equal(StatusCodes.Status501NotImplemented, status);
        Assert.Equal("outage_alerts_unavailable", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_public_leaf_alerts_channel_is_a_conflict()
    {
        var harness = await Harness.CreateAsync();

        var (status, body) = await harness.SetUpAsync(harness.Discussion(), new RecordingAlerts(),
            (_, _) => throw new DiscordOutageAlertsConflictException("#leaf-alerts exists and others can read it"));

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("outage_alerts_channel_conflict", body.GetProperty("error").GetString());
    }

    [Fact]
    public void The_webhook_url_is_discords_and_escapes_its_parts()
        => Assert.Equal(WebhookUrl, DiscordOutageAlerts.WebhookUrl("1580000000000000001", "secret-token-value-0123456789"));

    private sealed class RecordingAlerts : IOutageAlerts
    {
        public List<string> Urls { get; } = [];
        public bool IsConfigured => Urls.Count > 0;

        public Task<OutageAlertsConfiguration> ConfigureDiscordWebhookAsync(string webhookUrl, CancellationToken ct = default)
        {
            Urls.Add(webhookUrl);
            return Task.FromResult(new OutageAlertsConfiguration(true, null));
        }
    }

    private sealed class Harness
    {
        public required MemoryEntityStore Entities { get; init; }
        public required DiscordBotConfigurationStore Configurations { get; init; }
        public required Guid BotId { get; init; }
        public required string AgentId { get; init; }
        public required string Owner { get; init; }

        public static async Task<Harness> CreateAsync(bool linkDiscord = true)
        {
            var entities = new MemoryEntityStore();
            var owner = entities.Seed("user", "laurent", "Laurent", null);
            if (linkDiscord)
                await entities.PatchAsync(owner.Id, new JsonObject { [DiscordUserIdentityResolver.UserField] = OwnerDiscordId });
            var agent = entities.Seed("agent", "nova", "Nova", owner.Id.ToString());
            var configurations = new DiscordBotConfigurationStore(new LeafDiscordConfigStore(entities));
            var config = await configurations.CreateAsync(new LeafDiscordConfigUpdate(
                ApplicationId, new string('a', 64), "test-bot-token", false, true,
                [], [], 10 * 1024 * 1024, "nova", "deep-codex"),
                ownerUserId: owner.Id.ToString());
            return new Harness
            {
                Entities = entities,
                Configurations = configurations,
                BotId = config.EntityId,
                AgentId = agent.Id.ToString(),
                Owner = owner.Id.ToString(),
            };
        }

        public DefaultHttpContext Discussion() => Signed("private", AgentId, Owner);

        public async Task<(int Status, JsonElement Body)> SetUpAsync(HttpContext http, IOutageAlerts? alerts,
            Func<IOutageAlerts, string, Task<DiscordOutageAlertsResult>> setUp, IAuthContext? auth = null)
        {
            var result = await DiscordEndpoints.SetUpOutageAlertsAsync(BotId, GuildId, http,
                auth ?? new FakeAuthContext(Owner, []), Configurations, new DiscordAgentResolver(Entities),
                new DiscordUserIdentityResolver(Entities, Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscordUserIdentityResolver>.Instance),
                alerts, setUp, CancellationToken.None);
            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;
            var value = (result as IValueHttpResult)?.Value;
            return (status, JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    private static DefaultHttpContext SignedIn(string userId)
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
        };

    private static DefaultHttpContext Signed(string kind, string agentId, string ownerId)
    {
        object[] context = kind switch
        {
            "discord-conversation" =>
            [
                new { kind = "discussion", id = "d1" },
                new { kind = "external-conversation", id = "binding-1" },
            ],
            "discord-session" =>
            [
                new { kind = "external-conversation", id = "binding-1" },
                new { kind = "session", id = "session-1" },
            ],
            "automation" => [new { kind = "automation", id = "automation-1" }],
            _ => [new { kind = "discussion", id = "d1" }],
        };
        var identity = JsonSerializer.Serialize(new
        {
            app = new { id = kind == "other-app" ? "discord" : "nova" },
            actor = new { kind = "agent", entityId = agentId },
            beneficiary = new { kind = "user", id = ownerId },
            context,
        });
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", "execution"),
                new Claim("execution_identity", identity),
            ], "test")),
        };
    }

    private sealed class FakeAuthContext(string? userId, IReadOnlyList<string> roles) : IAuthContext
    {
        public string? UserId { get; } = userId;
        public string? Email => null;
        public IReadOnlyList<string> Roles { get; } = roles;
        public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }
}
