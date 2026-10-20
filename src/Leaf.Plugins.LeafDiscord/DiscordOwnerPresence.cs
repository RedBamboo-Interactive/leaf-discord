using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Discord;
using Discord.WebSocket;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

internal sealed record DiscordOwnerPresenceGuild(
    string Id,
    string Name,
    DiscordGuildContextMember Owner,
    IReadOnlyList<DiscordGuildContextMember> Members);

internal static class DiscordOwnerPresenceBuilder
{
    internal const int MaxParticipants = 20;
    private const int MaxTextLength = 120;

    public static JsonObject Build(
        string ownerUserId,
        string applicationId,
        IReadOnlyList<DiscordOwnerPresenceGuild> guilds,
        DateTimeOffset observedAt)
    {
        var voiceGuild = guilds.FirstOrDefault(guild =>
            !string.IsNullOrWhiteSpace(guild.Owner.VoiceChannel));
        var playing = guilds.SelectMany(guild => guild.Owner.Activities)
            .FirstOrDefault(activity => IsType(activity, "Playing"));
        var listening = guilds.SelectMany(guild => guild.Owner.Activities)
            .FirstOrDefault(activity => IsType(activity, "Listening"));
        var streaming = guilds.SelectMany(guild => guild.Owner.Activities)
            .FirstOrDefault(activity => IsType(activity, "Streaming"));
        var primary = playing ?? listening ?? streaming;
        var category = playing is not null ? "gaming"
            : listening is not null ? "listening"
            : streaming is not null ? "streaming"
            : voiceGuild is not null ? "voice"
            : null;
        var title = playing is not null ? Bounded(playing.Name)
            : listening is not null
                ? "Listening to " + Bounded(listening.Name)
                  + (string.IsNullOrWhiteSpace(listening.Details)
                      ? "" : " by " + Bounded(listening.Details))
            : streaming is not null ? "Streaming " + Bounded(streaming.Name)
            : null;
        var voiceChannel = voiceGuild is null
            ? null
            : Bounded(voiceGuild.Name) + " / " + Bounded(voiceGuild.Owner.VoiceChannel!);

        var participants = new List<JsonObject>();
        var includedIds = new HashSet<string>(StringComparer.Ordinal);
        if (voiceGuild is not null)
        {
            foreach (var member in voiceGuild.Members
                         .Where(member => !member.IsBot
                             && member.Id != voiceGuild.Owner.Id
                             && member.VoiceChannel == voiceGuild.Owner.VoiceChannel)
                         .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (participants.Count >= MaxParticipants) break;
                includedIds.Add(member.Id);
                participants.Add(Participant(member.DisplayName, "shared-session"));
            }
        }
        if (playing is not null)
        {
            var ownerIds = guilds.Select(guild => guild.Owner.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var member in guilds.SelectMany(guild => guild.Members)
                         .Where(member => !member.IsBot
                             && !ownerIds.Contains(member.Id)
                             && !includedIds.Contains(member.Id)
                             && member.Activities.Any(activity =>
                                 IsType(activity, "Playing")
                                 && string.Equals(activity.Name, playing.Name,
                                     StringComparison.OrdinalIgnoreCase)))
                         .GroupBy(member => member.Id, StringComparer.Ordinal)
                         .Select(group => group.First())
                         .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (participants.Count >= MaxParticipants) break;
                includedIds.Add(member.Id);
                participants.Add(Participant(member.DisplayName, "same-game"));
            }
        }

        var active = primary is not null || voiceGuild is not null;
        return new JsonObject
        {
            ["schema"] = "leaf-discord-owner-presence/v1",
            ["owner_user_id"] = ownerUserId,
            ["application_id"] = applicationId,
            ["status"] = active ? "active" : "inactive",
            ["category"] = category,
            ["title"] = title,
            ["provider_item_id"] = playing is null ? null : Bounded(playing.Name),
            ["voice_channel"] = voiceChannel,
            ["participants"] = new JsonArray(participants
                .Select(item => (JsonNode?)item).ToArray()),
            ["priority"] = voiceGuild is not null ? 300
                : playing is not null ? 200
                : listening is not null ? 150
                : streaming is not null ? 125
                : 0,
            ["confidence"] = "high",
            ["visibility"] = "trusted-extensions",
            ["observed_at"] = observedAt.ToString("O"),
            ["expires_at"] = observedAt.AddMinutes(5).ToString("O"),
        };
    }

