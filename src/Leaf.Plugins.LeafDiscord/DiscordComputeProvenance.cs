using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Provenance for Compute work the bridge starts itself (the outbound Sentinel reviews), built
/// the way Nova builds its inbound review provenance: app leaf-discord, actor the bound Agent,
/// beneficiary the owner, and the binding and reply as context. The reviewer often runs with no
/// HTTP request and no execution scope to inherit; without this RedLeaf's ComputeGateway cannot
/// issue a signed execution identity and refuses the call.
/// </summary>
internal static class DiscordComputeProvenance
{
    public const string ReviewRoute = "/api/apps/leaf-discord/outbound-review";

    public static async Task<ComputeProvenance> CreateAsync(
        IEntityStore entities,
        string agentRef,
        string? ownerUserId,
        IReadOnlyList<ComputeContextReference> context,
        string? correlationId,
        CancellationToken ct)
    {
        var plugin = await entities.GetBySlugAsync("plugin", LeafDiscordPlugin.PluginId, ct);
        var app = plugin is { TypeSlug: "plugin" }
            ? new ComputeAppReference("plugin", plugin.Slug, plugin.Id.ToString(), plugin.Name,
                Str(plugin.Data, "icon"))
            : new ComputeAppReference("plugin", LeafDiscordPlugin.PluginId, null, "Discord");
        var agent = Guid.TryParse(agentRef, out var agentId)
            ? await entities.GetAsync(agentId, ct)
            : await entities.GetBySlugAsync("agent", agentRef, ct);
        var actor = agent is { TypeSlug: "agent" }
            ? new ComputeActor("agent", agent.Name, agent.Id.ToString(), agent.Slug)
            : new ComputeActor("agent", agentRef, Id: agentRef);
        return new ComputeProvenance(
            ComputeProvenance.CurrentSchemaVersion,
            new ComputeOrigin("redleaf", app, new ComputeEntrypoint("discord", ReviewRoute, "REVIEW")),
            actor,
            await BeneficiaryAsync(entities, ownerUserId, ct),
            context.Take(15).ToArray(),
            new ComputeTrace(CorrelationId: correlationId),
            ComputeProvenanceAssurance.Verified,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The durable binding owner when it still resolves to a User. Legacy/local bindings and
    /// missing owners use the bounded system principal rather than naming a user that RedLeaf's
    /// execution-token issuer cannot authenticate.
    /// </summary>
    internal static async Task<ComputeBeneficiary> BeneficiaryAsync(
        IEntityStore entities, string? ownerUserId, CancellationToken ct)
    {
        if (ownerUserId == "system")
            return new ComputeBeneficiary("system", Reason: "Discord work for a system-owned binding");
        if (string.IsNullOrWhiteSpace(ownerUserId) || ownerUserId == "local-user")
            return new ComputeBeneficiary("system",
                Reason: "Discord work for a binding without an authenticated owner");
        if (Guid.TryParse(ownerUserId, out var id)
            && await entities.GetAsync(id, ct) is { TypeSlug: "user" } user)
            return new ComputeBeneficiary("user", user.Id.ToString(), user.Name,
                Str(user.Data, "avatar_url") ?? Str(user.Data, "avatar"));
        return new ComputeBeneficiary("system",
            Reason: "Discord work for a binding whose owner is no longer resolvable");
    }

    private static string? Str(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
