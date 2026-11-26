using Leaf.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// 2026-11-26: the bot's gateway sessions closed only when the host's container disposed
/// DiscordBotService. For up to fourteen seconds of every in-process restart the clients kept
/// receiving gateway events against a container that was already disposing, and each presence or
/// message event failed with ObjectDisposedException. The host now closes them inside its own stop.
/// </summary>
public sealed class DiscordBotShutdownTests
{
    [Fact]
    public void The_plugin_registers_a_hosted_service_that_closes_the_bot_while_the_host_stops()
    {
        var services = new ServiceCollection();
        new LeafDiscordPlugin().ConfigureServices(services, new PluginContext
        {
            Manifest = PluginManifest.Parse("""{ "id": "leaf-discord", "name": "Leaf Discord" }"""),
            PluginDirectory = Path.GetTempPath(),
            DevMode = true,
        });
        using var provider = services.BuildServiceProvider();

        // Resolving the hosted services must not build the bot: a host that never started it
        // has nothing to close.
        var hosted = provider.GetServices<IHostedService>().ToList();

        var shutdown = Assert.Single(hosted.OfType<DiscordBotShutdown>());
        Assert.Same(provider.GetRequiredService<DiscordBotShutdown>(), shutdown);
    }

    [Fact]
    public async Task The_bot_closes_during_the_host_stop_while_its_container_still_exists()
    {
        using var host = HostWith(out var shutdown);
        var container = host.Services.GetRequiredService<ContainerProbe>();
        bool? containerAliveAtStop = null;
        shutdown.Started(() =>
        {
            containerAliveAtStop = !container.Disposed;
            return Task.CompletedTask;
        });

        await host.StartAsync();
        await host.StopAsync();
        Assert.False(container.Disposed);
        host.Dispose();

        Assert.True(containerAliveAtStop);
        Assert.True(container.Disposed);
    }

    [Fact]
    public async Task A_bot_that_fails_to_close_does_not_fail_the_host_stop()
    {
        using var host = HostWith(out var shutdown);
        shutdown.Started(() => Task.FromException(new InvalidOperationException("gateway gone")));

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task A_bot_that_never_closes_is_left_at_the_host_shutdown_timeout()
    {
        using var host = HostWith(out var shutdown, TimeSpan.FromMilliseconds(200));
        shutdown.Started(() => new TaskCompletionSource().Task);

        await host.StartAsync();
        var stop = host.StopAsync();

        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10))));
        await stop;
    }

    [Fact]
    public async Task A_host_that_never_started_the_bot_stops_nothing()
    {
        using var host = HostWith(out _);

        await host.StartAsync();
        await host.StopAsync();
    }

    private static IHost HostWith(out DiscordBotShutdown shutdown, TimeSpan? shutdownTimeout = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ContainerProbe>();
        builder.Services.AddSingleton(_ => new DiscordBotShutdown());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordBotShutdown>());
        if (shutdownTimeout is { } timeout)
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = timeout);
        var host = builder.Build();
        shutdown = host.Services.GetRequiredService<DiscordBotShutdown>();
        return host;
    }

    private sealed class ContainerProbe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
