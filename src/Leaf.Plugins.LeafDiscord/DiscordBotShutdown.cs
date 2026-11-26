using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Closes the bot's gateway sessions while the host stops, before its container is disposed.
/// <para>
/// The host awaits a hosted service's <see cref="StopAsync"/> inside its own stop, before it
/// disposes its services. Without this the bot closed only when the container disposed
/// <see cref="DiscordBotService"/>, and its clients kept receiving gateway events for up to
/// fourteen seconds after the container had started disposing. Every presence or message event in
/// that window failed with ObjectDisposedException (2026-11-26, three of seven in-process restarts).
/// </para>
/// <para>
/// Until this stop, the bot keeps writing what it receives to its durable gateway inbox, which the
/// next host drains. It stops last on purpose, not at the start of the kernel's teardown.
/// </para>
/// <para>
/// It stops only a bot this host started (<see cref="Started(DiscordBotService)"/>), and never throws: the host
/// rethrows a hosted service's failure out of its own stop, which would fail the kernel's restart.
/// </para>
/// </summary>
public sealed class DiscordBotShutdown(ILogger<DiscordBotShutdown>? logger = null) : IHostedService
{
    private volatile Func<Task>? _stopBot;

    /// <summary>The bot this host started, from the plugin's startup.</summary>
    public void Started(DiscordBotService bot) => Started(bot.StopAsync);

    /// <summary>How to stop what this host started.</summary>
    public void Started(Func<Task> stopBot) => _stopBot = stopBot;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
        => _stopBot is { } stopBot ? StopAsync(stopBot, cancellationToken) : Task.CompletedTask;

    private async Task StopAsync(Func<Task> stopBot, CancellationToken cancellationToken)
    {
        try
        {
            await stopBot().WaitAsync(cancellationToken);
            // The bot's own client log is detached before it stops, so this line is the record.
            logger?.LogInformation("Closed the Discord gateway sessions while the host stopped");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Discord gateway sessions did not close before the host's shutdown timeout");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Discord gateway sessions could not be closed while the host stopped");
        }
    }
}
