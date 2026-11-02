using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>A message as the reviewed read returns it. Text, names and filenames are null when withheld.</summary>
internal sealed record DiscordReviewedMessageView(
    string Id,
    DateTimeOffset Timestamp,
    DateTimeOffset? EditedTimestamp,
    string AuthorId,
    string? AuthorName,
    string? Username,
    bool AuthorIsBot,
    string Source,
    string? Content,
    IReadOnlyList<DiscordReviewedAttachmentView> Attachments);

internal sealed record DiscordReviewedAttachmentView(
    string Id, string? Filename, string? ContentType, int Size);

/// <param name="Sentinel">The verdict object returned to the caller, shaped like Mail's <c>sentinel</c>.</param>
/// <param name="Truncated">Messages after <paramref name="Messages"/> were left out to keep the
/// review within the Sentinel's bound; read on from the last returned message ID.</param>
internal sealed record DiscordReviewedRead(
    IReadOnlyList<DiscordReviewedMessageView> Messages,
    JsonObject Sentinel,
    bool Withheld,
    bool Truncated);

/// <summary>
/// Reviews the Discord text a private Agent session reads through the shared Sentinel
/// (<see cref="IUntrustedContentReview"/>) before any of it is returned, the way Mail reviews
/// what it returns. One review covers the whole response. Fail closed: with no Sentinel, no
/// verdict, or text the deterministic screen could not cover, nothing is returned
/// (<see cref="ReviewAsync"/> yields null and the route answers 503 <c>sentinel_unavailable</c>).
/// A high-risk verdict (or <c>require_private_approval</c>) returns message metadata only.
/// </summary>
internal sealed class DiscordChannelReadReviewer(
    IUntrustedContentReview? review, ILogger<DiscordChannelReadReviewer>? logger = null)
{
    /// <summary>The Sentinel reviews at most this many characters (its own review bound).</summary>
    internal const int ReviewBound = 200_000;

    public async Task<DiscordReviewedRead?> ReviewAsync(
        IReadOnlyList<DiscordGuildMessageView> messages,
        string sourceRef,
        string? ownerUserId,
        string agentRef,
        ComputeProvenance? provenance,
        CancellationToken ct)
    {
        // Nothing to read means nothing to review: no Sentinel call, and none needed to answer.
        if (messages.Count == 0)
            return new DiscordReviewedRead([], new JsonObject { ["reviewed"] = false }, false, false);
        if (review is null) return null;

        var included = new List<DiscordGuildMessageView>();
        var block = new StringBuilder();
        foreach (var message in messages)
        {
            var section = Section(message);
            if (included.Count > 0 && block.Length + section.Length > ReviewBound) break;
            if (section.Length > ReviewBound) return null;
            block.Append(section);
            included.Add(message);
        }
        var content = block.ToString();

        UntrustedContentReviewResult result;
        try
        {
            result = await review.ReviewAsync(new UntrustedContentReviewRequest
            {
                Content = content,
                Origin = new UntrustedContentOrigin(
                    UntrustedContentSources.Discord, sourceRef, ownerUserId, LeafDiscordPlugin.PluginId),
                AgentRef = agentRef,
                ComputeProvenance = provenance,
                IdempotencyKey = $"discord-read-{Hash(content)}",
                JobName = "Discord channel read review",
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger?.LogWarning("Discord channel read review failed ({Type})", ex.GetType().Name);
            return null;
        }
        var verdict = result.Verdict;
        if (verdict is null || !verdict.Available) return null;

        var sentinel = new JsonObject
        {
            ["available"] = true,
            ["risk"] = verdict.Risk,
            ["signals"] = new JsonArray(verdict.Signals.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
            ["handling"] = verdict.Handling,
            ["isolation"] = verdict.Isolation,
            ["jobId"] = result.JobId,
        };
        var truncated = included.Count < messages.Count;

        if (string.Equals(verdict.Risk, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verdict.Handling, "require_private_approval", StringComparison.OrdinalIgnoreCase))
        {
            sentinel["withheld"] = true;
            sentinel["reason"] = "high_risk";
            return new DiscordReviewedRead(
                included.Select(message => Project(message, withheld: true, Identity)).ToArray(),
                sentinel, Withheld: true, truncated);
        }

        // The model reviewed the combined text; every returned string also goes through the
        // deterministic screen, which removes hidden content. A string it could not cover
        // fails the whole read closed.
        var hidden = false;
        string? Visible(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var screening = review.Screen(raw, UntrustedContentFormat.Text);
            if (screening.InjectionSignals.Contains(UntrustedContentScreening.ContentTooLargeSignal))
                throw new InvalidOperationException("screening_unavailable");
            hidden |= screening.HasHiddenContent;
            return screening.VisibleContent;
        }

        try
        {
            var projected = included.Select(message => Project(message, withheld: false, Visible)).ToArray();
            sentinel["screening"] = new JsonObject { ["hiddenContent"] = hidden };
            return new DiscordReviewedRead(projected, sentinel, Withheld: false, truncated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger?.LogWarning("Discord channel read screening failed ({Type})", ex.GetType().Name);
            return null;
        }
    }

    private static string? Identity(string? value) => value;

    private static DiscordReviewedMessageView Project(
        DiscordGuildMessageView message, bool withheld, Func<string?, string?> text)
        => new(
            message.Id, message.Timestamp, message.EditedTimestamp, message.AuthorId,
            withheld ? null : text(message.AuthorName),
            withheld ? null : text(message.Username),
            message.AuthorIsBot, message.Source,
            withheld ? null : text(message.Content),
            message.Attachments.Select(attachment => new DiscordReviewedAttachmentView(
                attachment.Id,
                withheld ? null : text(attachment.Filename),
                attachment.ContentType,
                attachment.Size)).ToArray());

    private static string Section(DiscordGuildMessageView message)
    {
        var text = new StringBuilder()
            .Append("Message ").Append(message.Id)
            .Append(" from ").Append(message.AuthorName).Append(" (").Append(message.Username).Append(")\n")
            .Append(message.Content).Append('\n');
        foreach (var attachment in message.Attachments)
            text.Append("Attachment: ").Append(attachment.Filename).Append('\n');
        return text.Append('\n').ToString();
    }

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
