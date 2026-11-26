using Microsoft.Extensions.Logging;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

/// <summary>
/// 2026-11-26: "Discord transcript reconciliation failed ... RedCompute transcript is unavailable" was an ERROR at every
/// rebuild: RedCompute drains and stops before the kernel's plugins do, and the maintenance sweep kept reading
/// transcripts into it. RedCompute not answering is a retry, logged as a Warning, and a phase that fails while the run
/// ends logs nothing: the next run requests the binding again.
/// </summary>
public sealed class DiscordMaintenanceFailureTests
{
    [Fact]
    public void RedCompute_being_unavailable_is_a_warning_and_a_retry()
    {
        var log = new RecordingLogger();

        DiscordBotRuntime.ReportMaintenanceFailure(log, "transcript reconciliation", "discord:1:2",
            new HttpRequestException("RedCompute transcript is unavailable (HTTP 503)", null,
                System.Net.HttpStatusCode.ServiceUnavailable), CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("discord:1:2", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phase_that_fails_while_the_run_ends_logs_nothing()
    {
        var log = new RecordingLogger();
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        DiscordBotRuntime.ReportMaintenanceFailure(log, "transcript reconciliation", "discord:1:2",
            new InvalidOperationException("RedCompute transcript is unavailable"), stopping.Token);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Any_other_failure_is_still_an_error()
    {
        var log = new RecordingLogger();
        var failure = new InvalidOperationException("RedCompute transcript epoch changed during settled read");

        DiscordBotRuntime.ReportMaintenanceFailure(log, "transcript reconciliation", "discord:1:2", failure,
            CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
