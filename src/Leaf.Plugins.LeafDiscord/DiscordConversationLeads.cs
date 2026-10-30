using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>An owner message sent into the binding, kept until the binding's session has heard it.</summary>
public sealed record DiscordConversationLeadMessage(
    string MessageUid,
    DateTimeOffset SentAt,
    string Content);

/// <summary>
/// One private Leaf discussion leads a Discord conversation it reached through outreach. While
/// it leads, the binding's own Agent session still receives every admitted message, in order,
/// but the bridge posts nothing it writes: the leading discussion is the only voice. When the
/// lead ends (released, or its window elapsed without a new owner message), the session's next
/// turn is told so, with the owner messages it never saw, and it answers on its own again.
/// The same row also carries owner messages sent outside any lead: a transcript injection never
/// reaches the session's model, so every owner message waits here for the session's next turn.
/// A row whose <see cref="DiscussionId"/> is empty has never been led.
/// </summary>
public sealed record DiscordConversationLead(
    Guid EntityId,
    string BindingId,
    Guid BindingEntityId,
    long Generation,
    string DiscussionId,
    string OwnerUserId,
    string AgentEntityId,
    TimeSpan Window,
    DateTimeOffset StartedAt,
    DateTimeOffset RenewedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ReleasedAt,
    string? ReleaseReason,
    DateTimeOffset? HandbackRelayedAt,
    IReadOnlyList<DiscordConversationLeadMessage> OwnerMessages,
    int OmittedOwnerMessages,
    IReadOnlyList<string> UnpostedReplies,
    IReadOnlyList<string> LedTurns,
    IReadOnlyList<string> UnpostedHistory)
{
    /// <summary>Whether a discussion ever led this binding generation (not only relayed messages).</summary>
    public bool EverLed => DiscussionId.Length > 0;

    public bool IsActive(DateTimeOffset now) => EverLed && ReleasedAt is null && ExpiresAt > now;

    /// <summary>When the lead stopped: its release, or the end of its window.</summary>
    public DateTimeOffset? EndedAt(DateTimeOffset now)
        => ReleasedAt ?? (ExpiresAt <= now ? ExpiresAt : null);

    /// <summary>The lead ended and the session has not yet been told on an admitted turn.</summary>
    public bool HandbackPending(DateTimeOffset now)
        => EverLed && !IsActive(now) && HandbackRelayedAt is null;
}

/// <summary>What <c>GET /bindings</c> and the outreach response show about who answers.</summary>
public sealed record DiscordConversationLeadStatus(
    string State,
    string? LeadDiscussionId = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? RenewedAt = null,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? EndedAt = null,
    string? EndReason = null,
    int PendingOwnerMessages = 0,
    int UnpostedReplies = 0,
    int LedTurns = 0);

/// <summary>A different discussion already leads the conversation.</summary>
public sealed class DiscordConversationLedElsewhereException(DiscordConversationLead lead)
    : Exception("Another Leaf discussion already leads this Discord conversation.")
{
    public DiscordConversationLead Lead { get; } = lead;
}

public sealed class DiscordConversationLeadStore(IEntityStore store)
{
    public const string TypeSlug = "leaf-discord-conversation-lead";
    public const string ContextSchema = "leaf-discord-conversation-lead/v1";

    /// <summary>The States <see cref="Project"/> reports.</summary>
    public const string SessionState = "session";
    public const string OwnerState = "owner";
    public const string HandingBackState = "handing_back";

    /// <summary>
    /// A lead lasts an hour unless the request names another window; each leader message renews
    /// it. A forgotten lead must not silence the session for long.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(60);
    /// <summary>The longest window a request may ask for: the reply-callback lifetime, which the
    /// leader's subscription must outlive.</summary>
    public static readonly TimeSpan MaxWindow = DiscordReplySubscriptionStore.Lifetime;
    public static readonly TimeSpan MinWindow = TimeSpan.FromMinutes(1);
    /// <summary>The <c>state</c> values of the session context.</summary>
    public const string OwnerLeadsContext = "owner_leads";
    public const string HandedBackContext = "handed_back";
    public const string SessionLeadsContext = "session_leads";
    public const int MaxOwnerMessages = 12;
    public const int MaxOwnerMessageLength = 4000;
    public const int MaxUnpostedReplies = 20;
    public const int MaxLedTurns = 50;
    public const int MaxUnpostedHistory = 100;

