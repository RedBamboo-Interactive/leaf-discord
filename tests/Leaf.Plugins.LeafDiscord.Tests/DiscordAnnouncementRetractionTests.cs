using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>Retraction Contract v1, leaf-discord side: route, Discord deletion, payload.</summary>
public sealed class DiscordAnnouncementRetractionTests
{
    private const string ApplicationId = "1546583557567287306";
    private const string Owner = "00000000-0000-4000-8000-000000000001";
    private const string GuildId = "1539243753775042630";
    private const string AnnouncementsId = "1556368920787820668";
    private const string GeneralId = "1556368920787820669";
    private const string MessageId = "1571775128134750239";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_763_300_000_000);

    [Fact]
    public void The_retraction_payload_is_exactly_the_contract()
    {
        var payload = DiscordCommonsRelayService.BuildAnnouncementRetractionPayload(
            MessageId, AnnouncementsId, 1_763_300_000_000);

        Assert.Equal(
            """{"type":"announcement_retraction","eventId":"announcement:1571775128134750239:retract","messageId":"1571775128134750239","channelId":"1556368920787820668","channel":"announcements","timestamp":1763300000000}""",
            payload.ToJsonString());
        Assert.Equal("announcement:1571775128134750239:retract",
            DiscordCommonsRelayService.AnnouncementRetractEventId(MessageId));
        Assert.Equal(DiscordBotRuntime.AnnouncementCreateEventId(1571775128134750239),
            DiscordCommonsRelayService.AnnouncementCreateEventId(MessageId));
    }

    [Theory]
    [InlineData("", AnnouncementsId)]
    [InlineData("0", AnnouncementsId)]
    [InlineData("not-a-snowflake", AnnouncementsId)]
    [InlineData(MessageId, "")]
    [InlineData(MessageId, "announcements")]
    public void The_retraction_payload_needs_snowflakes(string messageId, string channelId)
        => Assert.Throws<ArgumentException>(() =>
            DiscordCommonsRelayService.BuildAnnouncementRetractionPayload(messageId, channelId, 1));

    // ---- Route: who may retract ----

    [Fact]
    public async Task A_private_discussion_of_the_bots_agent_for_its_owner_retracts()
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayCreateAsync(MessageId);

        var (status, body) = await harness.RetractAsync(
            MessageId, harness.Discussion(), reason: "  The owner's complaint, not news.  ");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(MessageId, body.GetProperty("messageId").GetString());
        Assert.Equal($"announcement:{MessageId}:retract", body.GetProperty("eventId").GetString());
        Assert.False(body.GetProperty("alreadyRetracted").GetBoolean());
        Assert.Equal($"announcement:{MessageId}:create",
            body.GetProperty("create").GetProperty("eventId").GetString());
        Assert.Equal("queued", body.GetProperty("create").GetProperty("state").GetString());
        Assert.Equal("queued", body.GetProperty("retract").GetProperty("state").GetString());

        // Queued in the create's guild with exactly the contract's payload; the reason and the
        // caller are audited on the row beside it, never relayed.
        var row = await harness.SingleRetractionAsync(MessageId);
        Assert.Equal(GuildId, row.GuildId);
        Assert.Equal(DiscordCommonsRelayService.BuildAnnouncementRetractionPayload(
                MessageId, AnnouncementsId, Now.ToUnixTimeMilliseconds()).ToJsonString(),
            row.Payload!.ToJsonString());
        var audit = harness.Audit(row);
        Assert.Equal("agent", audit["requested_by_kind"]!.GetValue<string>());
        Assert.Equal(harness.AgentId, audit["requested_by"]!.GetValue<string>());
        Assert.Equal(Owner, audit["beneficiary"]!.GetValue<string>());
        Assert.Equal("d1", audit["discussion_id"]!.GetValue<string>());
        Assert.Equal("The owner's complaint, not news.", audit["reason"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(Owner, "owner")]
    [InlineData("local-user", "admin")]
    public async Task The_owner_or_the_local_administrator_retracts_directly(string userId, string role)
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayCreateAsync(MessageId);

        var (status, body) = await harness.RetractAsync(
            MessageId, SignedIn(userId), new FakeAuthContext(userId, [role]));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal($"announcement:{MessageId}:retract", body.GetProperty("eventId").GetString());
        var audit = harness.Audit(await harness.SingleRetractionAsync(MessageId));
        Assert.Equal("user", audit["requested_by_kind"]!.GetValue<string>());
        Assert.Equal(userId, audit["requested_by"]!.GetValue<string>());
        Assert.Null(audit["reason"]);
    }

    [Theory]
    [InlineData("discord-conversation")]
    [InlineData("discord-session")]
    [InlineData("automation")]
    [InlineData("other-app")]
    public async Task A_discord_session_or_any_other_signed_session_is_refused(string kind)
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayCreateAsync(MessageId);

        // Even when its token carries the owner's identity and roles.
        var (status, _) = await harness.RetractAsync(
            MessageId, Signed(kind, harness.AgentId, Owner),
            new FakeAuthContext(Owner, ["owner", "admin"]));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(await harness.Deliveries.ExistsAsync(
            harness.BotId, DiscordCommonsRelayService.AnnouncementRetractEventId(MessageId)));
    }

    [Fact]
    public async Task Another_agent_or_another_beneficiary_is_refused()
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayCreateAsync(MessageId);
        var other = harness.Entities.Seed("agent", "other", "Other", Owner);

        Assert.Equal(StatusCodes.Status403Forbidden, (await harness.RetractAsync(
            MessageId, Signed("private", other.Id.ToString(), Owner))).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await harness.RetractAsync(
            MessageId, Signed("private", harness.AgentId,
                "00000000-0000-4000-8000-000000000002"))).Status);
        Assert.False(await harness.Deliveries.ExistsAsync(
            harness.BotId, DiscordCommonsRelayService.AnnouncementRetractEventId(MessageId)));
    }

    [Fact]
    public async Task A_user_who_does_not_own_the_bot_is_refused()
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayCreateAsync(MessageId);
        const string stranger = "00000000-0000-4000-8000-000000000002";

        Assert.Equal(StatusCodes.Status403Forbidden, (await harness.RetractAsync(
            MessageId, SignedIn(stranger), new FakeAuthContext(stranger, ["owner"]))).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await harness.RetractAsync(
            MessageId, SignedIn(stranger), new FakeAuthContext(stranger, ["member"]))).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await harness.RetractAsync(
            MessageId, SignedIn(Owner), new FakeAuthContext(Owner, []))).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await harness.RetractAsync(
            MessageId, new DefaultHttpContext(), new FakeAuthContext(null, ["owner"]))).Status);
        Assert.False(await harness.Deliveries.ExistsAsync(
            harness.BotId, DiscordCommonsRelayService.AnnouncementRetractEventId(MessageId)));
    }

    // ---- Route: what it retracts ----

    [Fact]
    public async Task A_message_the_bot_never_relayed_is_404_and_queues_nothing()
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayCreateAsync(MessageId);
        // The same message relayed by another bot is not this bot's.
        var other = await Harness.CreateAsync();

        var (status, body) = await harness.RetractAsync("1571775128134750240", harness.Discussion());
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("announcement_not_relayed", body.GetProperty("error").GetString());
        Assert.Equal(StatusCodes.Status404NotFound,
            (await other.RetractAsync(MessageId, other.Discussion())).Status);
        Assert.Equal(StatusCodes.Status404NotFound,
            (await harness.RetractAsync(MessageId, harness.Discussion(), botId: Guid.NewGuid())).Status);
        Assert.Empty(await harness.RetractionsAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("announcement:1571775128134750239:create")]
    public async Task A_message_id_that_is_not_a_snowflake_is_400(string messageId)
    {
        var harness = await Harness.CreateAsync();
        Assert.Equal(StatusCodes.Status400BadRequest,
            (await harness.RetractAsync(messageId, harness.Discussion())).Status);
    }

    [Fact]
    public async Task A_reason_over_the_bound_is_400()
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayCreateAsync(MessageId);

        var (status, _) = await harness.RetractAsync(MessageId, harness.Discussion(),
            reason: new string('x', DiscordAnnouncementRetractionAudit.MaxReasonLength + 1));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Empty(await harness.RetractionsAsync());
    }

    [Fact]
    public async Task A_repeat_returns_the_existing_retraction_and_its_delivery_state()
    {
        var harness = await Harness.CreateAsync();
        var create = await harness.RelayCreateAsync(MessageId);
        await harness.Deliveries.MarkSentAsync(create);

        var (_, first) = await harness.RetractAsync(MessageId, harness.Discussion(), reason: "first");
        var queued = await harness.SingleRetractionAsync(MessageId);
        await harness.Deliveries.MarkSentAsync(queued);
        var (status, repeat) = await harness.RetractAsync(
            MessageId, SignedIn(Owner), new FakeAuthContext(Owner, ["owner"]), reason: "second",
            now: Now.AddMinutes(5));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(first.GetProperty("alreadyRetracted").GetBoolean());
        Assert.True(repeat.GetProperty("alreadyRetracted").GetBoolean());
        Assert.Equal(first.GetProperty("eventId").GetString(), repeat.GetProperty("eventId").GetString());
        Assert.Equal("sent", repeat.GetProperty("create").GetProperty("state").GetString());
        Assert.Equal("sent", repeat.GetProperty("retract").GetProperty("state").GetString());
        // Nothing new was recorded: the first request's audit stands.
        var row = Assert.Single(await harness.RetractionsAsync());
        Assert.Equal("first", harness.Audit(row)["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_create_sent_before_channels_were_recorded_takes_the_guilds_announcements()
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayLegacySentCreateAsync(MessageId);
        string? askedGuild = null;

        var (status, _) = await harness.RetractAsync(MessageId, harness.Discussion(),
            findChannel: (_, guild, _) =>
            {
                askedGuild = guild;
                return Task.FromResult<string?>(AnnouncementsId);
            });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(GuildId, askedGuild);
        var row = await harness.SingleRetractionAsync(MessageId);
        Assert.Equal(AnnouncementsId, row.Payload!["channelId"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_recorded_channel_is_never_looked_up()
    {
        var harness = await Harness.CreateAsync();
        var create = await harness.RelayCreateAsync(MessageId);
        await harness.Deliveries.MarkSentAsync(create);

        var (status, _) = await harness.RetractAsync(MessageId, harness.Discussion(),
            findChannel: (_, _, _) => throw new InvalidOperationException("Discord bot is not running."));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(AnnouncementsId,
            (await harness.SingleRetractionAsync(MessageId)).Payload!["channelId"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_channel_is_409_and_queues_nothing()
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayLegacySentCreateAsync(MessageId);

        var notFound = await harness.RetractAsync(MessageId, harness.Discussion(),
            findChannel: (_, _, _) => Task.FromResult<string?>(null));
        var offline = await harness.RetractAsync(MessageId, harness.Discussion(),
            findChannel: (_, _, _) => throw new InvalidOperationException("Discord bot is not running."));

        Assert.Equal(StatusCodes.Status409Conflict, notFound.Status);
        Assert.Equal("announcement_channel_unknown", notFound.Body.GetProperty("error").GetString());
        Assert.Equal(StatusCodes.Status409Conflict, offline.Status);
        Assert.Empty(await harness.RetractionsAsync());
    }

    // ---- Discord deletion ----

    [Fact]
    public void Only_a_deletion_in_announcements_reaches_the_inbox()
    {
        Assert.Equal([(MessageId, $"announcement:{MessageId}:retract")],
            DiscordBotRuntime.AnnouncementDeletions("announcements", [1571775128134750239]));
        Assert.Single(DiscordBotRuntime.AnnouncementDeletions("Announcements", [1571775128134750239]));
        Assert.Empty(DiscordBotRuntime.AnnouncementDeletions("general", [1571775128134750239]));
        Assert.Empty(DiscordBotRuntime.AnnouncementDeletions("showcase", [1571775128134750239]));
        // A bulk delete leaves one item per message.
        Assert.Equal(3, DiscordBotRuntime.AnnouncementDeletions("announcements",
            [1571775128134750239, 1571775128134750240, 1571775128134750241, 1571775128134750239]).Count);
    }

    [Fact]
    public async Task A_deleted_relayed_announcement_queues_one_retraction()
    {
        var harness = await Harness.CreateAsync();
        var create = await harness.RelayCreateAsync(MessageId);
        await harness.Deliveries.MarkSentAsync(create);
        var item = await harness.Inbox.EnqueueAsync(harness.BotId, MessageId, AnnouncementsId, GuildId,
            DiscordGatewayInboxStore.AnnouncementDeleteKind,
            DiscordCommonsRelayService.AnnouncementRetractEventId(MessageId));

        var first = await DiscordBotRuntime.RetractDeletedAnnouncementAsync(
            harness.Relay, harness.BotId, item, Now);
        // The gateway can report a deletion twice (single then bulk, or a replayed item).
        var again = await DiscordBotRuntime.RetractDeletedAnnouncementAsync(
            harness.Relay, harness.BotId, item, Now.AddSeconds(1));

        Assert.True(first!.Queued);
        Assert.False(again!.Queued);
        var row = await harness.SingleRetractionAsync(MessageId);
        Assert.Equal(DiscordCommonsRelayService.BuildAnnouncementRetractionPayload(
                MessageId, AnnouncementsId, Now.ToUnixTimeMilliseconds()).ToJsonString(),
            row.Payload!.ToJsonString());
        Assert.Equal("discord", harness.Audit(row)["requested_by_kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_deleted_message_never_relayed_leaves_nothing()
    {
        var harness = await Harness.CreateAsync();
        var item = await harness.Inbox.EnqueueAsync(harness.BotId, MessageId, AnnouncementsId, GuildId,
            DiscordGatewayInboxStore.AnnouncementDeleteKind,
            DiscordCommonsRelayService.AnnouncementRetractEventId(MessageId));

        Assert.Null(await DiscordBotRuntime.RetractDeletedAnnouncementAsync(
            harness.Relay, harness.BotId, item, Now));
        Assert.Empty(await harness.RetractionsAsync());
    }

    [Fact]
    public async Task A_bulk_delete_retracts_each_relayed_announcement_and_nothing_else()
    {
        var harness = await Harness.CreateAsync();
        ulong[] deleted = [1571775128134750239, 1571775128134750240, 1571775128134750241];
        await harness.RelayCreateAsync(deleted[0].ToString());
        await harness.RelayLegacySentCreateAsync(deleted[2].ToString());

        foreach (var (messageId, eventId) in DiscordBotRuntime.AnnouncementDeletions("announcements", deleted))
            await harness.Inbox.EnqueueAsync(harness.BotId, messageId, AnnouncementsId, GuildId,
                DiscordGatewayInboxStore.AnnouncementDeleteKind, eventId);
        foreach (var item in await harness.Inbox.PendingAsync(harness.BotId))
        {
            Assert.Equal(DiscordGatewayInboxStore.AnnouncementDeleteKind, item.Kind);
            await DiscordBotRuntime.RetractDeletedAnnouncementAsync(harness.Relay, harness.BotId, item, Now);
            await harness.Inbox.MarkCompletedAsync(item);
        }

        Assert.Equal(
            [$"announcement:{deleted[0]}:retract", $"announcement:{deleted[2]}:retract"],
            (await harness.RetractionsAsync()).Select(row => row.EventId).Order().ToArray());
        // A create sent before channels were recorded takes the deleted message's channel.
        Assert.Equal(AnnouncementsId, (await harness.SingleRetractionAsync(deleted[2].ToString()))
            .Payload!["channelId"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_inbox_keeps_a_deletion_once_and_orders_it_after_the_create()
    {
        var harness = await Harness.CreateAsync();
        var created = await harness.Inbox.EnqueueAsync(harness.BotId, MessageId, AnnouncementsId, GuildId);
        var deletion = await harness.Inbox.EnqueueAsync(harness.BotId, MessageId, AnnouncementsId, GuildId,
            DiscordGatewayInboxStore.AnnouncementDeleteKind,
            DiscordCommonsRelayService.AnnouncementRetractEventId(MessageId));
        var duplicate = await harness.Inbox.EnqueueAsync(harness.BotId, MessageId, AnnouncementsId, GuildId,
            DiscordGatewayInboxStore.AnnouncementDeleteKind,
            DiscordCommonsRelayService.AnnouncementRetractEventId(MessageId));

        Assert.Equal(deletion.EntityId, duplicate.EntityId);
        Assert.Equal(created.EntityId, Assert.Single(DiscordGatewayInboxStore.DueChannelHeads(
            await harness.Inbox.PendingAsync(harness.BotId), DateTimeOffset.UtcNow.AddMinutes(1))).EntityId);
        await harness.Inbox.MarkCompletedAsync(created);
        Assert.Equal(deletion.EntityId, Assert.Single(DiscordGatewayInboxStore.DueChannelHeads(
            await harness.Inbox.PendingAsync(harness.BotId), DateTimeOffset.UtcNow.AddMinutes(1))).EntityId);
    }

    // ---- After a retraction ----

    [Fact]
    public async Task A_retraction_is_what_stops_later_edits()
    {
        var harness = await Harness.CreateAsync();
        await harness.RelayCreateAsync(MessageId);
        Assert.False(await harness.Relay.WasRetractedAsync(harness.BotId, MessageId));

        await harness.RetractAsync(MessageId, harness.Discussion());

        Assert.True(await harness.Relay.WasRetractedAsync(harness.BotId, MessageId));
        Assert.False(await harness.Relay.WasRetractedAsync(harness.BotId, "1571775128134750240"));
        Assert.False(await harness.Relay.WasRetractedAsync(Guid.NewGuid(), MessageId));
        // The create is still there, so only the retraction can stop the update.
        Assert.True(await harness.Relay.WasEnqueuedAsync(
            harness.BotId, DiscordCommonsRelayService.AnnouncementCreateEventId(MessageId)));
        Assert.Equal(new DiscordCommonsInboundRoute(false, DiscordCommonsInboundNext.Stop),
            DiscordBotRuntime.RouteCommonsInbound(
                true, true, false, true, Discord.MessageSource.User, false, false,
                edit: true, createRelayed: true, retracted: true));
    }

    // ---- Fixture ----

    private static DefaultHttpContext SignedIn(string userId)
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
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

    private sealed class Harness
    {
        public required MemoryEntityStore Entities { get; init; }
        public required DiscordBotConfigurationStore Configurations { get; init; }
        public required DiscordCommonsIngestStore Deliveries { get; init; }
        public required DiscordCommonsRelayService Relay { get; init; }
        public required DiscordGatewayInboxStore Inbox { get; init; }
        public required Guid BotId { get; init; }
        public required string AgentId { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var entities = new MemoryEntityStore();
            var agent = entities.Seed("agent", "nova", "Nova", Owner);
            var configurations = new DiscordBotConfigurationStore(new LeafDiscordConfigStore(entities));
            var config = await configurations.CreateAsync(new LeafDiscordConfigUpdate(
                ApplicationId, new string('a', 64), "test-bot-token", false, true,
                [], [], 10 * 1024 * 1024, "nova", "deep-codex"),
                ownerUserId: Owner);
            var deliveries = new DiscordCommonsIngestStore(entities);
            return new Harness
            {
                Entities = entities,
                Configurations = configurations,
                Deliveries = deliveries,
                Relay = new DiscordCommonsRelayService(
                    new DiscordCommonsConfigurationStore(entities), deliveries,
                    NullLogger<DiscordCommonsRelayService>.Instance),
                Inbox = new DiscordGatewayInboxStore(entities),
                BotId = config.EntityId,
                AgentId = agent.Id.ToString(),
            };
        }

        public DefaultHttpContext Discussion() => Signed("private", AgentId, Owner);

        /// <summary>The create row the announcement path records today.</summary>
        public Task<DiscordCommonsIngestDelivery> RelayCreateAsync(string messageId)
        {
            var eventId = DiscordCommonsRelayService.AnnouncementCreateEventId(messageId);
            return Deliveries.EnqueueAsync(BotId, GuildId, eventId,
                DiscordCommonsRelayService.BuildAnnouncementPayload(
                    eventId, messageId, "create", "human", "20", "Laurent", "Release notes",
                    AnnouncementsId, "announcements", 1_234));
        }

        /// <summary>
        /// A create row as one queued before channels were recorded looks once sent: no
        /// <c>channel_id</c> and, being sent, no payload (16 Nov 2026's row).
        /// </summary>
        public async Task RelayLegacySentCreateAsync(string messageId)
        {
            var row = await RelayCreateAsync(messageId);
            await Entities.PatchAsync(row.EntityId, new JsonObject { ["channel_id"] = null });
            await Deliveries.MarkSentAsync(row);
            Assert.Null((await Deliveries.GetAsync(BotId,
                DiscordCommonsRelayService.AnnouncementCreateEventId(messageId)))!.ChannelId);
        }

        public async Task<(int Status, JsonElement Body)> RetractAsync(
            string messageId,
            HttpContext http,
            IAuthContext? auth = null,
            string? reason = null,
            Func<Guid, string?, CancellationToken, Task<string?>>? findChannel = null,
            DateTimeOffset? now = null,
            Guid? botId = null)
            => Read(await DiscordEndpoints.RetractAnnouncementAsync(
                botId ?? BotId, messageId,
                reason is null ? null : new DiscordAnnouncementRetractRequest(reason),
                http, auth ?? new FakeAuthContext(Owner, []), Configurations,
                new DiscordAgentResolver(Entities), Relay,
                findChannel ?? ((_, _, _) => throw new InvalidOperationException("Not expected.")),
                now ?? Now, CancellationToken.None));

        /// <summary>Every retraction row in the store, whichever bot queued it.</summary>
        public async Task<IReadOnlyList<DiscordCommonsIngestDelivery>> RetractionsAsync()
        {
            var rows = new List<DiscordCommonsIngestDelivery>();
            foreach (var entity in await Entities.QueryAsync(
                         new EntityQuery { TypeSlug = DiscordCommonsIngestStore.TypeSlug }))
            {
                var eventId = JsonRead.String(entity.Data, "event_id") ?? "";
                if (eventId.EndsWith(":retract", StringComparison.Ordinal)
                    && Guid.TryParse(JsonRead.String(entity.Data, "bot_id"), out var botId)
                    && await Deliveries.GetAsync(botId, eventId) is { } row)
                    rows.Add(row);
            }
            return rows;
        }

        public async Task<DiscordCommonsIngestDelivery> SingleRetractionAsync(string messageId)
        {
            var row = await Deliveries.GetAsync(BotId,
                DiscordCommonsRelayService.AnnouncementRetractEventId(messageId));
            Assert.NotNull(row);
            Assert.Single(await RetractionsAsync(), item => item.EventId == row.EventId);
            return row;
        }

        public JsonObject Audit(DiscordCommonsIngestDelivery row)
            => Entities.StoredData(row.EntityId)["audit"]!.AsObject();

        private static (int Status, JsonElement Body) Read(IResult result)
        {
            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode
                ?? StatusCodes.Status200OK;
            var value = (result as IValueHttpResult)?.Value;
            return (status, JsonSerializer.SerializeToElement(value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    private sealed class FakeAuthContext(string? userId, IReadOnlyList<string> roles) : IAuthContext
    {
        public string? UserId { get; } = userId;
        public string? Email => null;
        public IReadOnlyList<string> Roles { get; } = roles;
        public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }
}
