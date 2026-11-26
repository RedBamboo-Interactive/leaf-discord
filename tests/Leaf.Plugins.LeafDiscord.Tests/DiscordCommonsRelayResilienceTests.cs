using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// 2026-11-26: one Postgres connect timeout faulted the Leaf Community relay loop for good. At the
/// next in-process restart its DisposeAsync rethrew that fault out of the host's container
/// disposal, the restart aborted with Kestrel already stopped, and RedLeaf served nothing for
/// fifteen minutes.
/// </summary>
public sealed class DiscordCommonsRelayResilienceTests
{
    [Fact]
    public async Task A_relay_whose_drain_failed_still_stops_and_disposes_without_throwing()
    {
        var store = new FlakyEntityStore(failures: int.MaxValue);
        var relay = new DiscordCommonsRelayService(
            new DiscordCommonsConfigurationStore(store), new DiscordCommonsIngestStore(store),
            NullLogger<DiscordCommonsRelayService>.Instance);

        relay.Start(CancellationToken.None);
        await store.WaitForQueriesAsync(1);

        await relay.DisposeAsync();
    }

    [Fact]
    public async Task A_transient_failure_is_retried_instead_of_ending_the_relay_loop()
    {
        var store = new FlakyEntityStore(failures: 2);
        await using var relay = new DiscordCommonsRelayService(
            new DiscordCommonsConfigurationStore(store), new DiscordCommonsIngestStore(store),
            NullLogger<DiscordCommonsRelayService>.Instance, retryDelay: TimeSpan.FromMilliseconds(20));

        relay.Start(CancellationToken.None);

        // Two failed drains, then one that reaches the store: the loop survived both failures.
        await store.WaitForQueriesAsync(3);
    }

    [Fact]
    public async Task A_relay_cancelled_by_its_host_stops_quietly()
    {
        var store = new FlakyEntityStore(failures: 0);
        using var stopping = new CancellationTokenSource();
        var relay = new DiscordCommonsRelayService(
            new DiscordCommonsConfigurationStore(store), new DiscordCommonsIngestStore(store),
            NullLogger<DiscordCommonsRelayService>.Instance);
        relay.Start(stopping.Token);
        await store.WaitForQueriesAsync(1);

        stopping.Cancel();

        await relay.DisposeAsync();
    }

    private sealed class FlakyEntityStore(int failures) : IEntityStore
    {
        private readonly MemoryEntityStore inner = new();
        private readonly SemaphoreSlim queried = new(0);
        private int remaining = failures;

        public int Queries { get; private set; }

        public async Task WaitForQueriesAsync(int count)
        {
            for (var i = 0; i < count; i++)
                Assert.True(await queried.WaitAsync(TimeSpan.FromSeconds(10)), $"query {i + 1} never came");
        }

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
        {
            Queries++;
            queried.Release();
            if (Interlocked.Decrement(ref remaining) >= 0)
                // What EF raises for a Postgres connect timeout.
                return Task.FromException<IReadOnlyList<LeafEntity>>(new InvalidOperationException(
                    "An exception has been raised that is likely due to a transient failure.",
                    new TimeoutException("The operation has timed out.")));
            return inner.QueryAsync(query, ct);
        }

        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default) => inner.GetBySlugAsync(slug, ct);
        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
            => inner.CreateAsync(typeSlug, name, data, ct);
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default)
            => inner.UpsertBySlugAsync(typeSlug, slug, name, data, ct);
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
            => inner.PatchAsync(id, dataPatch, name, ct);
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
            => inner.ReplaceDataAsync(id, data, name, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    }
}
