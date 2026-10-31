using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Watches whether each bot's owner User still carries a Discord link. Without it the owner's
/// own Discord messages arrive unverified, so the bridge warns once when the link is missing
/// or disappears and once more only after it was restored and lost again, never per message.
/// </summary>
public sealed class DiscordOwnerLinkMonitor(
    DiscordUserIdentityResolver identities,
    ILogger<DiscordOwnerLinkMonitor> logger,
    TimeProvider? clock = null)
{
    /// <summary>How long an observation made while handling messages is reused.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(10);

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, (bool Linked, DateTimeOffset CheckedAt)> states = new();

    /// <summary>Reads the owner's link now and reports a change.</summary>
    public async Task<bool> CheckAsync(
        Guid botId, string? ownerUserId, CancellationToken ct = default)
    {
        var linked = !string.IsNullOrWhiteSpace(ownerUserId)
            && await identities.ResolveExternalIdAsync(ownerUserId, ct) is not null;
        var previous = states.TryGetValue(botId, out var state) ? state.Linked : (bool?)null;
        states[botId] = (linked, time.GetUtcNow());
        if (!linked && previous != false)
            logger.LogWarning(
                "Discord bot {BotId}: its owner User {OwnerUserId} has no {Field}, so the owner's Discord messages are not recognized as the owner's",
                botId, ownerUserId ?? "(none)", DiscordUserIdentityResolver.UserField);
        else if (linked && previous == false)
            logger.LogInformation(
                "Discord bot {BotId}: its owner User {OwnerUserId} is linked to Discord again",
                botId, ownerUserId);
        return linked;
    }

    /// <summary>Like <see cref="CheckAsync"/>, but reads at most once per recheck interval.</summary>
    public async Task<bool> ObserveAsync(
        Guid botId, string? ownerUserId, CancellationToken ct = default)
        => states.TryGetValue(botId, out var state)
           && time.GetUtcNow() - state.CheckedAt < RecheckInterval
            ? state.Linked
            : await CheckAsync(botId, ownerUserId, ct);
}
