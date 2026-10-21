using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordCommonsConfiguration(
    Guid EntityId,
    Guid BotId,
    string RelayUrl,
    string? HostCredential,
    IReadOnlySet<string> GuildAllowlist,
    IReadOnlyDictionary<string, string> WebhookUrls)
{
    public bool Ready => Uri.TryCreate(RelayUrl, UriKind.Absolute, out var relay)
                         && relay.Scheme == Uri.UriSchemeHttps
                         && !string.IsNullOrWhiteSpace(HostCredential)
                         && GuildAllowlist.Count > 0;
}

public sealed record DiscordCommonsConfigurationUpdate(
    string RelayUrl,
    string? HostCredential,
    bool ClearHostCredential,
    IReadOnlyList<string>? GuildAllowlist);

public sealed record DiscordCommonsConfigurationView(
    string RelayUrl,
    bool HostCredentialConfigured,
    IReadOnlyList<string> GuildAllowlist,
    IReadOnlyList<string> WebhookChannelIds);

public sealed class DiscordCommonsConfigurationStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-commons-config";

    public async Task<DiscordCommonsConfiguration?> GetAsync(
        Guid botId, CancellationToken ct = default)
    {
        var entity = await store.GetBySlugAsync(TypeSlug, Slug(botId), ct);
        entity ??= (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["bot_id"] = botId.ToString(),
            },
            Limit = 2,
        }, ct)).SingleOrDefault();
        entity ??= await store.UpsertBySlugAsync(
            TypeSlug,
            Slug(botId),
            "Discord Commons relay",
            new JsonObject
            {
                ["bot_id"] = botId.ToString(),
                ["relay_url"] = "",
                ["guild_allowlist"] = new JsonArray(),
                ["webhook_urls"] = "{}",
            },
            ct);
        return Map(entity);
    }

    public async Task<DiscordCommonsConfiguration> SaveAsync(
        Guid botId,
        DiscordCommonsConfigurationUpdate update,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(botId, ct);
        var relayUrl = NormalizeRelayUrl(update.RelayUrl);
        var credential = update.ClearHostCredential
            ? null
            : string.IsNullOrWhiteSpace(update.HostCredential)
                ? existing?.HostCredential
                : update.HostCredential.Trim();
        var guilds = (update.GuildAllowlist ?? [])
            .Select(RequiredSnowflake)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (guilds.Length == 0)
            throw new ArgumentException("At least one Commons guild is required.");

        var data = new JsonObject
        {
            ["bot_id"] = botId.ToString(),
            ["relay_url"] = relayUrl,
            ["host_credential"] = credential,
            ["guild_allowlist"] = new JsonArray(guilds
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["webhook_urls"] = SerializeWebhookUrls(
                existing?.WebhookUrls ?? new Dictionary<string, string>()),
        };
        var entity = existing is null
            ? await store.UpsertBySlugAsync(
                TypeSlug, Slug(botId), "Discord Commons relay", data, ct)
            : await store.PatchAsync(
                existing.EntityId, data, name: "Discord Commons relay", ct: ct);
        return Map(entity);
    }

    public async Task<DiscordCommonsConfiguration> SetWebhookUrlAsync(
        Guid botId,
        string channelId,
        string webhookUrl,
        CancellationToken ct = default)
    {
        channelId = RequiredSnowflake(channelId);
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.Equals(parsed.Host, "discord.com", StringComparison.OrdinalIgnoreCase)
            || !parsed.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
            throw new ArgumentException("The managed webhook URL is invalid.");
        var existing = await GetAsync(botId, ct)
            ?? throw new InvalidOperationException("Discord Commons is not configured.");
        var urls = new Dictionary<string, string>(existing.WebhookUrls, StringComparer.Ordinal)
        {
            [channelId] = webhookUrl,
        };
        var entity = await store.PatchAsync(existing.EntityId, new JsonObject
        {
            ["webhook_urls"] = SerializeWebhookUrls(urls),
        }, ct: ct);
        return Map(entity);
    }

    public static DiscordCommonsConfigurationView View(DiscordCommonsConfiguration config)
        => new(
            config.RelayUrl,
            !string.IsNullOrWhiteSpace(config.HostCredential),
            config.GuildAllowlist.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            config.WebhookUrls.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray());

    internal static string NormalizeRelayUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not ("" or "/"))
            throw new ArgumentException(
                "The Commons relay URL must be an HTTPS origin without credentials, path, query, or fragment.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static DiscordCommonsConfiguration Map(LeafEntity entity)
    {
        var data = entity.Data;
        var botId = Guid.TryParse(JsonRead.String(data, "bot_id"), out var parsed)
            ? parsed
            : Guid.Empty;
        return new DiscordCommonsConfiguration(
            entity.Id,
            botId,
            JsonRead.String(data, "relay_url") ?? "",
            JsonRead.String(data, "host_credential"),
            JsonRead.StringSet(data, "guild_allowlist"),
            ParseWebhookUrls(JsonRead.String(data, "webhook_urls")));
    }

    private static IReadOnlyDictionary<string, string> ParseWebhookUrls(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonNode.Parse(value) is JsonObject parsed
                ? parsed.Where(pair => pair.Value is JsonValue)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value!.GetValue<string>(),
                        StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string SerializeWebhookUrls(IReadOnlyDictionary<string, string> values)
        => new JsonObject(values.Select(pair =>
            KeyValuePair.Create<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))))
            .ToJsonString();

    private static string RequiredSnowflake(string value)
        => ulong.TryParse(value?.Trim(), out var parsed) && parsed > 0
            ? parsed.ToString()
            : throw new ArgumentException("Commons guild and channel IDs must be Discord snowflakes.");

    private static string Slug(Guid botId) => $"leaf-discord-commons-{botId:N}";
}

