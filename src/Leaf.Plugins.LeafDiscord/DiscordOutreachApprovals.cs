using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Leaf.Plugins.LeafDiscord;

internal sealed record DiscordOutreachApproval(
    Guid BindingEntityId, string BindingId, long Generation, string SessionId, string HandleId, string AgentRef,
    string ChannelId, string ChannelName, string? GuildId, string? GuildName, string ChannelKind,
    Guid OriginEntityId, LeafAgentCaller Caller, DiscordBindingMessageRequest Request,
    DiscordPreparedOutreach Prepared, JsonObject Review,
    string MessageUid, DateTimeOffset CreatedAt)
{
    public string? ReleasedBy { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public DateTimeOffset? DiscardedAt { get; init; }
    public string? DiscardedBy { get; init; }
    public DiscordOutreachReceipt? Receipt { get; init; }
    public bool NeedsApproval => JsonRead.String(Review, "outcome") != "delivered";
    public bool IsOpen => NeedsApproval && DiscardedAt is null && Receipt is null;

    public bool Matches(DiscordBinding binding)
        => binding.Enabled && BindingEntityId == binding.EntityId && BindingId == binding.BindingId
           && Generation == binding.Generation && SessionId == binding.SessionId
           && HandleId == binding.ConversationHandleId && ChannelId == binding.ConversationId
           && string.Equals(AgentRef, binding.AgentId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Caller.BeneficiaryId, binding.OwnerId, StringComparison.OrdinalIgnoreCase);
}

internal sealed record DiscordOutreachReceipt(
    string BindingId, string ConversationName, string SessionId, string MessageUid,
    DiscordReplySubscription? ReplyCallback, DiscordConversationLeadStatus? ConversationLead);

internal sealed record DiscordStoredOutreach(DiscordOutreachApproval Draft, LeafEntity Entity);

/// <summary>
/// Generic entity APIs can see an index row. Only the bridge can read or authenticate its
/// protected draft and decision. The envelope binds both to the immutable operation slug.
/// </summary>
public sealed class DiscordOutreachApprovalStore(IEntityStore entities, IDataProtectionProvider protection)
{
    internal const string TypeSlug = "leaf-discord-outreach-approval";
    private readonly IDataProtector protector = protection.CreateProtector("LeafDiscord.OutreachApproval.v1");

    internal async Task<LeafEntity?> FindOriginAsync(string discussionId, CancellationToken ct)
    {
        if (discussionId.Length is 0 or > 100) return null;
        var origins = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "discussion",
            DataEquals = new Dictionary<string, object?>
            {
                ["app"] = "nova",
                ["discussion_id"] = discussionId,
            },
            Limit = 2,
        }, ct);
        return origins.Count == 1 && IsActivePrivateOrigin(origins[0].Data) ? origins[0] : null;
    }

    internal static bool IsActivePrivateOrigin(JsonObject data)
        => (JsonRead.String(data, "type") ?? "chat") is ("chat" or "live" or "heartbeat")
           && JsonRead.String(data, "status") is not ("archiving" or "archived")
           && JsonRead.String(data, "source_app") is null or "nova"
           && !string.IsNullOrWhiteSpace(JsonRead.String(data, "owner_id"));

    internal Task<DiscordStoredOutreach?> FindByKeyAsync(Guid bindingId, string key, CancellationToken ct)
        => ReadBySlugAsync(Slug(bindingId, key), ct);

    internal async Task<DiscordStoredOutreach?> FindAsync(Guid bindingId, string messageUid, CancellationToken ct)
    {
        var rows = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = TypeSlug,
            DataEquals = new Dictionary<string, object?>
            {
                ["binding_entity_id"] = bindingId.ToString(),
                ["message_uid"] = messageUid,
            },
            Limit = 2,
        }, ct);
        if (rows.Count == 0) return null;
        if (rows.Count == 1 && Read(rows[0]) is { } stored
            && stored.Draft.BindingEntityId == bindingId && stored.Draft.MessageUid == messageUid)
            return stored;
        throw new InvalidOperationException("The Discord outreach approval index is inconsistent.");
    }

    internal async Task<IReadOnlyList<DiscordOutreachApproval>> ListOpenAsync(Guid originId, CancellationToken ct)
    {
        var drafts = new List<DiscordOutreachApproval>();
        Guid? after = null;
        while (true)
        {
            var rows = await entities.QueryAsync(new EntityQuery
            {
                TypeSlug = TypeSlug,
                DataEquals = new Dictionary<string, object?>
                {
                    ["origin_entity_id"] = originId.ToString(),
                    ["open"] = true,
                },
                OrderById = true,
                AfterId = after,
                Limit = 100,
            }, ct);
            foreach (var row in rows)
            {
                var draft = Read(row).Draft;
                if (draft.OriginEntityId == originId && draft.IsOpen)
                    drafts.Add(draft);
            }
            if (rows.Count < 100) return drafts.OrderBy(draft => draft.CreatedAt).ToArray();
            after = rows[^1].Id;
        }
    }

    internal async Task<DiscordStoredOutreach> SaveAsync(
        DiscordOutreachApproval draft, DiscordStoredOutreach? previous, CancellationToken ct)
    {
        var slug = Slug(draft.BindingEntityId, draft.Request.IdempotencyKey);
        var data = new JsonObject
        {
            ["binding_entity_id"] = draft.BindingEntityId.ToString(),
            ["message_uid"] = draft.MessageUid,
            ["origin_entity_id"] = draft.OriginEntityId.ToString(),
            ["open"] = draft.IsOpen,
            ["protected_draft"] = protector.CreateProtector(slug).Protect(JsonSerializer.Serialize(draft)),
        };
        var entity = await entities.CompareExchangeBySlugAsync(TypeSlug, slug, "Discord outreach approval",
            data, previous?.Entity.UpdatedAt, ct);
        return entity is null
            ? throw new InvalidOperationException("The Discord outreach approval changed. Retry the same action.")
            : Read(entity);
    }

    private async Task<DiscordStoredOutreach?> ReadBySlugAsync(string slug, CancellationToken ct)
        => await entities.GetBySlugAsync(TypeSlug, slug, ct) is { } entity ? Read(entity) : null;

    private DiscordStoredOutreach Read(LeafEntity row)
    {
        var envelope = JsonRead.String(row.Data, "protected_draft")
            ?? throw new InvalidOperationException("The Discord outreach approval has no protected draft.");
        DiscordOutreachApproval? draft;
        try
        {
            draft = JsonSerializer.Deserialize<DiscordOutreachApproval>(
                protector.CreateProtector(row.Slug).Unprotect(envelope));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            throw new InvalidOperationException("The protected Discord outreach approval could not be read.", ex);
        }
        if (draft is null || row.Slug != Slug(draft.BindingEntityId, draft.Request.IdempotencyKey))
            throw new InvalidOperationException("The protected Discord outreach approval has an invalid identity.");
        return new(draft, row);
    }

    private static string Slug(Guid bindingId, string key)
        => $"discord-outreach-{DiscordBindingStore.Hash($"{bindingId}:{key.Trim()}")}";
}