    // Every change is read-modify-write on one row per binding. Instances share the lock, so the
    // coordinator's store and the endpoints' store never interleave on the same binding.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>The binding's lead record, or null when it has none for this binding and generation.</summary>
    public async Task<DiscordConversationLead?> GetAsync(
        DiscordBinding binding, CancellationToken ct = default)
    {
        var entity = await store.GetBySlugAsync(TypeSlug, Slug(binding.BindingId), ct);
        return entity is null ? null : Current(binding, Map(entity));
    }

    /// <summary>
    /// Starts a lead for <paramref name="discussionId"/>, or renews it when that discussion
    /// already leads. Messages still waiting for the session from an earlier lead are kept.
    /// Returns the row as it was before, so a send that then fails can put it back.
    /// </summary>
    public async Task<(DiscordConversationLead Lead, JsonObject? Previous)> ClaimAsync(
        DiscordBinding binding,
        string discussionId,
        string ownerUserId,
        string agentEntityId,
        TimeSpan window,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(discussionId) || discussionId.Length > 100)
            throw new ArgumentException("A bounded discussion ID is required.");
        if (window < MinWindow || window > MaxWindow)
            throw new ArgumentOutOfRangeException(nameof(window));
        return await LockedAsync(binding.BindingId, async () =>
        {
            var entity = await store.GetBySlugAsync(TypeSlug, Slug(binding.BindingId), ct);
            var previous = entity?.Data.DeepClone() as JsonObject;
            var existing = entity is null ? null : Current(binding, Map(entity));
            if (existing is not null && existing.IsActive(now)
                && !string.Equals(existing.DiscussionId, discussionId, StringComparison.Ordinal))
                throw new DiscordConversationLedElsewhereException(existing);
            var continuing = existing is not null && existing.IsActive(now);
            var lead = new DiscordConversationLead(
                Guid.Empty, binding.BindingId, binding.EntityId, binding.Generation,
                discussionId, ownerUserId, agentEntityId, window,
                continuing ? existing!.StartedAt : now, now, now.Add(window),
                null, null, null,
                existing?.OwnerMessages ?? [],
                existing?.OmittedOwnerMessages ?? 0,
                existing?.UnpostedReplies ?? [],
                continuing ? existing!.LedTurns : [],
                existing?.UnpostedHistory ?? []);
            return (await SaveAsync(binding, lead, ct), previous);
        });
    }

    /// <summary>Puts back the row a failed claim replaced.</summary>
    public Task RevertClaimAsync(
        DiscordBinding binding, JsonObject? previous, CancellationToken ct = default)
        => LockedAsync(binding.BindingId, async () =>
        {
            var slug = Slug(binding.BindingId);
            if (previous is null)
            {
                if (await store.GetBySlugAsync(TypeSlug, slug, ct) is { } created)
                    await store.DeleteAsync(created.Id, ct);
            }
            else
            {
                await store.UpsertBySlugAsync(TypeSlug, slug, Title(binding), previous, ct);
            }
            return true;
        });

    /// <summary>Each owner message from the leading discussion moves the window forward.</summary>
    public Task<DiscordConversationLead?> RenewAsync(
        DiscordBinding binding, string discussionId, TimeSpan? window, DateTimeOffset now,
        CancellationToken ct = default)
        => MutateAsync(binding, lead =>
        {
            if (!lead.IsActive(now)
                || !string.Equals(lead.DiscussionId, discussionId, StringComparison.Ordinal))
                return null;
            var span = window ?? lead.Window;
            return lead with { Window = span, RenewedAt = now, ExpiresAt = now.Add(span) };
        }, ct);

    /// <summary>
    /// Ends an active lead early. With a discussion ID only that discussion's lead ends; without
    /// one (the owner in the Leaf UI) any lead does.
    /// </summary>
    public Task<DiscordConversationLead?> ReleaseAsync(
        DiscordBinding binding, string reason, DateTimeOffset now, string? discussionId = null,
        CancellationToken ct = default)
        => MutateAsync(binding, lead =>
            !lead.IsActive(now) || discussionId is not null
                && !string.Equals(lead.DiscussionId, discussionId, StringComparison.Ordinal)
                ? null
                : lead with { ReleasedAt = now, ReleaseReason = reason }, ct);

    /// <summary>
    /// Keeps an owner message sent into the binding until the session's next admitted turn, so
    /// the session learns what was said for it, with or without a lead. The first message of a
    /// binding that was never led creates its row.
    /// </summary>
    public Task<DiscordConversationLead?> RecordOwnerMessageAsync(
        DiscordBinding binding, string messageUid, string content, DateTimeOffset now,
        CancellationToken ct = default)
        => MutateAsync(binding, lead =>
        {
            if (lead.OwnerMessages.Any(item => item.MessageUid == messageUid))
                return null;
            var text = content.Length <= MaxOwnerMessageLength
                ? content
                : content[..(MaxOwnerMessageLength - 1)] + "…";
            var messages = lead.OwnerMessages
                .Append(new DiscordConversationLeadMessage(messageUid, now, text)).ToList();
            var omitted = Math.Max(0, messages.Count - MaxOwnerMessages);
            return lead with
            {
                OwnerMessages = messages.Skip(omitted).ToArray(),
                OmittedOwnerMessages = lead.OmittedOwnerMessages + omitted,
            };
        }, ct, createIfMissing: true);

    /// <summary>A reply the session wrote while the lead held it back from Discord.</summary>
    public Task<DiscordConversationLead?> RecordUnpostedReplyAsync(
        DiscordBinding binding, string messageUid, CancellationToken ct = default)
        => MutateAsync(binding, lead => lead.UnpostedHistory.Contains(messageUid)
            ? null
            : lead with
            {
                UnpostedReplies = lead.UnpostedReplies.Append(messageUid)
                    .TakeLast(MaxUnpostedReplies).ToArray(),
                UnpostedHistory = lead.UnpostedHistory.Append(messageUid)
                    .TakeLast(MaxUnpostedHistory).ToArray(),
            }, ct);

    /// <summary>
    /// The session admitted a turn that carried <paramref name="relayed"/>. What it carried is
    /// no longer pending. A turn admitted during the lead is remembered, so whatever the
    /// session writes for it stays unposted even after the lead ends; the first turn after
    /// the lead ended completes the hand-back.
    /// </summary>
    public Task<DiscordConversationLead?> RecordAdmittedTurnAsync(
        DiscordBinding binding, string discordMessageId, JsonObject? relayed, DateTimeOffset now,
        CancellationToken ct = default)
        => MutateAsync(binding, lead =>
        {
            var relayedOwner = (relayed?["owner_messages"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(item => JsonRead.String(item, "message_uid"))
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            var relayedUnposted = (relayed?["unposted_replies"] as JsonArray ?? [])
                .Select(item => item?.GetValue<string>())
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            var updated = lead with
            {
                OwnerMessages = lead.OwnerMessages
                    .Where(item => !relayedOwner.Contains(item.MessageUid)).ToArray(),
                OmittedOwnerMessages = relayed is null ? lead.OmittedOwnerMessages : 0,
                UnpostedReplies = lead.UnpostedReplies
                    .Where(item => !relayedUnposted.Contains(item)).ToArray(),
            };
            return lead.IsActive(now)
                ? updated with
                {
                    LedTurns = updated.LedTurns.Append(discordMessageId)
                        .Distinct(StringComparer.Ordinal).TakeLast(MaxLedTurns).ToArray(),
                }
                : updated.EverLed
                    ? updated with { HandbackRelayedAt = updated.HandbackRelayedAt ?? now }
                    : updated;
        }, ct);

    /// <summary>
    /// Whether the session's current output must stay off Discord: the lead is active, or the
    /// newest admitted message (the turn being answered) arrived while it was.
    /// </summary>
    public static bool HoldsReplies(DiscordConversationLead? lead, DiscordBinding binding, DateTimeOffset now)
    {
        if (lead is null) return false;
        if (lead.IsActive(now)) return true;
        var turn = binding.LastDeliveredGeneration == binding.Generation
            ? binding.LastDeliveredSnowflake
            : null;
        return turn is not null && lead.LedTurns.Contains(turn, StringComparer.Ordinal);
    }

    /// <summary>
    /// Trusted bridge state for the session's next turn, or null when there is nothing to say:
    /// no row, or no lead and nothing pending. Outside a lead the state is
    /// <see cref="SessionLeadsContext"/> and the session answers as usual; the block only tells
    /// it what the owner sent since its last turn.
    /// </summary>
    public static JsonObject? SessionContext(DiscordConversationLead? lead, DateTimeOffset now)
    {
        if (lead is null) return null;
        var active = lead.IsActive(now);
        var handingBack = lead.HandbackPending(now);
        if (!active && !handingBack
            && lead.OwnerMessages.Count == 0 && lead.UnpostedReplies.Count == 0)
            return null;
        var state = active ? OwnerLeadsContext : handingBack ? HandedBackContext : SessionLeadsContext;
        var context = new JsonObject
        {
            ["schema"] = ContextSchema,
            ["source"] = "leaf_discord_bridge",
            ["trust"] = "trusted_bridge_state",
            ["state"] = state,
            ["reply_policy"] = active ? "observe_only" : "answer",
            ["lead_started_at"] = active || handingBack ? Utc(lead.StartedAt) : null,
            ["lead_expires_at"] = active ? Utc(lead.ExpiresAt) : null,
            ["lead_ended_at"] = handingBack ? Utc(lead.EndedAt(now)!.Value) : null,
            ["end_reason"] = handingBack ? lead.ReleaseReason ?? "expired" : null,
            ["owner_messages"] = new JsonArray(lead.OwnerMessages.Select(item => (JsonNode?)new JsonObject
            {
                ["message_uid"] = item.MessageUid,
                ["sent_at"] = Utc(item.SentAt),
                ["content"] = item.Content,
            }).ToArray()),
            ["omitted_owner_messages"] = lead.OmittedOwnerMessages,
            ["unposted_replies"] = new JsonArray(
                lead.UnpostedReplies.Select(item => (JsonNode?)item).ToArray()),
        };
        return context;
    }

    public static DiscordConversationLeadStatus Project(DiscordConversationLead? lead, DateTimeOffset now)
    {
        if (lead is null) return new DiscordConversationLeadStatus(SessionState);
        if (!lead.EverLed)
            return new DiscordConversationLeadStatus(
                SessionState, PendingOwnerMessages: lead.OwnerMessages.Count);
        var state = lead.IsActive(now) ? OwnerState
            : lead.HandbackPending(now) ? HandingBackState
            : SessionState;
        return state == SessionState && lead.OwnerMessages.Count == 0 && lead.UnpostedReplies.Count == 0
            ? new DiscordConversationLeadStatus(SessionState)
            : new DiscordConversationLeadStatus(
                state,
                lead.DiscussionId,
                lead.StartedAt,
                lead.RenewedAt,
                lead.IsActive(now) ? lead.ExpiresAt : null,
                lead.EndedAt(now),
                lead.IsActive(now) ? null : lead.ReleaseReason ?? "expired",
                lead.OwnerMessages.Count,
                lead.UnpostedReplies.Count,
                lead.LedTurns.Count);
    }

    private async Task<DiscordConversationLead?> MutateAsync(
        DiscordBinding binding,
        Func<DiscordConversationLead, DiscordConversationLead?> change,
        CancellationToken ct,
        bool createIfMissing = false)
        => await LockedAsync(binding.BindingId, async () =>
        {
            var entity = await store.GetBySlugAsync(TypeSlug, Slug(binding.BindingId), ct);
            var current = entity is null ? null : Current(binding, Map(entity));
            // A row from an older generation is replaced, never carried over.
            if (current is null && createIfMissing) current = NeverLed(binding);
            if (current is null) return null;
            var updated = change(current);
            return updated is null ? null : await SaveAsync(binding, updated, ct);
        });

    private async Task<DiscordConversationLead> SaveAsync(
        DiscordBinding binding, DiscordConversationLead lead, CancellationToken ct)
    {
        var entity = await store.UpsertBySlugAsync(TypeSlug, Slug(binding.BindingId), Title(binding),
            new JsonObject
            {
                ["binding_id"] = lead.BindingId,
                ["binding_entity_id"] = lead.BindingEntityId.ToString(),
                ["generation"] = lead.Generation,
                ["discussion_id"] = lead.DiscussionId,
                ["owner_user_id"] = lead.OwnerUserId,
                ["agent_entity_id"] = lead.AgentEntityId,
                ["window_seconds"] = (long)lead.Window.TotalSeconds,
                ["started_at"] = lead.StartedAt.ToString("O"),
                ["renewed_at"] = lead.RenewedAt.ToString("O"),
                ["expires_at"] = lead.ExpiresAt.ToString("O"),
                ["released_at"] = lead.ReleasedAt?.ToString("O"),
                ["release_reason"] = lead.ReleaseReason,
                ["handback_relayed_at"] = lead.HandbackRelayedAt?.ToString("O"),
                ["owner_messages"] = new JsonArray(lead.OwnerMessages.Select(item => (JsonNode?)new JsonObject
                {
                    ["message_uid"] = item.MessageUid,
                    ["sent_at"] = item.SentAt.ToString("O"),
                    ["content"] = item.Content,
                }).ToArray()),
                ["omitted_owner_messages"] = lead.OmittedOwnerMessages,
                ["unposted_replies"] = Strings(lead.UnpostedReplies),
                ["led_turns"] = Strings(lead.LedTurns),
                ["unposted_history"] = Strings(lead.UnpostedHistory),
            }, ct);
        return Map(entity);
    }

    private static DiscordConversationLead NeverLed(DiscordBinding binding)
        => new(Guid.Empty, binding.BindingId, binding.EntityId, binding.Generation,
            "", "", "", DefaultWindow,
            DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MinValue,
            null, null, null, [], 0, [], [], []);

    /// <summary>A row left by an older generation or a recreated binding means nothing here.</summary>
    private static DiscordConversationLead? Current(DiscordBinding binding, DiscordConversationLead lead)
        => lead.BindingEntityId == binding.EntityId && lead.Generation == binding.Generation
            ? lead
            : null;

    private static DiscordConversationLead Map(LeafEntity entity)
    {
        var data = entity.Data;
        _ = Guid.TryParse(JsonRead.String(data, "binding_entity_id"), out var bindingEntityId);
        return new DiscordConversationLead(
            entity.Id,
            JsonRead.String(data, "binding_id") ?? "",
            bindingEntityId,
            JsonRead.Long(data, "generation", -1),
            JsonRead.String(data, "discussion_id") ?? "",
            JsonRead.String(data, "owner_user_id") ?? "",
            JsonRead.String(data, "agent_entity_id") ?? "",
            TimeSpan.FromSeconds(JsonRead.Long(data, "window_seconds", (long)DefaultWindow.TotalSeconds)),
            Time(data, "started_at") ?? DateTimeOffset.MinValue,
            Time(data, "renewed_at") ?? DateTimeOffset.MinValue,
            Time(data, "expires_at") ?? DateTimeOffset.MinValue,
            Time(data, "released_at"),
            JsonRead.String(data, "release_reason"),
            Time(data, "handback_relayed_at"),
            (data["owner_messages"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(item => new DiscordConversationLeadMessage(
                    JsonRead.String(item, "message_uid") ?? "",
                    Time(item, "sent_at") ?? DateTimeOffset.MinValue,
                    JsonRead.String(item, "content") ?? ""))
                .Where(item => item.MessageUid.Length > 0)
                .ToArray(),
            (int)JsonRead.Long(data, "omitted_owner_messages", 0),
            ReadStrings(data, "unposted_replies"),
            ReadStrings(data, "led_turns"),
            ReadStrings(data, "unposted_history"));
    }

    private static async Task<T> LockedAsync<T>(string bindingId, Func<Task<T>> action)
    {
        var gate = Gates.GetOrAdd(bindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private static DateTimeOffset? Time(JsonObject data, string key)
        => DateTimeOffset.TryParse(JsonRead.String(data, key), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value) ? value : null;

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static JsonArray Strings(IEnumerable<string> values)
        => new(values.Select(value => (JsonNode?)value).ToArray());

    private static string[] ReadStrings(JsonObject data, string key)
        => (data[key] as JsonArray ?? [])
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .OfType<string>().ToArray();

    private static string Title(DiscordBinding binding) => $"Conversation lead {binding.ConversationName}";

    private static string Slug(string bindingId)
        => $"discord-lead-{DiscordBindingStore.Hash(bindingId)}";
}
