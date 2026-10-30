using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordDeliveryStoreTests
{
    [Fact]
    public async Task Pending_legacy_markdown_row_upgrades_in_place_to_asset_delivery()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        const string canonical = "![Generated](/api/assets/image.png)";
        var message = Assistant("turn-asset", canonical);
        var legacy = await deliveries.EnqueueOutboundAsync(
            binding, message, 0, canonical);

        var upgraded = await deliveries.EnqueueOutboundAsync(
            binding, message, 0, "",
            [new ExternalConversationOutputAsset("image.png", "Generated")]);

        Assert.Equal(legacy.EntityId, upgraded.EntityId);
        Assert.Equal("", upgraded.Payload?["content"]?.GetValue<string>());
        Assert.Single(Assert.IsType<JsonArray>(upgraded.Payload?["assets"]));
    }

    [Fact]
    public async Task Received_inbox_is_retryable_until_compute_admission_is_recorded()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var payload = new JsonObject { ["content"] = "hello" };

        var first = await deliveries.ReceiveAsync(binding, "1001", payload);
        var retry = await deliveries.ReceiveAsync(binding, "1001", payload);
        await deliveries.MarkAdmittedAsync(first.Delivery,
            new ExternalConversationAdmission("message-1", "delivered"));
        var settledDuplicate = await deliveries.ReceiveAsync(binding, "1001", payload);

        Assert.True(first.IsNew);
        Assert.True(retry.IsNew);
        Assert.Equal(first.Delivery.EntityId, retry.Delivery.EntityId);
        Assert.False(settledDuplicate.IsNew);
        Assert.Equal("admitted", settledDuplicate.Delivery.State);
    }

    [Fact]
    public async Task Admitted_inbound_keeps_author_attribution_but_drops_the_payload()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var payload = new JsonObject
        {
            ["content"] = "ola!",
            ["requestor"] = new JsonObject
            {
                ["id"] = "42",
                ["display_name"] = "Alice Example",
                ["username"] = "alice",
            },
            ["verified_leaf_user"] = new JsonObject
            {
                ["user_id"] = "owner-1",
                ["display_name"] = "Owner Example",
                ["is_conversation_owner"] = true,
            },
            ["attachments"] = new JsonArray(new JsonObject { ["id"] = "a1" }),
            ["guild_context"] = new JsonObject { ["server_map"] = new JsonObject() },
        };

        var received = await deliveries.ReceiveAsync(binding, "1001", payload);
        var admitted = await deliveries.MarkAdmittedAsync(received.Delivery,
            new ExternalConversationAdmission("message-1", "delivered"));
        var attribution = await deliveries.FindInboundAttributionAsync(binding, "1001");

        Assert.Null(admitted.Payload);
        Assert.Equal(new DiscordInboundAttribution(
            "42", "Alice Example", "alice", "Owner Example", true, 1, 4), attribution);
        Assert.Null(await deliveries.FindInboundAttributionAsync(binding, "1002"));
        Assert.Null(await deliveries.FindInboundAttributionAsync(
            binding with { BindingId = "discord:other:binding" }, "1001"));
    }

    [Fact]
    public async Task Reconciliation_does_not_requeue_an_already_sent_outbound_chunk()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var message = Assistant("reply-1", "answer");

        var queued = await deliveries.EnqueueOutboundAsync(binding, message, 0, "answer");
        await deliveries.MarkSentAsync(queued, "discord-2001");
        var repeated = await deliveries.EnqueueOutboundAsync(binding, message, 0, "answer");

        Assert.Equal(queued.EntityId, repeated.EntityId);
        Assert.Equal("sent", repeated.State);
        Assert.Equal("discord-2001", repeated.DiscordId);
        Assert.Empty(await deliveries.PendingOutboundAsync(binding.BindingId));
    }

    [Fact]
    public async Task Sent_outbound_delivery_can_be_resolved_for_a_binding_scoped_pin()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var message = Assistant("reply-to-pin", "install guide");
        var queued = await deliveries.EnqueueOutboundAsync(
            binding, message, 0, "install guide");
        await deliveries.MarkSentAsync(queued, "1559000000000000000");

        var sent = await deliveries.FindSentOutboundAsync(
            binding.BindingId, binding.Generation, message.MessageUid);

        Assert.NotNull(sent);
        Assert.Equal("1559000000000000000", sent.DiscordId);
        Assert.Null(await deliveries.FindSentOutboundAsync(
            binding.BindingId, binding.Generation + 1, message.MessageUid));
        Assert.Null(await deliveries.FindSentOutboundAsync(
            "another-binding", binding.Generation, message.MessageUid));
    }

    [Fact]
    public async Task Message_correction_ownership_is_exactly_binding_generation_uid_and_outbound()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var message = Assistant("reply-to-correct", "one\ntwo");
        var first = await deliveries.EnqueueOutboundAsync(binding, message, 0, "one");
        var second = await deliveries.EnqueueOutboundAsync(binding, message, 1, "two");
        await deliveries.MarkSentAsync(first, "1559000000000000001");
        await deliveries.MarkSentAsync(second, "1559000000000000002");

        var inbound = await deliveries.ReceiveAsync(
            binding, "1559000000000000003", new JsonObject { ["content"] = "not Nova" });
        await deliveries.MarkAdmittedAsync(inbound.Delivery,
            new ExternalConversationAdmission("inbound-message-uid", "delivered"));

        var owned = await deliveries.FindOwnedOutboundAsync(
            binding.BindingId, binding.Generation, message.MessageUid);

        Assert.Equal(
            ["1559000000000000001", "1559000000000000002"],
            owned.Select(item => item.DiscordId!).ToArray());
        Assert.Empty(await deliveries.FindOwnedOutboundAsync(
            binding.BindingId, binding.Generation, "inbound-message-uid"));
        Assert.Empty(await deliveries.FindOwnedOutboundAsync(
            binding.BindingId, binding.Generation + 1, message.MessageUid));
        Assert.Empty(await deliveries.FindOwnedOutboundAsync(
            "another-binding", binding.Generation, message.MessageUid));
    }

    [Fact]
    public async Task Edit_and_idempotent_delete_leave_durable_audit_timestamps()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var message = Assistant("reply-audit", "before");
        var queued = await deliveries.EnqueueOutboundAsync(binding, message, 0, "before");
        await deliveries.MarkSentAsync(queued, "1559000000000000010");
        var sent = Assert.Single(await deliveries.FindOwnedOutboundAsync(
            binding.BindingId, binding.Generation, message.MessageUid));

        await deliveries.MarkEditedAsync(sent);
        var edited = await store.GetAsync(sent.EntityId);
        Assert.Equal("edited", edited!.Data["state"]?.GetValue<string>());
        Assert.True(DateTimeOffset.TryParse(
            edited.Data["edited_at"]?.GetValue<string>(), out _));

        await deliveries.MarkDeletedAsync(sent);
        var deleted = await store.GetAsync(sent.EntityId);
        var deletedAt = deleted!.Data["deleted_at"]?.GetValue<string>();
        Assert.Equal("deleted", deleted.Data["state"]?.GetValue<string>());
        Assert.True(DateTimeOffset.TryParse(deletedAt, out _));

        await deliveries.MarkDeletedAsync(sent);
        var repeated = await store.GetAsync(sent.EntityId);
        Assert.Equal(deletedAt, repeated!.Data["deleted_at"]?.GetValue<string>());
        Assert.Equal("deleted", repeated.Data["state"]?.GetValue<string>());
    }

    [Fact]
    public async Task Outreach_reconciliation_reuses_the_direct_delivery_across_cursor_change()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        const string uid = "discord-outreach-stable-1";
        const string canonical = "![Generated](/api/assets/image.png)";
        var direct = new ExternalConversationMessage(
            new ExternalConversationCursor("leaf-discord-outreach", 0), uid,
            "assistant", "text", canonical, DateTimeOffset.UtcNow);
        var reconciled = new ExternalConversationMessage(
            new ExternalConversationCursor("provider-epoch", 1179), uid,
            "assistant", "text", canonical, DateTimeOffset.UtcNow);

        var queued = await deliveries.EnqueueOutboundAsync(
            binding, direct, 0, canonical);
        await deliveries.MarkSentAsync(queued, "discord-2002");
        var repeated = await deliveries.EnqueueOutboundAsync(
            binding, reconciled, 0, "",
            [new ExternalConversationOutputAsset("image.png", "Generated")]);

        Assert.Equal(queued.EntityId, repeated.EntityId);
        Assert.Equal("sent", repeated.State);
        Assert.Equal("discord-2002", repeated.DiscordId);
        Assert.Empty(await deliveries.PendingOutboundAsync(binding.BindingId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            deliveries.EnqueueOutboundAsync(binding, direct, 0, "Changed."));
    }

    [Fact]
    public async Task Stable_outbound_identity_rejects_changed_content()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var message = Assistant("reply-1", "first");
        await deliveries.EnqueueOutboundAsync(binding, message, 0, "first");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            deliveries.EnqueueOutboundAsync(binding, message, 0, "changed"));
    }

    [Fact]
    public async Task Forum_post_reservation_is_durable_and_rejects_changed_payload()
    {
        var deliveries = new DiscordDeliveryStore(new MemoryEntityStore());

        var first = await deliveries.ReserveForumPostAsync(
            "app-1", "guild-1", "forum-1", "workstream-1",
            "Discord forum ownership", "Opening this workstream.");
        var retry = await deliveries.ReserveForumPostAsync(
            "app-1", "guild-1", "forum-1", "workstream-1",
            "Discord forum ownership", "Opening this workstream.");

        Assert.Equal(first.EntityId, retry.EntityId);
        Assert.Equal(DiscordDeliveryStore.ForumPostKind, first.Kind);
        Assert.Equal("queued", first.State);
        Assert.Equal("workstream-1", first.MessageUid);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            deliveries.ReserveForumPostAsync(
                "app-1", "guild-1", "forum-1", "workstream-1",
                "Discord forum ownership", "Changed starter."));
    }

    [Fact]
    public async Task Commentary_and_final_from_the_same_turn_have_distinct_delivery_identities()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var commentary = new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 10), "reply-1",
            "assistant", "commentary", "working", DateTimeOffset.UtcNow);
        var final = new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 20), "reply-1",
            "assistant", "text", "done", DateTimeOffset.UtcNow);

        var commentaryDelivery = await deliveries.EnqueueOutboundAsync(
            binding, commentary, 0, "working");
        var finalDelivery = await deliveries.EnqueueOutboundAsync(
            binding, final, 0, "done");

        Assert.NotEqual(commentaryDelivery.EntityId, finalDelivery.EntityId);
        Assert.NotEqual(
            DiscordRestTransport.Nonce(commentaryDelivery),
            DiscordRestTransport.Nonce(finalDelivery));
        Assert.Equal(
            ["working", "done"],
            (await deliveries.PendingOutboundAsync(binding.BindingId))
            .Select(item => item.Payload?["content"]?.GetValue<string>() ?? "")
            .ToArray());
    }

    [Fact]
    public async Task Progress_snapshots_replace_commentary_append_every_marker_and_replay_once()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var now = DateTimeOffset.UtcNow;

        var first = await deliveries.EnqueueProgressAsync(binding,
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 10), "turn-1",
                "assistant", "commentary", "Checking.", now),
            "Checking.", null);
        Assert.NotNull(first);
        await deliveries.MarkSentAsync(first!, "discord-progress-1");

        for (var sequence = 11; sequence <= 40; sequence++)
            _ = await deliveries.EnqueueProgressAsync(binding,
                new ExternalConversationMessage(
                    new ExternalConversationCursor("epoch-1", sequence), "turn-1",
                    "assistant", sequence % 2 == 0 ? "tool_result" : "tool_use",
                    null, now),
                null, sequence % 2 == 0
                    ? DiscordDeliveryStore.ToolResultMarker
                    : DiscordDeliveryStore.ToolUseMarker);

        var latestMessage = new ExternalConversationMessage(
            new ExternalConversationCursor("epoch-1", 41), "turn-1",
            "assistant", "commentary", "Found the cause.", now);
        var latest = await deliveries.EnqueueProgressAsync(
            binding, latestMessage, "Found the cause.", null);
        var replay = await deliveries.EnqueueProgressAsync(
            binding, latestMessage, "Found the cause.", null);

        Assert.NotNull(latest);
        Assert.Null(replay);
        Assert.Equal("progress", latest!.Kind);
        var content = latest.Payload?["content"]?.GetValue<string>();
        Assert.StartsWith("Found the cause.\n\n-# ", content, StringComparison.Ordinal);
        Assert.DoesNotContain(" +", content, StringComparison.Ordinal);
        Assert.Equal(30,
            System.Text.RegularExpressions.Regex.Matches(
                content!,
                $"{System.Text.RegularExpressions.Regex.Escape(DiscordDeliveryStore.ToolUseMarker)}|" +
                System.Text.RegularExpressions.Regex.Escape(DiscordDeliveryStore.ToolResultMarker)).Count);
        Assert.DoesNotContain(
            DiscordDeliveryStore.ToolUseMarker + DiscordDeliveryStore.ToolResultMarker,
            content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            DiscordDeliveryStore.ToolResultMarker + DiscordDeliveryStore.ToolUseMarker,
            content,
            StringComparison.Ordinal);
        Assert.Equal(
            "discord-progress-1",
            await deliveries.FindProgressReceiptAsync(latest));
    }

    [Theory]
    [InlineData("Edit", "{}", DiscordDeliveryStore.MutatingToolMarker)]
    [InlineData("PowerShell", "{\"command\":\"Set-Content -LiteralPath file.txt -Value changed\"}", DiscordDeliveryStore.MutatingToolMarker)]
    [InlineData("PowerShell", "{\"command\":\"Get-Content -LiteralPath file.txt\"}", DiscordDeliveryStore.ToolUseMarker)]
    [InlineData("inspect", "{}", DiscordDeliveryStore.ToolUseMarker)]
    public void Tool_use_marker_preserves_mutating_semantics_without_exposing_tool_data(
        string toolName, string toolInput, string expectedMarker)
    {
        var marker = DiscordDeliveryStore.ToolUseMarkerFor(toolName, toolInput);

        Assert.Equal(expectedMarker, marker);
        Assert.DoesNotContain(toolName, marker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(toolInput, marker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marker_only_progress_uses_spaced_Discord_subtext_instead_of_jumbo_emoji()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var now = DateTimeOffset.UtcNow;

        _ = await deliveries.EnqueueProgressAsync(binding,
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 1), "turn-1",
                "assistant", "tool_use", null, now),
            null, DiscordDeliveryStore.ToolUseMarker);
        var latest = await deliveries.EnqueueProgressAsync(binding,
            new ExternalConversationMessage(
                new ExternalConversationCursor("epoch-1", 2), "turn-1",
                "assistant", "tool_result", null, now),
            null, DiscordDeliveryStore.ToolResultMarker);

        Assert.Equal(
            $"-# {DiscordDeliveryStore.ToolUseMarker} {DiscordDeliveryStore.ToolResultMarker}",
            latest!.Payload?["content"]?.GetValue<string>());
    }

    [Fact]
    public async Task Reaction_target_must_be_an_admitted_inbound_message_in_the_binding_generation()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var received = await deliveries.ReceiveAsync(
            binding, "1001", new JsonObject { ["content"] = "hello" });

        Assert.False(await deliveries.ContainsAdmittedInboundAsync(
            binding.BindingId, binding.Generation, "1001"));

        await deliveries.MarkAdmittedAsync(received.Delivery,
            new ExternalConversationAdmission("message-1", "delivered"));

        Assert.True(await deliveries.ContainsAdmittedInboundAsync(
            binding.BindingId, binding.Generation, "1001"));
        Assert.False(await deliveries.ContainsAdmittedInboundAsync(
            binding.BindingId, binding.Generation + 1, "1001"));
        Assert.False(await deliveries.ContainsAdmittedInboundAsync(
            "another-binding", binding.Generation, "1001"));
    }

    [Fact]
    public async Task Only_outreach_native_posts_in_announcements_are_announcements()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var settled = DiscordBotRuntime.NativeAnnouncementReceiptGrace + TimeSpan.FromSeconds(1);

        async Task<bool?> SentAs(string uid, string discordId, Func<ExternalConversationMessage,
            Task<DiscordDelivery>> enqueue)
        {
            await deliveries.MarkSentAsync(
                await enqueue(Assistant(uid, "text")), discordId);
            return DiscordBotRuntime.IsNativeAnnouncementPost(
                await deliveries.FindSentMessageUidAsync(discordId), TimeSpan.Zero);
        }

        // Release Manager posts through binding outreach.
        Assert.True(await SentAs(
            DiscordConversationCoordinator.OutreachMessageUid(binding, "nightly-42"),
            "1559000000000000100",
            message => deliveries.EnqueueOutboundAsync(binding, message, 0, "Nightly")));
        // The channel's own session answering a member.
        Assert.False(await SentAs(
            "turn-reply-1", "1559000000000000101",
            message => deliveries.EnqueueOutboundForReviewAsync(
                binding, message, 0, "Bonne question", [], 1, "turn-1", false)));
        Assert.False(await SentAs(
            DiscordConversationCoordinator.NoticeMessageUid(binding, "1559000000000000050"),
            "1559000000000000102",
            message => deliveries.EnqueueOutboundAsync(binding, message, 0, "notice")));
        Assert.False(await SentAs(
            "turn-progress-1", "1559000000000000103",
            async message => (await deliveries.EnqueueProgressAsync(
                binding, message, "Je regarde", null))!));

        // Not recorded yet: wait for the outbox, then a post no delivery sent is the host's.
        Assert.Null(await deliveries.FindSentMessageUidAsync("1559000000000000104"));
        Assert.Null(DiscordBotRuntime.IsNativeAnnouncementPost(null, TimeSpan.FromSeconds(2)));
        Assert.True(DiscordBotRuntime.IsNativeAnnouncementPost(null, settled));
    }

    [Fact]
    public async Task A_queued_delivery_has_no_sent_message_uid_until_discord_answers()
    {
        var store = new MemoryEntityStore();
        var deliveries = new DiscordDeliveryStore(store);
        var binding = await CreateBindingAsync(store);
        var queued = await deliveries.EnqueueOutboundAsync(
            binding, Assistant("turn-queued", "text"), 0, "text");

        Assert.Null(await deliveries.FindSentMessageUidAsync("1559000000000000200"));
        await deliveries.MarkSentAsync(queued, "1559000000000000200");
        Assert.Equal("turn-queued",
            await deliveries.FindSentMessageUidAsync("1559000000000000200"));
    }

    [Fact]
    public async Task Clear_generation_and_new_session_are_persisted_together()
    {
        var store = new MemoryEntityStore();
        var bindings = new DiscordBindingStore(store);
        var binding = await CreateBindingAsync(store);
        var handle = new ExternalConversationHandle(
            "nova-normal-session", binding.BindingId, 2,
            "discord:binding:generation:2", "session-2");

        var advanced = await bindings.AdvanceGenerationAndAttachAsync(
            binding, handle, "interaction-2");

        Assert.Equal(2, advanced.Generation);
        Assert.Equal(handle.ConversationId, advanced.ConversationHandleId);
        Assert.Equal("session-2", advanced.SessionId);
        Assert.Equal("interaction-2", advanced.LastClearRequestId);
        Assert.Null(advanced.Epoch);
        Assert.Equal(0, advanced.Sequence);
    }

    private static ExternalConversationMessage Assistant(string uid, string content) => new(
        new ExternalConversationCursor("epoch-1", 10), uid, "assistant", "text",
        content, DateTimeOffset.UtcNow);

    private static async Task<DiscordBinding> CreateBindingAsync(MemoryEntityStore store)
        => await new DiscordBindingStore(store).CreateAsync(new CreateDiscordBinding(
            "100000000000000001", "123456789012345678", "guild_channel",
            "223456789012345678", null, "nova", "owner-1",
            "nova-discord-external", "participate"));
}

