using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Discord;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordApplicationEmojiCreateRequest(string? Name, string? AssetId);

/// <summary>An emoji owned by the bot's Discord application, usable in every guild and DM.</summary>
public sealed record DiscordApplicationEmoji(string Id, string Name, bool Animated, string Markdown);

/// <summary>A validated RedLeaf image Asset, ready to upload as an application emoji.</summary>
internal sealed record DiscordApplicationEmojiImage(string AssetId, string ContentType, byte[] Bytes);

internal sealed class DiscordApplicationEmojiAssetNotFoundException(string message)
    : Exception(message);

internal static partial class DiscordApplicationEmojis
{
    /// <summary>Discord's application emoji upload limit.</summary>
    internal const int MaxImageBytes = 256 * 1024;

    // Serializes list-then-create per bot so two concurrent uploads of one name
    // cannot both pass the duplicate check.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> CreateGates = new();

    [GeneratedRegex("^[A-Za-z0-9_]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    internal static string ValidateName(string? name)
        => name is not null && NamePattern().IsMatch(name)
            ? name
            : throw new ArgumentException(
                "Emoji name must be 2-32 characters of letters, digits, or underscores.");

    internal static DiscordApplicationEmoji Project(Emote emote)
        => Project(emote.Id.ToString(), emote.Name, emote.Animated);

    internal static DiscordApplicationEmoji Project(string id, string name, bool animated)
        => new(id, name, animated, $"<{(animated ? "a" : "")}:{name}:{id}>");

    internal static SemaphoreSlim CreateGate(Guid botId)
        => CreateGates.GetOrAdd(botId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Loads the image only through the RedLeaf Asset store. The Asset must declare a PNG,
    /// JPEG, GIF, or WebP type, its bytes must carry that format's signature, and it must
    /// fit Discord's 256 KiB application emoji limit.
    /// </summary>
    internal static async Task<DiscordApplicationEmojiImage> ReadImageAsync(
        string? assetReference, IAssets assets, CancellationToken ct)
    {
        var assetId = DiscordBotRuntime.CanonicalAssetId(assetReference)
            ?? throw new ArgumentException("Emoji image must identify a canonical RedLeaf Asset.");
        var file = await assets.ReadAsync(assetId, ct)
            ?? throw new DiscordApplicationEmojiAssetNotFoundException(
                "Emoji image Asset was not found.");
        var contentType = file.ContentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (contentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")
            || !HasSignature(contentType, file.Bytes))
            throw new ArgumentException("Emoji image must be a PNG, JPEG, GIF, or WebP image.");
        if (file.Bytes.Length > MaxImageBytes)
            throw new ArgumentException("Emoji image must be at most 256 KiB.");
        return new DiscordApplicationEmojiImage(assetId, contentType, file.Bytes);
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private static bool HasSignature(string contentType, ReadOnlySpan<byte> bytes)
        => contentType switch
        {
            "image/png" => bytes.StartsWith(PngSignature),
            "image/jpeg" => bytes.StartsWith(JpegSignature),
            "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            "image/webp" => bytes.Length >= 12 && bytes.StartsWith("RIFF"u8)
                            && bytes[8..12].SequenceEqual("WEBP"u8),
            _ => false,
        };
}
