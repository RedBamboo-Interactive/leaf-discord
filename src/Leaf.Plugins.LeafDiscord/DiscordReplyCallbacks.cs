using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// A private Leaf discussion that sent a message into a Discord binding and wants to hear
/// about the replies. The binding's own Agent session keeps answering on Discord; this only
/// tells the sending discussion that someone answered.
/// </summary>
public sealed record DiscordReplySubscription(
    Guid EntityId,
    string BindingId,
    Guid BindingEntityId,
    string DiscussionId,
    string OwnerUserId,
    string AgentEntityId,
    string? SentMessageUid,
    DateTimeOffset SubscribedAt,
    DateTimeOffset ExpiresAt);

public sealed class DiscordReplySubscriptionStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-reply-subscription";
    /// <summary>Each send renews the window, so a live exchange stays subscribed.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(48);
    public const int MaxPerBinding = 8;

    public async Task<DiscordReplySubscription> SubscribeAsync(
        DiscordBinding binding,
        string discussionId,
        string ownerUserId,
        string agentEntityId,
        string? sentMessageUid,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(discussionId) || discussionId.Length > 100)
            throw new ArgumentException("A bounded discussion ID is required.");
        var slug = Slug(binding.BindingId, discussionId);
        var existing = await store.GetBySlugAsync(TypeSlug, slug, ct);
        var subscribedAt = existing is null
            ? now
            : Map(existing).SubscribedAt;
        var entity = await store.UpsertBySlugAsync(TypeSlug, slug,
            $"Reply callback {binding.ConversationName}", new JsonObject
            {
                ["binding_id"] = binding.BindingId,
                ["binding_entity_id"] = binding.EntityId.ToString(),
                ["discussion_id"] = discussionId,
                ["owner_user_id"] = ownerUserId,
                ["agent_entity_id"] = agentEntityId,
                ["sent_message_uid"] = sentMessageUid,
                ["subscribed_at"] = subscribedAt.ToString("O"),
                ["expires_at"] = now.Add(Lifetime).ToString("O"),
            }, ct);
        await PruneAsync(binding.BindingId, now, ct);
        return Map(entity);
    }

    /// <summary>Unexpired subscriptions for a binding. Expired rows are deleted on read.</summary>
    public async Task<IReadOnlyList<DiscordReplySubscription>> ActiveAsync(
        string bindingId, DateTimeOffset now, CancellationToken ct = default)
    {
        var active = new List<DiscordReplySubscription>();
        foreach (var subscription in await ListAsync(bindingId, ct))
        {
            if (subscription.ExpiresAt <= now)
                await store.DeleteAsync(subscription.EntityId, ct);
            else
                active.Add(subscription);
        }
        return active;
    }

    private async Task PruneAsync(string bindingId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await ActiveAsync(bindingId, now, ct);
        foreach (var stale in active
                     .OrderByDescending(item => item.ExpiresAt)
                     .Skip(MaxPerBinding))
            await store.DeleteAsync(stale.EntityId, ct);
    }

    private async Task<IReadOnlyList<DiscordReplySubscription>> ListAsync(
        string bindingId, CancellationToken ct)
        => (await store.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?> { ["binding_id"] = bindingId },
            Limit = 100,
        }, ct)).Select(Map).ToArray();

    private static DiscordReplySubscription Map(LeafEntity entity)
    {
        var data = entity.Data;
        _ = Guid.TryParse(JsonRead.String(data, "binding_entity_id"), out var bindingEntityId);
        _ = DateTimeOffset.TryParse(JsonRead.String(data, "subscribed_at"), out var subscribedAt);
        _ = DateTimeOffset.TryParse(JsonRead.String(data, "expires_at"), out var expiresAt);
        return new DiscordReplySubscription(
            entity.Id,
            JsonRead.String(data, "binding_id") ?? "",
            bindingEntityId,
            JsonRead.String(data, "discussion_id") ?? "",
            JsonRead.String(data, "owner_user_id") ?? "",
            JsonRead.String(data, "agent_entity_id") ?? "",
            JsonRead.String(data, "sent_message_uid"),
            subscribedAt,
            expiresAt);
    }

    private static string Slug(string bindingId, string discussionId)
        => "discord-reply-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{bindingId}\n{discussionId}"))).ToLowerInvariant()[..32];
}