/// <summary>
/// In-memory <see cref="IEntityStore"/>. Given declared secret fields it also models RedLeaf's
/// vault: every write moves those top-level keys out of the stored document (an explicit null
/// or empty value clears the vaulted one, an absent key keeps it), reads hydrate them back, and
/// <see cref="Redacted"/> is what the entity HTTP API returns.
/// </summary>
internal sealed class MemoryEntityStore(
    IReadOnlyDictionary<string, IReadOnlySet<string>>? secretFields = null) : IEntityStore
{
    public const string RedactedSentinel = "__redacted__";

    private readonly List<LeafEntity> items = [];
    private readonly Dictionary<(Guid EntityId, string Key), string> vault = [];

    /// <summary>Secret fields exactly as the plugin seeds declare them.</summary>
    public static MemoryEntityStore WithSeedSecretFields()
    {
        var root = PluginPackagingContractTests.FindRepositoryRoot();
        using var seeds = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "seeds", "field-definitions.json")));
        var fields = seeds.RootElement.EnumerateArray()
            .Where(field => field.GetProperty("fieldType").GetString() == "secret")
            .GroupBy(field => field.GetProperty("parentType").GetString()!)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group
                    // RedLeaf keys a field by its slug with hyphens as underscores.
                    .Select(field => field.GetProperty("slug").GetString()!.Replace('-', '_'))
                    .ToHashSet(StringComparer.Ordinal));
        return new MemoryEntityStore(fields);
    }

    public LeafEntity Seed(string typeSlug, string slug, string name, string? createdBy)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new LeafEntity(
            Guid.NewGuid(), typeSlug, slug, name, [], now, now, createdBy);
        items.Add(entity);
        return entity;
    }

    /// <summary>The document at rest, as RedLeaf stores it in the entity row.</summary>
    public JsonObject StoredData(Guid id) => Clone(items.Single(item => item.Id == id).Data);

    public string? VaultValue(Guid id, string key)
        => vault.TryGetValue((id, key), out var value) ? value : null;

    /// <summary>The document as the entity HTTP API returns it.</summary>
    public JsonObject Redacted(Guid id)
    {
        var entity = items.Single(item => item.Id == id);
        var data = Clone(entity.Data);
        foreach (var key in SecretKeys(entity.TypeSlug))
        {
            var stored = data[key] is JsonValue value && value.TryGetValue<string>(out var text)
                && text.Length > 0;
            if (stored || vault.ContainsKey((id, key))) data[key] = RedactedSentinel;
        }
        return data;
    }

    public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Hydrate(items.FirstOrDefault(item => item.Id == id)));

    public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(Hydrate(items.FirstOrDefault(item => item.Slug == slug)));

    public Task<LeafEntity?> GetBySlugAsync(
        string typeSlug, string slug, CancellationToken ct = default)
        => Task.FromResult(Hydrate(items.FirstOrDefault(item =>
            item.TypeSlug == typeSlug && item.Slug == slug)));

    public Task<IReadOnlyList<LeafEntity>> QueryAsync(
        EntityQuery query, CancellationToken ct = default)
    {
        IEnumerable<LeafEntity> result = items.Where(item => item.TypeSlug == query.TypeSlug);
        if (query.DataEquals is not null)
            result = result.Where(item => query.DataEquals.All(pair => Equal(item.Data[pair.Key], pair.Value)));
        return Task.FromResult<IReadOnlyList<LeafEntity>>(
            result.Skip(query.Offset).Take(query.Limit).Select(item => Hydrate(item)!).ToArray());
    }

    public Task<LeafEntity> CreateAsync(
        string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
        => UpsertBySlugAsync(typeSlug, $"{typeSlug}-{Guid.NewGuid():N}", name, data, ct);

    public Task<LeafEntity> UpsertBySlugAsync(
        string typeSlug, string slug, string name, JsonObject? data = null,
        CancellationToken ct = default)
    {
        var existing = items.FirstOrDefault(item => item.TypeSlug == typeSlug && item.Slug == slug);
        if (existing is null)
        {
            var now = DateTimeOffset.UtcNow;
            existing = Protect(new LeafEntity(
                Guid.NewGuid(), typeSlug, slug, name, Clone(data), now, now, "test"));
            items.Add(existing);
        }
        else
        {
            existing = Protect(existing with
            {
                Name = name, Data = Clone(data), UpdatedAt = DateTimeOffset.UtcNow,
            });
            Replace(existing);
        }
        return Task.FromResult(Hydrate(existing)!);
    }

    public Task<LeafEntity> PatchAsync(
        Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
    {
        var existing = items.Single(item => item.Id == id);
        var data = Clone(existing.Data);
        foreach (var (key, value) in dataPatch) data[key] = value?.DeepClone();
        var updated = Protect(existing with
        {
            Name = name ?? existing.Name,
            Data = data,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        Replace(updated);
        return Task.FromResult(Hydrate(updated)!);
    }

    public Task<LeafEntity> ReplaceDataAsync(
        Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
    {
        var existing = items.Single(item => item.Id == id);
        var updated = Protect(existing with
        {
            Name = name ?? existing.Name,
            Data = Clone(data),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        Replace(updated);
        return Task.FromResult(Hydrate(updated)!);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        items.RemoveAll(item => item.Id == id);
        foreach (var key in vault.Keys.Where(key => key.EntityId == id).ToArray())
            vault.Remove(key);
        return Task.CompletedTask;
    }

    private IReadOnlySet<string> SecretKeys(string typeSlug)
        => secretFields?.GetValueOrDefault(typeSlug) ?? new HashSet<string>();

    /// <summary>The kernel write boundary: it scans the whole stored document, not the patch.</summary>
    private LeafEntity Protect(LeafEntity entity)
    {
        foreach (var key in SecretKeys(entity.TypeSlug))
        {
            if (!entity.Data.TryGetPropertyValue(key, out var node)) continue;
            var text = node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
            if (text != RedactedSentinel)
            {
                if (string.IsNullOrEmpty(text)) vault.Remove((entity.Id, key));
                else vault[(entity.Id, key)] = text;
            }
            entity.Data.Remove(key);
        }
        return entity;
    }

    private LeafEntity? Hydrate(LeafEntity? entity)
    {
        if (entity is null) return null;
        var keys = SecretKeys(entity.TypeSlug);
        if (keys.Count == 0) return entity;
        var data = Clone(entity.Data);
        foreach (var key in keys)
            if (vault.TryGetValue((entity.Id, key), out var value)) data[key] = value;
        return entity with { Data = data };
    }

    private void Replace(LeafEntity entity)
    {
        var index = items.FindIndex(item => item.Id == entity.Id);
        items[index] = entity;
    }

    private static JsonObject Clone(JsonObject? data)
        => data?.DeepClone() as JsonObject ?? [];

    private static bool Equal(JsonNode? node, object? expected)
    {
        if (expected is null) return node is null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text == expected.ToString();
        return node?.ToJsonString() == JsonValue.Create(expected)?.ToJsonString();
    }
}
