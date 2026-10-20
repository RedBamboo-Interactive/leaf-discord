using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordSettingOption(
    string Value,
    string Label,
    string? Description = null,
    string? EntityId = null,
    string? EntityTypeSlug = null);

public sealed record DiscordQualityMode(
    string Id, string Name, string Provider, string QualityTier,
    string? Model, string? Effort, string? Description, string? EntityId = null);

public sealed class DiscordRuntimeOptionsResolver(IEntityStore entities)
{
    public async Task<IReadOnlyList<DiscordSettingOption>> ListAgentsAsync(CancellationToken ct = default)
        => (await entities.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 200 }, ct))
            .OrderBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entity => new DiscordSettingOption(entity.Slug, entity.Name,
                JsonRead.String(entity.Data, "description"),
                entity.Id.ToString(), entity.TypeSlug))
            .ToArray();

    public async Task<IReadOnlyList<DiscordQualityMode>> ListQualityModesAsync(
        CancellationToken ct = default)
        => (await entities.QueryAsync(new EntityQuery { TypeSlug = "quality-mode", Limit = 200 }, ct))
            .Select(Map)
            .Where(mode => !string.IsNullOrWhiteSpace(mode.Provider)
                && !string.IsNullOrWhiteSpace(mode.QualityTier))
            .OrderBy(mode => mode.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<DiscordQualityMode?> ResolveQualityModeAsync(
        string? reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var entity = Guid.TryParse(reference, out var id)
            ? await entities.GetAsync(id, ct)
            : await entities.GetBySlugAsync("quality-mode", reference.Trim(), ct);
        return entity is { TypeSlug: "quality-mode" } ? Map(entity) : null;
    }

    private static DiscordQualityMode Map(LeafEntity entity) => new(
        entity.Slug,
        entity.Name,
        JsonRead.String(entity.Data, "provider") ?? "",
        JsonRead.String(entity.Data, "quality_tier") ?? "",
        JsonRead.String(entity.Data, "model"),
        JsonRead.String(entity.Data, "effort"),
        JsonRead.String(entity.Data, "description"),
        entity.Id.ToString());
}