/// <summary>
/// Publishes <c>leaf-discord.reply-received</c> for every subscribed discussion when a human
/// message is admitted on a binding. The kernel stamps the event source, so a receiver can
/// trust that only this plugin produced it. The kernel also mirrors every event into
/// <c>os:events</c> and broadcasts it to every frontend socket, so the payload carries
/// identifiers and never the message text; the receiver reads the binding transcript.
/// A notice can reach a busy discussion long after the message was written, so it carries
/// the message's own Discord send time, not only when the bridge admitted it.
/// </summary>
public sealed class DiscordReplyNotifier(
    DiscordReplySubscriptionStore subscriptions,
    IPluginEvents events,
    PromptInjectionDetector injection,
    ILogger<DiscordReplyNotifier> logger)
{
    public const string EventType = "reply-received";

    /// <summary>
    /// DMs notify on every admitted counterpart message. Guild channels and threads notify
    /// only when the message addresses the bot (mention or reply), so a busy room does not
    /// stream into a private discussion.
    /// </summary>
    public static bool ShouldNotify(string bindingKind, bool mentioned, bool repliesToBot)
        => bindingKind == "dm" || mentioned || repliesToBot;

    /// <summary>Never throws: the Discord turn is already admitted and must not fail here.</summary>
    public async Task<int> NotifyAsync(
        DiscordBinding binding,
        string discordMessageId,
        ExternalRequestor requestor,
        string content,
        int attachmentCount,
        string admittedMessageUid,
        bool mentioned,
        bool repliesToBot,
        DateTimeOffset now,
        DateTimeOffset? discordSentAt = null,
        CancellationToken ct = default)
    {
        if (!ShouldNotify(binding.Kind, mentioned, repliesToBot)) return 0;
        try
        {
            var active = await subscriptions.ActiveAsync(binding.BindingId, now, ct);
            if (active.Count == 0) return 0;
            var assessment = injection.Assess(content);
            var published = 0;
            foreach (var subscription in active)
            {
                if (subscription.BindingEntityId != binding.EntityId) continue;
                await events.PublishAsync(EventType, BuildPayload(
                    binding, subscription, discordMessageId, requestor,
                    attachmentCount, admittedMessageUid, assessment, now,
                    discordSentAt), ct);
                published++;
            }
            return published;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Discord reply callbacks failed for binding {BindingId}", binding.BindingId);
            return 0;
        }
    }

    internal static JsonObject BuildPayload(
        DiscordBinding binding,
        DiscordReplySubscription subscription,
        string discordMessageId,
        ExternalRequestor requestor,
        int attachmentCount,
        string admittedMessageUid,
        InjectionAssessment assessment,
        DateTimeOffset now,
        DateTimeOffset? discordSentAt = null)
        => new()
        {
            ["discussion_id"] = subscription.DiscussionId,
            ["owner_user_id"] = subscription.OwnerUserId,
            ["agent_entity_id"] = subscription.AgentEntityId,
            ["binding_entity_id"] = binding.EntityId.ToString(),
            ["binding_kind"] = binding.Kind,
            ["conversation_name"] = binding.ConversationName,
            ["guild_name"] = binding.GuildName,
            ["session_id"] = binding.SessionId,
            ["discord_message_id"] = discordMessageId,
            ["admitted_message_uid"] = admittedMessageUid,
            ["requestor_id"] = requestor.ExternalId,
            ["requestor_display_name"] = requestor.DisplayName,
            ["attachment_count"] = attachmentCount,
            ["injection_suspected"] = assessment.Suspicious,
            ["subscription_expires_at"] = subscription.ExpiresAt.ToString("O"),
            ["received_at"] = now.ToString("O"),
            // When the person wrote it, from Discord's own message timestamp, always in UTC.
            ["discord_sent_at"] = discordSentAt?.ToUniversalTime().ToString("O"),
            ["discord_sent_at_label"] = SentAtLabel(discordSentAt),
        };

    /// <summary>The send time as a reader sees it, with its zone spelled out.</summary>
    internal static string? SentAtLabel(DateTimeOffset? sentAt)
        => sentAt?.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
}
