using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordOwnerDirectMessageTests
{
    private const string ApplicationId = "100000000000000001";
    private const string Owner = "00000000-0000-4000-8000-000000000001";
    private const string Recipient = "98090659440779264";
    private const string DmChannel = "300000000000000003";

    [Fact]
    public async Task Opens_a_new_dm_binding_session_and_records_the_opening()
    {
        var harness = await Harness.CreateAsync();

        var (status, body) = await harness.OpenAsync(Request("Hello Merendar.", "key-1"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("created", body.GetProperty("binding").GetString());
        Assert.True(body.GetProperty("bindingCreated").GetBoolean());
        Assert.False(body.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, body.GetProperty("generation").GetInt64());
        Assert.Equal(DmChannel, body.GetProperty("channelId").GetString());

        var binding = Assert.Single(await harness.Bindings.ListAsync());
        Assert.Equal(binding.EntityId, body.GetProperty("bindingEntityId").GetGuid());
        Assert.Equal("dm", binding.Kind);
        Assert.Equal(Recipient, binding.CounterpartyUserId);
        Assert.Equal(Owner, binding.OwnerId);
        Assert.Equal("nova", binding.AgentId);
        Assert.Equal("Merendar", binding.ConversationName);
        Assert.Equal(DiscordResponseModes.Direct, binding.ResponseMode);
        Assert.Equal(binding.SessionId, body.GetProperty("sessionId").GetString());
        Assert.Equal(1, harness.Provider.OpenAttempts);
        var config = await harness.Configurations.GetAsync(harness.BotId);
        Assert.Equal(config!.AudienceProfile, binding.AudienceProfile);
        Assert.Equal(config.AudienceProfile, harness.Provider.LastOpen!.AudienceProfileId);
        Assert.Equal("dm", harness.Provider.LastOpen.Scope.Kind);

        var inject = Assert.Single(harness.Compute.Requests);
        Assert.Contains($"/ai-session/sessions/{binding.SessionId}/inject", inject.Uri);
        using (var injected = JsonDocument.Parse(inject.Body))
        {
            Assert.Equal("assistant", injected.RootElement.GetProperty("role").GetString());
            Assert.Equal("Hello Merendar.", injected.RootElement.GetProperty("content").GetString());
        }

        var post = Assert.Single(harness.Discord.MessagePosts);
        Assert.Contains("\"enforce_nonce\":true", post);
        Assert.Contains("Hello Merendar.", post);
        var delivery = await harness.Deliveries.FindSentOutboundAsync(
            binding.BindingId, binding.Generation, body.GetProperty("messageUid").GetString()!);
        Assert.NotNull(delivery);
        Assert.Equal(delivery.EntityId, body.GetProperty("deliveryId").GetGuid());
        Assert.Equal("msg-1", delivery.DiscordId);
        Assert.Empty(await harness.Deliveries.PendingOutboundAsync(binding.BindingId));
    }

    [Fact]
    public async Task Reuses_an_existing_dm_binding_without_changing_generation_or_session()
    {
        var harness = await Harness.CreateAsync();
        var existing = await harness.Bindings.CreateAsync(new CreateDiscordBinding(
            ApplicationId, DmChannel, "dm", null, null, "nova", Owner,
            "nova-discord-external", DiscordResponseModes.Direct, "Merendar", null, Recipient));
        existing = await harness.Coordinator.EnsureReadyAsync(existing);

        var (status, body) = await harness.OpenAsync(Request("Following up.", "key-2"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("reused", body.GetProperty("binding").GetString());
        Assert.Equal(existing.EntityId, body.GetProperty("bindingEntityId").GetGuid());
        Assert.Equal(existing.Generation, body.GetProperty("generation").GetInt64());
        Assert.Equal(existing.SessionId, body.GetProperty("sessionId").GetString());
        Assert.Equal(1, harness.Provider.OpenAttempts);
        Assert.Single(await harness.Bindings.ListAsync());
        Assert.Single(harness.Discord.MessagePosts);
    }

    [Fact]
    public async Task Idempotent_retry_returns_the_same_result_without_a_second_delivery()
    {
        var harness = await Harness.CreateAsync();

        var (_, first) = await harness.OpenAsync(Request("Hello.", "key-3"));
        var (status, second) = await harness.OpenAsync(Request("Hello.", "key-3"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(second.GetProperty("replayed").GetBoolean());
        foreach (var field in new[] { "bindingEntityId", "sessionId", "deliveryId", "messageUid", "discordMessageId" })
            Assert.Equal(first.GetProperty(field).ToString(), second.GetProperty(field).ToString());
        Assert.Equal("created", second.GetProperty("binding").GetString());
        Assert.Single(harness.Discord.MessagePosts);
        Assert.Single(harness.Compute.Requests);
    }

    [Fact]
    public async Task Same_key_with_different_content_is_a_conflict()
    {
        var harness = await Harness.CreateAsync();
        await harness.OpenAsync(Request("Hello.", "key-4"));

        var (status, body) = await harness.OpenAsync(Request("Something else.", "key-4"));

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("idempotency_key_conflict", body.GetProperty("error").GetString());
        Assert.Single(harness.Discord.MessagePosts);
        Assert.Single(harness.Compute.Requests);
    }

    [Fact]
    public async Task Recipient_outside_the_allowlist_is_forbidden_without_side_effects()
    {
        var harness = await Harness.CreateAsync();

        var (status, body) = await harness.OpenAsync(
            Request("Hello.", "key-5") with { RecipientUserId = "123456789012345678" });

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("recipient_not_allowlisted", body.GetProperty("error").GetString());
        Assert.Empty(harness.Discord.Requests);
        Assert.Empty(await harness.Bindings.ListAsync());
    }

    [Fact]
    public async Task External_discord_conversation_caller_is_rejected()
    {
        var harness = await Harness.CreateAsync();
        var http = Http(Identity(harness.AgentId, Owner, new object[]
        {
            new { kind = "discussion", id = "d1" },
            new { kind = "external-conversation", id = "discord:binding" },
            new { kind = "session", id = "session-1" },
        }));

        var (status, _) = await harness.OpenAsync(Request("Hello.", "key-6"), http);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(0, harness.OpenInvocations);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Agent_or_owner_mismatch_is_rejected(bool agentMatches, bool ownerMatches)
    {
        var harness = await Harness.CreateAsync();
        var http = Http(Identity(
            agentMatches ? harness.AgentId : Guid.NewGuid().ToString(),
            ownerMatches ? Owner : "owner-2",
            new object[] { new { kind = "discussion", id = "d1" } }));

        var (status, _) = await harness.OpenAsync(Request("Hello.", "key-7"), http);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(0, harness.OpenInvocations);
    }

    [Fact]
    public async Task Unsigned_caller_is_rejected()
    {
        var harness = await Harness.CreateAsync();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Owner)], "test")),
        };

        var (status, _) = await harness.OpenAsync(Request("Hello.", "key-8"), http);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(0, harness.OpenInvocations);
    }

    [Fact]
    public async Task Disabled_bot_is_reported_truthfully()
    {
        var harness = await Harness.CreateAsync(enabled: false);

        var (status, body) = await harness.OpenAsync(Request("Hello.", "key-9"));

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("discord_bot_disabled", body.GetProperty("error").GetString());
        Assert.Equal(0, harness.OpenInvocations);
    }

    [Fact]
    public async Task An_owner_dm_with_an_image_is_reviewed_with_the_image_and_sent()
    {
        var sentinel = new TestSentinel();
        var harness = await Harness.CreateAsync(sentinel: sentinel);

        var (status, _) = await harness.OpenAsync(new DiscordOwnerDirectMessageRequest(
            Recipient, "Here is the picture.", "key-image", "Here is the picture.",
            [new DiscordBindingOutputAssetRequest("stable-cat.png", "A cartoon cat")]));

        Assert.Equal(StatusCodes.Status200OK, status);
        var review = Assert.Single(sentinel.Requests);
        Assert.Equal(new UntrustedContentImage("stable-cat.png", "image/png", "A cartoon cat"),
            Assert.Single(review.Images!));
        Assert.Single(harness.Discord.MessagePosts);
    }

    [Theory]
    [InlineData(false)] // the Sentinel holds the image
    [InlineData(true)]  // the Sentinel is unavailable
    public async Task An_owner_dm_whose_image_is_held_or_unreviewable_is_never_sent(bool unavailable)
    {
        var verdict = unavailable
            ? UntrustedContentVerdict.Unavailable()
            : new UntrustedContentVerdict("high", ["image-private-screenshot"], "require_private_approval", true,
                UntrustedContentIsolation.VerifiedToolFree);
        var harness = await Harness.CreateAsync(sentinel: new FixedSentinel(verdict));
        var config = (await harness.Configurations.GetAsync(harness.BotId))!;

        var refused = await Assert.ThrowsAsync<DiscordSentinelRefusedException>(() => harness.Messenger.OpenAsync(
            config, Owner, new DiscordOwnerDirectMessageRequest(
                Recipient, "Look.", "key-held", "Look.",
                [new DiscordBindingOutputAssetRequest("stable-inbox.png", "My inbox")])));

        Assert.Equal(unavailable, refused.Unavailable);
        Assert.Empty(harness.Discord.MessagePosts);
        Assert.Empty(harness.Compute.Requests);
        Assert.Empty(await harness.Bindings.ListAsync());
    }

    private sealed class FixedSentinel(UntrustedContentVerdict verdict) : IUntrustedContentReview
    {
        public Task<UntrustedContentReviewResult> ReviewAsync(
            UntrustedContentReviewRequest request, CancellationToken ct = default)
            => Task.FromResult(new UntrustedContentReviewResult(
                verdict, null, [], new UntrustedContentScreening(0, [], [], request.Content, []), "job", "trace")
            {
                Direction = request.Direction,
                ImagesReviewed = verdict.Available ? request.Images?.Count ?? 0 : 0,
            });

        public UntrustedContentScreening Screen(
            string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
            string? ownerDisplayName = null)
            => new(0, [], [], content, []);
    }

    [Fact]
    public async Task Discord_refusal_leaves_no_binding_or_session_behind()
    {
        var harness = await Harness.CreateAsync();
        harness.Discord.RefuseMessages = true;

        var (status, body) = await harness.OpenAsync(Request("Hello.", "key-10"));

        Assert.Equal(StatusCodes.Status502BadGateway, status);
        Assert.Equal("discord_dm_refused", body.GetProperty("error").GetString());
        Assert.Contains("50007", body.GetProperty("message").GetString());
        Assert.Empty(await harness.Bindings.ListAsync());
        Assert.Equal(0, harness.Provider.OpenAttempts);
        Assert.Empty(harness.Compute.Requests);

        // Once the user accepts DMs, the same request can be retried with the same key.
        harness.Discord.RefuseMessages = false;
        var (retryStatus, retry) = await harness.OpenAsync(Request("Hello.", "key-10"));
        Assert.Equal(StatusCodes.Status200OK, retryStatus);
        Assert.Equal("created", retry.GetProperty("binding").GetString());
    }

    [Fact]
    public async Task Retry_after_session_failure_does_not_deliver_twice()
    {
        var harness = await Harness.CreateAsync();
        harness.Provider.FailOpen = true;

        var (status, _) = await harness.OpenAsync(Request("Hello.", "key-11"));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Single(harness.Discord.MessagePosts);

        harness.Provider.FailOpen = false;
        var (retryStatus, retry) = await harness.OpenAsync(Request("Hello.", "key-11"));

        Assert.Equal(StatusCodes.Status200OK, retryStatus);
        Assert.Equal("created", retry.GetProperty("binding").GetString());
        Assert.Equal("msg-1", retry.GetProperty("discordMessageId").GetString());
        Assert.Single(harness.Discord.MessagePosts);
        Assert.Single(harness.Compute.Requests);
    }

    private static DiscordOwnerDirectMessageRequest Request(string content, string key)
        => new(Recipient, content, key);

    private static string Identity(string actorEntityId, string beneficiaryId, object[] context)
        => JsonSerializer.Serialize(new
        {
            app = new { id = "nova" },
            actor = new { kind = "agent", entityId = actorEntityId },
            beneficiary = new { kind = "user", id = beneficiaryId },
            context,
        });

    private static DefaultHttpContext Http(string identity)
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", "execution"),
                new Claim("execution_identity", identity),
            ], "test")),
        };

    private sealed class Harness
    {
        public required MemoryEntityStore Entities { get; init; }
        public required DiscordBotConfigurationStore Configurations { get; init; }
        public required DiscordBindingStore Bindings { get; init; }
        public required DiscordDeliveryStore Deliveries { get; init; }
        public required DiscordConversationCoordinator Coordinator { get; init; }
        public required DiscordOwnerDirectMessenger Messenger { get; init; }
        public required SessionProvider Provider { get; init; }
        public required InjectGateway Compute { get; init; }
        public required FakeDiscord Discord { get; init; }
        public required Guid BotId { get; init; }
        public required string AgentId { get; init; }
        public int OpenInvocations { get; private set; }

        public static async Task<Harness> CreateAsync(bool enabled = true, IUntrustedContentReview? sentinel = null)
        {
            var entities = new MemoryEntityStore();
            _ = await entities.UpsertBySlugAsync("quality-mode", "deep-codex", "Deep (Codex)",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["provider"] = "codex-default",
                    ["quality_tier"] = "deep",
                    ["model"] = "gpt-5.6-sol",
                    ["effort"] = "high",
                });
            var agent = entities.Seed("agent", "nova", "Nova", Owner);
            var configurations = new DiscordBotConfigurationStore(new LeafDiscordConfigStore(entities));
            var config = await configurations.CreateAsync(new LeafDiscordConfigUpdate(
                ApplicationId, new string('a', 64), "test-bot-token", false, enabled,
                [Recipient], [], 10 * 1024 * 1024, "nova", "deep-codex"),
                ownerUserId: Owner);
            var bindings = new DiscordBindingStore(entities);
            var deliveries = new DiscordDeliveryStore(entities);
            var provider = new SessionProvider();
            var compute = new InjectGateway();
            var coordinator = new DiscordConversationCoordinator(
                bindings, deliveries, configurations, new DiscordBehaviorStore(entities),
                new DiscordRuntimeOptionsResolver(entities),
                new DiscordUserIdentityResolver(entities, NullLogger<DiscordUserIdentityResolver>.Instance),
                compute, [provider], new PromptInjectionDetector(), new DiscordEgressGuard(),
                new DiscordTypingLeases(), NullLogger<DiscordConversationCoordinator>.Instance,
                sentinel ?? TestSentinel.Clean);
            var discord = new FakeDiscord();
            var messenger = new DiscordOwnerDirectMessenger(
                bindings, new DiscordBehaviorStore(entities), deliveries, coordinator,
                new DiscordRestTransport(discord), new NoAssets());
            return new Harness
            {
                Entities = entities,
                Configurations = configurations,
                Bindings = bindings,
                Deliveries = deliveries,
                Coordinator = coordinator,
                Messenger = messenger,
                Provider = provider,
                Compute = compute,
                Discord = discord,
                BotId = config.EntityId,
                AgentId = agent.Id.ToString(),
            };
        }

        public Task<(int Status, JsonElement Body)> OpenAsync(
            DiscordOwnerDirectMessageRequest request)
            => OpenAsync(request, Http(Identity(AgentId, Owner,
                new object[] { new { kind = "discussion", id = "d1" } })));

        public async Task<(int Status, JsonElement Body)> OpenAsync(
            DiscordOwnerDirectMessageRequest request, HttpContext http)
        {
            var result = await DiscordEndpoints.OpenOwnerDirectMessageAsync(
                BotId, http, Configurations, new DiscordAgentResolver(Entities),
                (config, ownerUserId, ct) =>
                {
                    OpenInvocations++;
                    return Messenger.OpenAsync(config, ownerUserId, request, ct);
                },
                CancellationToken.None);
            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode
                ?? StatusCodes.Status200OK;
            var value = (result as IValueHttpResult)?.Value;
            return (status, JsonSerializer.SerializeToElement(value));
        }
    }

    private sealed class SessionProvider : IExternalAgentConversationProvider
    {
        public int OpenAttempts { get; private set; }
        public bool FailOpen { get; set; }
        public ExternalConversationOpenRequest? LastOpen { get; private set; }
        public string ProviderId => "test";
        public bool CanHandle(string agentSlugOrId) => agentSlugOrId == "nova";

        public Task<ExternalConversationHandle> OpenAsync(
            ExternalConversationOpenRequest request, CancellationToken ct = default)
        {
            if (FailOpen) throw new InvalidOperationException("Agent session unavailable");
            OpenAttempts++;
            LastOpen = request;
            return Task.FromResult(new ExternalConversationHandle(
                ProviderId, request.BindingId, request.Generation,
                $"conversation-{OpenAttempts}", $"session-{OpenAttempts}"));
        }

        public Task<ExternalConversationAdmission> SendAsync(
            ExternalConversationHandle handle, ExternalConversationInput input,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ExternalConversationPage> ReadSettledAsync(
            ExternalConversationHandle handle, ExternalConversationCursor? after = null,
            int limit = 100, CancellationToken ct = default)
            => Task.FromResult(new ExternalConversationPage([], after, true));

        public IDisposable SubscribeSettled(
            Func<ExternalConversationSettled, CancellationToken, Task> handler)
            => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class InjectGateway : IComputeGateway
    {
        public List<(string Uri, string Body)> Requests { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri?.ToString() ?? "", body));
            var messageUid = JsonDocument.Parse(body).RootElement.GetProperty("messageUid").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { injected = true, messageUid })),
            };
        }
    }

    private sealed class FakeDiscord : HttpMessageHandler
    {
        public bool RefuseMessages { get; set; }
        public List<string> Requests { get; } = [];
        public List<string> MessagePosts { get; } = [];
        private readonly List<string> sentNonces = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Post && path.EndsWith("/users/@me/channels"))
                return Json(HttpStatusCode.OK, new
                {
                    id = DmChannel,
                    type = 1,
                    recipients = new[] { new { id = Recipient, username = "merendar", global_name = "Merendar" } },
                });
            if (request.Method == HttpMethod.Get && path.Contains($"/channels/{DmChannel}/messages"))
                return Json(HttpStatusCode.OK, sentNonces.Select((nonce, index) => new
                {
                    id = $"msg-{index + 1}",
                    nonce,
                    attachments = Array.Empty<object>(),
                }).ToArray());
            if (request.Method == HttpMethod.Post && path.EndsWith($"/channels/{DmChannel}/messages"))
            {
                if (RefuseMessages)
                    return Json(HttpStatusCode.Forbidden,
                        new { message = "Cannot send messages to this user", code = 50007 });
                MessagePosts.Add(body);
                var nonce = JsonDocument.Parse(body).RootElement.GetProperty("nonce").GetString()!;
                sentNonces.Add(nonce);
                return Json(HttpStatusCode.OK, new
                {
                    id = $"msg-{sentNonces.Count}",
                    nonce,
                    attachments = Array.Empty<object>(),
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };
    }

    private sealed class NoAssets : IAssets
    {
        public Task<AssetRef> UploadAsync(Stream content, string fileName,
            string? contentType = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AssetRef> UploadIdempotentAsync(Stream content, string stableKey,
            string fileName, string? contentType = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public string GetUrl(string assetId) => $"/api/assets/{assetId}";
        public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
            => Task.FromResult<AssetFile?>(null);
    }
}
