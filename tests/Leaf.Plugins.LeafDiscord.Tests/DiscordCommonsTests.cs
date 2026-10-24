using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Leaf.Plugins.LeafDiscord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordCommonsTests
{
    private const string LabGuildId = "600000000000000006";

    [Fact]
    public async Task Fresh_configuration_has_no_relay_or_guild_default()
    {
        var configurations = new DiscordCommonsConfigurationStore(new MemoryEntityStore());

        var config = await configurations.GetAsync(Guid.NewGuid());

        Assert.NotNull(config);
        Assert.Empty(config.RelayUrl);
        Assert.Empty(config.GuildAllowlist);
        Assert.False(config.Ready);
    }

    [Fact]
    public void Lab_is_silent_but_production_participant_keeps_native_bindings_outside_exclusions()
    {
        var lab = new DiscordCommonsGuildScope(
            LabGuildId, true, new HashSet<string>(), new HashSet<string>(),
            new HashSet<string>(), DiscordCommonsNativeAgentModes.SilentHost,
            "https://lab.example.test", "lab-host");
        var production = new DiscordCommonsGuildScope(
            "400000000000000004", true,
            new HashSet<string>(), new HashSet<string>(),
            new HashSet<string>(["700000000000000007"]),
            DiscordCommonsNativeAgentModes.Participant,
            "https://relay.example.test", "production-host");

        Assert.False(DiscordCommonsRelayService.ShouldReachNativeAgent(
            lab, "any-channel", null));
        Assert.True(DiscordCommonsRelayService.ShouldReachNativeAgent(
            production, "600000000000000006", "500000000000000005"));
        Assert.True(DiscordCommonsRelayService.ShouldReachNativeAgent(
            production, "700000000000000007", null));
        Assert.True(DiscordCommonsRelayService.ShouldReachNativeAgent(
            null, "800000000000000008", null));
    }

    [Fact]
    public async Task Configuration_projection_never_returns_credentials_or_webhook_urls()
    {
        var store = new MemoryEntityStore();
        var configurations = new DiscordCommonsConfigurationStore(store);
        var botId = Guid.NewGuid();
        var saved = await configurations.SaveAsync(botId,
            new DiscordCommonsConfigurationUpdate(
                "https://relay.example.test",
                "host-secret-for-test",
                false,
                [LabGuildId]));
        _ = await configurations.SetWebhookUrlAsync(
            botId,
            "700000000000000007",
            "https://discord.com/api/webhooks/123456789012345678/test-token");

        var serialized = JsonSerializer.Serialize(
            DiscordCommonsConfigurationStore.View(
                (await configurations.GetAsync(botId))!));

        Assert.True(saved.Ready);
        Assert.True(saved.Scope(LabGuildId)!.WholeGuild);
        Assert.DoesNotContain("host-secret-for-test", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", serialized, StringComparison.Ordinal);
        Assert.Contains("HostCredentialConfigured", serialized, StringComparison.Ordinal);
        Assert.Contains("700000000000000007", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingest_queue_is_durable_and_idempotent_by_event_id()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordCommonsIngestStore(store);
        var botId = Guid.NewGuid();
        var payload = new JsonObject
        {
            ["type"] = "human_message",
            ["eventId"] = "gateway-event-1",
            ["content"] = "hello",
        };

        var first = await deliveries.EnqueueAsync(
            botId, LabGuildId, "gateway-event-1", payload);
        var duplicate = await deliveries.EnqueueAsync(
            botId, LabGuildId, "gateway-event-1", payload);

        Assert.Equal(first.EntityId, duplicate.EntityId);
        Assert.Equal(LabGuildId, first.GuildId);
        Assert.Single(await deliveries.PendingAsync());
        await deliveries.MarkSentAsync(first);
        Assert.Empty(await deliveries.PendingAsync());
        Assert.Null((await store.GetAsync(first.EntityId))!.Data["payload"]);
    }

    [Fact]
    public void Announcements_are_read_only_events_from_humans_or_the_native_host_only()
    {
        var payload = DiscordCommonsRelayService.BuildAnnouncementPayload(
            "announcement:10:create", "10", "create", "human", "20", "Laurent",
            "Release notes", "30", "announcements", 1_234);

        Assert.Equal("announcement", payload["type"]!.GetValue<string>());
        Assert.Equal("10", payload["messageId"]!.GetValue<string>());
        Assert.Equal("announcements", payload["channel"]!.GetValue<string>());
        Assert.True(DiscordBotRuntime.ShouldForwardAnnouncement(
            MessageSource.User, false, 20, 99));
        Assert.True(DiscordBotRuntime.ShouldForwardAnnouncement(
            MessageSource.Bot, true, 99, 99));
        Assert.False(DiscordBotRuntime.ShouldForwardAnnouncement(
            MessageSource.Bot, true, 98, 99));
        Assert.False(DiscordBotRuntime.ShouldForwardAnnouncement(
            MessageSource.Webhook, true, 99, 99));
    }

    [Fact]
    public async Task Per_guild_scopes_keep_separate_encrypted_relay_targets()
    {
        var configurations = new DiscordCommonsConfigurationStore(new MemoryEntityStore());
        var botId = Guid.NewGuid();
        var saved = await configurations.SaveAsync(botId,
            new DiscordCommonsConfigurationUpdate("", null, false, null,
            [
                new DiscordCommonsGuildScopeUpdate(
                    LabGuildId, true, [], [], "https://lab.example.test", "lab-secret"),
                new DiscordCommonsGuildScopeUpdate(
                    "400000000000000004", false, ["500000000000000005"], [],
                    "https://relay.example.test", "production-secret", false,
                    ["700000000000000007"], DiscordCommonsNativeAgentModes.Participant),
            ]));

        Assert.Equal("https://lab.example.test", saved.Scope(LabGuildId)!.RelayUrl);
        Assert.True(saved.Scope(LabGuildId)!.Contains("anything", null));
        var production = saved.Scope("400000000000000004")!;
        Assert.True(production.Contains("600000000000000006", "500000000000000005"));
        Assert.False(production.Contains("700000000000000007", "500000000000000005"));
        Assert.False(production.Contains("600000000000000006", "700000000000000007"));
        Assert.Equal(DiscordCommonsNativeAgentModes.Participant, production.NativeAgentMode);
        var view = JsonSerializer.Serialize(DiscordCommonsConfigurationStore.View(saved));
        Assert.DoesNotContain("lab-secret", view, StringComparison.Ordinal);
        Assert.DoesNotContain("production-secret", view, StringComparison.Ordinal);
        Assert.Equal(2, saved.GuildScopes.Count);

        var preserved = await configurations.SaveAsync(botId,
            new DiscordCommonsConfigurationUpdate("", null, false, null,
                saved.GuildScopes.Values.Select(scope =>
                    new DiscordCommonsGuildScopeUpdate(
                        scope.GuildId, scope.WholeGuild,
                        scope.CategoryIds.ToArray(), scope.ChannelIds.ToArray(),
                        scope.RelayUrl, null, false,
                        scope.ExcludedChannelIds.ToArray(), scope.NativeAgentMode)).ToArray()));
        Assert.Equal("lab-secret", preserved.Scope(LabGuildId)!.HostCredential);
        Assert.Equal("production-secret",
            preserved.Scope("400000000000000004")!.HostCredential);
    }

    [Fact]
    public async Task Production_whole_guild_participant_scope_persists_read_only_exclusions()
    {
        var configurations = new DiscordCommonsConfigurationStore(new MemoryEntityStore());
        var botId = Guid.NewGuid();
        var saved = await configurations.SaveAsync(botId,
            new DiscordCommonsConfigurationUpdate("", null, false, null,
            [
                new DiscordCommonsGuildScopeUpdate(
                    "400000000000000004", true, [], [],
                    "https://relay.example.test", "production-secret", false,
                    ["700000000000000007"], DiscordCommonsNativeAgentModes.Participant),
            ]));

        var scope = saved.Scope("400000000000000004")!;
        Assert.True(scope.WholeGuild);
        Assert.False(scope.Contains("700000000000000007", null));
        Assert.True(scope.Contains("800000000000000008", null));
        Assert.False(scope.SuppressesNativeAgent);
        var view = DiscordCommonsConfigurationStore.View(saved).GuildScopes.Single();
        Assert.Equal(["700000000000000007"], view.ExcludedChannelIds);
        Assert.Equal(DiscordCommonsNativeAgentModes.Participant, view.NativeAgentMode);
    }

    [Fact]
    public async Task Fresh_verified_consult_binding_is_created_and_governed_through_posted_reply()
    {
        var store = new MemoryEntityStore();
        var turns = new DiscordCommonsHostTurnStore(store);
        var bindingStore = new DiscordBindingStore(store);
        var binding = await bindingStore.GetOrCreateAsync(new CreateDiscordBinding(
            "100000000000000001", "200000000000000002", "thread",
            "400000000000000004", "300000000000000003", "agent-1", "owner-1", "public",
            "participate", "Fresh consult", "Leaf", null));
        Assert.Equal(binding.EntityId,
            (await bindingStore.FindAsync(
                "100000000000000001", "200000000000000002"))!.EntityId);
        var message = new DiscordCommonsHostMessage(
            "message-1", "envoy-1", "Atlas · Alice", "Alice", "Nova, can you help?",
            "200000000000000002", "consult", "agent-consult", "300000000000000003");

        var pending = await turns.SetPendingAsync(
            binding, "300000000000000003", "agent-consult", message, "host:app");
        Assert.Equal("pending", (await turns.GetPendingAsync(binding))!.State);
        await turns.CompleteAsync(pending, "authorized");
        Assert.Equal("authorized", (await turns.GetPendingAsync(binding))!.State);
        await turns.CompleteAsync(pending, "posted");
        Assert.Null(await turns.GetPendingAsync(binding));
    }

    [Fact]
    public async Task Human_admission_completes_the_pending_peer_turn_before_native_reply()
    {
        var store = new MemoryEntityStore();
        var turns = new DiscordCommonsHostTurnStore(store);
        var binding = await new DiscordBindingStore(store).CreateAsync(
            new CreateDiscordBinding(
                "100000000000000001", "200000000000000002", "thread",
                "400000000000000004", "300000000000000003", "agent-1", "owner-1",
                "public", "participate", "Fresh consult", "Leaf", null));
        var message = new DiscordCommonsHostMessage(
            "message-1", "envoy-1", "Atlas · Alice", "Alice", "Nova, help", "thread-1",
            "consult", "agent-consult", "300000000000000003");
        await turns.SetPendingAsync(
            binding, "300000000000000003", "agent-consult", message, "host:app");

        Assert.True(await turns.CompletePendingAsync(binding, "human-joined"));
        Assert.Null(await turns.GetPendingAsync(binding));
        Assert.False(await turns.CompletePendingAsync(binding, "human-joined"));
    }

    [Theory]
    [InlineData(true, DiscordResponseModes.Participate, true)]
    [InlineData(true, DiscordResponseModes.Selective, true)]
    [InlineData(true, DiscordResponseModes.ContextOnly, false)]
    [InlineData(false, DiscordResponseModes.Participate, false)]
    public async Task Verified_peer_respects_binding_enabled_and_context_only_modes(
        bool enabled, string responseMode, bool expected)
    {
        var store = new MemoryEntityStore();
        var bindings = new DiscordBindingStore(store);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "200000000000000002", "thread",
            "400000000000000004", "300000000000000003", "agent-1", "owner-1",
            "public", responseMode, "Fresh consult", "Leaf", null));
        if (!enabled)
        {
            await store.PatchAsync(binding.EntityId,
                new JsonObject { ["enabled"] = false });
            binding = (await bindings.GetAsync(binding.EntityId))!;
        }

        var admitted = await DiscordBotRuntime.ApplyVerifiedPeerBindingPolicyAsync(
            bindings, binding, responseMode, "peer-message-1");

        Assert.Equal(expected, admitted);
        var updated = (await bindings.GetAsync(binding.EntityId))!;
        Assert.Equal(expected ? null : "peer-message-1", updated.LastReceivedSnowflake);
    }

    [Theory]
    [InlineData("project-showcase", "", true)]
    [InlineData("showcases", "", false)]
    [InlineData("feedback", "Share projects, builds, creations, and demos here", false)]
    [InlineData("general", "Share your builds here", false)]
    [InlineData("general", "General conversation [leaf-community:showcase]", true)]
    [InlineData("general", "[LEAF-COMMUNITY:SHOWCASE]", true)]
    [InlineData("agent-consult", "Public Agent consultations", false)]
    public void Showcase_defaults_require_a_name_word_or_explicit_topic_marker(
        string name, string topic, bool expected)
        => Assert.Equal(expected,
            DiscordBotRuntime.LooksLikeShowcaseChannel(name, topic));

    [Theory]
    [InlineData(false, true, false, "text")]
    [InlineData(false, false, true, "forum")]
    [InlineData(false, false, false, null)]
    [InlineData(true, true, false, null)]
    [InlineData(true, false, true, null)]
    public void Catalog_contains_only_top_level_text_and_forum_channels(
        bool isThread, bool isText, bool isForum, string? expected)
        => Assert.Equal(expected,
            DiscordBotRuntime.CommonsCatalogChannelKind(isThread, isText, isForum));

    [Theory]
    [InlineData("général", "general")]
    [InlineData("GENERAL", "general")]
    [InlineData("nova-consult", "nova-consult")]
    public void Discord_channel_names_map_to_protocol_channels(
        string source, string expected)
        => Assert.Equal(expected, DiscordCommonsRelayService.ChannelName(source));

    [Fact]
    public void Human_ingest_forwards_reply_reference_and_role_mentions_without_opening_floor()
    {
        var payload = DiscordCommonsRelayService.BuildHumanMessagePayload(
            "message-1", "human-1", "Lynial", "hello", [],
            ["900000000000000009"], "700000000000000007", "nova-consult", "thread-1",
            "800000000000000008", 1234);

        Assert.Equal("800000000000000008",
            payload["referencedMessageId"]!.GetValue<string>());
        Assert.Equal("900000000000000009",
            payload["mentionRoles"]![0]!.GetValue<string>());
        Assert.Equal("700000000000000007",
            payload["channelId"]!.GetValue<string>());
        Assert.False(payload["opensFloor"]!.GetValue<bool>());
    }

    [Fact]
    public void Human_ingest_describes_attachments_for_the_relay_and_omits_the_key_without_any()
    {
        var payload = DiscordCommonsRelayService.BuildHumanMessagePayload(
            "message-1", "human-1", "Lynial", "What is wrong here?", ["user-1"], [],
            "700000000000000007", "nova-consult", "thread-1", null, 1234,
            [
                new("111", "shot.png", "image/png", 48213, 1280, 720,
                    "https://cdn.discordapp.com/attachments/7/111/shot.png?ex=1", "the error"),
                new("112", "notes.pdf", "application/pdf", 900, null, null,
                    "https://cdn.discordapp.com/attachments/7/112/notes.pdf", null),
            ]);

        var attachments = payload["attachments"]!.AsArray();
        Assert.Equal(2, attachments.Count);
        var image = attachments[0]!.AsObject();
        Assert.Equal("111", image["id"]!.GetValue<string>());
        Assert.Equal("shot.png", image["filename"]!.GetValue<string>());
        Assert.Equal("image/png", image["contentType"]!.GetValue<string>());
        Assert.Equal(48213, image["size"]!.GetValue<long>());
        Assert.Equal(1280, image["width"]!.GetValue<int>());
        Assert.Equal(720, image["height"]!.GetValue<int>());
        Assert.Equal("https://cdn.discordapp.com/attachments/7/111/shot.png?ex=1", image["url"]!.GetValue<string>());
        Assert.Equal("the error", image["description"]!.GetValue<string>());
        // The relay decides what is forwarded; the host reports every attachment it saw.
        Assert.Equal("application/pdf", attachments[1]!["contentType"]!.GetValue<string>());

        var plain = DiscordCommonsRelayService.BuildHumanMessagePayload(
            "message-2", "human-1", "Lynial", "hello", [], [], "700000000000000007", "nova-consult",
            "thread-1", null, 1234, []);
        Assert.False(plain.ContainsKey("attachments"));
        var announcement = DiscordCommonsRelayService.BuildHumanMessagePayload(
            "message-3", "human-1", "Lynial", "news", [], [], "700000000000000007", "announcements",
            null, null, 1234, [new("113", "a.png", "image/png", 10, 1, 1, "https://cdn.discordapp.com/a.png", null)]);
        Assert.False(announcement.ContainsKey("attachments"));
    }

    [Fact]
    public void Native_host_posts_report_their_attachments_to_the_relay()
    {
        var attachments = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "77", ["filename"] = "chart.png", ["content_type"] = "image/png", ["size"] = 3,
            ["url"] = "https://cdn.discordapp.com/attachments/1/77/chart.png",
        });

        var payload = DiscordCommonsRelayService.BuildHostParticipantPostPayload(
            "sent-1", "host:nova", "Here is the chart.", "envoy-1", "nova-consult", "700",
            "thread-1", 1234, attachments);
        var plain = DiscordCommonsRelayService.BuildHostParticipantPostPayload(
            "sent-2", "host:nova", "Text.", "envoy-1", "nova-consult", "700", "thread-1", 1234, null);

        Assert.Equal("host_participant_post", payload["type"]!.GetValue<string>());
        Assert.Equal("host-post:sent-1", payload["eventId"]!.GetValue<string>());
        Assert.Equal("envoy-1", payload["addresses"]![0]!.GetValue<string>());
        Assert.Equal("https://cdn.discordapp.com/attachments/1/77/chart.png",
            payload["attachments"]![0]!["url"]!.GetValue<string>());
        Assert.False(plain.ContainsKey("attachments"));
    }

    [Fact]
    public void Announcements_ingest_is_listening_only_and_strips_every_addressing_signal()
    {
        var payload = DiscordCommonsRelayService.BuildHumanMessagePayload(
            "announcement-1", "human-1", "Lynial", "Nova Lynial ships this",
            ["user-mention"], ["role-mention"], "700000000000000007", "announcements", "announcements",
            "envoy-message", 1234);

        Assert.Empty(payload["mentions"]!.AsArray());
        Assert.Empty(payload["mentionRoles"]!.AsArray());
        Assert.Null(payload["referencedMessageId"]);
        Assert.Null(payload["threadRef"]);
        Assert.False(payload["opensFloor"]!.GetValue<bool>());
        Assert.Equal("Nova Lynial ships this", payload["content"]!.GetValue<string>());
    }

    [Fact]
    public void Envoy_roles_are_mentionable_permissionless_and_follow_public_name()
    {
        var envoy = new DiscordCommonsRelayEnvoy(
            "envoy-1", "user-1", "Owner", "agent-1", "Atlas", "Lynial", "Atlas · Lynial",
            "active", "role-1");
        var stale = new DiscordManagedRole(
            "role-1", "Nova Old", "8", false, false);

        Assert.Equal("Atlas Lynial", DiscordCommonsRolePolicy.RoleName(envoy));
        Assert.True(DiscordCommonsRolePolicy.NeedsUpdate(stale, "Atlas Lynial"));
        Assert.False(DiscordCommonsRolePolicy.NeedsUpdate(
            stale with { Name = "Atlas Lynial", Permissions = "0", Mentionable = true },
            "Atlas Lynial"));
    }

    [Fact]
    public void Role_reconciliation_fails_clearly_without_manage_roles_and_caps_active_envoys()
    {
        var envoy = new DiscordCommonsRelayEnvoy(
            "envoy-1", "user-1", "Owner", "agent-1", "Atlas", "Owner", "Atlas · Owner",
            "active", null);
        var missing = Assert.Throws<UnauthorizedAccessException>(() =>
            DiscordCommonsRolePolicy.EnsureCanReconcile(false, [envoy]));
        Assert.Contains("Manage Roles", missing.Message, StringComparison.Ordinal);

        var tooMany = Enumerable.Range(0, DiscordCommonsRolePolicy.MaximumRolesPerGuild + 1)
            .Select(index => envoy with { Id = $"envoy-{index}" }).ToArray();
        var capped = Assert.Throws<InvalidOperationException>(() =>
            DiscordCommonsRolePolicy.EnsureCanReconcile(true, tooMany));
        Assert.Contains("cap", capped.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suspended_envoys_are_not_counted_as_active_role_targets()
    {
        var suspended = new DiscordCommonsRelayEnvoy(
            "envoy-1", "user-1", "Owner", "agent-1", "Atlas", "Owner", "Atlas · Owner",
            "suspended", "role-1");
        var manySuspended = Enumerable.Range(0, 100)
            .Select(index => suspended with { Id = $"envoy-{index}" }).ToArray();

        DiscordCommonsRolePolicy.EnsureCanReconcile(true, manySuspended);
    }
}
