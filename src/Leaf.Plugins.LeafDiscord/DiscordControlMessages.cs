namespace Leaf.Plugins.LeafDiscord;

internal static class DiscordControlMessages
{
    public const string NoReply = "<discord-no-reply/>";

    public static bool IsNoReply(string? content)
        => string.Equals(content?.Trim(), NoReply, StringComparison.Ordinal);
}
