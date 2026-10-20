using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Durable record of the last full guild server map handed to one binding session.
/// It is valid only for the exact binding generation and session that received it.
/// </summary>
public sealed record DiscordServerMapDisclosureState(
    long Generation,
    string? SessionId,
    string Hash,
    long TurnsSinceFull);

internal sealed record DiscordServerMapPlan(
    JsonObject? GuildContext,
    DiscordServerMapDisclosureState? NextState);

/// <summary>
/// Decides whether a turn carries the full server map or a compact reference to the map
/// the session already holds. The full map is sent on the first guild turn of a session,
/// whenever its content changes, and again after <see cref="RefreshTurns"/> turns so
/// model-side context compaction cannot silently lose it.
/// </summary>
internal static class DiscordServerMapDisclosure
{
    internal const int RefreshTurns = 25;
    internal const int HashLength = 16;
    internal const string ReferenceSchema = "leaf-discord-guild-map-ref/v1";
    internal const string RefreshRoute = "GET /api/apps/leaf-discord/guild/context";

    private static readonly HashSet<string> VolatileFields = new(StringComparer.Ordinal)
    {
        "captured_at",
        "hash",
    };

    public static DiscordServerMapPlan Plan(DiscordBinding binding, JsonObject? guildContext)
    {
        if (guildContext?["server_map"] is not JsonObject serverMap)
            return new DiscordServerMapPlan(guildContext, null);

        var hash = ContentHash(serverMap);
        var prior = binding.ServerMapDisclosure;
        var priorValid = prior is not null
            && prior.Generation == binding.Generation
            && string.Equals(prior.SessionId, binding.SessionId, StringComparison.Ordinal);
        var turnsSinceFull = priorValid ? prior!.TurnsSinceFull + 1 : 0;
        var sendFull = !priorValid
            || !string.Equals(prior!.Hash, hash, StringComparison.Ordinal)
            || turnsSinceFull >= RefreshTurns;

        var planned = guildContext.DeepClone().AsObject();
        if (sendFull)
        {
            var full = serverMap.DeepClone().AsObject();
            full["hash"] = hash;
            planned["server_map"] = full;
            return new DiscordServerMapPlan(planned, new DiscordServerMapDisclosureState(
                binding.Generation, binding.SessionId, hash, 0));
        }

        planned["server_map"] = Reference(hash, turnsSinceFull);
        return new DiscordServerMapPlan(planned, new DiscordServerMapDisclosureState(
            binding.Generation, binding.SessionId, hash, turnsSinceFull));
    }

    internal static JsonObject Reference(string hash, long turnsSinceFull) => new()
    {
        ["schema"] = ReferenceSchema,
        ["unchanged"] = true,
        ["hash"] = hash,
        ["turns_since_full"] = turnsSinceFull,
        ["refresh"] = RefreshRoute,
    };

    /// <summary>
    /// SHA-256 over a key-sorted canonical serialization that ignores capture time, so an
    /// identical map captured at a different moment hashes identically.
    /// </summary>
    internal static string ContentHash(JsonObject serverMap)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, serverMap, topLevel: true);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))
            .ToLowerInvariant()[..HashLength];
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node, bool topLevel)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var (key, child) in value.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    if (topLevel && VolatileFields.Contains(key)) continue;
                    writer.WritePropertyName(key);
                    WriteCanonical(writer, child, topLevel: false);
                }
                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (var child in value) WriteCanonical(writer, child, topLevel: false);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    internal static JsonObject? ToJson(DiscordServerMapDisclosureState? state)
        => state is null ? null : new JsonObject
        {
            ["generation"] = state.Generation,
            ["session_id"] = state.SessionId,
            ["hash"] = state.Hash,
            ["turns_since_full"] = state.TurnsSinceFull,
        };

    internal static DiscordServerMapDisclosureState? FromJson(JsonNode? node)
    {
        if (node is not JsonObject value) return null;
        var hash = JsonRead.String(value, "hash");
        var generation = JsonRead.Long(value, "generation", -1);
        if (hash is null || generation < 0) return null;
        return new DiscordServerMapDisclosureState(
            generation,
            JsonRead.String(value, "session_id"),
            hash,
            Math.Max(0, JsonRead.Long(value, "turns_since_full", 0)));
    }
}
