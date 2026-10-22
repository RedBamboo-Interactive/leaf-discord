using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordGatewayInboxItem(
    Guid EntityId,
    Guid BotId,
    string MessageId,
    string ChannelId,
    string? GuildId,
    string Kind,
    string EventId,
    string State,
    int Attempts,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? NextAttemptAt);

/// <summary>
/// Durable hand-off between Discord.Net's gateway callback and the slower Agent
/// admission path. Coordinates are sufficient to refetch the canonical message
/// from Discord; content and credentials are never copied into this queue.
/// </summary>
public sealed class DiscordGatewayInboxStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-gateway-inbox";

    public async Task<DiscordGatewayInboxItem> EnqueueAsync(
        Guid botId,
        string messageId,
        string channelId,
        string? guildId,
        string kind = "message",
        string? eventId = null,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(messageId, out _) || !ulong.TryParse(channelId, out _))
            throw new ArgumentException("Discord inbox coordinates must be snowflakes.");
        if (kind is not ("message" or "announcement-update"))
            throw new ArgumentException("Unknown Discord inbox event kind.", nameof(kind));
        eventId ??= messageId;
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 200)
            throw new ArgumentException("A bounded Discord event ID is required.", nameof(eventId));
        var slug = $"discord-gateway-{Hash($"{botId:N}:{channelId}:{messageId}:{kind}:{eventId}")}";
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        if (existing is not null) return Map(existing);
        var now = DateTimeOffset.UtcNow;
        return Map(await store.UpsertBySlugAsync(TypeSlug, slug,
            $"Discord gateway message {messageId}", new JsonObject
            {
                ["bot_id"] = botId.ToString(),
                ["message_id"] = messageId,
                ["channel_id"] = channelId,
                ["guild_id"] = guildId,
                ["kind"] = kind,
                ["event_id"] = eventId,
                ["state"] = "queued",
                ["attempts"] = 0,
                ["enqueued_at"] = now.ToString("O"),
                ["next_attempt_at"] = now.ToString("O"),
            }, ct));
    }

    public async Task<IReadOnlyList<DiscordGatewayInboxItem>> PendingAsync(
        Guid botId, CancellationToken ct = default)
    {
        var result = new List<DiscordGatewayInboxItem>();
        foreach (var state in new[] { "queued", "retry" })
        {
            var entities = await store.QueryAsync(new EntityQuery
            {
                TypeSlug = TypeSlug,
                DataEquals = new Dictionary<string, object?>
                {
                    ["bot_id"] = botId.ToString(),
                    ["state"] = state,
                },
                Limit = 500,
            }, ct);
            result.AddRange(entities.Select(Map));
        }

        return result
            .OrderBy(item => Snowflake(item.MessageId))
            .ThenBy(item => item.EnqueuedAt)
            .ToArray();
    }

    public async Task MarkCompletedAsync(
        DiscordGatewayInboxItem item, CancellationToken ct = default)
        => _ = await store.PatchAsync(item.EntityId, new JsonObject
        {
            ["state"] = "completed",
            ["last_error"] = null,
            ["next_attempt_at"] = null,
        }, ct: ct);

    public async Task MarkRetryAsync(
        DiscordGatewayInboxItem item,
        string safeError,
        TimeSpan delay,
        CancellationToken ct = default)
        => _ = await store.PatchAsync(item.EntityId, new JsonObject
        {
            ["state"] = "retry",
            ["attempts"] = item.Attempts + 1,
            ["last_error"] = safeError.Length <= 200 ? safeError : safeError[..200],
            ["next_attempt_at"] = DateTimeOffset.UtcNow.Add(delay).ToString("O"),
        }, ct: ct);

    internal static IReadOnlyList<DiscordGatewayInboxItem> DueChannelHeads(
        IEnumerable<DiscordGatewayInboxItem> pending, DateTimeOffset now)
        => pending
            .GroupBy(item => item.ChannelId, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => Snowflake(item.MessageId))
                .ThenBy(item => item.EnqueuedAt)
                .First())
            .Where(item => item.NextAttemptAt is null || item.NextAttemptAt <= now)
            .OrderBy(item => Snowflake(item.MessageId))
            .ToArray();

    internal static TimeSpan RetryDelay(int attempts)
        => TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(6, Math.Max(0, attempts))));

    private static DiscordGatewayInboxItem Map(LeafEntity entity)
    {
        var data = entity.Data;
        _ = Guid.TryParse(JsonRead.String(data, "bot_id"), out var botId);
        _ = DateTimeOffset.TryParse(JsonRead.String(data, "enqueued_at"), out var enqueuedAt);
        _ = DateTimeOffset.TryParse(JsonRead.String(data, "next_attempt_at"), out var nextAttemptAt);
        return new DiscordGatewayInboxItem(
            entity.Id,
            botId,
            JsonRead.String(data, "message_id") ?? "",
            JsonRead.String(data, "channel_id") ?? "",
            JsonRead.String(data, "guild_id"),
            JsonRead.String(data, "kind") ?? "message",
            JsonRead.String(data, "event_id") ?? JsonRead.String(data, "message_id") ?? "",
            JsonRead.String(data, "state") ?? "queued",
            (int)JsonRead.Long(data, "attempts", 0),
            enqueuedAt == default ? entity.CreatedAt : enqueuedAt,
            nextAttemptAt == default ? null : nextAttemptAt);
    }

    private static ulong Snowflake(string value)
        => ulong.TryParse(value, out var parsed) ? parsed : ulong.MaxValue;

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..32];
}
