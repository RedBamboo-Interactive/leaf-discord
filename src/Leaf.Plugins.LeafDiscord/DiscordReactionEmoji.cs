using Discord;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>Parses the two reaction identifiers Discord accepts: Unicode emoji and name:id.</summary>
internal static class DiscordReactionEmoji
{
    public static IEmote Parse(string? value)
    {
        var normalized = Normalize(value);
        var separator = normalized.IndexOf(':');
        return separator > 0
            ? Emote.Parse($"<:{normalized[..separator]}:{normalized[(separator + 1)..]}>")
            : new Emoji(normalized);
    }

    public static string Normalize(string? value)
    {
        var input = value?.Trim();
        if (string.IsNullOrWhiteSpace(input) || input.Length > 128)
            throw Invalid();
        if (ContainsInvalidUnicode(input))
            throw new ArgumentException(
                "Discord reaction emoji must contain valid Unicode without replacement characters",
                nameof(value));

        // Accept Discord's display form for a custom emoji as a convenience, but send the
        // documented reaction identifier (name:id). Animation is not part of that identifier.
        if (Emote.TryParse(input, out var displayedCustom))
            return $"{displayedCustom.Name}:{displayedCustom.Id}";

        // Callers that already supply Discord's documented custom identifier keep that exact
        // semantic form. Wrapping it lets Discord.Net apply its established name/id validation.
        var separator = input.IndexOf(':');
        if (separator > 0
            && Emote.TryParse($"<:{input[..separator]}:{input[(separator + 1)..]}>", out var custom))
            return $"{custom.Name}:{custom.Id}";

        if (input.All(character => character <= 0x7F)
            || !Emoji.TryParse(input, out var unicode))
            throw new ArgumentException(
                "Discord reaction emoji must be one Unicode emoji or a custom emoji identifier in name:id form",
                nameof(value));

        // Discord reaction identity is presentation-insensitive for a standalone symbol, and its
        // API inconsistently rejects U+FE0F there. In joined and keycap sequences the selector is
        // part of the standardized sequence, so preserve it along with joiners and modifiers.
        return unicode.Name.Contains('\u200D') || unicode.Name.Contains('\u20E3')
            ? unicode.Name
            : unicode.Name.Replace("\uFE0F", "", StringComparison.Ordinal);
    }

    private static bool ContainsInvalidUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '\uFFFD') return true;
            if (!char.IsSurrogate(current)) continue;
            if (!char.IsHighSurrogate(current)
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[++index]))
                return true;
        }
        return false;
    }

    private static ArgumentException Invalid()
        => new("Discord reaction emoji is required and must be at most 128 characters", "value");
}
