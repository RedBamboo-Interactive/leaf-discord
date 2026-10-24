using System.Net;
using System.Text.Json;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordCoordinatorTests
{
    [Fact]
    public void Coordinator_exposes_one_public_constructor_for_dependency_injection()
        => Assert.Single(typeof(DiscordConversationCoordinator).GetConstructors());

    [Fact]
    public void External_conversation_page_keeps_legacy_constructor_abi()
    {
        var constructor = typeof(ExternalConversationPage).GetConstructor([
            typeof(IReadOnlyList<ExternalConversationMessage>),
            typeof(ExternalConversationCursor),
            typeof(bool),
        ]);

        Assert.NotNull(constructor);
        Assert.NotNull(typeof(ExternalConversationPage).GetProperty(nameof(ExternalConversationPage.HasMore)));
    }

    [Fact]
    public void External_conversation_message_keeps_legacy_constructor_abi()
    {
        var constructor = typeof(ExternalConversationMessage).GetConstructor([
            typeof(ExternalConversationCursor), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(DateTimeOffset), typeof(System.Text.Json.Nodes.JsonObject),
            typeof(string), typeof(string), typeof(string),
            typeof(System.Text.Json.Nodes.JsonObject), typeof(string),
        ]);

        Assert.NotNull(constructor);
        Assert.NotNull(typeof(ExternalConversationMessage).GetProperty(
            nameof(ExternalConversationMessage.DeliveryContent)));
        Assert.NotNull(typeof(ExternalConversationMessage).GetProperty(
            nameof(ExternalConversationMessage.OutputAssets)));
    }

    [Fact]
    public async Task Received_message_retries_after_compute_admission_failure()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var config = new LeafDiscordConfigStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider();
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, new DiscordBotConfigurationStore(config),
            new DiscordRuntimeOptionsResolver(entities),
            IdentityResolver(entities), new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);
        var attachment = new ExternalConversationAttachment(
            "a1", "trace.txt", "text/plain", 12, new string('a', 64), "/api/assets/a1.txt");
        var guildContext = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = "leaf-discord-guild-context/v1",
            ["source"] = "discord_gateway",
            ["trust"] = "untrusted_social_context",
            ["mode"] = "active",
        };
        var behaviorContext = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = "leaf-discord-behavior/v1",
            ["source"] = "leaf_owner_configuration",
            ["trust"] = "trusted_owner_configuration",
            ["scope"] = "channel",
            ["response_mode"] = "selective",
            ["instructions"] = "Stay focused on diagnostics.",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SubmitAsync(
            binding, "1001", new ExternalRequestor("42", "Alice"), "diagnostic", [attachment],
            guildContext: guildContext, behaviorContext: behaviorContext));
        Assert.Single(await deliveries.PendingInboundAsync(binding.BindingId));
        await coordinator.EnqueueNoticeAsync(binding, "1001",
            "I could not admit that message. The failure has been recorded in Leaf.");
        Assert.Single(await deliveries.PendingOutboundAsync(binding.BindingId));
        guildContext["mode"] = "mutated-after-receipt";
        behaviorContext["instructions"] = "mutated-after-receipt";

        await coordinator.RetryPendingAsync(binding);

        Assert.Empty(await deliveries.PendingInboundAsync(binding.BindingId));
        Assert.Empty(await deliveries.PendingOutboundAsync(binding.BindingId));
        Assert.Equal(2, provider.SendAttempts);
        // The bridge no longer chooses anything about the Sentinel: no sentinel_* metadata at all.
        Assert.Equal("nova", provider.LastInput?.Metadata?["agent_id"]?.GetValue<string>());
        Assert.DoesNotContain(provider.LastInput!.Metadata!,
            pair => pair.Key.StartsWith("sentinel_", StringComparison.Ordinal));
        Assert.Equal(attachment, Assert.Single(provider.LastInput!.Attachments));
        Assert.Equal("active",
            provider.LastInput.Metadata?["discord_guild_context"]?["mode"]?.GetValue<string>());
        Assert.Equal("Stay focused on diagnostics.",
            provider.LastInput.Metadata?["discord_behavior"]?["instructions"]?.GetValue<string>());
    }

    [Fact]
    public async Task Server_map_is_sent_in_full_once_then_referenced_until_it_changes()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false);
        using var coordinator = Coordinator(entities, bindings, deliveries, provider);

        await Submit(coordinator, binding, "1001", "general");
        Assert.NotNull(SentServerMap(provider)["channels"]);
        var fullHash = SentServerMap(provider)["hash"]!.GetValue<string>();

        await Submit(coordinator, binding, "1002", "general");
        var reference = SentServerMap(provider);
        Assert.True(reference["unchanged"]!.GetValue<bool>());
        Assert.Equal(fullHash, reference["hash"]!.GetValue<string>());
        Assert.Null(reference["channels"]);
        // The full map stays in the inbound record so a retry can re-plan correctly.
        Assert.Equal(2, provider.SendAttempts);

        // Durable: a fresh store (as after a RedLeaf restart) sees the recorded disclosure.
        var persisted = (await new DiscordBindingStore(entities).GetAsync(binding.EntityId))!
            .ServerMapDisclosure!;
        Assert.Equal(fullHash, persisted.Hash);
        Assert.Equal(1, persisted.TurnsSinceFull);
        Assert.Equal("session", persisted.SessionId);

        await Submit(coordinator, binding, "1003", "renamed-general");
        Assert.Equal("renamed-general",
            SentServerMap(provider)["channels"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_or_duplicate_turns_do_not_advance_server_map_disclosure()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider();
        using var coordinator = Coordinator(entities, bindings, deliveries, provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Submit(coordinator, binding, "1001", "general"));
        Assert.Null((await bindings.GetAsync(binding.EntityId))!.ServerMapDisclosure);

        await coordinator.RetryPendingAsync(binding);
        Assert.NotNull(SentServerMap(provider)["channels"]);
        Assert.Equal(0, (await bindings.GetAsync(binding.EntityId))!
            .ServerMapDisclosure!.TurnsSinceFull);

        await Submit(coordinator, binding, "1002", "general");
        Assert.True(SentServerMap(provider)["unchanged"]!.GetValue<bool>());
        var attempts = provider.SendAttempts;

        Assert.Null(await Submit(coordinator, binding, "1002", "general"));
        Assert.Equal(attempts, provider.SendAttempts);
        Assert.Equal(1, (await bindings.GetAsync(binding.EntityId))!
            .ServerMapDisclosure!.TurnsSinceFull);
    }

    [Fact]
    public async Task Clear_resets_server_map_disclosure_for_the_next_generation()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var configurations = await SeedBotConfigurationAsync(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false);
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, configurations,
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await Submit(coordinator, binding, "1001", "general");
        await Submit(coordinator, binding, "1002", "general");
        Assert.True(SentServerMap(provider)["unchanged"]!.GetValue<bool>());

        var cleared = await coordinator.ClearAsync(binding, "clear-1");
        Assert.Null(cleared.ServerMapDisclosure);

        await Submit(coordinator, cleared, "1003", "general");
        Assert.NotNull(SentServerMap(provider)["channels"]);
        Assert.Equal(2, (await bindings.GetAsync(binding.EntityId))!
            .ServerMapDisclosure!.Generation);
    }

    private static DiscordConversationCoordinator Coordinator(
        MemoryEntityStore entities, DiscordBindingStore bindings,
        DiscordDeliveryStore deliveries, FlakyProvider provider)
        => new(bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

    private static Task<ExternalConversationAdmission?> Submit(
        DiscordConversationCoordinator coordinator, DiscordBinding binding,
        string messageId, string channelName)
        => coordinator.SubmitAsync(
            binding, messageId, new ExternalRequestor("42", "Alice"), "ola!",
            guildContext: DiscordServerMapDisclosureTests.Context(
                DiscordServerMapDisclosureTests.Map(channelName, DateTimeOffset.UtcNow)),
            behaviorContext: new System.Text.Json.Nodes.JsonObject());

    private static System.Text.Json.Nodes.JsonObject SentServerMap(FlakyProvider provider)
        => provider.LastInput!.Metadata!["discord_guild_context"]!["server_map"]!.AsObject();

    [Fact]
    public async Task Proactive_message_is_injected_into_the_dm_session_and_queued_once()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "dm",
            null, null, "nova", "owner-1",
            "nova-discord-external", "direct", "Alice"));
        var compute = new FakeComputeGateway();
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), compute,
            [new FlakyProvider(false)], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        var result = await coordinator.SendProactiveAsync(
            binding, "followup-1", "Could you send me the complete trace?");

        Assert.NotNull(result.Binding.SessionId);
        var request = Assert.Single(compute.Requests);
        Assert.EndsWith($"/ai-session/sessions/{result.Binding.SessionId}/inject",
            request.Uri, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("assistant", body.RootElement.GetProperty("role").GetString());
        Assert.Equal(result.MessageUid, body.RootElement.GetProperty("messageUid").GetString());
        var queued = Assert.Single(await deliveries.PendingOutboundAsync(binding.BindingId));
        Assert.Equal(result.MessageUid, queued.MessageUid);
    }

    [Fact]
    public async Task Already_delivered_forum_starter_is_recorded_without_being_queued_again()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "323456789012345678", "thread",
            "223456789012345678", "123456789012345678", "nova", "owner-1",
            "nova-discord-external", "participate", "Forum ownership"));
        var compute = new FakeComputeGateway();
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), compute,
            [new FlakyProvider(false)], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        var result = await coordinator.RecordDeliveredProactiveAsync(
            binding, "forum-create-1", "Opening this workstream.",
            "323456789012345678");

        Assert.NotNull(result.Binding.SessionId);
        Assert.Single(compute.Requests);
        Assert.Empty(await deliveries.PendingOutboundAsync(binding.BindingId));
        var recorded = await deliveries.EnqueueOutboundAsync(
            result.Binding,
            new ExternalConversationMessage(
                new ExternalConversationCursor("leaf-discord-outreach", 0),
                result.MessageUid, "assistant", "text", "Opening this workstream.",
                DateTimeOffset.UtcNow),
            0,
            "Opening this workstream.");
        Assert.Equal("sent", recorded.State);
        Assert.Equal("323456789012345678", recorded.DiscordId);
    }

    [Fact]
    public async Task Existing_binding_message_reuses_the_exact_session_and_retry_identity()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate", "general"));
        var provider = new FlakyProvider(false);
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation,
            "existing-conversation", "existing-session"));
        var compute = new FakeComputeGateway();
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), compute,
            [provider], new PromptInjectionDetector(), new DiscordEgressGuard(),
            new DiscordTypingLeases(), NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        var first = await coordinator.SendExistingProactiveAsync(
            binding, "owner-request-1", "Please apply RedLeaf 0.1.22.");
        var retry = await coordinator.SendExistingProactiveAsync(
            binding, "owner-request-1", "Please apply RedLeaf 0.1.22.");

        Assert.Equal("existing-session", first.Binding.SessionId);
        Assert.Equal(first.MessageUid, retry.MessageUid);
        Assert.Equal(0, provider.OpenAttempts);
        Assert.Equal(2, compute.Requests.Count);
        Assert.All(compute.Requests, request => Assert.EndsWith(
            "/ai-session/sessions/existing-session/inject", request.Uri,
            StringComparison.Ordinal));
        Assert.Single(await deliveries.PendingOutboundAsync(binding.BindingId));
    }

    [Fact]
    public async Task Existing_binding_message_with_an_innocuous_image_is_reviewed_with_it_then_injected_and_queued()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate", "general"));
        var provider = new FlakyProvider(false);
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation,
            "existing-conversation", "existing-session"));
        var compute = new FakeComputeGateway();
        var sentinel = new TestSentinel();
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), compute,
            [provider], new PromptInjectionDetector(), new DiscordEgressGuard(),
            new DiscordTypingLeases(), NullLogger<DiscordConversationCoordinator>.Instance, sentinel);
        const string canonical =
            "A casual image.\n\n![Quiet science-fiction reading nook](/api/assets/casual.png)";

        // The Sentinel sees the image itself; an innocuous one passes like innocuous text.
        await coordinator.SendExistingProactiveAsync(
            binding,
            "owner-image-request-1",
            canonical,
            "A casual image.",
            [new ExternalConversationOutputAsset(
                "casual.png", "Quiet science-fiction reading nook")]);

        var review = Assert.Single(sentinel.Requests);
        Assert.Equal("A casual image.", review.Content);
        Assert.Equal(new UntrustedContentImage("casual.png", "image/png", "Quiet science-fiction reading nook"),
            Assert.Single(review.Images!));
        Assert.Single(compute.Requests);
        var queued = Assert.Single(await deliveries.PendingOutboundAsync(binding.BindingId));
        Assert.Equal("delivered", queued.OutboundReview?["outcome"]?.GetValue<string>());
        Assert.Equal(1, queued.OutboundReview?["images_reviewed"]?.GetValue<int>());
        Assert.Equal("casual.png", queued.Payload?["assets"]?[0]?["asset_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Existing_binding_message_rejects_private_asset_markdown_without_typed_asset()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate", "general"));
        var provider = new FlakyProvider(false);
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation,
            "existing-conversation", "existing-session"));
        var compute = new FakeComputeGateway();
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), compute,
            [provider], new PromptInjectionDetector(), new DiscordEgressGuard(),
            new DiscordTypingLeases(), NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.SendExistingProactiveAsync(
                binding,
                "owner-image-request-without-asset",
                "![Reading nook](/api/assets/casual.png)"));

        Assert.Contains("typed Discord output Assets", error.Message, StringComparison.Ordinal);
        Assert.Empty(compute.Requests);
        Assert.Empty(await deliveries.PendingOutboundAsync(binding.BindingId));
    }

    [Fact]
    public async Task Rejected_transcript_injection_never_queues_a_discord_delivery()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate", "general"));
        var provider = new FlakyProvider(false);
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation,
            "existing-conversation", "existing-session"));
        var compute = new FakeComputeGateway { InjectionAccepted = false };
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), compute,
            [provider], new PromptInjectionDetector(), new DiscordEgressGuard(),
            new DiscordTypingLeases(), NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.SendExistingProactiveAsync(
                binding, "owner-request-1", "Please apply RedLeaf 0.1.22."));

        Assert.Empty(await deliveries.PendingOutboundAsync(binding.BindingId));
    }

    [Fact]
    public async Task Existing_binding_message_never_creates_a_missing_session()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "dm",
            null, null, "nova", "owner-1", "nova-discord-external", "direct", "Alice"));
        var provider = new FlakyProvider(false);
        var compute = new FakeComputeGateway();
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), compute,
            [provider], new PromptInjectionDetector(), new DiscordEgressGuard(),
            new DiscordTypingLeases(), NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await Assert.ThrowsAsync<DiscordBindingSessionChangedException>(() =>
            coordinator.SendExistingProactiveAsync(
                binding, "owner-request-1", "Please apply RedLeaf 0.1.22."));

        Assert.Equal(0, provider.OpenAttempts);
        Assert.Empty(compute.Requests);
        Assert.Empty(await deliveries.PendingOutboundAsync(binding.BindingId));
    }
    [Fact]
    public async Task Reconcile_projects_one_editable_progress_turn_then_final_without_replay()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false);
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        provider.Messages.AddRange([
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 1), "user-1",
                "user", "text", "question", DateTimeOffset.UtcNow),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 2), "turn-1",
                "assistant", "commentary", "I am checking that now.", DateTimeOffset.UtcNow),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 3), "turn-1",
                "assistant", "tool_use", null, DateTimeOffset.UtcNow, ToolName: "inspect"),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 4), "turn-1",
                "assistant", "tool_result", null, DateTimeOffset.UtcNow, ToolName: "inspect"),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 5), "turn-1",
                "assistant", "tool_use", null, DateTimeOffset.UtcNow,
                ToolName: "PowerShell",
                ToolInput: "{\"command\":\"Set-Content -LiteralPath file.txt -Value changed\"}"),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 6), "turn-1",
                "assistant", "commentary", "I found the cause.", DateTimeOffset.UtcNow),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 7), "turn-1",
                "assistant", "text", "Found it.", DateTimeOffset.UtcNow),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 8), "reaction-only-1",
                "assistant", "text", DiscordControlMessages.NoReply, DateTimeOffset.UtcNow),
        ]);
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), new FakeComputeGateway(),
            [provider], new PromptInjectionDetector(), new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await coordinator.ReconcileAsync(binding);
        var firstPass = await deliveries.PendingOutboundAsync(binding.BindingId);

        // Commentary is model-written and never posted; only the markers rail is.
        Assert.Equal(4, firstPass.Count);
        Assert.All(firstPass.Take(3), item =>
        {
            Assert.Equal("turn-1", item.MessageUid);
            Assert.Equal(DiscordDeliveryStore.ProgressKind, item.Kind);
        });
        Assert.Equal(
            $"-# {DiscordDeliveryStore.ToolUseMarker} {DiscordDeliveryStore.ToolResultMarker} {DiscordDeliveryStore.MutatingToolMarker}",
            firstPass[2].Payload?["content"]?.GetValue<string>());
        Assert.DoesNotContain(firstPass, item =>
            item.Payload?["content"]?.GetValue<string>().Contains("cause", StringComparison.Ordinal) == true
            || item.Payload?["content"]?.GetValue<string>().Contains("checking", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("Set-Content", firstPass[2].Payload?["content"]?.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Equal("turn-1", firstPass[3].MessageUid);
        Assert.Equal(DiscordDeliveryStore.MessageKind, firstPass[3].Kind);
        Assert.Equal(DiscordDeliveryStore.ReviewingState, firstPass[3].State);
        Assert.Equal("Found it.", firstPass[3].Payload?["content"]?.GetValue<string>());

        binding = await bindings.GetAsync(binding.EntityId) ?? throw new InvalidOperationException();
        Assert.Equal("epoch-1", binding.Epoch);
        Assert.Equal(8, binding.Sequence);

        await coordinator.ReconcileAsync(binding);

        Assert.Equal(4, (await deliveries.PendingOutboundAsync(binding.BindingId)).Count);
    }

    [Fact]
    public async Task Reconcile_omits_tool_squares_when_the_bot_hides_tool_activity()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var configurations = await SeedBotConfigurationAsync(entities);
        var config = (await configurations.ListAsync()).Single();
        await configurations.SaveAsync(config.EntityId, new LeafDiscordConfigUpdate(
            config.ApplicationId, config.InteractionPublicKey, null, false, false,
            [], [], config.MaxAttachmentBytes, config.AgentId,
            config.SessionQualityMode, ShowToolActivity: false));
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false);
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        provider.Messages.AddRange([
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 1), "turn-1",
                "assistant", "commentary", "I am checking that now.", DateTimeOffset.UtcNow),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 2), "turn-1",
                "assistant", "tool_use", null, DateTimeOffset.UtcNow, ToolName: "inspect"),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 3), "turn-1",
                "assistant", "tool_result", null, DateTimeOffset.UtcNow, ToolName: "inspect"),
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 4), "turn-1",
                "assistant", "text", "Found it.", DateTimeOffset.UtcNow),
        ]);
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, configurations,
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities), new FakeComputeGateway(),
            [provider], new PromptInjectionDetector(), new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await coordinator.ReconcileAsync(binding);
        var pending = await deliveries.PendingOutboundAsync(binding.BindingId);

        // No tool squares and no commentary: only the reply, waiting for its review.
        var reply = Assert.Single(pending);
        Assert.Equal(DiscordDeliveryStore.MessageKind, reply.Kind);
        Assert.Equal("Found it.", reply.Payload?["content"]?.GetValue<string>());
        binding = await bindings.GetAsync(binding.EntityId) ?? throw new InvalidOperationException();
        Assert.Equal(4, binding.Sequence);
    }

    [Fact]
    public async Task Reconcile_queues_delivery_text_and_asset_without_leaking_local_markdown()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false);
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        const string canonical = """
            Voilà, capturée sur la vraie interface avec Playwright :

            ![Settings page update banner](/api/assets/00000000-0000-4000-8000-000000000003.png)
            """;
        provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 1), "turn-1",
            "assistant", "text", canonical, DateTimeOffset.UtcNow)
        {
            DeliveryContent = "Voilà, capturée sur la vraie interface avec Playwright :",
            OutputAssets =
            [
                new ExternalConversationOutputAsset(
                    "00000000-0000-4000-8000-000000000003.png",
                    "Settings page update banner"),
            ],
        });
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await coordinator.ReconcileAsync(binding);

        var queued = Assert.Single(await deliveries.PendingOutboundAsync(binding.BindingId));
        Assert.Equal("Voilà, capturée sur la vraie interface avec Playwright :",
            queued.Payload?["content"]?.GetValue<string>());
        Assert.DoesNotContain("/api/assets/", queued.Payload?.ToJsonString(), StringComparison.Ordinal);
        var asset = Assert.IsType<System.Text.Json.Nodes.JsonObject>(
            Assert.Single(Assert.IsType<System.Text.Json.Nodes.JsonArray>(queued.Payload?["assets"])));
        Assert.Equal("00000000-0000-4000-8000-000000000003.png",
            asset["asset_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Reconcile_advances_past_sent_legacy_outreach_without_duplicate_delivery()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false) { IsQuiescent = true };
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        const string uid = "discord-outreach-legacy-image";
        const string canonical = "![Generated](/api/assets/image.png)";
        var direct = new ExternalConversationMessage(
            new ExternalConversationCursor("leaf-discord-outreach", 0), uid,
            "assistant", "text", canonical, DateTimeOffset.UtcNow);
        var sent = await deliveries.EnqueueOutboundAsync(binding, direct, 0, canonical);
        await deliveries.MarkSentAsync(sent, "discord-legacy-image");
        provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 1), uid,
            "assistant", "text", canonical, DateTimeOffset.UtcNow)
        {
            DeliveryContent = "",
            OutputAssets = [new ExternalConversationOutputAsset("image.png", "Generated")],
        });
        provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 2), "turn-after-image",
            "assistant", "text", "Later reply.", DateTimeOffset.UtcNow));
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        var quiescent = await coordinator.ReconcileAsync(binding);

        var current = await bindings.GetAsync(binding.EntityId)
            ?? throw new InvalidOperationException();
        var queued = Assert.Single(await deliveries.PendingOutboundAsync(binding.BindingId));
        var receipt = await deliveries.FindSentOutboundAsync(
            binding.BindingId, binding.Generation, uid);
        Assert.True(quiescent);
        Assert.Equal(2, current.Sequence);
        Assert.Equal("turn-after-image", queued.MessageUid);
        Assert.NotNull(receipt);
        Assert.Equal("discord-legacy-image", receipt.DiscordId);
    }

    [Fact]
    public async Task Reconcile_queues_an_image_only_final()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false);
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 1), "turn-1",
            "assistant", "text", "![Generated](/api/assets/image.png)", DateTimeOffset.UtcNow)
        {
            DeliveryContent = "",
            OutputAssets = [new ExternalConversationOutputAsset("image.png", "Generated")],
        });
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await coordinator.ReconcileAsync(binding);

        var queued = Assert.Single(await deliveries.PendingOutboundAsync(binding.BindingId));
        Assert.Equal("", queued.Payload?["content"]?.GetValue<string>());
        Assert.Single(Assert.IsType<System.Text.Json.Nodes.JsonArray>(queued.Payload?["assets"]));
    }

    [Fact]
    public async Task Reconcile_drains_every_complete_page_in_canonical_sequence_order()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false) { ReversePageOrder = true, IsQuiescent = true };
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        provider.Messages.AddRange(Enumerable.Range(1, 205).Select(sequence =>
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", sequence), $"turn-{sequence}",
                "assistant", "text", $"reply-{sequence}", DateTimeOffset.UtcNow)));
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        var quiescent = await coordinator.ReconcileAsync(binding);

        var outbound = await deliveries.PendingOutboundAsync(binding.BindingId);
        var current = await bindings.GetAsync(binding.EntityId)
            ?? throw new InvalidOperationException();
        Assert.True(quiescent);
        Assert.Equal(205, outbound.Count);
        Assert.Equal(3, provider.ReadAttempts);
        Assert.Equal(205, current.Sequence);
    }

    [Fact]
    public async Task Concurrent_reconciliation_is_serialized_and_does_not_duplicate_delivery()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false) { IsQuiescent = true };
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 1), "turn-1",
            "assistant", "text", "reply", DateTimeOffset.UtcNow));
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await Task.WhenAll(
            coordinator.ReconcileAsync(binding),
            coordinator.ReconcileAsync(binding));

        Assert.Single(await deliveries.PendingOutboundAsync(binding.BindingId));
        Assert.Equal(1, (await bindings.GetAsync(binding.EntityId))?.Sequence);
    }

    [Fact]
    public async Task Clear_waits_for_old_generation_reconciliation_and_keeps_new_cursor_at_zero()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var configurations = await SeedBotConfigurationAsync(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false) { IsQuiescent = true };
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 10), "turn-old",
            "assistant", "text", "old reply", DateTimeOffset.UtcNow));
        provider.PauseReads = true;
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, configurations,
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        var reconciliation = coordinator.ReconcileAsync(binding);
        await provider.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var clear = coordinator.ClearAsync(binding, "clear-1");
        provider.ContinueRead.TrySetResult(true);
        await Task.WhenAll(reconciliation, clear);

        var current = await bindings.GetAsync(binding.EntityId)
            ?? throw new InvalidOperationException();
        Assert.Equal(2, current.Generation);
        Assert.Null(current.Epoch);
        Assert.Equal(0, current.Sequence);

        var readsBeforeStaleCallback = provider.ReadAttempts;
        await coordinator.ReconcileAsync(binding);
        Assert.Equal(readsBeforeStaleCallback, provider.ReadAttempts);
        Assert.Equal(0, (await bindings.GetAsync(binding.EntityId))?.Sequence);
    }

    [Fact]
    public async Task Reconcile_releases_reaction_only_typing_once_when_the_session_is_quiescent()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false) { IsQuiescent = true };
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 1), "reaction-only-1",
            "assistant", "text", DiscordControlMessages.NoReply, DateTimeOffset.UtcNow));
        var typing = new DiscordTypingLeases();
        var indicatorCreated = false;
        var admission = typing.Begin(binding.BindingId, () =>
        {
            indicatorCreated = true;
            return new FakeIndicator();
        }, startImmediately: false);
        typing.MarkAdmitted(admission, "reaction-only-1");
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), typing,
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await coordinator.ReconcileAsync(binding);
        binding = await bindings.GetAsync(binding.EntityId)
            ?? throw new InvalidOperationException();
        await coordinator.ReconcileAsync(binding);

        Assert.False(indicatorCreated);
        Assert.Empty(await deliveries.PendingOutboundAsync(binding.BindingId));
        Assert.Equal("epoch-1", binding.Epoch);
        Assert.Equal(1, binding.Sequence);
    }

    [Theory]
    [InlineData("commentary", "Working on it.")]
    [InlineData("tool_use", null)]
    [InlineData("text", "Here is the answer.")]
    public async Task Reconcile_activates_deferred_typing_for_matching_outward_work(
        string kind,
        string? content)
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "selective"));
        var provider = new FlakyProvider(false) { IsQuiescent = true };
        binding = await bindings.AttachConversationAsync(binding, new ExternalConversationHandle(
            provider.ProviderId, binding.BindingId, binding.Generation, "conversation", "session"));
        provider.Messages.Add(new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 1), "turn-1",
            "assistant", kind, content, DateTimeOffset.UtcNow,
            ToolName: kind == "tool_use" ? "inspect" : null));
        var typing = new DiscordTypingLeases();
        var indicator = new FakeIndicator();
        var createCount = 0;
        var admission = typing.Begin(binding.BindingId, () =>
        {
            createCount++;
            return indicator;
        }, startImmediately: false);
        typing.MarkAdmitted(admission, "turn-1");
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), typing,
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await coordinator.ReconcileAsync(binding);

        Assert.Equal(1, createCount);
        Assert.Equal(1, indicator.DisposeCount);
        // Commentary still shows typing but is never posted.
        Assert.Equal(kind == "commentary" ? 0 : 1,
            (await deliveries.PendingOutboundAsync(binding.BindingId)).Count);
    }

    [Fact]
    public async Task Linked_requestor_identity_is_added_as_verified_transport_metadata()
    {
        var entities = new MemoryEntityStore();
        await SeedQualityMode(entities);
        var owner = await entities.UpsertBySlugAsync("user", "owner", "Owner Example",
            new System.Text.Json.Nodes.JsonObject
            {
                [DiscordUserIdentityResolver.UserField] = "200000000000000002",
            });
        var bindings = new DiscordBindingStore(entities);
        var deliveries = new DiscordDeliveryStore(entities);
        var binding = await bindings.CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", owner.Id.ToString(),
            "nova-discord-external", "participate"));
        var provider = new FlakyProvider(false);
        using var coordinator = new DiscordConversationCoordinator(
            bindings, deliveries, BotConfigurations(entities),
            new DiscordRuntimeOptionsResolver(entities), IdentityResolver(entities),
            new FakeComputeGateway(), [provider], new PromptInjectionDetector(),
            new DiscordEgressGuard(), new DiscordTypingLeases(),
            NullLogger<DiscordConversationCoordinator>.Instance, TestSentinel.Clean);

        await coordinator.SubmitAsync(binding, "1002",
            new ExternalRequestor("200000000000000002", "Owner"), "Hello");

        var linked = Assert.IsType<System.Text.Json.Nodes.JsonObject>(
            provider.LastInput?.Metadata?["verified_leaf_user"]);
        Assert.Equal(owner.Id.ToString(), linked["user_id"]?.GetValue<string>());
        Assert.Equal("Owner Example", linked["display_name"]?.GetValue<string>());
        Assert.True(linked["is_conversation_owner"]?.GetValue<bool>());
    }

    private static DiscordUserIdentityResolver IdentityResolver(MemoryEntityStore entities)
        => new(entities, NullLogger<DiscordUserIdentityResolver>.Instance);

    private static DiscordBotConfigurationStore BotConfigurations(MemoryEntityStore entities)
        => new(new LeafDiscordConfigStore(entities));

    private static async Task<DiscordBotConfigurationStore> SeedBotConfigurationAsync(
        MemoryEntityStore entities)
    {
        var configurations = BotConfigurations(entities);
        await configurations.CreateAsync(new LeafDiscordConfigUpdate(
            "100000000000000001",
            new string('a', 64),
            null,
            false,
            false,
            [],
            [],
            10 * 1024 * 1024,
            "nova",
            "deep-codex"), ownerUserId: "owner-1");
        return configurations;
    }

    private static async Task SeedQualityMode(MemoryEntityStore entities)
        => _ = await entities.UpsertBySlugAsync(
            "quality-mode", "deep-codex", "Deep (Codex)",
            new System.Text.Json.Nodes.JsonObject
            {
                ["provider"] = "codex-default",
                ["quality_tier"] = "deep",
                ["model"] = "gpt-5.6-sol",
                ["effort"] = "high",
            });

    private sealed class FlakyProvider(bool failFirst = true) : IExternalAgentConversationProvider
    {
        public int OpenAttempts { get; private set; }
        public int SendAttempts { get; private set; }
        public int ReadAttempts { get; private set; }
        public ExternalConversationInput? LastInput { get; private set; }
        public List<ExternalConversationMessage> Messages { get; } = [];
        public bool IsQuiescent { get; init; }
        public bool ReversePageOrder { get; init; }
        public bool PauseReads { get; set; }
        public TaskCompletionSource<bool> ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ContinueRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "test";
        public bool CanHandle(string agentSlugOrId) => agentSlugOrId == "nova";

        public Task<ExternalConversationHandle> OpenAsync(
            ExternalConversationOpenRequest request, CancellationToken ct = default)
        {
            OpenAttempts++;
            return Task.FromResult(new ExternalConversationHandle(
                ProviderId, request.BindingId, request.Generation, "conversation", "session"));
        }

        public Task<ExternalConversationAdmission> SendAsync(
            ExternalConversationHandle handle, ExternalConversationInput input,
            CancellationToken ct = default)
        {
            SendAttempts++;
            LastInput = input;
            if (failFirst && SendAttempts == 1) throw new InvalidOperationException("compute unavailable");
            return Task.FromResult(new ExternalConversationAdmission("uid-1", "delivered"));
        }

        public async Task<ExternalConversationPage> ReadSettledAsync(
            ExternalConversationHandle handle, ExternalConversationCursor? after = null,
            int limit = 100, CancellationToken ct = default)
        {
            ReadAttempts++;
            ReadStarted.TrySetResult(true);
            if (PauseReads)
                await ContinueRead.Task.WaitAsync(ct);
            var remaining = Messages
                .Where(message => after is null
                    || !string.Equals(message.Cursor.Epoch, after.Epoch, StringComparison.Ordinal)
                    || message.Cursor.Sequence > after.Sequence)
                .OrderBy(message => message.Cursor.Sequence)
                .ToList();
            var messages = remaining
                .Take(limit)
                .ToList();
            if (ReversePageOrder)
                messages.Reverse();
            return new ExternalConversationPage(
                messages,
                messages.Count == 0
                    ? after
                    : messages.MaxBy(message => message.Cursor.Sequence)!.Cursor,
                IsQuiescent)
            {
                HasMore = remaining.Count > messages.Count,
            };
        }

        public IDisposable SubscribeSettled(
            Func<ExternalConversationSettled, CancellationToken, Task> handler)
            => new NoopDisposable();
    }

    private sealed class FakeComputeGateway : IComputeGateway
    {
        public bool InjectionAccepted { get; init; } = true;
        public List<(string Uri, string Body)> Requests { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            Requests.Add((
                request.RequestUri?.ToString() ?? "",
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        injected = InjectionAccepted,
                        messageUid = Requests.Count == 0
                            ? null
                            : JsonDocument.Parse(Requests[^1].Body).RootElement
                                .GetProperty("messageUid").GetString(),
                    })),
            };
        }
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    private sealed class FakeIndicator : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
