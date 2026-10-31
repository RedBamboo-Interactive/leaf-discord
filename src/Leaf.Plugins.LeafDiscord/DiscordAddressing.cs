using System.Text.Json.Nodes;
using Discord;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// The message a Discord reply points at. Only the ID is known when Discord did not resolve
/// the referenced message (deleted, or not returned); the author and content are then null.
/// </summary>
public sealed record DiscordReplyReference(
    string MessageId,
    string? AuthorId = null,
    string? AuthorName = null,
    bool AuthorIsBot = false,
    bool? AuthorIsThisBot = null,
    string? Content = null);

/// <summary>Who an admitted message is addressed to, as the authenticated Gateway reports it.</summary>
public sealed record DiscordAddressing(
    bool Mentioned,
    bool RepliesToBot,
    bool OpenInvitation,
    DiscordReplyReference? ReplyTo);

/// <summary>
/// Builds the server-generated <c>addressing</c> transport metadata. The referenced author's
/// identity comes only from its authenticated snowflake through the same resolver as the
/// requestor's; names and content never establish it. The excerpt is untrusted content.
/// </summary>
internal static class DiscordAddressings
{
    public const string Schema = "leaf-discord-addressing/v1";
    /// <summary>Most characters of the referenced message carried as its excerpt.</summary>
    public const int MaxExcerptCharacters = 500;

    /// <summary>
    /// The message a reply points at, from what Discord already resolved. Nothing is fetched:
    /// an unresolved reference keeps only its ID.
    /// </summary>
    public static DiscordReplyReference? ReplyReference(IMessage message, ulong? botUserId)
    {
        if (message.Type != MessageType.Reply
            || message.Reference?.MessageId is not { IsSpecified: true } referencedId)
            return null;
        if (message is not IUserMessage { ReferencedMessage: { } referenced })
            return new DiscordReplyReference(referencedId.Value.ToString());
        return new DiscordReplyReference(
            referenced.Id.ToString(),
            referenced.Author.Id.ToString(),
            referenced.Author.GlobalName ?? referenced.Author.Username,
            referenced.Author.IsBot || referenced.Source == MessageSource.Webhook,
            botUserId is { } bot && referenced.Author.Id == bot,
            referenced.Content ?? "");
    }

    public static DiscordAddressing From(
        IUserMessage message, ulong botUserId, bool mentioned, bool repliesToBot,
        bool openInvitation)
        => new(mentioned, repliesToBot, openInvitation, ReplyReference(message, botUserId));

    /// <summary>Bounds untrusted referenced content without splitting a surrogate pair.</summary>
    public static string Excerpt(string content)
    {
        if (content.Length <= MaxExcerptCharacters) return content;
        var length = MaxExcerptCharacters - 1;
        if (char.IsHighSurrogate(content[length - 1])) length--;
        return content[..length] + "…";
    }

    /// <summary>
    /// The stored and transported form. A bot or webhook author is never resolved to a User.
    /// </summary>
    public static async Task<JsonObject> ToRecordAsync(
        DiscordAddressing addressing,
        Func<string, CancellationToken, Task<DiscordVerifiedLeafUser?>> resolveIdentity,
        CancellationToken ct = default)
    {
        JsonObject? replyTo = null;
        if (addressing.ReplyTo is { } reply)
        {
            var identity = reply.AuthorId is { } authorId && !reply.AuthorIsBot
                ? await resolveIdentity(authorId, ct)
                : null;
            replyTo = new JsonObject
            {
                ["message_id"] = reply.MessageId,
                ["author_name"] = reply.AuthorName,
                ["author_is_this_bot"] = reply.AuthorIsThisBot,
                ["author_verified_leaf_identity"] = IdentityJson(identity),
                ["excerpt"] = reply.Content is null ? null : Excerpt(reply.Content),
            };
        }
        return new JsonObject
        {
            ["schema"] = Schema,
            ["mentioned"] = addressing.Mentioned,
            ["replies_to_bot"] = addressing.RepliesToBot,
            ["open_invitation"] = addressing.OpenInvitation,
            ["reply_to"] = replyTo,
        };
    }

    private static JsonObject? IdentityJson(DiscordVerifiedLeafUser? user)
        => user is null ? null : new JsonObject
        {
            ["display_name"] = user.DisplayName,
            ["is_conversation_owner"] = user.IsConversationOwner,
        };
}
