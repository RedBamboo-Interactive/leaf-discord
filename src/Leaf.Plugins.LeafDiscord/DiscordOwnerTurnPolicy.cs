using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.LeafDiscord;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiscordOwnerTurnAsset(string AssetId, string Description);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiscordOwnerTurnPayload(
    Guid BindingId, long BindingGeneration, string Content, string? DeliveryContent,
    DiscordOwnerTurnAsset[] OutputAssets);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiscordOwnerTurnAdmission(
    int Version, string Operation, string IntentId, string PayloadSha256, DateTimeOffset ExpiresAt,
    string DiscussionId, string SessionId, string OwnerId, string AgentId, string QueueItemId,
    string MessageUid, string ConnectionId, string ThreadId, string TurnId, string CallId,
    DiscordOwnerTurnPayload Draft);

internal sealed record DiscordOwnerTurnResult(string Status, string? MessageUid = null);

/// <summary>Only stored in the existing protected envelope. A new call ID can retry the same turn intent.</summary>
internal sealed record DiscordOwnerTurnProof(
    string IntentId, string PayloadSha256, string DiscussionId, string SessionId, string OwnerId,
    string AgentId, string QueueItemId, string MessageUid, string ConnectionId,
    string ThreadId, string TurnId, string CallId, string OriginAgentRef)
{
    public bool MatchesIntent(DiscordOwnerTurnProof other) => this == (other with { CallId = CallId });
}

