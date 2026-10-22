using System.Net;
using System.Text;
using System.Text.Json;
using Leaf.Plugins.LeafDiscord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordChannelEditTests
{
    [Fact]
    public void Text_channel_creation_payload_is_bounded_and_explicit()
    {
        var payload = DiscordChannelEdits.BuildTextChannelCreatePayload(
            new DiscordGuildChannelCreateRequest(
                "  showcase  ",
                "Projects built with Leaf.",
                "223456789012345678",
                2,
                SlowModeSeconds: 15));

        Assert.Equal("showcase", payload["name"]!.GetValue<string>());
        Assert.Equal(0, payload["type"]!.GetValue<int>());
        Assert.Equal("Projects built with Leaf.", payload["topic"]!.GetValue<string>());
        Assert.Equal("223456789012345678", payload["parent_id"]!.GetValue<string>());
        Assert.Equal(2, payload["position"]!.GetValue<int>());
        Assert.False(payload["nsfw"]!.GetValue<bool>());
        Assert.Equal(15, payload["rate_limit_per_user"]!.GetValue<int>());
    }

    [Fact]
    public void Category_creation_payload_is_bounded_and_explicit()
    {
        var payload = DiscordChannelEdits.BuildCategoryCreatePayload(
            new DiscordGuildCategoryCreateRequest("  Leaf Community  ", 3));

        Assert.Equal("Leaf Community", payload["name"]!.GetValue<string>());
        Assert.Equal(4, payload["type"]!.GetValue<int>());
        Assert.Equal(3, payload["position"]!.GetValue<int>());
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildCategoryCreatePayload(
            new DiscordGuildCategoryCreateRequest("valid", -1)));
    }

    [Fact]
    public void Invalid_text_channel_creation_fields_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildTextChannelCreatePayload(
            new DiscordGuildChannelCreateRequest(" ")));
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildTextChannelCreatePayload(
            new DiscordGuildChannelCreateRequest("valid", Topic: new string('x', 1_025))));
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildTextChannelCreatePayload(
            new DiscordGuildChannelCreateRequest("valid", ParentCategoryId: "not-a-snowflake")));
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildTextChannelCreatePayload(
            new DiscordGuildChannelCreateRequest("valid", Position: -1)));
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildTextChannelCreatePayload(
            new DiscordGuildChannelCreateRequest("valid", SlowModeSeconds: 21_601)));
    }

    [Fact]
    public void Forum_channel_creation_payload_is_bounded_and_explicit()
    {
        var payload = DiscordChannelEdits.BuildForumChannelCreatePayload(
            new DiscordGuildForumChannelCreateRequest(
                " nova-consult ",
                "Ask a Commons Envoy.",
                "223456789012345678",
                3,
                SlowModeSeconds: 10,
                DefaultAutoArchiveMinutes: 4_320));

        Assert.Equal("nova-consult", payload["name"]!.GetValue<string>());
        Assert.Equal(15, payload["type"]!.GetValue<int>());
        Assert.Equal(4_320,
            payload["default_auto_archive_duration"]!.GetValue<int>());
        Assert.Equal(10, payload["rate_limit_per_user"]!.GetValue<int>());
        Assert.Throws<ArgumentException>(() =>
            DiscordChannelEdits.BuildForumChannelCreatePayload(
                new DiscordGuildForumChannelCreateRequest(
                    "bugs", DefaultAutoArchiveMinutes: 42)));
    }

    [Fact]
    public void Text_channel_patch_preserves_explicit_changes_and_null_clears()
    {
        var patch = DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(
                Name: "  renamed  ",
                ClearTopic: true,
                ParentCategoryId: "223456789012345678",
                Position: 4,
                Nsfw: false,
                SlowModeSeconds: 30),
            "text");

        Assert.Equal("renamed", patch["name"]!.GetValue<string>());
        Assert.True(patch.ContainsKey("topic"));
        Assert.Null(patch["topic"]);
        Assert.Equal("223456789012345678",
            patch["parent_id"]!.GetValue<string>());
        Assert.Equal(4, patch["position"]!.GetValue<int>());
        Assert.False(patch["nsfw"]!.GetValue<bool>());
        Assert.Equal(30, patch["rate_limit_per_user"]!.GetValue<int>());
    }

    [Fact]
    public void Forum_topics_use_the_documented_larger_limit()
    {
        var topic = new string('x', 4_096);

        var patch = DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(Topic: topic), "forum");

        Assert.Equal(topic, patch["topic"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(Topic: new string('x', 1_025)), "text"));
    }

    [Theory]
    [InlineData("category")]
    [InlineData("voice")]
    [InlineData("stage")]
    public void Non_text_channels_reject_topics(string kind)
        => Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(Topic: "not supported"), kind));

    [Fact]
    public void Empty_conflicting_and_out_of_range_edits_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(), "text"));
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(Topic: "topic", ClearTopic: true), "text"));
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(SlowModeSeconds: 21_601), "text"));
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(ParentCategoryId: "not-a-snowflake"), "text"));
        Assert.Throws<ArgumentException>(() => DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(Nsfw: true), "category"));
    }

    [Fact]
    public async Task Transport_uses_bot_auth_and_an_audit_log_reason()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"123","name":"renamed","position":2,"topic":"hello","parent_id":null,"nsfw":false,"rate_limit_per_user":0}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var transport = new DiscordRestTransport(handler);
        var changes = DiscordChannelEdits.BuildPatch(
            new DiscordChannelEditRequest(Name: "renamed", Topic: "hello"), "text");

        var result = await transport.ModifyGuildChannelAsync(
            "secret", "123", changes, "Leaf owner requested this channel edit through Nova");

        Assert.Equal("renamed", result["name"]!.GetValue<string>());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("https://discord.com/api/v10/channels/123", request.Uri);
        Assert.Equal("Bot secret", request.Authorization);
        Assert.Equal(
            Uri.EscapeDataString("Leaf owner requested this channel edit through Nova"),
            request.AuditReason);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("renamed", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("hello", body.RootElement.GetProperty("topic").GetString());
        Assert.False(body.RootElement.TryGetProperty("position", out _));
    }

    [Fact]
    public async Task Creation_transport_uses_guild_route_bot_auth_and_audit_reason()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"456","type":0,"name":"showcase","position":2,"topic":"Projects built with Leaf.","parent_id":"223456789012345678","nsfw":false,"rate_limit_per_user":0}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var transport = new DiscordRestTransport(handler);
        var payload = DiscordChannelEdits.BuildTextChannelCreatePayload(
            new DiscordGuildChannelCreateRequest(
                "showcase", "Projects built with Leaf.", "223456789012345678"));

        var result = await transport.CreateGuildChannelAsync(
            "secret", "123", payload,
            "Leaf owner requested this channel creation through Nova");

        Assert.Equal("456", result["id"]!.GetValue<string>());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://discord.com/api/v10/guilds/123/channels", request.Uri);
        Assert.Equal("Bot secret", request.Authorization);
        Assert.Equal(
            Uri.EscapeDataString("Leaf owner requested this channel creation through Nova"),
            request.AuditReason);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("showcase", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("type").GetInt32());
    }

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri,
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Audit-Log-Reason", out var values)
                    ? values.Single()
                    : null));
            return response;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string? Uri,
        string? Body,
        string? Authorization,
        string? AuditReason);
}
