using System.Net;
using System.Text;
using System.Text.Json;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordTransportTests
{
    [Fact]
    public async Task Enforced_nonce_recovers_an_existing_message_without_a_second_post()
    {
        var nonce = "0123456789abcdef01234567";
        var handler = new QueueHandler(
            Json("[]"),
            Json(JsonSerializer.Serialize(new { id = "9001", nonce })),
            Json(JsonSerializer.Serialize(new[] { new { id = "9001", nonce } })));
        using var transport = new DiscordRestTransport(handler);

        var first = await transport.SendIdempotentAsync("secret", "123", "answer", nonce);
        var recovered = await transport.SendIdempotentAsync("secret", "123", "answer", nonce);

        Assert.Equal("9001", first);
        Assert.Equal(first, recovered);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post));
        var post = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.True(body.RootElement.GetProperty("enforce_nonce").GetBoolean());
        Assert.Equal(nonce, body.RootElement.GetProperty("nonce").GetString());
        Assert.Empty(body.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.Equal("Bot secret", post.Authorization);
    }

    [Fact]
    public async Task Sent_messages_return_the_attachments_discord_created()
    {
        const string created = "{\"id\":\"9001\",\"attachments\":[{\"id\":\"77\",\"filename\":\"chart.png\",\"content_type\":\"image/png\",\"size\":3,\"url\":\"https://cdn.discordapp.com/attachments/1/77/chart.png\"}]}";
        var handler = new QueueHandler(Json("[]"), Json(created), Json(created));
        using var transport = new DiscordRestTransport(handler);
        DiscordOutboundFile[] files = [new("chart.png", "image/png", "Chart", [1, 2, 3])];

        var bot = await transport.SendIdempotentMessageAsync("secret", "123", "Here.", "0123456789abcdef01234567", files);
        var webhook = await transport.SendWebhookMessageAsync("hook", "token", "Here.", files);

        foreach (var sent in new[] { bot, webhook })
        {
            Assert.Equal("9001", sent.Id);
            var attachment = Assert.Single(sent.Attachments!)!.AsObject();
            Assert.Equal("https://cdn.discordapp.com/attachments/1/77/chart.png", attachment["url"]!.GetValue<string>());
            Assert.Equal("image/png", attachment["content_type"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Outbound_image_uses_discord_multipart_with_nonce_and_alt_description()
    {
        var nonce = "0123456789abcdef01234567";
        var handler = new QueueHandler(
            Json("[]"),
            Json("{\"id\":\"9001\",\"attachments\":[{\"id\":\"0\"}]}"));
        using var transport = new DiscordRestTransport(handler);

        var sent = await transport.SendIdempotentAsync(
            "secret", "123", "Screenshot attached.", nonce,
            [new DiscordOutboundFile(
                "shared-image-1.png", "image/png", "Settings updates", [1, 2, 3])]);

        Assert.Equal("9001", sent);
        var post = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.StartsWith("multipart/form-data", post.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("payload_json", post.Body, StringComparison.Ordinal);
        Assert.Contains("files[0]", post.Body, StringComparison.Ordinal);
        Assert.Contains("shared-image-1.png", post.Body, StringComparison.Ordinal);
        Assert.Contains("Settings updates", post.Body, StringComparison.Ordinal);
        Assert.Contains(nonce, post.Body, StringComparison.Ordinal);
        Assert.Contains("enforce_nonce", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_image_uses_multipart_without_bot_authorization()
    {
        var handler = new QueueHandler(
            Json("{\"id\":\"9002\",\"attachments\":[{\"id\":\"0\"}]}"));
        using var transport = new DiscordRestTransport(handler);

        var sent = await transport.SendWebhookAsync(
            "777", "webhook-secret", "",
            [new DiscordOutboundFile(
                "shared-image-1.webp", "image/webp", "Generated image", [4, 5, 6])]);

        Assert.Equal("9002", sent);
        var post = Assert.Single(handler.Requests);
        Assert.StartsWith("multipart/form-data", post.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shared-image-1.webp", post.Body, StringComparison.Ordinal);
        Assert.Contains("Generated image", post.Body, StringComparison.Ordinal);
        Assert.Null(post.Authorization);
    }

    [Fact]
    public async Task Successful_discord_response_without_required_attachment_is_rejected()
    {
        var nonce = "0123456789abcdef01234567";
        var handler = new QueueHandler(
            Json("[]"),
            Json("{\"id\":\"9001\",\"attachments\":[]}"));
        using var transport = new DiscordRestTransport(handler);

        var error = await Assert.ThrowsAsync<DiscordRestException>(() =>
            transport.SendIdempotentAsync(
                "secret", "123", "Screenshot attached.", nonce,
                [new DiscordOutboundFile(
                    "shared-image-1.png", "image/png", "Settings updates", [1, 2, 3])]));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Contains("0 attachments; 1 were required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outbound_asset_resolution_reads_canonical_asset_and_degrades_missing_images_safely()
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["content"] = "",
            ["assets"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["asset_id"] = "image.png",
                    ["description"] = "Generated image",
                },
            },
        };
        var delivery = new DiscordDelivery(
            Guid.NewGuid(), "outbound", DiscordDeliveryStore.MessageKind,
            "binding-1", 1, "queued", null, "turn-1", 0, "hash", payload,
            0, null, null, DateTimeOffset.UtcNow);
        var assets = new FakeAssets
        {
            ReadFile = new AssetFile([1, 2, 3], "image/png"),
        };

        var resolved = await DiscordBotRuntime.ResolveOutboundPayloadAsync(
            delivery, Config(maxAttachmentBytes: 1024), assets);

        var file = Assert.Single(resolved.Files);
        Assert.Equal("shared-image-1.png", file.Filename);
        Assert.Equal("Generated image", file.Description);
        assets.ReadFile = null;

        var missing = await DiscordBotRuntime.ResolveOutboundPayloadAsync(
            delivery, Config(maxAttachmentBytes: 1024), assets);

        Assert.Empty(missing.Files);
        Assert.Equal("[Image unavailable: Generated image]", missing.Content);
        Assert.DoesNotContain("/api/assets/", missing.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("❤️", "❤")]
    [InlineData("👍", "👍")]
    [InlineData("🎉", "🎉")]
    [InlineData("👨‍👩‍👧‍👦", "👨‍👩‍👧‍👦")]
    [InlineData("👩‍❤️‍👩", "👩‍❤️‍👩")]
    public void Reaction_parses_standard_emoji_and_preserves_semantic_sequences(
        string emoji, string normalized)
    {
        var parsed = DiscordReactionEmoji.Parse(emoji);

        var unicode = Assert.IsType<Discord.Emoji>(parsed);
        Assert.Equal(normalized, unicode.Name);
    }

    [Theory]
    [InlineData("party:123456789012345678")]
    [InlineData("<a:party:123456789012345678>")]
    public void Reaction_preserves_Discord_custom_emoji_identifiers(string emoji)
    {
        var parsed = DiscordReactionEmoji.Parse(emoji);

        var custom = Assert.IsType<Discord.Emote>(parsed);
        Assert.Equal("party", custom.Name);
        Assert.Equal(123456789012345678UL, custom.Id);
    }

    [Theory]
    [InlineData("not an emoji")]
    [InlineData("ðŸ˜…")]
    [InlineData("�")]
    [InlineData("%F0%9F%91%8D")]
    public void Reaction_rejects_malformed_or_pre_encoded_emoji_before_transport(string emoji)
    {
        var error = Assert.Throws<ArgumentException>(() => DiscordReactionEmoji.Normalize(emoji));

        Assert.Contains("Discord reaction emoji", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_unicode_escape_reaches_the_reaction_parser_as_the_intended_emoji()
    {
        var parsed = JsonSerializer.Deserialize<DiscordReactionRequest>(
            "{\"messageId\":\"456\",\"emoji\":\"\\uD83D\\uDC4D\"}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("456", parsed!.MessageId);
        Assert.Equal("👍", Assert.IsType<Discord.Emoji>(
            DiscordReactionEmoji.Parse(parsed.Emoji)).Name);
    }

    [Fact]
    public async Task Progress_edit_targets_the_existing_message_without_mentions()
    {
        var handler = new QueueHandler(Json("{\"id\":\"456\"}"));
        using var transport = new DiscordRestTransport(handler);

        var progress =
            $"Checking.\n\n-# {DiscordDeliveryStore.ToolUseMarker} {DiscordDeliveryStore.ToolResultMarker}";
        await transport.EditMessageAsync(
            "secret", "123", "456", progress);

        var patch = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Equal(
            "https://discord.com/api/v10/channels/123/messages/456",
            patch.Uri);
        Assert.Equal("Bot secret", patch.Authorization);
        using var body = JsonDocument.Parse(patch.Body!);
        Assert.Equal(progress, body.RootElement.GetProperty("content").GetString());
        Assert.Empty(
            body.RootElement.GetProperty("allowed_mentions")
                .GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public async Task Correction_replaces_attachments_on_the_owned_bot_message()
    {
        var handler = new QueueHandler(
            Json("{\"id\":\"456\",\"attachments\":[{\"id\":\"0\"}]}"),
            Json("{\"id\":\"456\",\"attachments\":[]}"));
        using var transport = new DiscordRestTransport(handler);

        await transport.EditMessageAsync(
            "secret", "123", "456", "Corrected.",
            [new DiscordOutboundFile(
                "shared-image-1.png", "image/png", "Corrected image", [1, 2, 3])]);
        await transport.EditMessageAsync(
            "secret", "123", "456", content: null, files: []);

        Assert.Collection(handler.Requests,
            replace =>
            {
                Assert.Equal(HttpMethod.Patch, replace.Method);
                Assert.StartsWith(
                    "multipart/form-data", replace.ContentType,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Corrected.", replace.Body, StringComparison.Ordinal);
                Assert.Contains("Corrected image", replace.Body, StringComparison.Ordinal);
                Assert.Contains("files[0]", replace.Body, StringComparison.Ordinal);
                Assert.Equal("Bot secret", replace.Authorization);
            },
            remove =>
            {
                Assert.Equal(HttpMethod.Patch, remove.Method);
                using var body = JsonDocument.Parse(remove.Body!);
                Assert.False(body.RootElement.TryGetProperty("content", out _));
                Assert.Empty(body.RootElement.GetProperty("attachments").EnumerateArray());
                Assert.Empty(body.RootElement.GetProperty("allowed_mentions")
                    .GetProperty("parse").EnumerateArray());
            });
    }

    [Fact]
    public async Task Delete_targets_the_exact_bot_message_route()
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        using var transport = new DiscordRestTransport(handler);

        await transport.DeleteMessageAsync("secret", "123", "456");

        var delete = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.Equal(
            "https://discord.com/api/v10/channels/123/messages/456",
            delete.Uri);
        Assert.Equal("Bot secret", delete.Authorization);
    }

    [Fact]
    public async Task Global_and_guild_avatars_use_native_authorized_Discord_routes()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.NoContent),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var transport = new DiscordRestTransport(handler);
        const string avatar = "data:image/png;base64,AQID";

        await transport.ModifyCurrentUserAvatarAsync("secret", avatar);
        await transport.ModifyCurrentGuildMemberAvatarAsync(
            "secret", "223456789012345678", avatar);

        Assert.Collection(handler.Requests,
            global =>
            {
                Assert.Equal(HttpMethod.Patch, global.Method);
                Assert.Equal("https://discord.com/api/v10/users/@me", global.Uri);
                Assert.Equal("Bot secret", global.Authorization);
                using var body = JsonDocument.Parse(global.Body!);
                Assert.Equal(avatar, body.RootElement.GetProperty("avatar").GetString());
            },
            guild =>
            {
                Assert.Equal(HttpMethod.Patch, guild.Method);
                Assert.Equal(
                    "https://discord.com/api/v10/guilds/223456789012345678/members/@me",
                    guild.Uri);
                Assert.Equal("Bot secret", guild.Authorization);
                using var body = JsonDocument.Parse(guild.Body!);
                Assert.Equal(avatar, body.RootElement.GetProperty("avatar").GetString());
            });
    }

    [Fact]
    public async Task Channel_avatar_webhook_is_managed_with_bot_auth_but_executes_without_it()
    {
        var handler = new QueueHandler(
            Json("{\"id\":\"777\",\"token\":\"webhook-secret\"}"),
            Json("{\"id\":\"888\"}"));
        using var transport = new DiscordRestTransport(handler);
        const string avatar = "data:image/webp;base64,AQID";

        var webhook = await transport.CreateWebhookAsync(
            "bot-secret", "123", "Nova", avatar);
        var messageId = await transport.SendWebhookAsync(
            webhook.Id, webhook.Token, "hello");

        Assert.Equal("777", webhook.Id);
        Assert.Equal("888", messageId);
        Assert.Collection(handler.Requests,
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                Assert.Equal("https://discord.com/api/v10/channels/123/webhooks", create.Uri);
                Assert.Equal("Bot bot-secret", create.Authorization);
                using var body = JsonDocument.Parse(create.Body!);
                Assert.Equal("Nova", body.RootElement.GetProperty("name").GetString());
                Assert.Equal(avatar, body.RootElement.GetProperty("avatar").GetString());
            },
            execute =>
            {
                Assert.Equal(HttpMethod.Post, execute.Method);
                Assert.Equal(
                    "https://discord.com/api/v10/webhooks/777/webhook-secret?wait=true",
                    execute.Uri);
                Assert.Null(execute.Authorization);
                using var body = JsonDocument.Parse(execute.Body!);
                Assert.Equal("hello", body.RootElement.GetProperty("content").GetString());
                Assert.Empty(body.RootElement.GetProperty("allowed_mentions")
                    .GetProperty("parse").EnumerateArray());
            });
    }

    [Fact]
    public async Task Attachment_ingestion_hashes_actual_bounded_bytes_and_uploads_to_assets()
    {
        var bytes = Encoding.UTF8.GetBytes("diagnostic trace");
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new("text/plain") },
            },
        });
        var assets = new FakeAssets();
        using var ingestor = new DiscordAttachmentIngestor(assets, handler);
        var config = Config(maxAttachmentBytes: 1024);

        var result = Assert.Single(await ingestor.IngestAsync(config, "5001",
            [new DiscordAttachmentSource("77", "trace.txt", "text/plain", bytes.Length,
                "https://cdn.discordapp.com/attachments/1/2/trace.txt")]));

        Assert.Equal(bytes, assets.Bytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant(), result.Sha256);
        Assert.Equal("/api/assets/quarantined.txt", result.ScopeToken);
    }

    [Fact]
    public async Task Attachment_declared_over_limit_is_rejected_before_download()
    {
        var handler = new QueueHandler();
        using var ingestor = new DiscordAttachmentIngestor(new FakeAssets(), handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ingestor.IngestAsync(
            Config(maxAttachmentBytes: 8), "5001",
            [new DiscordAttachmentSource("77", "huge.txt", "text/plain", 9,
                "https://cdn.discordapp.com/attachments/1/2/huge.txt")]));
        Assert.Empty(handler.Requests);
    }

    private static LeafDiscordConfig Config(long maxAttachmentBytes) => new(
        "100000000000000001", new string('a', 64), "token", true,
        "nova", "deep-codex",
        "nova-discord-external", "avatar", new HashSet<string>(),
        new HashSet<string>(), maxAttachmentBytes);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString()));
            return responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method, string? Uri, string? Body, string? Authorization,
        string? ContentType);

    private sealed class FakeAssets : IAssets
    {
        public byte[]? Bytes { get; private set; }
        public AssetFile? ReadFile { get; set; }
        public async Task<AssetRef> UploadAsync(Stream content, string fileName,
            string? contentType = null, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            Bytes = buffer.ToArray();
            return new AssetRef("quarantined", "/api/assets/quarantined.txt");
        }
        public Task<AssetRef> UploadIdempotentAsync(Stream content, string stableKey,
            string fileName, string? contentType = null, CancellationToken ct = default)
            => UploadAsync(content, fileName, contentType, ct);
        public string GetUrl(string assetId) => $"/api/assets/{assetId}";
        public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
            => Task.FromResult(ReadFile);
    }
}
