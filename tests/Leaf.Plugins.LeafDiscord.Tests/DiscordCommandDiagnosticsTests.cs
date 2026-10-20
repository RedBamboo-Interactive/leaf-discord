using System.Text.Json;
using Discord;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordCommandDiagnosticsTests
{
    private static readonly Guid AgentEntity = Guid.Parse("00000000-0000-4000-8000-000000000002");
    private static LeafDiscordConfig Config => DiscordBotConfigurationStore.EmptyDefaults() with
    {
        EntityId = AgentEntity, AgentId = "agent-alpha", ApplicationId = "100000000000000011",
        OwnerUserId = "owner-1",
    };

    [Theory]
    [InlineData(null, "owner", 403)]
    [InlineData("owner-1", "user", 403)]
    [InlineData("other", "owner", 403)]
    [InlineData("other", "admin", 403)]
    [InlineData("owner-1", "owner", 200)]
    [InlineData("local-user", "admin", 200)]
    public async Task Endpoint_enforces_existing_bot_owner_admin_policy_before_remote_read(
        string? user, string role, int expectedStatus)
    {
        var reads = 0;
        var loads = 0;
        var result = await DiscordEndpoints.ReadBotCommandsAsync(AgentEntity, new Auth(user, role),
            (id, _) => { Assert.Equal(AgentEntity, id); loads++; return Task.FromResult<LeafDiscordConfig?>(Config); },
            (id, _) => { Assert.Equal(AgentEntity, id); reads++; return Task.FromResult(Report()); }, default);
        Assert.Equal(expectedStatus, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(expectedStatus == 200 ? 1 : 0, reads);
        if (user is null || role == "user") Assert.Equal(0, loads);
    }

    [Fact]
    public async Task Missing_config_returns_404_without_remote_read()
    {
        var result = await DiscordEndpoints.ReadBotCommandsAsync(AgentEntity, new Auth("owner-1", "owner"),
            (_, _) => Task.FromResult<LeafDiscordConfig?>(null),
            (_, _) => throw new Exception("Remote read must not run"), default);
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("disconnected", 503, "discord_bot_unavailable")]
    [InlineData("api", 502, "discord_commands_read_failed")]
    [InlineData("missing", 404, null)]
    public async Task Errors_are_sanitized_and_never_return_exception_contents(
        string failure, int status, string? code)
    {
        const string sensitive = "opaque response secret-token private-content unrelated-identity";
        Exception exception = failure switch
        {
            "disconnected" => new InvalidOperationException(sensitive),
            "missing" => new KeyNotFoundException(sensitive),
            _ => new HttpRequestException(sensitive),
        };
        var result = await DiscordEndpoints.ReadBotCommandsAsync(AgentEntity, new Auth("owner-1", "owner"),
            (_, _) => Task.FromResult<LeafDiscordConfig?>(Config),
            (_, _) => throw exception, default);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var json = JsonSerializer.Serialize((result as IValueHttpResult)?.Value);
        Assert.DoesNotContain(sensitive, json);
        if (code is not null) Assert.Contains(code, json);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_Discord_api_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => DiscordEndpoints.ReadBotCommandsAsync(
            AgentEntity, new Auth("owner-1", "owner"),
            (_, _) => Task.FromResult<LeafDiscordConfig?>(Config),
            (_, ct) => throw new OperationCanceledException(ct), cancellation.Token));
    }

    [Fact]
    public void Identity_requires_exact_bot_runtime_and_connected_application()
    {
        DiscordCommandDiagnostics.ValidateIdentity(Config, AgentEntity, 100000000000000011);
        Assert.Throws<InvalidOperationException>(() =>
            DiscordCommandDiagnostics.ValidateIdentity(Config, Guid.NewGuid(), 100000000000000011));
        Assert.Throws<InvalidOperationException>(() =>
            DiscordCommandDiagnostics.ValidateIdentity(Config, AgentEntity, 123));
        Assert.Throws<InvalidOperationException>(() =>
            DiscordCommandDiagnostics.ValidateIdentity(Config with { ApplicationId = "invalid" }, AgentEntity, 123));
    }

    [Fact]
    public void Expected_atlas_missing_is_distinct_from_remote_present_with_contexts()
    {
        var missing = Report();
        Assert.Equal("expected_commands_missing", missing.Status);
        Assert.Equal(["atlas"], missing.MissingExpected);
        Assert.Equal(1, missing.MatchingExplicitHandlerCount);
        Assert.False(Assert.Single(missing.Expected, command => command.Name == "atlas").Registered);
        var present = DiscordCommandDiagnostics.Compare(Config, [new Action()],
            Core().Append(new("atlas", "Slash", ["Guild", "BotDm", "PrivateChannel"])).ToArray());
        Assert.Equal("remote_present", present.Status);
        Assert.Empty(present.MissingExpected);
        Assert.Equal(["Guild", "BotDm", "PrivateChannel"],
            Assert.Single(present.Registered, command => command.Name == "atlas").Contexts!);
    }

    [Fact]
    public void Only_handlers_matching_both_bot_and_agent_contribute_expected_commands()
    {
        var absent = DiscordCommandDiagnostics.Compare(Config,
            [new Action(Guid.NewGuid()), new Action(AgentEntity, "other-agent")], Core());
        Assert.Equal(0, absent.MatchingExplicitHandlerCount);
        Assert.Equal("no_matching_explicit_handlers", absent.Status);
        Assert.DoesNotContain(absent.Expected, command => command.Name == "atlas");
        Assert.All(absent.Expected, command => Assert.Equal("core", command.Source));
    }

    [Fact]
    public void Same_named_context_menu_does_not_satisfy_expected_slash_command()
    {
        var report = DiscordCommandDiagnostics.Compare(Config, [new Action()],
            Core().Append(new("atlas", "User", null)).ToArray());
        Assert.Equal(["atlas"], report.MissingExpected);
        Assert.Null(Assert.Single(report.Registered, command => command.Name == "atlas").Contexts);
        var noCore = DiscordCommandDiagnostics.Compare(Config, [], []);
        Assert.Equal(["clear", "help", "privacy", "status"], noCore.MissingExpected);
    }

    private static DiscordRegisteredCommand[] Core() => [
        new("clear", "Slash", null), new("help", "Slash", null),
        new("privacy", "Slash", null), new("status", "Slash", null)];
    private static DiscordCommandReport Report() => DiscordCommandDiagnostics.Compare(Config, [new Action()], Core());
    private sealed class Auth(string? user, string role) : IAuthContext
    {
        public string? UserId => user;
        public string? Email => null;
        public IReadOnlyList<string> Roles => [role];
        public bool IsInRole(string value) => role == value;
    }
    private sealed class Action(Guid? bot = null, string agent = "agent-alpha") : IDiscordExplicitAction
    {
        public string CommandName => "atlas";
        public string Description => "Unused by readback";
        public bool Handles(Guid botId, string agentId) => botId == (bot ?? AgentEntity) && agentId == agent;
        public Task<string> ExecuteAsync(VerifiedDiscordAction request,
            Func<string, CancellationToken, Task<bool>> sendPrivateMessage, CancellationToken ct = default)
            => throw new Exception("Readback must never execute an action");
    }
}
