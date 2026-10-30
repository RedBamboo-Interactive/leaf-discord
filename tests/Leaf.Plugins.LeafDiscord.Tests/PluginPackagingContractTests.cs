using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class PluginPackagingContractTests
{
    [Fact]
    public void Plugin_uses_dynamic_loading_without_the_discord_umbrella()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "Leaf.Plugins.LeafDiscord",
            "Leaf.Plugins.LeafDiscord.csproj");
        var project = XDocument.Load(projectPath);
        var elements = project.Descendants().ToList();

        Assert.Contains(elements, element =>
            element.Name.LocalName == "EnableDynamicLoading"
            && element.Value.Equals("true", StringComparison.OrdinalIgnoreCase));

        var packages = elements
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .ToList();
        Assert.Contains("Discord.Net.WebSocket", packages);
        Assert.DoesNotContain("Discord.Net", packages);
    }

    [Fact]
    public void Field_definition_defaults_match_redleaf_string_seed_contract()
    {
        var root = FindRepositoryRoot();
        using var seeds = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "field-definitions.json")));

        foreach (var field in seeds.RootElement.EnumerateArray())
        {
            if (field.TryGetProperty("defaultValue", out var value))
                Assert.Equal(JsonValueKind.String, value.ValueKind);
        }
    }

    [Fact]
    public void Plugin_contributes_native_redleaf_settings()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "plugin.json")));

        var frontend = manifest.RootElement.GetProperty("frontend");
        Assert.Equal(JsonValueKind.Object, frontend.GetProperty("settings").ValueKind);
    }

    [Fact]
    public void Plugin_is_a_nova_section_instead_of_a_standalone_app()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "plugin.json")));

        var frontend = manifest.RootElement.GetProperty("frontend");
        Assert.False(frontend.TryGetProperty("route", out _));
        Assert.False(frontend.TryGetProperty("sections", out _));
        Assert.Equal(JsonValueKind.Object, frontend.GetProperty("settings").ValueKind);

        var extension = Assert.Single(frontend.GetProperty("extensions").EnumerateArray());
        Assert.Equal("discord", extension.GetProperty("id").GetString());
        Assert.Equal("nova", extension.GetProperty("target").GetString());
        Assert.Equal("app-section", extension.GetProperty("slot").GetString());
        Assert.Equal("discord", extension.GetProperty("section").GetProperty("path").GetString());
        Assert.Equal("/apps/leaf-discord",
            extension.GetProperty("section").GetProperty("legacyRoute").GetString());

        var frontendSource = File.ReadAllText(Path.Combine(root, "web", "src", "plugin.tsx"));
        Assert.Contains("extensions: { discord: Page }", frontendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("commands:", frontendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Bot_token_is_an_encrypted_entity_secret_field()
    {
        var root = FindRepositoryRoot();
        using var seeds = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "field-definitions.json")));

        var token = seeds.RootElement.EnumerateArray().Single(field =>
            field.GetProperty("parentType").GetString() == LeafDiscordConfigStore.TypeSlug
            && field.GetProperty("slug").GetString() == "bot_token");

        Assert.Equal("secret", token.GetProperty("fieldType").GetString());

        var perBotToken = seeds.RootElement.EnumerateArray().Single(field =>
            field.GetProperty("parentType").GetString() == DiscordBotConfigurationStore.TypeSlug
            && field.GetProperty("slug").GetString() == "bot_token");
        Assert.Equal("secret", perBotToken.GetProperty("fieldType").GetString());

        foreach (var slug in new[] { "host-credential", "webhook-urls", "relay-targets" })
        {
            var commonsSecret = seeds.RootElement.EnumerateArray().Single(field =>
                field.GetProperty("parentType").GetString()
                    == DiscordCommonsConfigurationStore.TypeSlug
                && field.GetProperty("slug").GetString() == slug);
            Assert.Equal("secret", commonsSecret.GetProperty("fieldType").GetString());
        }

        var webhookToken = seeds.RootElement.EnumerateArray().Single(field =>
            field.GetProperty("parentType").GetString() == DiscordBindingStore.TypeSlug
            && field.GetProperty("slug").GetString() == "webhook_token");
        Assert.Equal("secret", webhookToken.GetProperty("fieldType").GetString());
        // Seed export and the vault key a field by its "{parent}--{slug}" row slug, taking the
        // part after "--" with hyphens as underscores: that must be the key the binding writes.
        var rowSlug = $"{DiscordBindingStore.TypeSlug}--{webhookToken.GetProperty("slug").GetString()}";
        Assert.Equal("webhook_token", rowSlug[(rowSlug.IndexOf("--") + 2)..].Replace("-", "_"));
    }

    [Fact]
    public void Plugin_does_not_depend_on_the_private_Outfits_extension()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "plugin.json")));

        var requirements = manifest.RootElement.GetProperty("requires")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.DoesNotContain("outfit", requirements);
        Assert.DoesNotContain("outfits", requirements);
    }

    [Fact]
    public void Plugin_contributes_discord_identity_to_the_user_schema()
    {
        var root = FindRepositoryRoot();
        using var seeds = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "field-definitions.json")));

        var identity = seeds.RootElement.EnumerateArray().Single(field =>
            field.GetProperty("parentType").GetString() == "user"
            && field.GetProperty("slug").GetString() == "discord-user-id");

        Assert.Equal("string", identity.GetProperty("fieldType").GetString());
        Assert.False(identity.TryGetProperty("defaultValue", out _));
    }

    [Fact]
    public void Discord_behavior_and_prompt_are_first_class_entity_types()
    {
        var root = FindRepositoryRoot();
        using var types = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "entity-types.json")));
        using var fields = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "field-definitions.json")));

        Assert.Contains(types.RootElement.EnumerateArray(), entity =>
            entity.GetProperty("slug").GetString() == DiscordBehaviorStore.BehaviorTypeSlug);
        Assert.Contains(types.RootElement.EnumerateArray(), entity =>
            entity.GetProperty("slug").GetString() == DiscordBehaviorStore.PromptTypeSlug);

        var behaviorFields = fields.RootElement.EnumerateArray().Where(field =>
            field.GetProperty("parentType").GetString() == DiscordBehaviorStore.BehaviorTypeSlug)
            .ToArray();
        Assert.DoesNotContain(behaviorFields, field =>
            field.GetProperty("fieldType").GetString() == "json");
        var promptReference = Assert.Single(behaviorFields, field =>
            field.GetProperty("slug").GetString() == "prompt");
        Assert.Equal("entity_ref", promptReference.GetProperty("fieldType").GetString());
        Assert.Contains(DiscordBehaviorStore.PromptTypeSlug,
            promptReference.GetProperty("constraints").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conversation_lead_seed_declares_every_field_the_store_writes()
    {
        var root = FindRepositoryRoot();
        using var types = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "entity-types.json")));
        using var fields = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "field-definitions.json")));
        var entities = new MemoryEntityStore();
        var binding = await new DiscordBindingStore(entities).CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "323456789012345678", "dm", null, null, "nova", "owner-1",
            "nova-discord-external", "direct", "Takit"));
        await new DiscordConversationLeadStore(entities).ClaimAsync(
            binding, "d1", "owner-1", "agent-1", TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
        var written = Assert.Single(await entities.QueryAsync(new Leaf.Sdk.EntityQuery
        {
            TypeSlug = DiscordConversationLeadStore.TypeSlug,
        })).Data.Select(pair => pair.Key).ToHashSet();

        Assert.Contains(types.RootElement.EnumerateArray(), entity =>
            entity.GetProperty("slug").GetString() == DiscordConversationLeadStore.TypeSlug);
        var declared = fields.RootElement.EnumerateArray()
            .Where(field => field.GetProperty("parentType").GetString()
                == DiscordConversationLeadStore.TypeSlug)
            .Select(field => field.GetProperty("slug").GetString())
            .ToHashSet();
        Assert.Equal(written.Order(), declared.Order()!);
    }

    [Fact]
    public void Plugin_declares_dm_counterparty_for_durable_avatar_projection()
    {
        var root = FindRepositoryRoot();
        using var seeds = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "field-definitions.json")));

        var counterparty = seeds.RootElement.EnumerateArray().Single(field =>
            field.GetProperty("parentType").GetString() == DiscordBindingStore.TypeSlug
            && field.GetProperty("slug").GetString() == "counterparty_user_id");

        Assert.Equal("string", counterparty.GetProperty("fieldType").GetString());
    }

    internal static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        // Deterministic release builds map source paths to "/_/...", so the caller path
        // only works in local builds; the test binary's own location works in both.
        string?[] starts = [Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory];
        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start)) continue;
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "plugin.json"))
                    && Directory.Exists(Path.Combine(directory.FullName, "seeds")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the leaf-discord repository root.");
    }
}