public sealed record DiscordCommonsIngestDelivery(
    Guid EntityId,
    Guid BotId,
    string EventId,
    JsonObject? Payload,
    string State,
    int Attempts,
    DateTimeOffset? NextAttemptAt);

public sealed class DiscordCommonsIngestStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-commons-ingest";

    public async Task<DiscordCommonsIngestDelivery> EnqueueAsync(
        Guid botId, string eventId, JsonObject payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 200)
            throw new ArgumentException("A bounded Commons event ID is required.");
        var slug = $"discord-commons-{Hash($"{botId:N}:{eventId}")}";
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        if (existing is not null) return Map(existing);
        return Map(await store.UpsertBySlugAsync(TypeSlug, slug,
            $"Commons ingest {eventId}", new JsonObject
            {
                ["bot_id"] = botId.ToString(),
                ["event_id"] = eventId,
                ["payload"] = payload.DeepClone(),
                ["state"] = "queued",
                ["attempts"] = 0,
                ["next_attempt_at"] = DateTimeOffset.UtcNow.ToString("O"),
            }, ct));
    }

    public async Task<IReadOnlyList<DiscordCommonsIngestDelivery>> PendingAsync(
        CancellationToken ct = default)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            Limit = 500,
        }, ct)).Select(Map)
            .Where(item => item.State is "queued" or "retry")
            .Where(item => item.Payload is not null)
            .Where(item => item.NextAttemptAt is null || item.NextAttemptAt <= DateTimeOffset.UtcNow)
            .OrderBy(item => item.NextAttemptAt)
            .ToArray();

    public async Task MarkSentAsync(
        DiscordCommonsIngestDelivery delivery, CancellationToken ct = default)
        => _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "sent",
            ["payload"] = null,
            ["last_error"] = null,
            ["next_attempt_at"] = null,
        }, ct: ct);

    public async Task MarkRetryAsync(
        DiscordCommonsIngestDelivery delivery,
        string safeError,
        TimeSpan delay,
        CancellationToken ct = default)
        => _ = await store.PatchAsync(delivery.EntityId, new JsonObject
        {
            ["state"] = "retry",
            ["attempts"] = delivery.Attempts + 1,
            ["last_error"] = safeError.Length <= 200 ? safeError : safeError[..200],
            ["next_attempt_at"] = DateTimeOffset.UtcNow.Add(delay).ToString("O"),
        }, ct: ct);

    private static DiscordCommonsIngestDelivery Map(LeafEntity entity)
    {
        var data = entity.Data;
        _ = Guid.TryParse(JsonRead.String(data, "bot_id"), out var botId);
        _ = DateTimeOffset.TryParse(JsonRead.String(data, "next_attempt_at"), out var next);
        return new DiscordCommonsIngestDelivery(
            entity.Id,
            botId,
            JsonRead.String(data, "event_id") ?? "",
            data["payload"]?.DeepClone() as JsonObject,
            JsonRead.String(data, "state") ?? "queued",
            (int)JsonRead.Long(data, "attempts", 0),
            next == default ? null : next);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..32];
}

