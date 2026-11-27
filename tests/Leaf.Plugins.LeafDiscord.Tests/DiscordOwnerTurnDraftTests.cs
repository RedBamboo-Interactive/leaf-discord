using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordOwnerTurnDraftTests
{
    private const string Route = "/api/apps/leaf-discord/outreach/owner-turn-drafts";
    private const string ListRoute = "/api/apps/leaf-discord/outreach/approvals?discussionId=private-live";
    private const string Asset = "38d88f0d-f427-4128-a7fb-7db151a932eb.png";
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");

    [Fact]
    public async Task One_held_card_survives_concurrent_and_lost_receipt_retries_without_publishing()
    {
        var f = await Fixture.CreateAsync();
        await using var first = await f.StartAsync(mapDraft: Environment.GetEnvironmentVariable("LEAF_OWNER_DRAFT_RED") != "1");
        var input = f.Input();
        var otherCall = input with { CallId = "call-retry" };
        f.Sentinel.ConcurrentReviews = true;
        var left = first.PostAsync(input);
        var right = first.PostAsync(otherCall);
        using var r1 = await left;
        using var r2 = await right;
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var held = await r2.Content.ReadFromJsonAsync<DiscordOwnerTurnResult>(Wire);
        Assert.Equal("held", held?.Status);
        Assert.NotNull(held!.MessageUid);

        // Discard the first receipt and restart the HTTP host and coordinator over the same
        // encrypted store. A new envelope for the exact intent must recover the receipt.
        await using var restarted = await f.StartAsync();
        var reviewCount = f.Sentinel.Requests.Count;
        using var replay = await restarted.PostAsync(otherCall with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(45) });
        Assert.Equal(held, await replay.Content.ReadFromJsonAsync<DiscordOwnerTurnResult>(Wire));
        Assert.Equal(reviewCount, f.Sentinel.Requests.Count);
        Assert.All(f.Sentinel.Requests, request =>
        {
            Assert.Equal("A synthetic hold.", request.Content);
            Assert.Equal(UntrustedContentDirection.Outbound, request.Direction);
            Assert.Equal(Asset, Assert.Single(request.Images!).AssetId);
        });

        using var ownerList = await restarted.GetListAsync("owner");
        Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
        var json = await ownerList.Content.ReadAsStringAsync();
        using var list = JsonDocument.Parse(json);
        var card = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(held.MessageUid, card.GetProperty("messageUid").GetString());
        Assert.Equal("held", card.GetProperty("sentinel").GetProperty("outcome").GetString());
        Assert.True(card.GetProperty("canRelease").GetBoolean());
        Assert.True(card.GetProperty("canDiscard").GetBoolean());
        Assert.Equal(Asset, Assert.Single(card.GetProperty("outputAssets").EnumerateArray()).GetProperty("assetId").GetString());

        using var serviceList = await restarted.GetListAsync("service");
        using var nonOwnerList = await restarted.GetListAsync("other-owner");
        using var agentList = await restarted.GetListAsync("execution");
        Assert.Equal(HttpStatusCode.Forbidden, serviceList.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonOwnerList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, agentList.StatusCode);

        var changed = SetDigest(input with { Draft = input.Draft with { DeliveryContent = "Changed" } });
        using var conflict = await restarted.PostAsync(changed);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var changedTurn = await restarted.PostAsync(input with { TurnId = "different-turn" });
        Assert.Equal(HttpStatusCode.Conflict, changedTurn.StatusCode);

        var row = Assert.Single(await f.Entities.QueryAsync(new EntityQuery { TypeSlug = DiscordOutreachApprovalStore.TypeSlug }));
        var protectedIndex = f.Entities.StoredData(row.Id).ToJsonString();
        Assert.DoesNotContain(input.Draft.Content, protectedIndex, StringComparison.Ordinal);
        Assert.DoesNotContain(input.QueueItemId, protectedIndex, StringComparison.Ordinal);
        Assert.Equal(held.MessageUid, Assert.Single(await f.Store.ListOpenAsync(f.Origin.Id, default)).MessageUid);
        await f.AssertNoTargetChangesAsync();

        if (Environment.GetEnvironmentVariable("LEAF_OWNER_DRAFT_CARD_JSON") is { Length: > 0 } artifact)
            await File.WriteAllTextAsync(artifact, json);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("owner")]
    [InlineData("execution")]
    [InlineData("external-execution")]
    [InlineData("automation-execution")]
    [InlineData("local-default")]
    [InlineData("wrong-subject")]
    [InlineData("wrong-client")]
    [InlineData("wrong-role")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("nonloopback")]
    [InlineData("browser-origin")]
    public async Task Draft_route_requires_the_specific_loopback_service(string identity)
    {
        var f = await Fixture.CreateAsync();
        await using var host = await f.StartAsync();
        using var response = await host.PostAsync(f.Input(), identity);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(f.Sentinel.Requests);
        Assert.Empty(await f.Store.ListOpenAsync(f.Origin.Id, default));
        await f.AssertNoTargetChangesAsync();
    }

    [Theory]
    [InlineData("version", HttpStatusCode.BadRequest)]
    [InlineData("operation", HttpStatusCode.BadRequest)]
    [InlineData("expired", HttpStatusCode.Forbidden)]
    [InlineData("future", HttpStatusCode.Forbidden)]
    [InlineData("digest", HttpStatusCode.BadRequest)]
    [InlineData("intent", HttpStatusCode.BadRequest)]
    [InlineData("header", HttpStatusCode.BadRequest)]
    [InlineData("origin", HttpStatusCode.Forbidden)]
    [InlineData("session", HttpStatusCode.Forbidden)]
    [InlineData("native-thread", HttpStatusCode.Forbidden)]
    [InlineData("owner", HttpStatusCode.Forbidden)]
    [InlineData("agent", HttpStatusCode.Forbidden)]
    [InlineData("confidential-origin", HttpStatusCode.Forbidden)]
    [InlineData("confidential-session", HttpStatusCode.Forbidden)]
    [InlineData("heartbeat", HttpStatusCode.Forbidden)]
    [InlineData("automation", HttpStatusCode.Forbidden)]
    [InlineData("external", HttpStatusCode.Forbidden)]
    [InlineData("disallowed-dm", HttpStatusCode.Forbidden)]
    [InlineData("wrong-binding-owner", HttpStatusCode.Forbidden)]
    [InlineData("disabled-bot", HttpStatusCode.Conflict)]
    [InlineData("stale-binding", HttpStatusCode.Conflict)]
    [InlineData("missing-asset", HttpStatusCode.Forbidden)]
    [InlineData("confidential-asset", HttpStatusCode.Forbidden)]
    [InlineData("wrong-asset-owner", HttpStatusCode.Forbidden)]
    [InlineData("changed-asset-bytes", HttpStatusCode.Forbidden)]
    [InlineData("unlisted-asset", HttpStatusCode.BadRequest)]
    public async Task Draft_admission_rejects_invalid_scope_and_payload_before_review(string variant, HttpStatusCode status)
    {
        var f = await Fixture.CreateAsync(dm: variant == "disallowed-dm");
        await using var host = await f.StartAsync();
        var input = f.Input();
        string? header = null;
        switch (variant)
        {
            case "version": input = input with { Version = 2 }; break;
            case "operation": input = input with { Operation = "discord.outreach.release" }; break;
            case "expired": input = input with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }; break;
            case "future": input = input with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3) }; break;
            case "digest": input = input with { PayloadSha256 = "wrong" }; break;
            case "intent": input = input with { IntentId = "wrong" }; break;
            case "header": header = "wrong"; break;
            case "origin": input = input with { DiscussionId = "different" }; break;
            case "session": input = SetIntent(input with { SessionId = "different" }); break;
            case "native-thread": input = input with { ThreadId = "different" }; break;
            case "owner": input = input with { OwnerId = Guid.NewGuid().ToString() }; break;
            case "agent": input = input with { AgentId = Guid.NewGuid().ToString() }; break;
            case "confidential-origin": await f.PatchAsync(f.Origin, "confidential", true); break;
            case "confidential-session": await f.PatchAsync(f.SourceSession, "confidential", true); break;
            case "heartbeat": await f.PatchAsync(f.Origin, "type", "heartbeat"); break;
            case "automation": await f.PatchAsync(f.Origin, "automation_attempt_job_id", "synthetic"); break;
            case "external": await f.PatchAsync(f.Origin, "source_app", "leaf-discord"); break;
            case "wrong-binding-owner": await f.PatchAsync(await f.BindingEntityAsync(), "owner_id", Guid.NewGuid().ToString()); break;
            case "disabled-bot": await f.PatchAsync(f.Bot, "enabled", false); break;
            case "stale-binding": input = SetDigest(input with { Draft = input.Draft with { BindingGeneration = f.Binding.Generation + 1 } }); break;
            case "missing-asset": f.Assets.Record = null; break;
            case "confidential-asset": f.Assets.Record = f.Assets.Record! with { Confidential = true }; break;
            case "wrong-asset-owner": f.Assets.Record = f.Assets.Record! with { OwnerId = Guid.NewGuid().ToString() }; break;
            case "changed-asset-bytes": f.Assets.Bytes = "changed"u8.ToArray(); break;
            case "unlisted-asset": input = SetDigest(input with { Draft = input.Draft with
                { Content = "![other](/api/assets/another.png)" } }); break;
        }
        using var response = await host.PostAsync(input, key: header);
        Assert.Equal(status, response.StatusCode);
        Assert.Empty(f.Sentinel.Requests);
        Assert.Empty(await f.Store.ListOpenAsync(f.Origin.Id, default));
        await f.AssertNoTargetChangesAsync();
    }

    [Theory]
    [InlineData(false, false, "clear")]
    [InlineData(true, false, "held")]
    [InlineData(false, true, "held")]
    public async Task The_review_receipt_never_publishes_and_raw_outreach_cannot_reuse_it(
        bool unavailable, bool partialImages, string status)
    {
        var f = await Fixture.CreateAsync();
        f.Sentinel.Hold = false;
        f.Sentinel.Available = !unavailable;
        f.Sentinel.ReviewImages = !partialImages;
        await using var host = await f.StartAsync();
        var input = f.Input();
        using var response = await host.PostAsync(input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, (await response.Content.ReadFromJsonAsync<DiscordOwnerTurnResult>())?.Status);
        var draft = (await f.Store.FindByKeyAsync(input.Draft.BindingId, "owner-turn-v1-" + input.IntentId, default))!.Draft;
        Assert.Equal(status == "held", draft.IsOpen);
        var error = await Assert.ThrowsAsync<DiscordOutreachConflictException>(() =>
            host.Coordinator.SubmitPrivateOutreachAsync(f.Binding, f.Origin, draft.Caller,
                new DiscordBindingMessageRequest(input.Draft.Content, draft.Request.IdempotencyKey,
                    input.Draft.DeliveryContent, [new(Asset, "Synthetic attachment")], NotifyReplies: false),
                new DiscordReplySubscriptionStore(f.Entities), default));
        Assert.Equal("idempotency_key_conflict", error.Code);
        await f.AssertNoTargetChangesAsync();
    }

    [Fact]
    public async Task Changes_to_canonical_scope_during_review_do_not_leave_an_approval()
    {
        var f = await Fixture.CreateAsync();
        f.Sentinel.BeforeReturn = () => f.PatchAsync(f.Origin, "session_id", "changed-while-reviewing");
        await using var host = await f.StartAsync();
        using var response = await host.PostAsync(f.Input());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await f.Store.ListOpenAsync(f.Origin.Id, default));
        await f.AssertNoTargetChangesAsync();
    }

    private static string Hash<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Wire))).ToLowerInvariant();

    private static DiscordOwnerTurnAdmission SetIntent(DiscordOwnerTurnAdmission input)
        => input with { IntentId = Hash(new { input.SessionId, input.QueueItemId, input.Draft.BindingId }) };

    private static DiscordOwnerTurnAdmission SetDigest(DiscordOwnerTurnAdmission input)
        => input with { PayloadSha256 = Hash(input.Draft) };

    private sealed class Fixture
    {
        private readonly IDataProtectionProvider keys = new EphemeralDataProtectionProvider();
        public MemoryEntityStore Entities { get; } = MemoryEntityStore.WithSeedSecretFields();
        public FakeAssets Assets { get; } = new();
        public FakeSentinel Sentinel { get; } = new();
        private readonly BlockedTransport transport = new();
        public DiscordOutreachApprovalStore Store => new(Entities, keys);
        public LeafEntity Origin { get; private set; } = null!;
        public LeafEntity SourceSession { get; private set; } = null!;
        public LeafEntity Bot { get; private set; } = null!;
        public LeafEntity Owner { get; private set; } = null!;
        public LeafEntity Agent { get; private set; } = null!;
        public DiscordBinding Binding { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(bool dm = false)
        {
            var f = new Fixture();
            f.Owner = await f.Entities.CreateAsync("user", "Synthetic owner", new JsonObject { ["status"] = "active" });
            f.Agent = await f.Entities.CreateAsync("agent", "Synthetic Nova", new JsonObject { ["enabled"] = true });
            var owner = f.Owner.Id.ToString();
            var agent = f.Agent.Id.ToString();
            var configurations = new DiscordBotConfigurationStore(new LeafDiscordConfigStore(f.Entities));
            var bot = await configurations.CreateAsync(new LeafDiscordConfigUpdate("100000000000000001", new string('a', 64),
                "fixture-never-used", false, true, [], [], 10 * 1024 * 1024, agent, "deep-codex"), ownerUserId: owner);
            f.Bot = (await f.Entities.GetAsync(bot.EntityId))!;
            var bindings = new DiscordBindingStore(f.Entities);
            var binding = await bindings.CreateAsync(new CreateDiscordBinding(
                bot.ApplicationId, "123456789012345678", dm ? "dm" : "guild_channel", dm ? null : "223456789012345678",
                null, agent, owner, "nova-discord-external", "participate",
                CounterpartyUserId: dm ? "323456789012345678" : null));
            f.Binding = await bindings.AttachConversationAsync(binding,
                new ExternalConversationHandle("fixture", binding.BindingId, binding.Generation, "target-handle", "target-session"));
            f.Origin = await f.Entities.CreateAsync("discussion", "Private synthetic discussion", new JsonObject
            {
                ["app"] = "nova", ["type"] = "live", ["status"] = "idle", ["owner_id"] = owner, ["agent"] = agent,
                ["discussion_id"] = "private-live", ["session_id"] = "owner-session", ["confidential"] = false,
            });
            f.SourceSession = await f.Entities.CreateAsync("ai-session", "Source mirror", new JsonObject
            {
                ["app"] = "redcompute", ["provider"] = "codex", ["session_id"] = "owner-session",
                ["external_session_id"] = "native-thread", ["user_id"] = owner, ["owner_id"] = owner,
                ["owner_agent_id"] = agent, ["confidential"] = false,
            });
            var provenance = new ComputeProvenance(ComputeProvenance.CurrentSchemaVersion,
                new ComputeOrigin("redleaf", new ComputeAppReference("app", "nova", null, "Nova"),
                    new ComputeEntrypoint("http", "/message", "POST")),
                new ComputeActor("agent", "Synthetic Nova", agent), new ComputeBeneficiary("user", owner),
                [], new ComputeTrace(), ComputeProvenanceAssurance.Verified, DateTimeOffset.UtcNow);
            f.Assets.Record = new(Asset, "regular", "image/png", Png.Length,
                Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant(), DateTimeOffset.UtcNow, provenance, null, false, owner);
            return f;
        }

        public async Task<LeafEntity> BindingEntityAsync()
            => (await Entities.GetAsync(Binding.EntityId))!;

        public Task<LeafEntity> PatchAsync(LeafEntity row, string field, JsonNode? value)
            => Entities.PatchAsync(row.Id, new JsonObject { [field] = value });

        public DiscordOwnerTurnAdmission Input()
            => SetDigest(SetIntent(new DiscordOwnerTurnAdmission(1, "discord.outreach.draft", "", "",
                DateTimeOffset.UtcNow.AddSeconds(50), "private-live", "owner-session", Owner.Id.ToString(),
                Agent.Id.ToString(), "q_synthetic_owner_input", "accepted-owner-message", "connection", "native-thread",
                "native-turn", "native-call",
                new(Binding.EntityId, Binding.Generation, "Canonical ![Synthetic attachment](/api/assets/" + Asset + ")",
                    "A synthetic hold.", [new(Asset, "Synthetic attachment")]))));

        public async Task<Host> StartAsync(bool mapDraft = true)
        {
            var bindings = new DiscordBindingStore(Entities);
            var deliveries = new DiscordDeliveryStore(Entities);
            var configs = new DiscordBotConfigurationStore(new LeafDiscordConfigStore(Entities));
            var coordinator = new DiscordConversationCoordinator(bindings, deliveries, configs,
                new DiscordBehaviorStore(Entities), new DiscordRuntimeOptionsResolver(Entities),
                new DiscordUserIdentityResolver(Entities, NullLogger<DiscordUserIdentityResolver>.Instance),
                transport, [], new PromptInjectionDetector(), new DiscordEgressGuard(), new DiscordTypingLeases(),
                NullLogger<DiscordConversationCoordinator>.Instance, Sentinel, approvals: Store, ownerTurnAssets: Assets);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(bindings);
            builder.Services.AddSingleton(Store);
            builder.Services.AddSingleton(coordinator);
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<IAuthContext, LabAuthContext>();
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var identity = context.Request.Headers["X-Fixture-Identity"].ToString();
                context.User = Principal(identity);
                if (identity == "nonloopback") context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                if (identity == "browser-origin") context.Request.Headers.Origin = "http://127.0.0.1";
                await next(context);
            });
            var group = app.MapGroup("/api/apps/leaf-discord");
            if (mapDraft) DiscordEndpoints.MapOutreachApprovals(group);
            await app.StartAsync();
            return new(app, coordinator);
        }

        private ClaimsPrincipal Principal(string fixture)
        {
            if (fixture == "anonymous") return new(new ClaimsIdentity());
            if (fixture is "owner" or "other-owner")
                return new(new ClaimsIdentity([new("sub", fixture == "owner" ? Owner.Id.ToString() : Guid.NewGuid().ToString()),
                    new("roles", "owner"), new("sid", "fixture-owner-browser")], "Bearer"));
            var claims = new List<Claim>
            {
                new("sub", fixture == "wrong-subject" ? "service:nova" : "service:redcompute"),
                new("client_id", fixture == "wrong-client" ? "nova" : "redcompute"),
                new("roles", fixture == "wrong-role" ? "admin" : "service"),
                new("iss", fixture == "wrong-issuer" ? "other" : "redsuite"),
                new("aud", fixture == "wrong-audience" ? "other" : "redsuite"),
            };
            if (fixture.EndsWith("execution", StringComparison.Ordinal))
                claims.Add(new("token_use", "execution"));
            return new(new ClaimsIdentity(claims, fixture == "local-default" ? "LocalDefault" : "Bearer"));
        }

        public async Task AssertNoTargetChangesAsync()
        {
            Assert.Equal(0, transport.Calls);
            Assert.Empty(await new DiscordDeliveryStore(Entities).PendingOutboundAsync(Binding.BindingId));
            Assert.Empty(await Entities.QueryAsync(new EntityQuery { TypeSlug = DiscordDeliveryStore.TypeSlug }));
            Assert.Null(await new DiscordConversationLeadStore(Entities).GetAsync(Binding));
            Assert.Null(await new DiscordReplySubscriptionStore(Entities).FindActiveAsync(
                Binding.BindingId, "private-live", DateTimeOffset.UtcNow));
        }
    }

    private sealed class LabAuthContext(IHttpContextAccessor http) : IAuthContext
    {
        public string? UserId => http.HttpContext?.User.FindFirstValue("sub");
        public string? Email => null;
        public IReadOnlyList<string> Roles => http.HttpContext?.User.FindAll("roles").Select(c => c.Value).ToArray() ?? [];
        public bool IsInRole(string role) => Roles.Contains(role);
    }

    private sealed class Host(WebApplication app, DiscordConversationCoordinator coordinator) : IAsyncDisposable
    {
        private readonly HttpClient http = new() { BaseAddress = new Uri(app.Urls.Single()) };
        public DiscordConversationCoordinator Coordinator => coordinator;

        public Task<HttpResponseMessage> PostAsync(DiscordOwnerTurnAdmission input, string identity = "service", string? key = null)
            => SendAsync(Route, HttpMethod.Post, identity, JsonContent.Create(input, options: Wire), key ?? input.IntentId);

        public Task<HttpResponseMessage> GetListAsync(string identity) => SendAsync(ListRoute, HttpMethod.Get, identity, null, null);

        private async Task<HttpResponseMessage> SendAsync(string path, HttpMethod method, string identity, HttpContent? content, string? key)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Add("X-Fixture-Identity", identity);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            return await http.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class BlockedTransport : IComputeGateway
    {
        public int Calls { get; private set; }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Target transcript transport must not be called.");
        }
    }

    private sealed class FakeAssets : IAssets
    {
        public AssetRecord? Record { get; set; }
        public byte[] Bytes { get; set; } = Png;
        public Task<AssetRecord?> GetRecordAsync(string assetId, CancellationToken ct = default)
            => Task.FromResult(assetId == Asset ? Record : null);
        public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
            => Task.FromResult<AssetFile?>(assetId == Asset ? new(Bytes, "image/png") : null);
        public string GetUrl(string assetId) => "/api/assets/" + assetId;
        public Task<AssetRef> UploadAsync(Stream content, string fileName, string? contentType = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeSentinel : IUntrustedContentReview
    {
        private readonly TaskCompletionSource paired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public ConcurrentQueue<UntrustedContentReviewRequest> Requests { get; } = [];
        public bool Hold { get; set; } = true;
        public bool Available { get; set; } = true;
        public bool ReviewImages { get; set; } = true;
        public bool ConcurrentReviews { get; set; }
        public Func<Task>? BeforeReturn { get; set; }

        public async Task<UntrustedContentReviewResult> ReviewAsync(UntrustedContentReviewRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (ConcurrentReviews)
            {
                if (Interlocked.Increment(ref arrivals) == 2) paired.TrySetResult();
                await paired.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
            }
            if (BeforeReturn is not null) await BeforeReturn();
            var verdict = new UntrustedContentVerdict(Hold ? "high" : "none", [],
                Hold ? "require_private_approval" : "normal", Available, UntrustedContentIsolation.VerifiedToolFree);
            return new(verdict, null, [], new UntrustedContentScreening(0, [], [], request.Content, []), null, "synthetic-review")
            { Direction = request.Direction, ImagesReviewed = Available && ReviewImages ? request.Images?.Count ?? 0 : 0 };
        }

        public UntrustedContentScreening Screen(string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
            string? ownerDisplayName = null) => new(0, [], [], content, []);
    }
}
