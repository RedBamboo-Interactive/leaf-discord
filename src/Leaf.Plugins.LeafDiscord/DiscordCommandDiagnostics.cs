using Discord;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordExpectedCommand(string Name, string Source, bool Registered);
public sealed record DiscordRegisteredCommand(string Name, string Type, string[]? Contexts);
public sealed record DiscordCommandReport(
    Guid BotId, string ApplicationId, int MatchingExplicitHandlerCount,
    string Status, DiscordExpectedCommand[] Expected,
    DiscordRegisteredCommand[] Registered, string[] MissingExpected);

internal static class DiscordCommandDiagnostics
{
    // Readback only: these mirror the four existing core registrations.
    private static readonly string[] CoreNames = ["clear", "status", "privacy", "help"];

    internal static void ValidateIdentity(
        LeafDiscordConfig config, Guid runtimeBotId, ulong connectedApplicationId)
    {
        if (config.EntityId != runtimeBotId
            || !ulong.TryParse(config.ApplicationId, out var applicationId)
            || applicationId != connectedApplicationId)
            throw new InvalidOperationException("Discord bot identity does not match its configuration.");
    }

    internal static DiscordCommandReport Compare(
        LeafDiscordConfig config, IEnumerable<IDiscordExplicitAction> handlers,
        IReadOnlyList<DiscordRegisteredCommand> registered)
    {
        var matching = handlers.Where(handler => handler.Handles(config.EntityId, config.AgentId)).ToArray();
        var slashNames = registered.Where(command => command.Type == ApplicationCommandType.Slash.ToString())
            .Select(command => command.Name).ToHashSet(StringComparer.Ordinal);
        var expected = CoreNames.Select(name => new DiscordExpectedCommand(name, "core", slashNames.Contains(name)))
            .Concat(matching.Select(handler => new DiscordExpectedCommand(
                handler.CommandName, "explicit", slashNames.Contains(handler.CommandName))))
            .OrderBy(command => command.Name, StringComparer.Ordinal).ToArray();
        var missing = expected.Where(command => !command.Registered).Select(command => command.Name)
            .Distinct(StringComparer.Ordinal).ToArray();
        return new(config.EntityId, config.ApplicationId, matching.Length,
            missing.Length > 0 ? "expected_commands_missing"
                : matching.Length == 0 ? "no_matching_explicit_handlers" : "remote_present",
            expected, registered.OrderBy(command => command.Name, StringComparer.Ordinal).ToArray(), missing);
    }
}
