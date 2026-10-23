using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordAttachmentSource(
    string Id, string Filename, string? ContentType, long Size, string Url);

public sealed record DiscordOutboundFile(
    string Filename, string ContentType, string Description, byte[] Bytes);

public sealed class DiscordAttachmentIngestor : IDisposable
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.discordapp.com",
        "media.discordapp.net",
    };

    private readonly IAssets assets;
    private readonly HttpClient http;

    public DiscordAttachmentIngestor(IAssets assets)
        : this(assets, new HttpClientHandler { AllowAutoRedirect = false }) { }

    internal DiscordAttachmentIngestor(IAssets assets, HttpMessageHandler handler)
    {
        this.assets = assets;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<IReadOnlyList<ExternalConversationAttachment>> IngestAsync(
        LeafDiscordConfig config,
        string discordMessageId,
        IReadOnlyCollection<DiscordAttachmentSource> attachments,
        CancellationToken ct = default)
    {
        var ingested = new List<ExternalConversationAttachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            if (attachment.Size > config.MaxAttachmentBytes)
                throw new InvalidOperationException(
                    $"Discord attachment '{attachment.Filename}' exceeds the configured size limit");
            if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
                throw new InvalidOperationException("Discord attachment URL is outside the allowed CDN boundary");

            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod or HttpStatusCode.RedirectKeepVerb
                or HttpStatusCode.PermanentRedirect)
                throw new InvalidOperationException("Discord attachment redirects are not accepted");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length
                && length > config.MaxAttachmentBytes)
                throw new InvalidOperationException(
                    $"Discord attachment '{attachment.Filename}' exceeds the configured size limit");

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var buffer = new MemoryStream(capacity: checked((int)Math.Min(
                config.MaxAttachmentBytes, response.Content.Headers.ContentLength ?? attachment.Size)));
            var block = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(block, ct);
                if (read == 0) break;
                total += read;
                if (total > config.MaxAttachmentBytes)
                    throw new InvalidOperationException(
                        $"Discord attachment '{attachment.Filename}' exceeds the configured size limit");
                await buffer.WriteAsync(block.AsMemory(0, read), ct);
            }

            var bytes = buffer.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await using var upload = new MemoryStream(bytes, writable: false);
            var mediaType = attachment.ContentType
                ?? response.Content.Headers.ContentType?.MediaType
                ?? "application/octet-stream";
            var asset = await assets.UploadIdempotentAsync(upload,
                $"leaf-discord:{config.ApplicationId}:{discordMessageId}:{attachment.Id}:{sha256}",
                attachment.Filename, mediaType, ct);
            ingested.Add(new ExternalConversationAttachment(
                attachment.Id, attachment.Filename, mediaType, total, sha256, asset.Url));
        }
        return ingested;
    }

    public void Dispose() => http.Dispose();
}

public sealed record DiscordRestMessage(string Id, string? Nonce, int AttachmentCount);
public sealed record DiscordWebhookCredentials(string Id, string Token);
public sealed record DiscordDirectMessageChannel(string ChannelId, string RecipientName);
public sealed record DiscordManagedRole(
    string Id, string Name, string Permissions, bool Mentionable, bool Managed);

public sealed class DiscordRestTransport : IDisposable
{
    private readonly HttpClient http;

    public DiscordRestTransport() : this(new HttpClientHandler()) { }

    internal DiscordRestTransport(HttpMessageHandler handler)
    {
        http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://discord.com/api/v10/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<string> SendIdempotentAsync(
        string botToken, string channelId, string content, string nonce,
        CancellationToken ct = default)
        => await SendIdempotentAsync(
            botToken, channelId, content, nonce, [], ct);

    public async Task<string> SendIdempotentAsync(
        string botToken, string channelId, string content, string nonce,
        IReadOnlyList<DiscordOutboundFile> files,
        CancellationToken ct = default)
    {
        var existing = await FindRecentByNonceAsync(botToken, channelId, nonce, ct);
        if (existing is not null)
        {
            EnsureAttachmentCount(existing, files.Count);
            return existing.Id;
        }

        using var request = Authorized(HttpMethod.Post, $"channels/{channelId}/messages", botToken);
        request.Content = files.Count == 0
            ? JsonContent.Create(new
            {
                content,
                nonce,
                enforce_nonce = true,
                allowed_mentions = new { parse = Array.Empty<string>() },
            })
            : MultipartMessage(content, files, nonce);
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        var sent = ReadMessage(raw);
        EnsureAttachmentCount(sent, files.Count);
        return sent.Id;
    }