public sealed class DiscordCommonsRelayService(
    DiscordCommonsConfigurationStore configurations,
    DiscordCommonsIngestStore deliveries,
    ILogger<DiscordCommonsRelayService> logger) : IAsyncDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private readonly SemaphoreSlim signal = new(0, 1);
    private CancellationTokenSource? runCts;
    private Task? loop;

    public async Task<bool> IsCommonsGuildAsync(
        Guid botId, string? guildId, CancellationToken ct = default)
        => await configurations.GetAsync(botId, ct) is { } config
           && !ShouldReachNativeAgent(guildId, config.GuildAllowlist);

    public Task<DiscordCommonsConfiguration?> GetConfigurationAsync(
        Guid botId, CancellationToken ct = default)
        => configurations.GetAsync(botId, ct);

    public async Task EnqueueAsync(
        Guid botId, string eventId, JsonObject payload, CancellationToken ct = default)
    {
        _ = await deliveries.EnqueueAsync(botId, eventId, payload, ct);
        try { signal.Release(); } catch (SemaphoreFullException) { }
    }

    public void Start(CancellationToken stoppingToken)
    {
        if (loop is not null) return;
        runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // Task.Run so the loop never captures the caller's context (the kernel's WPF
        // dispatcher); otherwise StopAsync waits on continuations a busy UI thread can't run.
        var token = runCts.Token;
        loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (runCts is null) return;
        await runCts.CancelAsync();
        if (loop is not null)
        {
            try { await loop; } catch (OperationCanceledException) { }
        }
        runCts.Dispose();
        runCts = null;
        loop = null;
    }

    internal static bool ShouldReachNativeAgent(
        string? guildId, IReadOnlySet<string> commonsGuilds)
        => string.IsNullOrWhiteSpace(guildId) || !commonsGuilds.Contains(guildId);

    internal static string ChannelName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "général" or "general" ? "general" : normalized;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await DrainAsync(ct);
            var delay = Task.Delay(TimeSpan.FromSeconds(30), ct);
            var wake = signal.WaitAsync(ct);
            await Task.WhenAny(delay, wake);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        foreach (var delivery in await deliveries.PendingAsync(ct))
        {
            var config = await configurations.GetAsync(delivery.BotId, ct);
            if (config is not { Ready: true } || delivery.Payload is null)
            {
                await deliveries.MarkRetryAsync(
                    delivery, "commons_not_configured", TimeSpan.FromMinutes(5), ct);
                continue;
            }
            try
            {
                var endpoint = new Uri(new Uri(config.RelayUrl + "/"), "v1/ingest");
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", config.HostCredential);
                request.Content = new StringContent(
                    delivery.Payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    await deliveries.MarkSentAsync(delivery, ct);
                    continue;
                }
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(delivery.Attempts + 1, 8)));
                await deliveries.MarkRetryAsync(
                    delivery,
                    $"relay_http_{(int)response.StatusCode}",
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(seconds),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(delivery.Attempts + 1, 8)));
                await deliveries.MarkRetryAsync(
                    delivery, $"relay_transport_{ex.GetType().Name}",
                    TimeSpan.FromSeconds(seconds), ct);
                logger.LogWarning(
                    "Commons ingest delivery {EventId} failed with {ErrorType}; retry scheduled",
                    delivery.EventId, ex.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        http.Dispose();
        signal.Dispose();
    }
}

public sealed record DiscordCommonsSecretInstallRequest(
    string SecretName,
    string WranglerProjectPath,
    string Environment = "lab");

public sealed record DiscordCommonsSecretInstallResult(
    string SecretName,
    string Environment,
    bool Installed);

public sealed class DiscordCommonsSecretInstaller(
    DiscordCommonsConfigurationStore configurations)
{
    private static readonly HashSet<string> AllowedSecretNames = new(
        ["WEBHOOK_SHOWCASE", "WEBHOOK_CONSULT", "WEBHOOK_BUGS", "WEBHOOK_GENERAL"],
        StringComparer.Ordinal);

    public async Task<DiscordCommonsSecretInstallResult> InstallAsync(
        Guid botId,
        string channelId,
        DiscordCommonsSecretInstallRequest request,
        CancellationToken ct = default)
    {
        if (!AllowedSecretNames.Contains(request.SecretName))
            throw new ArgumentException("The Wrangler secret name is not a Commons webhook slot.");
        if (!string.Equals(request.Environment, "lab", StringComparison.Ordinal))
            throw new ArgumentException("Only the isolated lab Wrangler environment is supported.");
        var projectPath = Path.GetFullPath(request.WranglerProjectPath);
        if (!File.Exists(Path.Combine(projectPath, "wrangler.toml"))
            || !File.Exists(Path.Combine(projectPath, "package.json")))
            throw new ArgumentException("The Wrangler project path is invalid.");
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new InvalidOperationException("Discord Commons is not configured.");
        if (!config.WebhookUrls.TryGetValue(channelId, out var webhookUrl))
            throw new KeyNotFoundException("No managed Commons webhook exists for that channel.");

        var wranglerEntrypoint = Path.Combine(
            projectPath, "node_modules", "wrangler", "bin", "wrangler.js");
        if (!File.Exists(wranglerEntrypoint))
            throw new ArgumentException(
                "The Wrangler project dependencies are not installed.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                // Launch the JavaScript entrypoint directly. Process.Start with
                // UseShellExecute=false cannot reliably execute Windows .cmd shims.
                FileName = OperatingSystem.IsWindows() ? "node.exe" : "node",
                WorkingDirectory = projectPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(wranglerEntrypoint);
        process.StartInfo.ArgumentList.Add("secret");
        process.StartInfo.ArgumentList.Add("put");
        process.StartInfo.ArgumentList.Add(request.SecretName);
        process.StartInfo.ArgumentList.Add("--env");
        process.StartInfo.ArgumentList.Add("lab");
        if (!process.Start())
            throw new InvalidOperationException("Wrangler secret installation could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteLineAsync(webhookUrl.AsMemory(), ct);
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct);
        _ = await stdout;
        _ = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Wrangler secret installation failed.");
        return new DiscordCommonsSecretInstallResult(
            request.SecretName, "lab", true);
    }
}