    private static JsonObject Participant(string name, string relationship) => new()
    {
        ["name"] = Bounded(name),
        ["relationship"] = relationship,
        ["source"] = "discord",
    };

    private static bool IsType(DiscordGuildContextActivity activity, string type)
        => string.Equals(activity.Type, type, StringComparison.OrdinalIgnoreCase);

    private static string Bounded(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaxTextLength
            ? normalized
            : normalized[..MaxTextLength];
    }
}

public sealed class DiscordOwnerPresencePublisher(
    IPluginEvents events,
    DiscordBindingStore bindings,
    DiscordUserIdentityResolver identities)
{
    public const string EventType = "leaf-discord.owner-presence-observed";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, PublishedState> published =
        new(StringComparer.Ordinal);

    public async Task PublishAsync(
        LeafDiscordConfig config,
        DiscordSocketClient client,
        bool forceRefresh,
        CancellationToken ct = default)
    {
        if (!config.PresenceExtensionFeedEnabled
            || config.PresenceContextMode == DiscordPresenceContextModes.Off
            || string.IsNullOrWhiteSpace(config.OwnerUserId))
            return;
        var externalId = await identities.ResolveExternalIdAsync(config.OwnerUserId, ct);
        if (!ulong.TryParse(externalId, out var ownerId)) return;
        var scopedGuildIds = (await bindings.ListAsync(ct))
            .Where(binding => binding.Enabled
                && binding.ApplicationId == config.ApplicationId
                && !string.IsNullOrWhiteSpace(binding.GuildId))
            .Select(binding => binding.GuildId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(guildId => guildId, StringComparer.Ordinal)
            .ToArray();
        var guilds = new List<DiscordOwnerPresenceGuild>();
        foreach (var guildId in scopedGuildIds)
        {
            if (!ulong.TryParse(guildId, out var parsedGuildId)) continue;
            var guild = client.GetGuild(parsedGuildId);
            var owner = guild?.GetUser(ownerId);
            if (guild is null || owner is null) continue;
            var members = guild.Users.Select(Member).ToArray();
            guilds.Add(new DiscordOwnerPresenceGuild(
                guild.Id.ToString(), guild.Name, Member(owner), members));
        }
        if (guilds.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var payload = DiscordOwnerPresenceBuilder.Build(
            config.OwnerUserId, config.ApplicationId, guilds, now);
        var semantic = payload.DeepClone() as JsonObject ?? [];
        semantic.Remove("observed_at");
        semantic.Remove("expires_at");
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(semantic.ToJsonString())));
        if (published.TryGetValue(config.ApplicationId, out var previous)
            && previous.Hash == hash
            && (!forceRefresh || now - previous.PublishedAt < RefreshInterval))
            return;
        await events.PublishAsync(EventType, payload, ct);
        published[config.ApplicationId] = new PublishedState(hash, now);
    }

    private static DiscordGuildContextMember Member(SocketGuildUser user) => new(
        user.Id.ToString(),
        user.DisplayName,
        user.Status.ToString(),
        user.Activities.Select(activity => new DiscordGuildContextActivity(
            activity.Type.ToString(),
            activity is SpotifyGame spotify ? spotify.TrackTitle : activity.Name,
            activity is SpotifyGame listening
                ? string.Join(", ", listening.Artists)
                : activity.Details,
            activity is SpotifyGame album
                ? album.AlbumTitle
                : activity is RichGame rich ? rich.State : null)).ToArray(),
        user.VoiceChannel?.Name,
        user.IsBot);

    private sealed record PublishedState(string Hash, DateTimeOffset PublishedAt);
}
