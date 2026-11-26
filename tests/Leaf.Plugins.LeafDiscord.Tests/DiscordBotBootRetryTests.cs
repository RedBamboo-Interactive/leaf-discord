using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// 2026-11-26 (N17 sweep): the bot started once, from a discarded task. A database blip while the
/// kernel booted (the bot list, or one bot's configuration, could not be read) left Discord down
/// until someone saved its settings. The start now retries those, and only those.
/// </summary>
public sealed class DiscordBotBootRetryTests
{
    [Fact]
    public async Task A_bot_list_that_cannot_be_read_at_boot_is_read_again()
    {
        var store = new FlakyEntityStore();
        await using var provider = await ProviderAsync(store);
        var bot = provider.GetRequiredService<DiscordBotService>();
        store.FailBotLists = 1;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await bot.StartAsync(cts.Token);

        Assert.Equal(0, store.FailBotLists);
        Assert.True(store.BotLists >= 2, $"the bot list was read {store.BotLists} time(s)");
    }

    [Fact]
    public async Task A_bot_whose_configuration_cannot_be_read_at_boot_starts_on_the_retry()
    {
        var store = new FlakyEntityStore();
        await using var provider = await ProviderAsync(store);
        var configurations = provider.GetRequiredService<DiscordBotConfigurationStore>();
        // Disabled: a start that reads its configuration settles as "disabled" without Discord.
        var config = await CreateBotAsync(configurations, enabled: false);
        var bot = provider.GetRequiredService<DiscordBotService>();
        store.FailGets[config.EntityId] = 1;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await bot.StartAsync(cts.Token);

        Assert.Equal(0, store.FailGets[config.EntityId]);
        var runtime = await StatusAsync(bot, config.EntityId);
        Assert.Equal("disabled", runtime.State);
        Assert.Null(runtime.LastError);
    }

    [Fact]
    public async Task A_login_that_fails_is_never_retried()
    {
        var store = new FlakyEntityStore();
        await using var provider = await ProviderAsync(store);
        var configurations = provider.GetRequiredService<DiscordBotConfigurationStore>();
        // Ready, with a token Discord.Net refuses before any network call.
        var config = await CreateBotAsync(configurations, enabled: true, token: "not-a-discord-token");
        var bot = provider.GetRequiredService<DiscordBotService>();
        var readsBefore = store.Gets.GetValueOrDefault(config.EntityId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await bot.StartAsync(cts.Token);

        // One configuration read for the one start: the failed login was not tried again.
        Assert.Equal(readsBefore + 1, store.Gets.GetValueOrDefault(config.EntityId));
        var runtime = await StatusAsync(bot, config.EntityId);
        Assert.Equal("error", runtime.State);
    }

    private static async Task<DiscordBotStatus> StatusAsync(DiscordBotService bot, Guid botId)
    {
        var status = Assert.Single(await bot.GetStatusesAsync(), candidate => candidate.BotId == botId);
        return status;
    }

    private static Task<LeafDiscordConfig> CreateBotAsync(
        DiscordBotConfigurationStore configurations, bool enabled, string token = "test-bot-token")
        => configurations.CreateAsync(new LeafDiscordConfigUpdate(
            "100000000000000001", new string('a', 64), token, false, enabled,
            [], [], 10 * 1024 * 1024, "nova", "deep-codex"));

    private static Task<ServiceProvider> ProviderAsync(IEntityStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddKeyedSingleton(LeafDiscordPlugin.PluginId, store);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        // Host services the bot's construction needs and these starts never call.
        foreach (var hostService in HostServices)
            services.AddSingleton(hostService, _ => Unused.For(hostService));
        foreach (var hostService in KeyedHostServices)
            services.AddKeyedSingleton(hostService, LeafDiscordPlugin.PluginId, (_, _) => Unused.For(hostService));
        new LeafDiscordPlugin().ConfigureServices(services, new PluginContext
        {
            Manifest = PluginManifest.Parse("""{ "id": "leaf-discord", "name": "Leaf Discord" }"""),
            PluginDirectory = Path.GetTempPath(),
            DevMode = true,
        });
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
        return Task.FromResult(provider);
    }

    private static readonly Type[] HostServices =
    [
        typeof(IComputeGateway),
        typeof(IAssets),
    ];

    private static readonly Type[] KeyedHostServices =
    [
        typeof(IPluginEvents),
    ];

    /// <summary>A host service that fails loudly if a start ever calls it.</summary>
    public class Unused : System.Reflection.DispatchProxy
    {
        public static object For(Type service)
            => typeof(System.Reflection.DispatchProxy).GetMethod(nameof(Create), 2, Type.EmptyTypes)!
                .MakeGenericMethod(service, typeof(Unused)).Invoke(null, null)!;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException($"The boot retry tests do not provide {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}.");
    }

    /// <summary>Fails the bot list, or one bot's configuration read, as EF does for a Postgres connect timeout.</summary>
    private sealed class FlakyEntityStore : IEntityStore
    {
        private readonly MemoryEntityStore inner = new();
        private readonly object gate = new();

        public int FailBotLists { get; set; }
        public int BotLists { get; private set; }
        public Dictionary<Guid, int> FailGets { get; } = [];
        public Dictionary<Guid, int> Gets { get; } = [];

        private static Exception Transient() => new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            new TimeoutException("The operation has timed out."));

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
        {
            if (query.TypeSlug == DiscordBotConfigurationStore.TypeSlug && query.DataEquals is null)
            {
                lock (gate)
                {
                    BotLists++;
                    if (FailBotLists > 0)
                    {
                        FailBotLists--;
                        return Task.FromException<IReadOnlyList<LeafEntity>>(Transient());
                    }
                }
            }
            return inner.QueryAsync(query, ct);
        }

        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
        {
            lock (gate)
            {
                Gets[id] = Gets.GetValueOrDefault(id) + 1;
                if (FailGets.TryGetValue(id, out var remaining) && remaining > 0)
                {
                    FailGets[id] = remaining - 1;
                    return Task.FromException<LeafEntity?>(Transient());
                }
            }
            return inner.GetAsync(id, ct);
        }

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
