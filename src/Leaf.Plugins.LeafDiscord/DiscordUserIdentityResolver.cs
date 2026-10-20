using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordVerifiedLeafUser(
    string UserId,
    string DisplayName,
    bool IsConversationOwner);

/// <summary>
/// Resolves a Discord author to an explicitly linked RedLeaf User. Display names,
/// usernames, and message content never participate in identity resolution.
/// </summary>
public sealed class DiscordUserIdentityResolver(
    IEntityStore entities,
    ILogger<DiscordUserIdentityResolver> logger)
{
    public const string UserField = "discord_user_id";

    public async Task<DiscordVerifiedLeafUser?> ResolveAsync(
        string externalId,
        string conversationOwnerId,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(externalId, out var snowflake) || snowflake == 0)
            return null;

        var canonicalId = snowflake.ToString();
        var matches = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "user",
            DataEquals = new Dictionary<string, object?>
            {
                [UserField] = canonicalId,
            },
            Limit = 2,
        }, ct);

        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
        {
            logger.LogError(
                "Discord user {DiscordUserId} is linked to multiple RedLeaf users; identity remains unverified",
                canonicalId);
            return null;
        }

        var user = matches[0];
        return new DiscordVerifiedLeafUser(
            user.Id.ToString(),
            user.Name,
            string.Equals(user.Id.ToString(), conversationOwnerId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string?> ResolveExternalIdAsync(
        string leafUserId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(leafUserId, out var userId)) return null;
        var user = await entities.GetAsync(userId, ct);
        if (user is not { TypeSlug: "user" }) return null;
        var externalId = JsonRead.String(user.Data, UserField);
        return ulong.TryParse(externalId, out var snowflake) && snowflake > 0
            ? snowflake.ToString()
            : null;
    }

    public async Task<string?> ResolveDisplayNameAsync(
        string leafUserId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(leafUserId, out var userId)) return null;
        return await entities.GetAsync(userId, ct) is { TypeSlug: "user" } user
            ? user.Name
            : null;
    }
}
