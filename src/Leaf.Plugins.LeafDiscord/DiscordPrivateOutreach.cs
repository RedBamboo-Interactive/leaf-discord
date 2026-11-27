using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

internal sealed class DiscordOutreachConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed partial class DiscordConversationCoordinator
{
    private DiscordOutreachApprovalStore? OutreachApprovals { get; } = approvals;

    internal async Task<DiscordOutreachReceipt> SubmitPrivateOutreachAsync(
        DiscordBinding binding, LeafEntity origin, LeafAgentCaller caller,
        DiscordBindingMessageRequest request, DiscordReplySubscriptionStore subscriptions,
        CancellationToken ct, Func<CancellationToken, Task>? requireConnected = null)
    {
        var store = OutreachApprovals
            ?? throw new InvalidOperationException("Discord outreach approval storage is unavailable.");
        var prepared = PreparePrivateOutreach(request);
        request = request with { OutputAssets = request.OutputAssets ?? [] };
        var key = request.IdempotencyKey.Trim();
        var messageUid = OutreachMessageUid(binding, key);

        // Run Sentinel off the binding gate. Incoming turns, typing and outbound reconciliation
        // can continue during review. Only the first durable verdict for this request wins.
        JsonObject? review = null;
        if (await store.FindByKeyAsync(binding.EntityId, key, ct) is null)
        {
            await ValidatePrivateTargetAsync(binding, ct);
            var recorded = await deliveries.FindOutboundReviewAsync(binding, messageUid, ct);
            if (recorded is not null && JsonRead.String(recorded, "outcome") == "delivered")
                review = recorded;
            else
                review = await ReviewPrivateOutreachAsync(binding, prepared, key, ct);
        }

        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var existing = await store.FindByKeyAsync(binding.EntityId, key, ct);
            var draft = existing?.Draft;
            if (draft is not null && (draft.OwnerTurn is not null || draft.OriginEntityId != origin.Id
                || draft.Caller != caller
                || JsonSerializer.Serialize(draft.Request) != JsonSerializer.Serialize(request)))
                throw new DiscordOutreachConflictException("idempotency_key_conflict",
                    "This idempotency key already belongs to different Discord outreach.");
            if (draft?.DiscardedAt is not null)
                throw OutreachDiscarded();
            if (draft?.Receipt is { } receipt)
                return receipt;
            var activeOrigin = await store.FindOriginAsync(caller.DiscussionId!, ct);
            if (activeOrigin is null || activeOrigin.Id != origin.Id
                || !SameOriginField(activeOrigin, origin, "owner_id") || !SameOriginField(activeOrigin, origin, "agent"))
                throw new UnauthorizedAccessException("The private Discord outreach discussion is no longer available.");

            var current = await bindings.GetAsync(binding.EntityId, ct);
            if (current is null || !current.Enabled || current.Generation != binding.Generation
                || current.SessionId != binding.SessionId || current.ConversationHandleId != binding.ConversationHandleId
                || (draft is not null && !draft.Matches(current)))
                throw OutreachStale();

            if (existing is null)
            {
                if (review is null)
                    throw new InvalidOperationException("The Discord outreach reservation changed. Retry the same request.");
                await deliveries.ValidateOutreachReplayAsync(current, messageUid, prepared, request.Announcement, ct);
                draft = new DiscordOutreachApproval(
                    current.EntityId, current.BindingId, current.Generation, current.SessionId!, current.ConversationHandleId!, current.AgentId,
                    current.ConversationId, current.ConversationName, current.GuildId, current.GuildName, current.Kind,
                    origin.Id, caller, request, prepared, review, messageUid, clock.GetUtcNow());
                existing = await store.SaveAsync(draft, null, ct);
            }
            draft = existing.Draft;
            if (draft.NeedsApproval)
                throw new DiscordSentinelRefusedException(
                    JsonRead.String(draft.Review, "outcome") == "withheld", draft.Review)
                {
                    ApprovalMessageUid = draft.MessageUid,
                };

            if (requireConnected is not null) await requireConnected(ct);
            return await CompletePrivateOutreachAsync(store, existing, current, subscriptions, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<DiscordReleaseResult> ReleasePrivateOutreachAsync(
        DiscordBinding binding, LeafEntity origin, string messageUid, string releasedBy,
        DiscordReplySubscriptionStore subscriptions, CancellationToken ct)
    {
        var store = OutreachApprovals
            ?? throw new InvalidOperationException("Discord outreach approval storage is unavailable.");
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var stored = await store.FindAsync(binding.EntityId, messageUid, ct);
            if (stored is null || !await HasActiveOutreachOriginAsync(store, origin, stored.Draft, ct))
                return new(DiscordReleaseOutcome.NotFound);
            var draft = stored.Draft;
            if (draft.DiscardedAt is not null) throw OutreachDiscarded();
            if (!draft.NeedsApproval) return new(DiscordReleaseOutcome.NotFound);
            if (draft.Receipt is not null)
                return new(DiscordReleaseOutcome.AlreadyReleased, draft.ReleasedBy, draft.ReleasedAt);
            var current = await bindings.GetAsync(binding.EntityId, ct);
            if (current is null || !draft.Matches(current))
                throw OutreachStale();

            if (draft.OwnerTurn is { } proof)
            {
                await DiscordOwnerTurnPolicy.ValidateOriginAsync(origin, proof, botConfigurations.EntityStore, ct);
                ValidateOwnerTurnBinding(current, draft.Generation);
                await ValidateOwnerTurnTargetAsync(current, draft.Caller, draft.Prepared, readBytes: true, ct);
            }
            else
                await ValidatePrivateTargetAsync(current, ct);
            ValidatePreparedDraft(draft);
            await CheckLeadBeforeOutreachAsync(current, draft, ct);
            var firstDecision = draft.ReleasedAt is null;
            if (firstDecision)
                stored = await store.SaveAsync(draft with
                {
                    ReleasedBy = releasedBy,
                    ReleasedAt = clock.GetUtcNow(),
                }, stored, ct);

            await CompletePrivateOutreachAsync(store, stored, current, subscriptions, ct);
            return new(firstDecision ? DiscordReleaseOutcome.Released : DiscordReleaseOutcome.AlreadyReleased,
                stored.Draft.ReleasedBy, stored.Draft.ReleasedAt);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<bool?> DiscardPrivateOutreachAsync(
        DiscordBinding binding, LeafEntity origin, string messageUid, string discardedBy, CancellationToken ct)
    {
        var store = OutreachApprovals
            ?? throw new InvalidOperationException("Discord outreach approval storage is unavailable.");
        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var stored = await store.FindAsync(binding.EntityId, messageUid, ct);
            if (stored is null || !await HasActiveOutreachOriginAsync(store, origin, stored.Draft, ct)
                || !stored.Draft.NeedsApproval)
                return null;
            if (stored.Draft.DiscardedAt is not null) return true;
            if (stored.Draft.ReleasedAt is not null)
                throw new DiscordOutreachConflictException("discord_outreach_already_released",
                    "This Discord outreach already has a release decision.");
            await store.SaveAsync(stored.Draft with
            {
                DiscardedAt = clock.GetUtcNow(),
                DiscardedBy = discardedBy,
            }, stored, ct);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static bool OwnsOutreach(LeafEntity origin, DiscordOutreachApproval draft)
        => DiscordOutreachApprovalStore.IsActivePrivateOrigin(origin.Data)
           && (draft.OwnerTurn is not { } proof || DiscordOwnerTurnPolicy.MatchesDiscussion(origin, proof))
           && origin.Id == draft.OriginEntityId
           && JsonRead.String(origin.Data, "discussion_id") == draft.Caller.DiscussionId
           && string.Equals(JsonRead.String(origin.Data, "owner_id"), draft.Caller.BeneficiaryId,
               StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> HasActiveOutreachOriginAsync(
        DiscordOutreachApprovalStore store, LeafEntity origin, DiscordOutreachApproval draft, CancellationToken ct)
        => OwnsOutreach(origin, draft)
           && await store.FindOriginAsync(draft.Caller.DiscussionId!, ct) is { } active
           && OwnsOutreach(active, draft) && SameOriginField(active, origin, "agent");

    private static bool SameOriginField(LeafEntity first, LeafEntity second, string key)
        => string.Equals(JsonRead.String(first.Data, key), JsonRead.String(second.Data, key),
            StringComparison.OrdinalIgnoreCase);

    private async Task<DiscordOutreachReceipt> CompletePrivateOutreachAsync(
        DiscordOutreachApprovalStore store, DiscordStoredOutreach stored, DiscordBinding binding,
        DiscordReplySubscriptionStore subscriptions, CancellationToken ct)
    {
        var draft = stored.Draft;
        var request = draft.Request;
        var caller = draft.Caller;
        var discussionId = caller.DiscussionId!;
        await ValidatePrivateTargetAsync(binding, ct);
        ValidatePreparedDraft(draft);
        var lead = await CheckLeadBeforeOutreachAsync(binding, draft, ct);
        DiscordConversationLead? claimed = null;
        JsonObject? previousLead = null;
        if (request.LeadConversation == true && lead is null)
        {
            (claimed, previousLead) = await leadStore.ClaimAsync(
                binding, discussionId, caller.BeneficiaryId, caller.ActorEntityId,
                request.LeadForMinutes is { } minutes
                    ? TimeSpan.FromMinutes(minutes) : DiscordConversationLeadStore.DefaultWindow,
                clock.GetUtcNow(), ct);
        }

        var admitted = false;
        try
        {
            var (target, uid) = await AdmitProactiveAsync(binding, request.IdempotencyKey.Trim(),
                draft.Prepared, draft.Review, null, new DiscordOutreachProvenance(
                    request.Announcement, caller.AppId, caller.ActorKind, caller.ActorEntityId), draft, ct);
            admitted = true;
            var callback = await DiscordEndpoints.SubscribeRepliesAsync(
                request.NotifyReplies, caller, target, uid, subscriptions, logger, ct);
            var settledLead = await DiscordEndpoints.SettleLeadAsync(
                request, caller, target, callback, leadStore, logger, ct);
            var receipt = new DiscordOutreachReceipt(
                target.BindingId, target.ConversationName, target.SessionId!, uid, callback, settledLead);
            await store.SaveAsync(draft with { Receipt = receipt }, stored, ct);
            return receipt;
        }
        finally
        {
            if (!admitted && claimed is not null)
                await leadStore.RevertClaimAsync(binding, previousLead, CancellationToken.None);
        }
    }

    private async Task<DiscordConversationLead?> CheckLeadBeforeOutreachAsync(
        DiscordBinding binding, DiscordOutreachApproval draft, CancellationToken ct)
    {
        var lead = await ActiveLeadAsync(binding, ct);
        if (draft.Request.LeadConversation == true
            && lead is not null && lead.DiscussionId != draft.Caller.DiscussionId)
            throw new DiscordConversationLedElsewhereException(lead);
        return lead;
    }

    private async Task ValidatePrivateTargetAsync(DiscordBinding binding, CancellationToken ct)
    {
        var config = await botConfigurations.FindByApplicationIdAsync(binding.ApplicationId, ct);
        if (config is null || !config.Enabled || !binding.Enabled || binding.SessionId is null
            || binding.ConversationHandleId is null)
            throw OutreachStale();
        DiscordBotService.ValidateExistingBindingTarget(binding, config.DmAllowlist);
    }

    private void ValidatePreparedDraft(DiscordOutreachApproval draft)
    {
        var prepared = PrepareOutreach(draft.Request.Content.Trim(), draft.Request.DeliveryContent,
            (draft.Request.OutputAssets ?? []).Select(asset =>
                new ExternalConversationOutputAsset(asset.AssetId, asset.Description)).ToArray());
        if (prepared.Content != draft.Prepared.Content || prepared.DeliveryContent != draft.Prepared.DeliveryContent
            || prepared.HiddenCharacters != draft.Prepared.HiddenCharacters
            || !prepared.OutputAssets.SequenceEqual(draft.Prepared.OutputAssets))
            throw new DiscordOutreachConflictException("discord_outreach_egress_changed",
                "The current egress checks cannot send this exact Discord draft.");
    }

    private async Task<HttpResponseMessage> InjectReleasedOutreachAsync(
        DiscordBinding binding, DiscordOutreachApproval draft, HttpRequestMessage inject, CancellationToken ct)
    {
        var provenance = await DiscordComputeProvenance.CreateAsync(
            botConfigurations.EntityStore, binding.AgentId, binding.OwnerId,
            [new ComputeContextReference("external-conversation", binding.BindingId),
             new ComputeContextReference("session", draft.SessionId),
             new ComputeContextReference("discussion", draft.Caller.DiscussionId!)], draft.MessageUid, ct);
        return await compute.SendAsync(inject, provenance, ct);
    }

    private DiscordPreparedOutreach PreparePrivateOutreach(DiscordBindingMessageRequest request)
    {
        if (request.OutputAssets?.Any(asset => asset is null
            || string.IsNullOrWhiteSpace(asset.AssetId) || asset.Description is null) == true)
            throw new ArgumentException("Discord attachments need an Asset ID and description.");
        var assets = (request.OutputAssets ?? [])
            .Select(asset => new ExternalConversationOutputAsset(asset.AssetId, asset.Description)).ToArray();
        DiscordBotRuntime.ValidateExistingBindingMessage(
            request.Content, request.IdempotencyKey, request.DeliveryContent, assets);
        var prepared = PrepareOutreach(request.Content.Trim(), request.DeliveryContent, assets);
        if (string.IsNullOrWhiteSpace(prepared.Content)
            || (string.IsNullOrWhiteSpace(prepared.DeliveryContent) && prepared.OutputAssets.Count == 0))
            throw new ArgumentException("Discord outreach needs text or an attachment after egress checks.");
        return prepared;
    }

    private async Task<JsonObject> ReviewPrivateOutreachAsync(DiscordBinding binding,
        DiscordPreparedOutreach prepared, string key, CancellationToken ct)
    {
        try
        {
            return await ReviewOutgoingOrThrowAsync(
                await OutgoingContextAsync(binding, ct), prepared.DeliveryContent,
                prepared.OutputAssets, prepared.HiddenCharacters, $"outreach:{binding.BindingId}:{key}", ct);
        }
        catch (DiscordSentinelRefusedException ex)
        {
            return ex.Review;
        }
    }

    private static DiscordOutreachConflictException OutreachStale()
        => new("discord_binding_session_changed", "The original Discord binding session is no longer available.");

    private static DiscordOutreachConflictException OutreachDiscarded()
        => new("discord_outreach_discarded", "This Discord outreach was discarded.");
}
