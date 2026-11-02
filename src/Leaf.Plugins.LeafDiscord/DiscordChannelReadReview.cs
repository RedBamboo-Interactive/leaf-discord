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

/// <summary>A forum post as the reviewed listing returns it. Title and tag names are null when withheld.</summary>
internal sealed record DiscordReviewedPostView(
    string Id,
    string? Name,
    DateTimeOffset CreatedAt,
    bool Archived,
    bool Locked,
    int MessageCount,
    IReadOnlyList<DiscordReviewedTagView> Tags);

internal sealed record DiscordReviewedTagView(string Id, string? Name, string? Emoji, bool Moderated);

/// <param name="Sentinel">The verdict object returned to the caller, shaped like Mail's <c>sentinel</c>.</param>
/// <param name="Truncated">Items after <paramref name="Items"/> were left out to keep the
/// review within the Sentinel's bound; read on from the last returned ID.</param>
internal sealed record DiscordReviewedItems<T>(
    IReadOnlyList<T> Items,
    JsonObject Sentinel,
    bool Withheld,
    bool Truncated);

/// <summary>
/// Reviews the Discord text a Leaf session reads through the shared Sentinel
/// (<see cref="IUntrustedContentReview"/>) before any of it is returned, the way Mail reviews
/// what it returns. One review covers the whole response. Fail closed: with no Sentinel, no
/// verdict, or text the deterministic screen could not cover, nothing is returned (the
/// methods yield null and the route answers 503 <c>sentinel_unavailable</c>). A high-risk
/// verdict (or <c>require_private_approval</c>) returns metadata only.
/// </summary>
internal sealed class DiscordChannelReadReviewer(
    IUntrustedContentReview? review,
    ILogger<DiscordChannelReadReviewer>? logger = null,
    IEntityStore? entities = null)
{
    /// <summary>The Sentinel reviews at most this many characters (its own review bound).</summary>
    internal const int ReviewBound = 200_000;

    internal const string SentinelPluginId = "cap-sentinel";

    internal TimeSpan AvailabilityCache { get; init; } = TimeSpan.FromSeconds(5);
    private (bool Value, long At)? availability;

    /// <summary>
    /// Whether the Sentinel can be used now: the review service is registered and, as Mail
    /// checks, cap-sentinel's plugin entity has status <c>enabled</c> (the kernel still
    /// registers a disabled plugin's services). Any doubt is unavailable.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (review is null) return false;
        if (entities is null) return true;
        if (availability is { } cached
            && System.Diagnostics.Stopwatch.GetElapsedTime(cached.At) < AvailabilityCache)
            return cached.Value;
        bool value;
        try
        {
            var plugin = await entities.GetBySlugAsync("plugin", SentinelPluginId, ct);
            var statusRef = plugin is null ? null : JsonRead.String(plugin.Data, "status");
            var status = Guid.TryParse(statusRef, out var statusId)
                ? await entities.GetAsync(statusId, ct)
                : null;
            value = status?.Slug == "enabled";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Sentinel status could not be read ({Type})", ex.GetType().Name);
            value = false;
        }
        availability = (value, System.Diagnostics.Stopwatch.GetTimestamp());
        return value;
    }

    public Task<DiscordReviewedItems<DiscordReviewedMessageView>?> ReviewAsync(
        IReadOnlyList<DiscordGuildMessageView> messages,
        string sourceRef, string? ownerUserId, string agentRef,
        ComputeProvenance? provenance, CancellationToken ct)
        => ReviewCoreAsync(
            messages, Section, Project, "Discord channel read review",
            sourceRef, ownerUserId, agentRef, provenance, ct);

    public Task<DiscordReviewedItems<DiscordReviewedPostView>?> ReviewPostsAsync(
        IReadOnlyList<DiscordForumPostView> posts,
        string sourceRef, string? ownerUserId, string agentRef,
        ComputeProvenance? provenance, CancellationToken ct)
        => ReviewCoreAsync(
            posts, Section, Project, "Discord forum read review",
            sourceRef, ownerUserId, agentRef, provenance, ct);

    private async Task<DiscordReviewedItems<TOut>?> ReviewCoreAsync<TIn, TOut>(
        IReadOnlyList<TIn> items,
        Func<TIn, string> section,
        Func<TIn, bool, Func<string?, string?>, TOut> project,
        string jobName,
        string sourceRef,
        string? ownerUserId,
        string agentRef,
        ComputeProvenance? provenance,
        CancellationToken ct)
    {
        // Nothing to read means nothing to review: no Sentinel call, and none needed to answer.
        if (items.Count == 0)
            return new DiscordReviewedItems<TOut>(
                [], new JsonObject { ["reviewed"] = false }, false, false);
        if (review is null) return null;

        var included = new List<TIn>();
        var block = new StringBuilder();
        foreach (var item in items)
        {
            var text = section(item);
            if (included.Count > 0 && block.Length + text.Length > ReviewBound) break;
            if (text.Length > ReviewBound) return null;
            block.Append(text);
            included.Add(item);
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
                JobName = jobName,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger?.LogWarning("Discord read review failed ({Type})", ex.GetType().Name);
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
        var truncated = included.Count < items.Count;

        if (string.Equals(verdict.Risk, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verdict.Handling, "require_private_approval", StringComparison.OrdinalIgnoreCase))
        {
            sentinel["withheld"] = true;
            sentinel["reason"] = "high_risk";
            return new DiscordReviewedItems<TOut>(
                included.Select(item => project(item, true, static text => text)).ToArray(),
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
            var projected = included.Select(item => project(item, false, Visible)).ToArray();
            sentinel["screening"] = new JsonObject { ["hiddenContent"] = hidden };
            return new DiscordReviewedItems<TOut>(projected, sentinel, Withheld: false, truncated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger?.LogWarning("Discord read screening failed ({Type})", ex.GetType().Name);
            return null;
        }
    }

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

    private static DiscordReviewedPostView Project(
        DiscordForumPostView post, bool withheld, Func<string?, string?> text)
        => new(
            post.Id,
            withheld ? null : text(post.Name),
            post.CreatedAt, post.Archived, post.Locked, post.MessageCount,
            post.Tags.Select(tag => new DiscordReviewedTagView(
                tag.Id,
                withheld ? null : text(tag.Name),
                withheld ? null : text(tag.Emoji),
                tag.Moderated)).ToArray());

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

    private static string Section(DiscordForumPostView post)
    {
        var text = new StringBuilder()
            .Append("Forum post ").Append(post.Id).Append(": ").Append(post.Name).Append('\n');
        foreach (var tag in post.Tags)
            text.Append("Tag: ").Append(tag.Name).Append(' ').Append(tag.Emoji).Append('\n');
        return text.Append('\n').ToString();
    }

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