    public async Task ModifyCurrentUserAvatarAsync(
        string botToken, string? imageData, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Patch, "users/@me", botToken);
        request.Content = JsonContent.Create(new { avatar = imageData });
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    public async Task ModifyCurrentGuildMemberAvatarAsync(
        string botToken, string guildId, string? imageData,
        CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Patch, $"guilds/{guildId}/members/@me", botToken);
        request.Content = JsonContent.Create(new { avatar = imageData });
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    public async Task<DiscordWebhookCredentials> CreateWebhookAsync(
        string botToken, string channelId, string name, string? imageData,
        CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Post, $"channels/{channelId}/webhooks", botToken);
        request.Content = JsonContent.Create(new { name, avatar = imageData });
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        return new DiscordWebhookCredentials(
            root.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Discord webhook response has no id"),
            root.GetProperty("token").GetString()
                ?? throw new InvalidOperationException("Discord webhook response has no token"));
    }

    public async Task<IReadOnlyList<DiscordManagedRole>> ListGuildRolesAsync(
        string botToken, string guildId, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Get, $"guilds/{guildId}/roles", botToken);
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw DiscordRestException.From(response, raw);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.EnumerateArray().Select(ReadRole).ToArray();
    }

    public async Task<DiscordManagedRole> CreateGuildRoleAsync(
        string botToken, string guildId, string name, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Post, $"guilds/{guildId}/roles", botToken);
        request.Content = JsonContent.Create(new
        {
            name,
            permissions = "0",
            mentionable = true,
            hoist = false,
        });
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw DiscordRestException.From(response, raw);
        using var document = JsonDocument.Parse(raw);
        return ReadRole(document.RootElement);
    }

    public async Task<DiscordManagedRole> ModifyGuildRoleAsync(
        string botToken, string guildId, string roleId, string name,
        CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Patch,
            $"guilds/{guildId}/roles/{roleId}", botToken);
        request.Content = JsonContent.Create(new
        {
            name,
            permissions = "0",
            mentionable = true,
            hoist = false,
        });
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw DiscordRestException.From(response, raw);
        using var document = JsonDocument.Parse(raw);
        return ReadRole(document.RootElement);
    }

    public async Task DeleteGuildRoleAsync(
        string botToken, string guildId, string roleId, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Delete,
            $"guilds/{guildId}/roles/{roleId}", botToken);
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    public async Task<IReadOnlyList<DiscordCommonsGuildMember>> ListGuildMembersAsync(
        string botToken, string guildId, CancellationToken ct = default)
    {
        var result = new List<DiscordCommonsGuildMember>();
        string? after = null;
        while (true)
        {
            var path = $"guilds/{guildId}/members?limit=1000"
                       + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
            using var request = Authorized(HttpMethod.Get, path, botToken);
            using var response = await http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw DiscordRestException.From(response, raw);
            using var document = JsonDocument.Parse(raw);
            var page = document.RootElement.EnumerateArray().ToArray();
            foreach (var member in page)
            {
                var user = member.GetProperty("user");
                var id = user.GetProperty("id").GetString() ?? "";
                var username = user.GetProperty("username").GetString() ?? id;
                var globalName = user.TryGetProperty("global_name", out var global)
                    && global.ValueKind == JsonValueKind.String ? global.GetString() : null;
                var nickname = member.TryGetProperty("nick", out var nick)
                    && nick.ValueKind == JsonValueKind.String ? nick.GetString() : null;
                var isBot = user.TryGetProperty("bot", out var bot)
                    && bot.ValueKind == JsonValueKind.True;
                result.Add(new DiscordCommonsGuildMember(
                    id, nickname ?? globalName ?? username, isBot));
            }
            if (page.Length < 1000) break;
            after = result[^1].UserId;
        }
        return result;
    }

    private static DiscordManagedRole ReadRole(JsonElement item)
        => new(
            item.GetProperty("id").GetString() ?? "",
            item.GetProperty("name").GetString() ?? "",
            item.TryGetProperty("permissions", out var permissions)
                ? permissions.GetString() ?? "0" : "0",
            item.TryGetProperty("mentionable", out var mentionable)
                && mentionable.ValueKind == JsonValueKind.True,
            item.TryGetProperty("managed", out var managed)
                && managed.ValueKind == JsonValueKind.True);

    public async Task ModifyWebhookAsync(
        string botToken, string webhookId, string name, string imageData,
        CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Patch, $"webhooks/{webhookId}", botToken);
        request.Content = JsonContent.Create(new { name, avatar = imageData });
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    public async Task DeleteWebhookAsync(
        string botToken, string webhookId, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Delete, $"webhooks/{webhookId}", botToken);
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    public async Task<string> SendWebhookAsync(
        string webhookId, string webhookToken, string content,
        CancellationToken ct = default)
        => await SendWebhookAsync(webhookId, webhookToken, content, [], ct);

    public async Task<string> SendWebhookAsync(
        string webhookId, string webhookToken, string content,
        IReadOnlyList<DiscordOutboundFile> files,
        CancellationToken ct = default)
    {
        var token = Uri.EscapeDataString(webhookToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"webhooks/{webhookId}/{token}?wait=true");
        request.Headers.UserAgent.ParseAdd("LeafDiscord/0.1.0");
        request.Content = files.Count == 0
            ? JsonContent.Create(new
            {
                content,
                allowed_mentions = new { parse = Array.Empty<string>() },
            })
            : MultipartMessage(content, files);
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        var sent = ReadMessage(raw);
        EnsureAttachmentCount(sent, files.Count);
        return sent.Id;
    }

    private static MultipartFormDataContent MultipartMessage(
        string content,
        IReadOnlyList<DiscordOutboundFile> files,
        string? nonce = null)
    {
        var attachments = files.Select((file, index) => new
        {
            id = index,
            filename = file.Filename,
            description = file.Description,
        }).ToArray();
        var payload = nonce is null
            ? JsonSerializer.Serialize(new
            {
                content = string.IsNullOrEmpty(content) ? null : content,
                attachments,
                allowed_mentions = new { parse = Array.Empty<string>() },
            })
            : JsonSerializer.Serialize(new
            {
                content = string.IsNullOrEmpty(content) ? null : content,
                attachments,
                nonce,
                enforce_nonce = true,
                allowed_mentions = new { parse = Array.Empty<string>() },
            });
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
        for (var index = 0; index < files.Count; index++)
        {
            var part = new ByteArrayContent(files[index].Bytes);
            part.Headers.ContentType = MediaTypeHeaderValue.Parse(files[index].ContentType);
            multipart.Add(part, $"files[{index}]", files[index].Filename);
        }
        return multipart;
    }

    public async Task EditWebhookMessageAsync(
        string webhookId, string webhookToken, string messageId, string content,
        CancellationToken ct = default)
    {
        var token = Uri.EscapeDataString(webhookToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"webhooks/{webhookId}/{token}/messages/{messageId}");
        request.Headers.UserAgent.ParseAdd("LeafDiscord/0.1.0");
        request.Content = JsonContent.Create(new
        {
            content,
            allowed_mentions = new { parse = Array.Empty<string>() },
        });
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    public async Task EditWebhookMessageAsync(
        string webhookId, string webhookToken, string messageId, string? content,
        IReadOnlyList<DiscordOutboundFile>? files,
        CancellationToken ct = default)
    {
        var token = Uri.EscapeDataString(webhookToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"webhooks/{webhookId}/{token}/messages/{messageId}");
        request.Headers.UserAgent.ParseAdd("LeafDiscord/0.1.0");
        request.Content = CorrectionContent(content, files);
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        if (files is not null)
            EnsureAttachmentCount(ReadMessage(raw), files.Count);
    }

    public async Task DeleteWebhookMessageAsync(
        string webhookId, string webhookToken, string messageId,
        CancellationToken ct = default)
    {
        var token = Uri.EscapeDataString(webhookToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"webhooks/{webhookId}/{token}/messages/{messageId}");
        request.Headers.UserAgent.ParseAdd("LeafDiscord/0.1.0");
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    internal async Task<DiscordRestMessage?> FindRecentByNonceAsync(
        string botToken, string channelId, string nonce, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get,
            $"channels/{channelId}/messages?limit=100", botToken);
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        using var document = JsonDocument.Parse(raw);
        foreach (var message in document.RootElement.EnumerateArray())
        {
            if (!message.TryGetProperty("nonce", out var value)) continue;
            var candidate = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
            if (string.Equals(candidate, nonce, StringComparison.Ordinal))
                return ReadMessage(message);
        }
        return null;
    }

    private static DiscordRestMessage ReadMessage(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return ReadMessage(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new DiscordRestException(HttpStatusCode.BadGateway,
                $"Discord returned an invalid Create Message response: {ex.Message}");
        }
    }

    private static DiscordRestMessage ReadMessage(JsonElement message)
    {
        var id = message.TryGetProperty("id", out var idValue)
            ? idValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new DiscordRestException(HttpStatusCode.BadGateway,
                "Discord Create Message response has no id");
        var nonce = message.TryGetProperty("nonce", out var nonceValue)
            ? nonceValue.ValueKind == JsonValueKind.String
                ? nonceValue.GetString()
                : nonceValue.ValueKind == JsonValueKind.Number
                    ? nonceValue.GetRawText()
                    : null
            : null;
        var attachmentCount = message.TryGetProperty("attachments", out var attachments)
                              && attachments.ValueKind == JsonValueKind.Array
            ? attachments.GetArrayLength()
            : 0;
        return new DiscordRestMessage(id, nonce, attachmentCount);
    }

    private static void EnsureAttachmentCount(
        DiscordRestMessage message, int expectedAttachmentCount)
    {
        if (message.AttachmentCount == expectedAttachmentCount) return;
        throw new DiscordRestException(HttpStatusCode.BadGateway,
            $"Discord acknowledged message {message.Id} with {message.AttachmentCount} " +
            $"attachments; {expectedAttachmentCount} were required");
    }

    public async Task PinMessageAsync(
        string botToken, string channelId, string messageId,
        CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Put, $"channels/{channelId}/pins/{messageId}", botToken);
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    public async Task EditMessageAsync(
        string botToken, string channelId, string messageId, string content,
        CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}", botToken);
        request.Content = JsonContent.Create(new
        {
            content,
            allowed_mentions = new { parse = Array.Empty<string>() },
        });
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    public async Task EditMessageAsync(
        string botToken, string channelId, string messageId, string? content,
        IReadOnlyList<DiscordOutboundFile>? files,
        CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}", botToken);
        request.Content = CorrectionContent(content, files);
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        if (files is not null)
            EnsureAttachmentCount(ReadMessage(raw), files.Count);
    }

    public async Task DeleteMessageAsync(
        string botToken, string channelId, string messageId,
        CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Delete, $"channels/{channelId}/messages/{messageId}", botToken);
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        throw DiscordRestException.From(response, raw);
    }

    private static HttpContent CorrectionContent(
        string? content, IReadOnlyList<DiscordOutboundFile>? files)
    {
        if (files is { Count: > 0 })
            return MultipartCorrection(content, files);

        var payload = new JsonObject
        {
            ["allowed_mentions"] = new JsonObject
            {
                ["parse"] = new JsonArray(),
            },
        };
        if (content is not null) payload["content"] = content;
        if (files is not null) payload["attachments"] = new JsonArray();
        return JsonContent.Create(payload);
    }

    private static MultipartFormDataContent MultipartCorrection(
        string? content, IReadOnlyList<DiscordOutboundFile> files)
    {
        var attachments = new JsonArray(files.Select((file, index) => (JsonNode?)new JsonObject
        {
            ["id"] = index,
            ["filename"] = file.Filename,
            ["description"] = file.Description,
        }).ToArray());
        var payload = new JsonObject
        {
            ["attachments"] = attachments,
            ["allowed_mentions"] = new JsonObject
            {
                ["parse"] = new JsonArray(),
            },
        };
        if (content is not null) payload["content"] = content;

        var multipart = new MultipartFormDataContent();
        multipart.Add(
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            "payload_json");
        for (var index = 0; index < files.Count; index++)
        {
            var part = new ByteArrayContent(files[index].Bytes);
            part.Headers.ContentType = MediaTypeHeaderValue.Parse(files[index].ContentType);
            multipart.Add(part, $"files[{index}]", files[index].Filename);
        }
        return multipart;
    }

    /// <summary>
    /// Opens, or returns the existing, DM channel between the bot and one user. Discord
    /// treats this as idempotent and it creates no message, so it is safe before any
    /// Leaf state exists.
    /// </summary>
    public async Task<DiscordDirectMessageChannel> CreateDirectMessageChannelAsync(
        string botToken, string recipientUserId, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Post, "users/@me/channels", botToken);
        request.Content = JsonContent.Create(new { recipient_id = recipientUserId });
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var channelId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(channelId))
                throw new DiscordRestException(HttpStatusCode.BadGateway,
                    "Discord Create DM response has no channel id");
            string? name = null;
            if (root.TryGetProperty("recipients", out var recipients)
                && recipients.ValueKind == JsonValueKind.Array)
                foreach (var recipient in recipients.EnumerateArray())
                {
                    if (recipient.TryGetProperty("id", out var userId)
                        && userId.GetString() != recipientUserId)
                        continue;
                    name = recipient.TryGetProperty("global_name", out var global)
                           && global.ValueKind == JsonValueKind.String
                        ? global.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(name)
                        && recipient.TryGetProperty("username", out var username)
                        && username.ValueKind == JsonValueKind.String)
                        name = username.GetString();
                }
            return new DiscordDirectMessageChannel(
                channelId, string.IsNullOrWhiteSpace(name) ? recipientUserId : name);
        }
        catch (JsonException ex)
        {
            throw new DiscordRestException(HttpStatusCode.BadGateway,
                $"Discord returned an invalid Create DM response: {ex.Message}");
        }
    }

    public async Task<JsonObject> ModifyGuildChannelAsync(
        string botToken,
        string channelId,
        JsonObject changes,
        string auditReason,
        CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Patch, $"channels/{channelId}", botToken);
        request.Headers.TryAddWithoutValidation(
            "X-Audit-Log-Reason", Uri.EscapeDataString(auditReason));
        request.Content = JsonContent.Create(changes);
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        return JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidOperationException(
                "Discord channel edit response is not an object.");
    }

    public async Task<JsonObject> CreateGuildChannelAsync(
        string botToken,
        string guildId,
        JsonObject channel,
        string auditReason,
        CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Post, $"guilds/{guildId}/channels", botToken);
        request.Headers.TryAddWithoutValidation(
            "X-Audit-Log-Reason", Uri.EscapeDataString(auditReason));
        request.Content = JsonContent.Create(channel);
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw DiscordRestException.From(response, raw);
        return JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidOperationException(
                "Discord channel creation response is not an object.");
    }

    internal static string Nonce(DiscordDelivery delivery)
    {
        var identity =
            $"{delivery.BindingId}:{delivery.Generation}:{delivery.EntityId:N}:{delivery.MessageUid}:{delivery.ChunkIndex}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        request.Headers.UserAgent.ParseAdd("LeafDiscord/0.1.0");
        return request;
    }

    public void Dispose() => http.Dispose();
}

public sealed class DiscordRestException(
    HttpStatusCode statusCode, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static DiscordRestException From(HttpResponseMessage response, string body)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("retry_after", out var value)
                    && value.TryGetDouble(out var seconds))
                    retryAfter = TimeSpan.FromSeconds(seconds);
            }
            catch (JsonException) { }
        }
        var summary = body.Length <= 500 ? body : body[..500];
        return new DiscordRestException(response.StatusCode,
            $"Discord REST {(int)response.StatusCode}: {summary}", retryAfter);
    }
}
