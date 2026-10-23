using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>A Sentinel that passes everything and proves the direction it applied.</summary>
internal sealed class TestSentinel : IUntrustedContentReview
{
    public static TestSentinel Clean { get; } = new();

    public Task<UntrustedContentReviewResult> ReviewAsync(
        UntrustedContentReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(new UntrustedContentReviewResult(
            new UntrustedContentVerdict("none", [], "normal", true, UntrustedContentIsolation.VerifiedToolFree),
            null, [], new UntrustedContentScreening(0, [], [], request.Content, []), "job", "trace")
        {
            Direction = request.Direction,
        });

    public UntrustedContentScreening Screen(
        string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
        string? ownerDisplayName = null)
        => new(0, [], [], content, []);
}
