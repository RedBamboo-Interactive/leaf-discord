using System.Text.Json.Nodes;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>What setting up outage alerts did. Never the webhook URL: it went straight to the kernel.</summary>
/// <param name="ChannelCreated">False when an existing private #leaf-alerts was reused.</param>
/// <param name="Scheduled">The kernel scheduled its outage watcher.</param>
/// <param name="Problem">Why the watcher is not scheduled; null when it is.</param>
/// <param name="ReplacedWebhooks">Earlier alert webhooks of this bot in that channel, deleted once the new one was stored.</param>
public sealed record DiscordOutageAlertsResult(
    string GuildId,
    string ChannelId,
    string ChannelName,
    bool ChannelCreated,
    bool Scheduled,
    string? Problem,
    int ReplacedWebhooks);

/// <summary>A #leaf-alerts channel exists that is not private: alerts are never posted where others read them.</summary>
public sealed class DiscordOutageAlertsConflictException(string message) : Exception(message);

/// <summary>One permission overwrite as Discord holds it.</summary>
internal sealed record DiscordOverwrite(ulong TargetId, bool IsRole, ulong Allow, ulong Deny);

/// <summary>
/// Outage alerts (2026-11-27): Leaf's outage watcher posts to a webhook in a private channel that only the
/// owner and the bot can read. This is that channel and webhook's fixed shape.
/// </summary>
internal static class DiscordOutageAlerts
{
    public const string ChannelName = "leaf-alerts";
    public const string WebhookName = "Leaf outage alerts";
    // Discord administrators bypass channel overwrites, so "private" is said with that exception.
    public const string Topic = "Leaf tells you here when it stops answering. Private to you and the bot (server admins can see it).";
    public const string AuditReason = "Leaf owner turned on outage alerts";

    // Discord permission bits.
    public const ulong ViewChannel = 1UL << 10;
    public const ulong SendMessages = 1UL << 11;
    public const ulong ReadMessageHistory = 1UL << 16;
    public const ulong ManageWebhooks = 1UL << 29;

    /// <summary>The owner reads; the bot reads, writes and keeps the webhook.</summary>
    public const ulong OwnerAllow = ViewChannel | ReadMessageHistory;
    public const ulong BotAllow = ViewChannel | SendMessages | ReadMessageHistory | ManageWebhooks;

    /// <summary>
    /// A text channel nobody but the owner and the bot can see: @everyone (whose role ID is the guild's own)
    /// is denied View Channel, and the two are allowed by member overwrites, so no role grants it to others.
    /// </summary>
    public static JsonObject ChannelPayload(string guildId, string ownerDiscordUserId, string botUserId)
        => new()
        {
            ["name"] = ChannelName,
            ["type"] = 0,
            ["topic"] = Topic,
            ["permission_overwrites"] = new JsonArray
            {
                Overwrite(guildId, role: true, allow: 0, deny: ViewChannel),
                Overwrite(ownerDiscordUserId, role: false, allow: OwnerAllow, deny: 0),
                Overwrite(botUserId, role: false, allow: BotAllow, deny: 0),
            },
        };

    /// <summary>
    /// An existing #leaf-alerts is reused only when it is exactly what setup makes: @everyone denied View Channel,
    /// the owner's member overwrite allowing View Channel and Read Message History, the bot's allowing View
    /// Channel, Send Messages and Manage Webhooks (and neither denying them), and nothing else granting View
    /// Channel. A channel with only the @everyone deny is private to nobody: the owner could not read the alerts.
    /// </summary>
    public static bool IsPrivate(IEnumerable<DiscordOverwrite> overwrites, ulong guildId, ulong ownerId, ulong botId)
    {
        var all = overwrites.ToArray();
        var everyone = all.FirstOrDefault(item => item.IsRole && item.TargetId == guildId);
        if (everyone is null || (everyone.Deny & ViewChannel) == 0) return false;
        if (!Grants(all, ownerId, OwnerAllow) || !Grants(all, botId, ViewChannel | SendMessages | ManageWebhooks)) return false;
        return all.All(item => (item.Allow & ViewChannel) == 0
                               || (!item.IsRole && (item.TargetId == ownerId || item.TargetId == botId)));
    }

    private static bool Grants(IEnumerable<DiscordOverwrite> overwrites, ulong memberId, ulong required)
        => overwrites.FirstOrDefault(item => !item.IsRole && item.TargetId == memberId) is { } member
           && (member.Allow & required) == required
           && (member.Deny & required) == 0;

    public static string WebhookUrl(string id, string token)
        => $"https://discord.com/api/webhooks/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(token)}";

    private static JsonObject Overwrite(string id, bool role, ulong allow, ulong deny)
        => new()
        {
            ["id"] = id,
            ["type"] = role ? 0 : 1,
            ["allow"] = allow.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deny"] = deny.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
}
