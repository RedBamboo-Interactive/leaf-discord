using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>A Sentinel that passes everything and proves the direction it applied and the images
/// it reviewed. Requests are recorded.</summary>
internal sealed class TestSentinel : IUntrustedContentReview
{
    public static TestSentinel Clean { get; } = new();

    public List<UntrustedContentReviewRequest> Requests { get; } = [];

    public Task<UntrustedContentReviewResult> ReviewAsync(
        UntrustedContentReviewRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(new UntrustedContentReviewResult(
            new UntrustedContentVerdict("none", [], "normal", true, UntrustedContentIsolation.VerifiedToolFree),
            null, [], new UntrustedContentScreening(0, [], [], request.Content, []), "job", "trace")
        {
            Direction = request.Direction,
            ImagesReviewed = request.Images?.Count ?? 0,
        });
    }

    public UntrustedContentScreening Screen(
        string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
        string? ownerDisplayName = null)
        => new(0, [], [], content, []);
}