internal static partial class DiscordOwnerTurnPolicy
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private const string ServiceSubject = "service:redcompute";

    internal static bool IsRedComputeService(HttpContext http, IAuthContext auth)
    {
        // The kernel authenticates and verifies the suite badge before the plugin runs.
        var user = http.User;
        return user.Identity?.IsAuthenticated == true && user.Identity.AuthenticationType != "LocalDefault"
            && user.FindFirstValue("sub") == ServiceSubject && auth.UserId == ServiceSubject
            && user.FindFirstValue("client_id") == "redcompute" && auth.IsInRole("service")
            && user.FindFirstValue("iss") == "redsuite" && user.HasClaim("aud", "redsuite")
            && !user.HasClaim(claim => claim.Type is "token_use" or "sid")
            && http.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
            && !http.Request.Headers.ContainsKey("Origin");
    }

    internal static void Validate(DiscordOwnerTurnAdmission input, string? key, DateTimeOffset now)
    {
        if (input.Version != 1 || input.Operation != "discord.outreach.draft")
            throw new ArgumentException("Unsupported owner-turn draft contract.");
        ValidateExpiry(input.ExpiresAt, now);
        if (input.Draft is not { } draft || draft.BindingId == Guid.Empty || draft.BindingGeneration < 1
            || draft.Content is not { Length: > 0 and <= 16000 }
            || draft.DeliveryContent is { Length: > 16000 } || draft.OutputAssets is not { Length: <= 10 }
            || draft.OutputAssets.Any(asset => asset is null || !AssetId(asset.AssetId)
                || asset.Description is not { Length: <= 400 })
            || draft.OutputAssets.Select(asset => asset.AssetId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != draft.OutputAssets.Length
            || !Identifier(input.DiscussionId, 100) || !Identifier(input.SessionId, 100)
            || !CanonicalGuid(input.OwnerId) || !CanonicalGuid(input.AgentId)
            || !Identifier(input.QueueItemId, 100) || !input.QueueItemId.StartsWith("q_", StringComparison.Ordinal)
            || !Identifier(input.MessageUid, 256) || !Identifier(input.ConnectionId, 256)
            || !Identifier(input.ThreadId, 256) || !Identifier(input.TurnId, 256) || !Identifier(input.CallId, 256))
            throw new ArgumentException("Invalid owner-turn draft envelope.");

        var intent = Digest(new { input.SessionId, input.QueueItemId, draft.BindingId });
        if (!string.Equals(input.IntentId, key, StringComparison.Ordinal)
            || !string.Equals(input.IntentId, intent, StringComparison.Ordinal)
            || !string.Equals(input.PayloadSha256, Digest(draft), StringComparison.Ordinal))
            throw new ArgumentException("Owner-turn draft integrity check failed.");
    }

    internal static void ValidateExpiry(DateTimeOffset expires, DateTimeOffset now)
    {
        if (expires <= now || expires > now.AddMinutes(1))
            throw new UnauthorizedAccessException("Owner-turn draft attestation has expired or is outside its validity window.");
    }

    private static bool Identifier(string? value, int max)
        => value is { Length: > 0 } && value.Length <= max
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    private static bool CanonicalGuid(string? value)
        => Guid.TryParseExact(value, "D", out var guid) && guid != Guid.Empty;

    private static bool AssetId(string? id)
        => Identifier(id, 120) && !id!.Contains(':') && !id.Contains("..", StringComparison.Ordinal)
           && DiscordBotRuntime.TryAttachmentExtension(DiscordConversationCoordinator.AssetMediaType(id), out _);

    internal static string Digest<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Wire))).ToLowerInvariant();

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Nova canonically treats a missing confidential field as false on ordinary discussions.
    // The corresponding Compute mirror must still explicitly say false.
    internal static bool MatchesDiscussion(LeafEntity origin, DiscordOwnerTurnProof proof)
        => origin.TypeSlug == "discussion"
           && JsonRead.String(origin.Data, "app") == "nova"
           && JsonRead.String(origin.Data, "discussion_id") == proof.DiscussionId
           && JsonRead.String(origin.Data, "session_id") == proof.SessionId
           && SameId(JsonRead.String(origin.Data, "owner_id"), proof.OwnerId)
           && SameId(JsonRead.String(origin.Data, "agent"), proof.OriginAgentRef)
           && JsonRead.String(origin.Data, "type") is "chat" or "live"
           && JsonRead.String(origin.Data, "status") is "idle" or "thinking" or "starting" or "stopped"
           && (origin.Data["source_app"] is null || JsonRead.String(origin.Data, "source_app") == "nova")
           && origin.Data["automation_attempt_job_id"] is null
           && origin.Data["external_conversation_id"] is null
           && (origin.Data["confidential"] is null || IsFalse(origin.Data["confidential"]));

    internal static async Task ValidateOriginAsync(
        LeafEntity origin, DiscordOwnerTurnProof proof, IEntityStore entities, CancellationToken ct)
    {
        if (!MatchesDiscussion(origin, proof))
            throw InvalidOrigin();
        if (await entities.GetAsync(Guid.Parse(proof.OwnerId), ct) is not { TypeSlug: "user" } owner
            || JsonRead.String(owner.Data, "status") != "active")
            throw InvalidOrigin();
        var agents = new DiscordAgentResolver(entities);
        var agent = await agents.ResolveAsync(proof.OriginAgentRef, ct);
        if (agent is null || !SameId(agent.Id.ToString(), proof.AgentId)
            || JsonRead.String(agent.Data, "status") is "disabled" or "deleted" or "stopped"
            || IsFalse(agent.Data["enabled"]))
            throw InvalidOrigin();
        var sessions = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "ai-session",
            DataEquals = new Dictionary<string, object?> { ["session_id"] = proof.SessionId },
            Limit = 2,
        }, ct);
        if (sessions.Count != 1 || !MatchesSession(sessions[0].Data, proof))
            throw InvalidOrigin();
        var bindings = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = DiscordBindingStore.TypeSlug,
            DataEquals = new Dictionary<string, object?> { ["session_id"] = proof.SessionId },
            Limit = 1,
        }, ct);
        if (bindings.Count != 0) throw InvalidOrigin();
    }

    private static bool MatchesSession(JsonObject data, DiscordOwnerTurnProof proof)
        => JsonRead.String(data, "app") == "redcompute" && JsonRead.String(data, "provider") == "codex"
           && JsonRead.String(data, "active_provider") is null or "codex"
           && JsonRead.String(data, "session_id") == proof.SessionId
           && JsonRead.String(data, "external_session_id") == proof.ThreadId
           && SameId(JsonRead.String(data, "owner_id"), proof.OwnerId)
           && SameId(JsonRead.String(data, "user_id"), proof.OwnerId)
           && SameId(JsonRead.String(data, "owner_agent_id"), proof.AgentId)
           && IsFalse(data["confidential"])
           && data["dismissed"]?.GetValueKind() != JsonValueKind.True
           && JsonRead.String(data, "source") is not ("discord" or "leaf-discord");

    private static bool IsFalse(JsonNode? node) => node?.GetValueKind() == JsonValueKind.False;

    internal static UnauthorizedAccessException InvalidOrigin()
        => new("The private owner-turn origin is unavailable.");

    internal static void ValidateAssetReferences(DiscordPreparedOutreach prepared)
    {
        if (prepared.DeliveryContent.Contains("/api/assets/", StringComparison.OrdinalIgnoreCase)
            || prepared.OutputAssets.Any(asset => asset.Description.Contains("/api/assets/", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Discord delivery must attach typed Leaf Assets without private asset URLs.");
        var references = AssetReferences().Matches(prepared.Content);
        var prefixCount = AssetPrefix().Matches(prepared.Content).Count;
        if (prefixCount != references.Count || references.Any(match =>
                prepared.OutputAssets.All(asset => asset.AssetId != match.Groups[1].Value)))
            throw new ArgumentException("Canonical Leaf asset references must match the typed Discord attachments.");
    }

    internal static async Task ValidateAssetsAsync(DiscordPreparedOutreach prepared, string ownerId,
        long maxBytes, IAssets? assets, bool readBytes, CancellationToken ct)
    {
        foreach (var asset in prepared.OutputAssets)
        {
            if (assets is null) throw new InvalidOperationException("Asset authority is unavailable.");
            var record = await assets.GetRecordAsync(asset.AssetId, ct);
            var mediaType = DiscordConversationCoordinator.AssetMediaType(asset.AssetId);
            if (record is null || record.AssetId != asset.AssetId || record.Confidential
                || record.Provenance.Assurance != ComputeProvenanceAssurance.Verified
                || record.Provenance.OnBehalfOf.Kind != "user"
                || !SameId(record.Provenance.OnBehalfOf.Id, ownerId)
                || record.OwnerId is { } assetOwner && !SameId(assetOwner, ownerId)
                || record.SizeBytes < 1 || record.SizeBytes > maxBytes
                || record.ContentType is { Length: > 0 } type && type != "application/octet-stream" && type != mediaType)
                throw new UnauthorizedAccessException("An attachment is unavailable for owner-turn outreach.");
            if (readBytes)
            {
                var file = await assets.ReadAsync(asset.AssetId, ct);
                if (file is null || file.Bytes.LongLength != record.SizeBytes
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(file.Bytes)), record.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("An attachment is unavailable for owner-turn outreach.");
            }
        }
    }

    [GeneratedRegex(@"/api/assets/([A-Za-z0-9_.-]+)(?=$|[\s)\]>'""])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetReferences();

    [GeneratedRegex(@"/api/assets/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetPrefix();
}
