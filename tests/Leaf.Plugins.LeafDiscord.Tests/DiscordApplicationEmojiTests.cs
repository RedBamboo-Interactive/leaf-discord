using System.Security.Claims;
using System.Text.Json;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordApplicationEmojiTests
{
    private const string ApplicationId = "1546583557567287306";
    private const string Owner = "00000000-0000-4000-8000-000000000001";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private static readonly byte[] Gif = "GIF89a\u0001\u0002"u8.ToArray();
    private static readonly byte[] Webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

    [Fact]
    public async Task Listing_returns_each_application_emoji_with_its_markdown()
    {
        var harness = await Harness.CreateAsync();
        harness.Discord.Emojis.Add(DiscordApplicationEmojis.Project("111", "leaf", false));
        harness.Discord.Emojis.Add(DiscordApplicationEmojis.Project("222", "nova_spin", true));

        var (status, body) = await harness.ListAsync();

        Assert.Equal(StatusCodes.Status200OK, status);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        AssertEmoji(items[0], "111", "leaf", false, "<:leaf:111>");
        AssertEmoji(items[1], "222", "nova_spin", true, "<a:nova_spin:222>");
    }

    [Fact]
    public async Task Creating_uploads_the_asset_bytes_and_returns_the_listed_shape()
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("leaf-logo.png", "image/png", Png);

        var (status, body) = await harness.CreateAsync(new("leaf", "leaf-logo.png"));

        Assert.Equal(StatusCodes.Status200OK, status);
        AssertEmoji(body, "900", "leaf", false, "<:leaf:900>");
        var upload = Assert.Single(harness.Discord.Uploads);
        Assert.Equal("leaf", upload.Name);
        Assert.Equal("leaf-logo.png", upload.Image.AssetId);
        Assert.Equal("image/png", upload.Image.ContentType);
        Assert.Equal(Png, upload.Image.Bytes);
    }

    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/gif", "gif")]
    [InlineData("image/webp", "webp")]
    [InlineData("IMAGE/PNG; charset=binary", "png")]
    public async Task Each_supported_image_format_is_accepted(string contentType, string extension)
    {
        var harness = await Harness.CreateAsync();
        var bytes = extension switch { "jpg" => Jpeg, "gif" => Gif, "webp" => Webp, _ => Png };
        harness.Assets.Add($"logo.{extension}", contentType, bytes);

        var (status, _) = await harness.CreateAsync(new("logo", $"/api/assets/logo.{extension}"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal($"logo.{extension}", Assert.Single(harness.Discord.Uploads).Image.AssetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33 characters
    [InlineData("leaf-logo")]
    [InlineData("leaf logo")]
    [InlineData(" leaf")]
    [InlineData("léaf")]
    [InlineData("leaf:1")]
    public async Task Names_outside_discord_rules_are_rejected_before_any_upload(string? name)
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("leaf-logo.png", "image/png", Png);

        var (status, body) = await harness.CreateAsync(new(name, "leaf-logo.png"));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("invalid_application_emoji", body.GetProperty("error").GetString());
        Assert.Empty(harness.Assets.Reads);
        Assert.Empty(harness.Discord.Uploads);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")] // 32 characters
    [InlineData("Nova_Leaf_2")]
    public async Task Names_inside_discord_rules_are_accepted(string name)
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("leaf-logo.png", "image/png", Png);

        var (status, _) = await harness.CreateAsync(new(name, "leaf-logo.png"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(name, Assert.Single(harness.Discord.Uploads).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("https://example.com/leaf.png")]
    [InlineData("C:\\images\\leaf.png")]
    [InlineData("../leaf.png")]
    [InlineData("images/leaf.png")]
    public async Task Only_a_canonical_redleaf_asset_can_supply_the_image(string? assetId)
    {
        var harness = await Harness.CreateAsync();

        var (status, body) = await harness.CreateAsync(new("leaf", assetId));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("invalid_application_emoji", body.GetProperty("error").GetString());
        Assert.Empty(harness.Assets.Reads);
        Assert.Empty(harness.Discord.Uploads);
    }

    [Fact]
    public async Task A_missing_asset_is_reported_as_not_found()
    {
        var harness = await Harness.CreateAsync();

        var (status, body) = await harness.CreateAsync(new("leaf", "absent.png"));

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("application_emoji_asset_not_found", body.GetProperty("error").GetString());
        Assert.Empty(harness.Discord.Uploads);
    }

    [Theory]
    [InlineData("text/plain", "png")]
    [InlineData("image/svg+xml", "png")]
    [InlineData("image/bmp", "png")]
    [InlineData("image/png", "jpg")]  // declared type does not match the bytes
    [InlineData("image/webp", "gif")]
    public async Task Unsupported_or_mislabelled_images_are_rejected(string contentType, string actual)
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("logo.bin", contentType, actual == "jpg" ? Jpeg : actual == "gif" ? Gif : Png);

        var (status, body) = await harness.CreateAsync(new("leaf", "logo.bin"));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("PNG, JPEG, GIF, or WebP", body.GetProperty("message").GetString());
        Assert.Empty(harness.Discord.Uploads);
    }

    [Fact]
    public async Task Images_above_256_kib_are_rejected_and_256_kib_is_accepted()
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("exact.png", "image/png", PngOfLength(256 * 1024));
        harness.Assets.Add("large.png", "image/png", PngOfLength(256 * 1024 + 1));

        var (largeStatus, large) = await harness.CreateAsync(new("large", "large.png"));
        var (exactStatus, _) = await harness.CreateAsync(new("exact", "exact.png"));

        Assert.Equal(StatusCodes.Status400BadRequest, largeStatus);
        Assert.Contains("256 KiB", large.GetProperty("message").GetString());
        Assert.Equal(StatusCodes.Status200OK, exactStatus);
        Assert.Equal("exact", Assert.Single(harness.Discord.Uploads).Name);
    }

    [Fact]
    public async Task An_existing_name_returns_409_with_the_existing_emoji_and_no_upload()
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("leaf-logo.png", "image/png", Png);
        harness.Discord.Emojis.Add(DiscordApplicationEmojis.Project("111", "leaf", false));

        var (status, body) = await harness.CreateAsync(new("leaf", "leaf-logo.png"));

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("application_emoji_exists", body.GetProperty("error").GetString());
        AssertEmoji(body.GetProperty("emoji"), "111", "leaf", false, "<:leaf:111>");
        Assert.Empty(harness.Discord.Uploads);
    }

    [Fact]
    public async Task Concurrent_uploads_of_one_name_create_it_once()
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("leaf-logo.png", "image/png", Png);
        harness.Discord.CreateDelay = TimeSpan.FromMilliseconds(50);

        var results = await Task.WhenAll(
            harness.CreateAsync(new("leaf", "leaf-logo.png")),
            harness.CreateAsync(new("leaf", "leaf-logo.png")));

        Assert.Single(harness.Discord.Uploads);
        Assert.Equal(
            new[] { StatusCodes.Status200OK, StatusCodes.Status409Conflict },
            results.Select(result => result.Status).Order().ToArray());
    }

    public static TheoryData<string, string, string, bool> Rejected => new()
    {
        // caller kind, agent, owner, bot enabled
        { "unsigned", "match", Owner, true },
        { "discord-conversation", "match", Owner, true },
        { "other-app", "match", Owner, true },
        { "automation", "match", Owner, true },
        { "private", "other", Owner, true },
        { "private", "match", "owner-2", true },
        { "private", "match", Owner, false },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task Only_the_bots_own_private_nova_agent_and_owner_can_list_or_create(
        string callerKind, string agent, string owner, bool enabled)
    {
        var harness = await Harness.CreateAsync(enabled);
        harness.Assets.Add("leaf-logo.png", "image/png", Png);
        var http = Caller(callerKind, agent == "match" ? harness.AgentId : Guid.NewGuid().ToString(), owner);

        var (listStatus, listBody) = await harness.ListAsync(http);
        var (createStatus, createBody) = await harness.CreateAsync(new("leaf", "leaf-logo.png"), http);

        Assert.Equal(StatusCodes.Status403Forbidden, listStatus);
        Assert.Equal(StatusCodes.Status403Forbidden, createStatus);
        Assert.Equal("forbidden", listBody.GetProperty("error").GetString());
        Assert.Equal("forbidden", createBody.GetProperty("error").GetString());
        Assert.Equal(0, harness.Discord.ListCalls);
        Assert.Empty(harness.Assets.Reads);
        Assert.Empty(harness.Discord.Uploads);
    }

    [Fact]
    public async Task An_unknown_bot_is_not_found()
    {
        var harness = await Harness.CreateAsync();

        var (status, _) = await harness.ListAsync(botId: Guid.NewGuid());

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal(0, harness.Discord.ListCalls);
    }

    [Fact]
    public async Task A_disconnected_transport_is_a_bounded_503_for_list_and_create()
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("leaf-logo.png", "image/png", Png);
        harness.Discord.Failure = new InvalidOperationException("Discord bot is not connected. token=secret");

        var (listStatus, list) = await harness.ListAsync();
        var (createStatus, create) = await harness.CreateAsync(new("leaf", "leaf-logo.png"));

        foreach (var (status, body) in new[] { (listStatus, list), (createStatus, create) })
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
            Assert.Equal("discord_bot_unavailable", body.GetProperty("error").GetString());
            Assert.DoesNotContain("secret", body.GetRawText());
        }
        Assert.Empty(harness.Discord.Uploads);
    }

    [Fact]
    public async Task An_unexpected_discord_failure_is_a_bounded_502()
    {
        var harness = await Harness.CreateAsync();
        harness.Assets.Add("leaf-logo.png", "image/png", Png);
        harness.Discord.CreateFailure = new HttpRequestException("Authorization: Bot secret-token");

        var (status, body) = await harness.CreateAsync(new("leaf", "leaf-logo.png"));

        Assert.Equal(StatusCodes.Status502BadGateway, status);
        Assert.Equal("discord_application_emoji_failed", body.GetProperty("error").GetString());
        Assert.DoesNotContain("secret", body.GetRawText());
    }

    private static void AssertEmoji(
        JsonElement emoji, string id, string name, bool animated, string markdown)
    {
        Assert.Equal(id, emoji.GetProperty("id").GetString());
        Assert.Equal(name, emoji.GetProperty("name").GetString());
        Assert.Equal(animated, emoji.GetProperty("animated").GetBoolean());
        Assert.Equal(markdown, emoji.GetProperty("markdown").GetString());
    }

    private static byte[] PngOfLength(int length)
    {
        var bytes = new byte[length];
        Png.CopyTo(bytes, 0);
        return bytes;
    }

    private static DefaultHttpContext Caller(string kind, string agentId, string ownerId)
    {
        if (kind == "unsigned")
            return new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, ownerId)], "test")),
            };
        object[] context = kind switch
        {
            "discord-conversation" =>
            [
                new { kind = "discussion", id = "d1" },
                new { kind = "external-conversation", id = "binding-1" },
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
        public required Guid BotId { get; init; }
        public required string AgentId { get; init; }
        public FakeAssets Assets { get; } = new();
        public FakeApplicationEmojis Discord { get; } = new();

        public static async Task<Harness> CreateAsync(bool enabled = true)
        {
            var entities = new MemoryEntityStore();
            var agent = entities.Seed("agent", "nova", "Nova", Owner);
            var configurations = new DiscordBotConfigurationStore(new LeafDiscordConfigStore(entities));
            var config = await configurations.CreateAsync(new LeafDiscordConfigUpdate(
                ApplicationId, new string('a', 64), "test-bot-token", false, enabled,
                [], [], 10 * 1024 * 1024, "nova", "deep-codex"),
                ownerUserId: Owner);
            return new Harness
            {
                Entities = entities,
                Configurations = configurations,
                BotId = config.EntityId,
                AgentId = agent.Id.ToString(),
            };
        }

        private DefaultHttpContext OwnCaller() => Caller("private", AgentId, Owner);

        public async Task<(int Status, JsonElement Body)> ListAsync(
            HttpContext? http = null, Guid? botId = null)
            => Read(await DiscordEndpoints.ListApplicationEmojisAsync(
                botId ?? BotId, http ?? OwnCaller(), Configurations,
                new DiscordAgentResolver(Entities), Discord.ListAsync, CancellationToken.None));

        public async Task<(int Status, JsonElement Body)> CreateAsync(
            DiscordApplicationEmojiCreateRequest request, HttpContext? http = null)
            => Read(await DiscordEndpoints.CreateApplicationEmojiAsync(
                BotId, request, http ?? OwnCaller(), Configurations,
                new DiscordAgentResolver(Entities), Assets,
                Discord.ListAsync, Discord.CreateAsync, CancellationToken.None));

        private static (int Status, JsonElement Body) Read(IResult result)
        {
            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode
                ?? StatusCodes.Status200OK;
            var value = (result as IValueHttpResult)?.Value;
            return (status, JsonSerializer.SerializeToElement(value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    /// <summary>Stands in for the connected client's application-emote calls.</summary>
    private sealed class FakeApplicationEmojis
    {
        private readonly Lock gate = new();
        public List<DiscordApplicationEmoji> Emojis { get; } = [];
        public List<(string Name, DiscordApplicationEmojiImage Image)> Uploads { get; } = [];
        public int ListCalls { get; private set; }
        public Exception? Failure { get; set; }
        public Exception? CreateFailure { get; set; }
        public TimeSpan CreateDelay { get; set; }

        public Task<IReadOnlyList<DiscordApplicationEmoji>> ListAsync(Guid botId, CancellationToken ct)
        {
            lock (gate)
            {
                ListCalls++;
                if (Failure is not null) throw Failure;
                return Task.FromResult<IReadOnlyList<DiscordApplicationEmoji>>(Emojis.ToArray());
            }
        }

        public async Task<DiscordApplicationEmoji> CreateAsync(
            Guid botId, string name, DiscordApplicationEmojiImage image, CancellationToken ct)
        {
            if (CreateDelay > TimeSpan.Zero) await Task.Delay(CreateDelay, ct);
            lock (gate)
            {
                if (CreateFailure is not null) throw CreateFailure;
                Uploads.Add((name, image));
                var created = DiscordApplicationEmojis.Project(
                    (900 + Uploads.Count - 1).ToString(), name, false);
                Emojis.Add(created);
                return created;
            }
        }
    }

    private sealed class FakeAssets : IAssets
    {
        private readonly Dictionary<string, AssetFile> files = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];

        public void Add(string assetId, string contentType, byte[] bytes)
            => files[assetId] = new AssetFile(bytes, contentType);

        public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
        {
            Reads.Add(assetId);
            return Task.FromResult(files.GetValueOrDefault(assetId));
        }

        public Task<AssetRef> UploadAsync(Stream content, string fileName,
            string? contentType = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AssetRef> UploadIdempotentAsync(Stream content, string stableKey,
            string fileName, string? contentType = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public string GetUrl(string assetId) => $"/api/assets/{assetId}";
    }
}
