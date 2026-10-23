using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordSettingsProviderTests
{
    [Fact]
    public void Descriptor_gives_entity_references_for_agent_and_quality_modes()
    {
        var config = new LeafDiscordConfig(
            "application", "public-key", "secret", true,
            "nova", "deep-codex",
            "external", "avatar", new HashSet<string>(), new HashSet<string>(), 1024);
        var status = new DiscordBotStatus(
            "connected", true, true, "Nova", 1, DateTimeOffset.UtcNow, null);
        var agents = new[]
        {
            new DiscordSettingOption(
                "nova", "Nova", "Persistent Agent", "agent-entity", "agent"),
        };
        var modes = new[]
        {
            new DiscordQualityMode(
                "deep-codex", "Deep (Codex)", "codex-default", "deep",
                "gpt-5.6-sol", "high", null, "deep-entity"),
            new DiscordQualityMode(
                "research-codex", "Research (Codex)", "codex-default", "research",
                "gpt-6-astra", "xhigh", null, "research-entity"),
        };

        var descriptor = DiscordSettingsProvider.BuildDescriptor(config, status, agents, modes);

        var access = descriptor["sections"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(section => section["id"]!.GetValue<string>() == "access");
        var fields = access["fields"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToDictionary(field => field["key"]!.GetValue<string>());
        var primary = fields["session_quality_mode"]["options"]!.AsArray();
        // The Sentinel's compute is not a bridge setting any more.
        Assert.False(fields.ContainsKey("sentinel_quality_mode"));

        Assert.Equal("entity_ref", fields["default_agent"]["type"]!.GetValue<string>());
        Assert.Equal("agent", fields["default_agent"]["targetType"]!.GetValue<string>());
        Assert.Equal("agent-entity",
            fields["default_agent"]["options"]![0]!["entity"]!["id"]!.GetValue<string>());
        Assert.Equal("entity_ref", fields["session_quality_mode"]["type"]!.GetValue<string>());
        Assert.Equal("quality-mode",
            fields["session_quality_mode"]["targetType"]!.GetValue<string>());
        Assert.Equal(2, primary.Count);
        Assert.Equal("deep-codex", primary[0]!["value"]!.GetValue<string>());
        Assert.Equal("deep-entity", primary[0]!["entity"]!["id"]!.GetValue<string>());
        Assert.Equal("quality-mode",
            primary[0]!["entity"]!["typeSlug"]!.GetValue<string>());
        var presence = descriptor["sections"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(section => section["id"]!.GetValue<string>() == "presence");
        var presenceFields = presence["fields"]!.AsArray();
        var presenceMode = presenceFields[0]!.AsObject();
        Assert.Equal("select", presenceMode["type"]!.GetValue<string>());
        Assert.Equal("off", presenceMode["value"]!.GetValue<string>());
        Assert.Equal(3, presenceMode["options"]!.AsArray().Count);
        Assert.Equal("presence_extension_feed_enabled",
            presenceFields[1]!["key"]!.GetValue<string>());
        Assert.False(presenceFields[1]!["value"]!.GetValue<bool>());
    }
}
