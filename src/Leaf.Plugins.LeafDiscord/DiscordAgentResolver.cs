using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Resolves agent references through this plugin's entity-store capability without
/// depending on the optional SDK-wide IAgentDirectory service.
/// </summary>
public sealed class DiscordAgentResolver(IEntityStore entities)
{
    public async Task<LeafEntity?> ResolveAsync(
        string? slugOrId, CancellationToken ct = default)
    {
        var reference = slugOrId?.Trim();
        if (string.IsNullOrEmpty(reference)) return null;

        var entity = Guid.TryParse(reference, out var id)
            ? await entities.GetAsync(id, ct)
            : await entities.GetBySlugAsync("agent", reference, ct);

        return entity?.TypeSlug == "agent" ? entity : null;
    }

    public async Task<string?> ResolveOwnerUserIdAsync(
        string? agentSlugOrId, CancellationToken ct = default)
    {
        var agent = await ResolveAsync(agentSlugOrId, ct);
        var creator = agent?.CreatedBy;
        if (string.IsNullOrWhiteSpace(creator)) return null;
        if (creator == "local-user") return creator;
        if (!Guid.TryParse(creator, out var userId)) return null;
        return await entities.GetAsync(userId, ct) is { TypeSlug: "user" } user
            ? user.Id.ToString()
            : null;
    }
}
