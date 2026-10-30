using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Projects a Discord binding's canonical Agent transcript for the owner transcript view:
/// user turns become the human Discord text plus typed author, Sentinel, and identity context.
/// Two user record shapes exist. Legacy records hold the full model envelope
/// (<c>&lt;discord-input-json&gt;</c>). Records written since RedCompute kept display text
/// hold only the raw Discord text; their <c>attachmentsJson</c> still carries the Discord
/// message IDs and Sentinel review, and authorship comes from the bridge's inbound store.
/// A leading channel-gap block (earlier unseen channel messages the session received ahead
/// of the turn) is removed, so each turn shows only what its author wrote. An assistant reply
/// carries its outbound Sentinel review as <c>metadata.discord.outboundSentinel</c>.
/// </summary>
internal static class DiscordTranscriptProjection
{
    private const string ReviewTag = "sentinel-review-json";
    private const string EnvelopeTag = "discord-input-json";
    private const string AppName = "leaf-discord";
    private const string IdentityAuthority =
        "identity-only; Discord is not a private Leaf approval surface";

    public static async Task<IReadOnlyList<ExternalConversationMessage>> ProjectAsync(
        IReadOnlyList<ExternalConversationMessage> messages,
        Func<string, CancellationToken, Task<DiscordUserProfile?>> resolveProfile,
        Func<string, CancellationToken, Task<DiscordInboundAttribution?>>? resolveInbound = null,
        CancellationToken ct = default,
        Func<string, CancellationToken, Task<JsonObject?>>? resolveOutboundReview = null,
        IReadOnlySet<string>? unpostedReplies = null)
    {
        var projected = new List<ExternalConversationMessage>(messages.Count);
        var profiles = new Dictionary<string, DiscordUserProfile?>(StringComparer.Ordinal);
        var inbound = new Dictionary<string, DiscordInboundAttribution?>(StringComparer.Ordinal);

        async Task<DiscordUserProfile?> Profile(string externalId)
        {
            if (!profiles.TryGetValue(externalId, out var profile))
            {
                profile = await resolveProfile(externalId, ct);
                profiles[externalId] = profile;
            }
            return profile;
        }

        async Task<DiscordInboundAttribution?> Inbound(string discordMessageId)
        {
            if (resolveInbound is null) return null;
            if (!inbound.TryGetValue(discordMessageId, out var attribution))
            {
                attribution = await resolveInbound(discordMessageId, ct);
                inbound[discordMessageId] = attribution;
            }
            return attribution;
        }

        foreach (var message in messages)
        {
            if (message.Role == "assistant"
                && DiscordControlMessages.IsNoReply(message.Content))
                continue;

            // The session wrote it while a private discussion led the conversation, so the
            // bridge kept it off Discord. The owner must not read it as something the person saw.
            if (message.Role == "assistant" && message.Kind == "text"
                && unpostedReplies?.Contains(message.MessageUid) == true)
            {
                var metadata = message.Metadata?.DeepClone() as JsonObject ?? new JsonObject();
                metadata["discord"] = new JsonObject
                {
                    ["conversationLead"] = new JsonObject
                    {
                        ["posted"] = false,
                        ["reason"] = "owner_discussion_led",
                    },
                };
                projected.Add(message with { Metadata = metadata });
                continue;
            }

            if (message.Role == "assistant" && message.Kind == "text"
                && resolveOutboundReview is not null
                && await resolveOutboundReview(message.MessageUid, ct) is { } outbound)
            {
                var metadata = message.Metadata?.DeepClone() as JsonObject ?? new JsonObject();
                metadata["discord"] = new JsonObject
                {
                    ["outboundSentinel"] = new JsonObject
                    {
                        ["outcome"] = outbound["outcome"]?.DeepClone(),
                        ["risk"] = outbound["risk"]?.DeepClone(),
                        ["signals"] = outbound["signals"]?.DeepClone(),
                        ["handling"] = outbound["handling"]?.DeepClone(),
                        ["available"] = outbound["available"]?.DeepClone(),
                    },
                };
                projected.Add(message with { Metadata = metadata });
                continue;
            }

            var parsed = TryParseEnvelope(message);
            if (parsed?.ExternalId is { } envelopeAuthor)
            {
                var profile = await Profile(envelopeAuthor);
                projected.Add(message with
                {
                    Content = DiscordChannelGaps.StripForDisplay(parsed.Content),
                    Metadata = WithDiscord(message, parsed, profile),
                });
                continue;
            }

            var record = TryParseDisplayRecord(message);
            if (record is null)
            {
                projected.Add(message);
                continue;
            }
            var inputs = record.Inputs;

            var attributions = new DiscordInboundAttribution?[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
                attributions[i] = await Inbound(inputs[i].DiscordMessageId);

            var segments = Split(message.Content ?? "", attributions);
            if (segments is null)
            {
                // A batched record whose parts cannot be separated stays one turn. It is
                // attributed only when every resolvable part has the same author.
                var authors = attributions.Where(item => item is not null)
                    .Select(item => item!.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
                var author = authors.Length == 1
                    ? attributions.First(item => item is not null)
                    : null;
                var attachmentCount = inputs.Count == 1
                    ? author?.AttachmentCount ?? record.AttachmentCount
                    : record.AttachmentCount;
                var display = DiscordChannelGaps.StripForDisplay(message.Content ?? "");
                projected.Add(message with
                {
                    Content = display,
                    Metadata = WithDiscord(message,
                        ToParsed(inputs[0], author, display, attachmentCount),
                        author is null ? null : await Profile(author.ExternalId)),
                });
                continue;
            }

            for (var i = 0; i < inputs.Count; i++)
            {
                var author = attributions[i]!;
                var display = DiscordChannelGaps.StripForDisplay(segments[i]);
                var part = message with
                {
                    MessageUid = inputs[i].InputMessageUid ?? $"{message.MessageUid}#{i}",
                    Content = display,
                };
                projected.Add(part with
                {
                    Metadata = WithDiscord(part,
                        ToParsed(inputs[i], author, display, author.AttachmentCount ?? 0),
                        await Profile(author.ExternalId)),
                });
            }
        }
        return projected;
    }

    private static JsonObject WithDiscord(
        ExternalConversationMessage message, ParsedDiscordInput parsed,
        DiscordUserProfile? profile)
    {
        var metadata = message.Metadata?.DeepClone() as JsonObject ?? new JsonObject();
        metadata["discord"] = new JsonObject
        {
            ["messageId"] = parsed.DiscordMessageId,
            ["requestor"] = parsed.ExternalId is null ? null : new JsonObject
            {
                ["externalId"] = parsed.ExternalId,
                ["displayName"] = parsed.DisplayName,
                ["username"] = parsed.Username,
                ["avatarUrl"] = profile?.AvatarUrl,
            },
            ["sentinel"] = new JsonObject
            {
                ["risk"] = parsed.Risk,
                ["signals"] = parsed.Signals?.DeepClone(),
                ["handling"] = parsed.Handling,
                ["available"] = parsed.Available,
            },
            ["verifiedLeafIdentity"] = parsed.VerifiedDisplayName is null ? null : new JsonObject
            {
                ["displayName"] = parsed.VerifiedDisplayName,
                ["isConversationOwner"] = parsed.IsConversationOwner,
                ["authority"] = parsed.IdentityAuthority,
            },
            ["attachmentCount"] = parsed.AttachmentCount,
        };
        return metadata;
    }

    private static ParsedDiscordInput ToParsed(
        DisplayInput input, DiscordInboundAttribution? author, string content,
        int attachmentCount)
        => new(
            author?.ExternalId,
            author?.DisplayName,
            author?.Username,
            content,
            input.Risk,
            input.Signals,
            input.Handling,
            input.Available,
            author?.VerifiedDisplayName,
            author?.VerifiedDisplayName is null ? null : author.IsConversationOwner,
            author?.VerifiedDisplayName is null ? null : IdentityAuthority,
            attachmentCount,
            input.DiscordMessageId);

    /// <summary>
    /// RedCompute joins a batch's display texts with a single newline. Separate them only when
    /// every part's admitted length is known and the separators line up exactly.
    /// </summary>
    private static string[]? Split(string content, IReadOnlyList<DiscordInboundAttribution?> parts)
    {
        if (parts.Count == 1)
            return parts[0] is null ? null : [content];
        if (parts.Any(part => part?.ContentLength is null)) return null;
        var lengths = parts.Select(part => part!.ContentLength!.Value).ToArray();
        if (lengths.Sum() + parts.Count - 1 != content.Length) return null;
        var segments = new string[parts.Count];
        var offset = 0;
        for (var i = 0; i < lengths.Length; i++)
        {
            segments[i] = content.Substring(offset, lengths[i]);
            offset += lengths[i];
            if (i < lengths.Length - 1)
            {
                if (content[offset] != '\n') return null;
                offset++;
            }
        }
        return segments;
    }

    private static DisplayRecord? TryParseDisplayRecord(
        ExternalConversationMessage message)
    {
        if (!string.Equals(message.Role, "user", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(message.AttachmentsJson))
            return null;
        try
        {
            if (JsonNode.Parse(message.AttachmentsJson) is not JsonObject envelope)
                return null;
            var metadata = envelope["metadata"] switch
            {
                JsonObject single => [single],
                JsonArray many => many.OfType<JsonObject>().ToArray(),
                _ => Array.Empty<JsonObject>(),
            };
            var uids = (envelope["inputMessageUids"] as JsonArray)?
                .Select(node => node is JsonValue value
                    && value.TryGetValue<string>(out var uid) ? uid : null)
                .ToArray() ?? [];
            // RedCompute drops inputs without metadata from this list, so per-input UIDs are
            // only trusted when both lists describe the same inputs.
            var alignedUids = uids.Length == metadata.Length;

            var inputs = new List<DisplayInput>(metadata.Length);
            for (var i = 0; i < metadata.Length; i++)
            {
                var item = metadata[i];
                if (!string.Equals(String(item, "app"), AppName, StringComparison.Ordinal))
                    return null;
                var discordMessageId = String(item, "externalRequestId", "external_request_id");
                if (discordMessageId is null) return null;
                var review = Node(item, "sentinel", "Sentinel") as JsonObject;
                inputs.Add(new DisplayInput(
                    discordMessageId,
                    alignedUids ? uids[i] : i == 0 ? message.MessageUid : null,
                    String(review, "Risk", "risk"),
                    Node(review, "Signals", "signals") as JsonArray,
                    String(review, "Handling", "handling"),
                    Bool(review, "Available", "available")));
            }
            return inputs.Count == 0
                ? null
                : new DisplayRecord(inputs, (envelope["attachments"] as JsonArray)?.Count ?? 0);
        }
        catch
        {
            return null;
        }
    }

    private static ParsedDiscordInput? TryParseEnvelope(ExternalConversationMessage message)
    {
        if (!string.Equals(message.Role, "user", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(message.Content))
            return null;
        var envelopeJson = ExtractTag(message.Content, EnvelopeTag);
        if (envelopeJson is null) return null;
        try
        {
            var envelope = JsonNode.Parse(envelopeJson) as JsonObject;
            var requestor = Node(envelope, "requestor") as JsonObject;
            var externalId = String(requestor, "ExternalId", "externalId", "id");
            var displayName = String(requestor, "DisplayName", "displayName", "display_name");
            var content = String(envelope, "message");
            if (externalId is null || displayName is null || content is null) return null;

            JsonObject? review = null;
            var reviewJson = ExtractTag(message.Content, ReviewTag);
            if (reviewJson is not null)
                review = JsonNode.Parse(reviewJson) as JsonObject;
            var attachments = Node(envelope, "attachments") as JsonArray;
            var verifiedIdentity = Node(
                envelope, "verifiedLeafIdentity", "verified_leaf_identity") as JsonObject;
            return new ParsedDiscordInput(
                externalId,
                displayName,
                String(requestor, "Username", "username"),
                content,
                String(review, "Risk", "risk"),
                Node(review, "Signals", "signals") as JsonArray,
                String(review, "Handling", "handling"),
                Bool(review, "Available", "available"),
                String(verifiedIdentity, "displayName", "display_name"),
                Bool(verifiedIdentity, "isConversationOwner", "is_conversation_owner"),
                String(verifiedIdentity, "authority"),
                attachments?.Count ?? 0,
                String(envelope, "messageId", "message_id"));
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractTag(string content, string tag)
    {
        var opening = $"<{tag}>";
        var closing = $"</{tag}>";
        var start = content.IndexOf(opening, StringComparison.Ordinal);
        if (start < 0) return null;
        start += opening.Length;
        var end = content.IndexOf(closing, start, StringComparison.Ordinal);
        return end < 0 ? null : content[start..end].Trim();
    }

    private static JsonNode? Node(JsonObject? value, params string[] names)
    {
        if (value is null) return null;
        foreach (var name in names)
            if (value.TryGetPropertyValue(name, out var node)) return node;
        return null;
    }

    private static string? String(JsonObject? value, params string[] names)
    {
        var node = Node(value, names);
        return node is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool? Bool(JsonObject? value, params string[] names)
    {
        var node = Node(value, names);
        return node is JsonValue json && json.TryGetValue<bool>(out var result) ? result : null;
    }

    private sealed record DisplayRecord(IReadOnlyList<DisplayInput> Inputs, int AttachmentCount);

    private sealed record DisplayInput(
        string DiscordMessageId,
        string? InputMessageUid,
        string? Risk,
        JsonArray? Signals,
        string? Handling,
        bool? Available);

    private sealed record ParsedDiscordInput(
        string? ExternalId,
        string? DisplayName,
        string? Username,
        string Content,
        string? Risk,
        JsonArray? Signals,
        string? Handling,
        bool? Available,
        string? VerifiedDisplayName,
        bool? IsConversationOwner,
        string? IdentityAuthority,
        int AttachmentCount,
        string? DiscordMessageId);
}
