using System.Text.Json.Nodes;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Who authored one admitted Discord message. The canonical Agent transcript keeps only the
/// human display text and the Discord message ID, so the bridge retains this small record on
/// the admitted inbound delivery to attribute transcript turns later.
/// </summary>
public sealed record DiscordInboundAttribution(
    string ExternalId,
    string DisplayName,
    string? Username,
    string? VerifiedDisplayName = null,
    bool? IsConversationOwner = null,
    int? AttachmentCount = null,
    int? ContentLength = null);

internal static class DiscordInboundAttributions
{
    /// <summary>Derives the retained attribution from a received inbound payload.</summary>
    public static JsonObject? FromPayload(JsonNode? payload)
    {
        if (payload is not JsonObject value
            || value["requestor"] is not JsonObject requestor)
            return null;
        var externalId = JsonRead.String(requestor, "id");
        var displayName = JsonRead.String(requestor, "display_name");
        if (externalId is null || displayName is null) return null;
        var verified = value["verified_leaf_user"] as JsonObject;
        var content = value["content"] is JsonValue text
                      && text.TryGetValue<string>(out var raw)
            ? raw
            : null;
        return new JsonObject
        {
            ["requestor"] = new JsonObject
            {
                ["id"] = externalId,
                ["display_name"] = displayName,
                ["username"] = JsonRead.String(requestor, "username"),
            },
            ["verified_leaf_user"] = verified is null ? null : new JsonObject
            {
                ["display_name"] = JsonRead.String(verified, "display_name"),
                ["is_conversation_owner"] = verified["is_conversation_owner"] is JsonValue owner
                    && owner.TryGetValue<bool>(out var isOwner) && isOwner,
            },
            ["attachment_count"] = (value["attachments"] as JsonArray)?.Count ?? 0,
            // The session received any channel gap block ahead of the message itself.
            ["content_length"] = content is null
                ? null
                : (DiscordChannelGaps.Prefix(value["channel_gap"])?.Length ?? 0) + content.Length,
        };
    }

    public static DiscordInboundAttribution? Parse(JsonNode? node)
    {
        if (node is not JsonObject value
            || value["requestor"] is not JsonObject requestor)
            return null;
        var externalId = JsonRead.String(requestor, "id");
        var displayName = JsonRead.String(requestor, "display_name");
        if (externalId is null || displayName is null) return null;
        var verified = value["verified_leaf_user"] as JsonObject;
        var verifiedName = verified is null ? null : JsonRead.String(verified, "display_name");
        var contentLength = JsonRead.Long(value, "content_length", -1);
        return new DiscordInboundAttribution(
            externalId,
            displayName,
            JsonRead.String(requestor, "username"),
            verifiedName,
            verifiedName is null ? null : JsonRead.Bool(verified!, "is_conversation_owner"),
            checked((int)Math.Max(0, JsonRead.Long(value, "attachment_count", 0))),
            contentLength < 0 ? null : checked((int)contentLength));
    }
}
