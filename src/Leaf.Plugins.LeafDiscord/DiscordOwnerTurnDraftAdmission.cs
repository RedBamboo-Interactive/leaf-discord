using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed partial class DiscordConversationCoordinator
{
    internal async Task<DiscordOwnerTurnResult> SubmitOwnerTurnDraftAsync(
        DiscordOwnerTurnAdmission input, string? key, CancellationToken ct)
    {
        DiscordOwnerTurnPolicy.Validate(input, key, clock.GetUtcNow());
        var store = OutreachApprovals
            ?? throw new InvalidOperationException("Discord outreach approval storage is unavailable.");
        var origin = await store.FindOriginAsync(input.DiscussionId, ct)
            ?? throw DiscordOwnerTurnPolicy.InvalidOrigin();
        var agentRef = JsonRead.String(origin.Data, "agent")
            ?? throw DiscordOwnerTurnPolicy.InvalidOrigin();
        var proof = new DiscordOwnerTurnProof(input.IntentId, input.PayloadSha256, input.DiscussionId,
            input.SessionId, input.OwnerId, input.AgentId, input.QueueItemId, input.MessageUid,
            input.ConnectionId, input.ThreadId, input.TurnId, input.CallId, agentRef);
        var caller = new LeafAgentCaller(proof.AgentId, proof.OwnerId) { DiscussionId = proof.DiscussionId };
        var payload = input.Draft;
        var request = new DiscordBindingMessageRequest(payload.Content, "owner-turn-v1-" + proof.IntentId,
            payload.DeliveryContent, payload.OutputAssets.Select(asset =>
                new DiscordBindingOutputAssetRequest(asset.AssetId, asset.Description)).ToArray(),
            NotifyReplies: false);
        var prepared = PreparePrivateOutreach(request);
        DiscordOwnerTurnPolicy.ValidateAssetReferences(prepared);
        var binding = await bindings.GetAsync(payload.BindingId, ct) ?? throw OutreachStale();
        ValidateOwnerTurnBinding(binding, payload.BindingGeneration);
        await DiscordOwnerTurnPolicy.ValidateOriginAsync(origin, proof, botConfigurations.EntityStore, ct);
        await ValidateOwnerTurnTargetAsync(binding, caller, prepared, readBytes: true, ct);

        var messageUid = OutreachMessageUid(binding, request.IdempotencyKey);
        var review = await store.FindByKeyAsync(binding.EntityId, request.IdempotencyKey, ct) is null
            ? await ReviewPrivateOutreachAsync(binding, prepared, request.IdempotencyKey, ct) : null;

        var gate = gates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            DiscordOwnerTurnPolicy.ValidateExpiry(input.ExpiresAt, clock.GetUtcNow());
            var active = await store.FindOriginAsync(proof.DiscussionId, ct);
            if (active?.Id != origin.Id) throw DiscordOwnerTurnPolicy.InvalidOrigin();
            await DiscordOwnerTurnPolicy.ValidateOriginAsync(active, proof, botConfigurations.EntityStore, ct);

            var current = await bindings.GetAsync(binding.EntityId, ct) ?? throw OutreachStale();
            ValidateOwnerTurnBinding(current, payload.BindingGeneration);
            if (current.SessionId != binding.SessionId || current.ConversationHandleId != binding.ConversationHandleId
                || current.BindingId != binding.BindingId || current.GuildId != binding.GuildId
                || current.ConversationId != binding.ConversationId || current.ApplicationId != binding.ApplicationId
                || current.Kind != binding.Kind)
                throw OutreachStale();
            await ValidateOwnerTurnTargetAsync(current, caller, prepared, readBytes: false, ct);

            var stored = await store.FindByKeyAsync(current.EntityId, request.IdempotencyKey, ct);
            if (stored is null)
            {
                if (review is null)
                    throw new InvalidOperationException("The owner-turn draft reservation changed. Retry the same intent.");
                var clear = JsonRead.String(review, "outcome") == "delivered";
                var draft = new DiscordOutreachApproval(
                    current.EntityId, current.BindingId, current.Generation, current.SessionId!, current.ConversationHandleId!,
                    current.AgentId, current.ConversationId, current.ConversationName, current.GuildId, current.GuildName,
                    current.Kind, origin.Id, caller,
                    clear ? request with { Content = "", DeliveryContent = null, OutputAssets = [] } : request,
                    clear ? new DiscordPreparedOutreach("", "", [], false) : prepared,
                    review, messageUid, clock.GetUtcNow())
                {
                    OwnerTurn = proof,
                };
                DiscordOwnerTurnPolicy.ValidateExpiry(input.ExpiresAt, clock.GetUtcNow());
                stored = await store.TrySaveAsync(draft, null, ct)
                    ?? await store.FindByKeyAsync(current.EntityId, request.IdempotencyKey, ct)
                    ?? throw new InvalidOperationException("The owner-turn draft reservation changed. Retry the same intent.");
            }

            var saved = stored.Draft;
            if (saved.OriginEntityId != origin.Id || saved.OwnerTurn?.MatchesIntent(proof) != true)
                throw new DiscordOutreachConflictException("idempotency_key_conflict",
                    "This intent already belongs to different owner-turn outreach.");
            if (!saved.Matches(current)) throw OutreachStale();
            if (saved.DiscardedAt is not null) throw OutreachDiscarded();
            // A clear review is a closed idempotency receipt in the protected store. It never
            // becomes an open draft and neither outcome reaches the transcript or outbox here.
            return saved.NeedsApproval ? new("held", saved.MessageUid) : new("clear");
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ValidateOwnerTurnBinding(DiscordBinding binding, long generation)
    {
        if (!binding.Enabled || string.IsNullOrWhiteSpace(binding.SessionId)
            || string.IsNullOrWhiteSpace(binding.ConversationHandleId) || binding.Generation != generation
            || binding.Kind is not ("dm" or "guild_channel" or "thread"))
            throw OutreachStale();
    }

    private async Task ValidateOwnerTurnTargetAsync(DiscordBinding binding, LeafAgentCaller caller,
        DiscordPreparedOutreach prepared, bool readBytes, CancellationToken ct)
    {
        var config = await botConfigurations.FindByApplicationIdAsync(binding.ApplicationId, ct);
        if (config is null || !config.Enabled) throw OutreachStale();
        var agents = new DiscordAgentResolver(botConfigurations.EntityStore);
        if (!DiscordEndpoints.CanAddressBinding(caller, binding, (await agents.ResolveAsync(binding.AgentId, ct))?.Id.ToString())
            || !DiscordEndpoints.CanAddressBot(caller, config, (await agents.ResolveAsync(config.AgentId, ct))?.Id.ToString()))
            throw new UnauthorizedAccessException("The owner turn cannot address that Discord binding.");
        DiscordBotRuntime.ValidateExistingBindingTarget(binding, config.DmAllowlist);
        await DiscordOwnerTurnPolicy.ValidateAssetsAsync(prepared, caller.BeneficiaryId, config.MaxAttachmentBytes,
            ownerTurnAssets, readBytes, ct);
    }
}
