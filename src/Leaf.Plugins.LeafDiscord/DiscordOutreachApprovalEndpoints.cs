using System.Security.Claims;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.LeafDiscord;

public static partial class DiscordEndpoints
{
    private static void MapOutreachApprovals(RouteGroupBuilder group)
    {
        group.MapGet("/outreach/approvals", async (
            string discussionId, HttpContext http, IAuthContext auth,
            DiscordOutreachApprovalStore approvals, DiscordBindingStore bindings, CancellationToken ct) =>
        {
            if (!CanApprovePrivateOutreach(http, auth)) return Forbidden("Sign in as the owner to review Discord outreach.");
            var origin = await approvals.FindOriginAsync(discussionId, ct);
            if (!CanManageOutreachOrigin(http, auth, origin)) return Results.NotFound();
            var items = new List<object>();
            foreach (var draft in await approvals.ListOpenAsync(origin!.Id, ct))
            {
                if (!DiscordConversationCoordinator.OwnsOutreach(origin, draft)) continue;
                var binding = await bindings.GetAsync(draft.BindingEntityId, ct);
                items.Add(new
                {
                    draft.BindingEntityId,
                    draft.MessageUid,
                    draft.ChannelId,
                    draft.ChannelName,
                    draft.ChannelKind,
                    draft.GuildId,
                    draft.GuildName,
                    draft.Generation,
                    draft.CreatedAt,
                    draft.Prepared.Content,
                    draft.Prepared.DeliveryContent,
                    deliveryChunks = DiscordMessageChunker.Split(draft.Prepared.DeliveryContent),
                    draft.Prepared.OutputAssets,
                    sentinel = draft.Review,
                    canRelease = binding is not null && draft.Matches(binding),
                    canDiscard = draft.ReleasedAt is null,
                    releasePending = draft.ReleasedAt is not null,
                });
            }
            return Results.Ok(new { items });
        }).WithSummary("Lists open Discord outreach approvals for one active private Nova discussion. Human owner or administrator only.");

        group.MapPost("/bindings/{id:guid}/messages/{messageUid}/discard", async (
            Guid id, string messageUid, string discussionId, HttpContext http, IAuthContext auth,
            DiscordOutreachApprovalStore approvals, DiscordBindingStore bindings,
            DiscordConversationCoordinator conversations, CancellationToken ct) =>
        {
            if (!CanApprovePrivateOutreach(http, auth)) return Forbidden("Sign in as the owner to discard held Discord outreach.");
            var origin = await approvals.FindOriginAsync(discussionId, ct);
            if (!CanManageOutreachOrigin(http, auth, origin)) return Results.NotFound();
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            try
            {
                var earlier = await conversations.DiscardPrivateOutreachAsync(binding, origin!, messageUid, auth.UserId!, ct);
                return earlier is null
                    ? Results.NotFound()
                    : Results.Ok(new { messageUid, discarded = true, alreadyDiscarded = earlier.Value });
            }
            catch (Exception ex) when (IsOutreachEndpointFailure(ex))
            {
                return OutreachEndpointFailure(ex);
            }
        }).WithSummary("Discards an open outreach draft from its originating private Nova discussion. Human owner or administrator only.");
    }

    internal static bool CanManageOutreachOrigin(HttpContext http, IAuthContext auth, LeafEntity? origin)
        => origin is not null && CanApprovePrivateOutreach(http, auth) && SameOwner(origin, auth.UserId);

    internal static bool CanApprovePrivateOutreach(HttpContext http, IAuthContext auth)
    {
        // Like RedLeaf's person-only actions, require an authenticated user session.
        // A loopback request with no credentials can otherwise inherit the admin role.
        var user = http.User;
        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return CanReleaseHeldReply(http, auth)
            && user.Identity?.IsAuthenticated == true
            && !string.Equals(user.Identity.AuthenticationType, "LocalDefault", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(user.FindFirstValue("sid"))
            && !user.HasClaim(claim => claim.Type == "token_use")
            && string.Equals(subject, auth.UserId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subject, "system", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subject, "local-user", StringComparison.OrdinalIgnoreCase)
            && subject?.StartsWith("service:", StringComparison.OrdinalIgnoreCase) == false;
    }

    private static bool SameOwner(LeafEntity origin, string? userId)
        => string.Equals(JsonRead.String(origin.Data, "owner_id"), userId, StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> SubmitPrivateOutreachAsync(
        DiscordBindingMessageRequest request, LeafAgentCaller caller, string discussionId,
        DiscordBinding binding, DiscordOutreachApprovalStore approvals,
        DiscordConversationCoordinator conversations, DiscordReplySubscriptionStore subscriptions,
        DiscordBotService bot, DiscordAgentResolver agents, CancellationToken ct)
    {
        var origin = await approvals.FindOriginAsync(discussionId, ct);
        if (origin is null || !SameOwner(origin, caller.BeneficiaryId)
            || JsonRead.String(origin.Data, "agent") is not { } agentRef
            || (await agents.ResolveAsync(agentRef, ct))?.Id.ToString() is not { } agentId
            || !string.Equals(agentId, caller.ActorEntityId, StringComparison.OrdinalIgnoreCase))
            return Forbidden("This Agent cannot submit Discord outreach from that private discussion.");
        try
        {
            var result = await conversations.SubmitPrivateOutreachAsync(
                binding, origin, caller, request, subscriptions, ct, token => bot.RequireConnectedAsync(binding, token));
            await bot.DrainApprovedOutreachAsync(binding, ct);
            return Results.Ok(new
            {
                ok = true,
                result.BindingId,
                result.ConversationName,
                result.SessionId,
                result.MessageUid,
                replyCallback = result.ReplyCallback is { } callback
                    ? new { callback.DiscussionId, callback.ExpiresAt }
                    : null,
                result.ConversationLead,
            });
        }
        catch (Exception ex) when (IsOutreachEndpointFailure(ex))
        {
            return OutreachEndpointFailure(ex);
        }
    }

    private static bool IsOutreachEndpointFailure(Exception ex)
        => ex is DiscordOutreachConflictException or DiscordConversationLedElsewhereException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException;

    private static IResult OutreachEndpointFailure(Exception ex)
        => ex switch
        {
            DiscordOutreachConflictException conflict => Results.Conflict(new { error = conflict.Code, message = conflict.Message }),
            DiscordConversationLedElsewhereException led => Results.Conflict(new
            {
                error = "discord_conversation_led_elsewhere",
                message = led.Message,
                conversationLead = DiscordConversationLeadStore.Project(led.Lead, DateTimeOffset.UtcNow),
            }),
            UnauthorizedAccessException => Forbidden(ex.Message),
            ArgumentException => Results.BadRequest(new { error = "invalid_message", message = ex.Message }),
            _ => Results.Json(new { error = "delivery_unavailable", message = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
}
