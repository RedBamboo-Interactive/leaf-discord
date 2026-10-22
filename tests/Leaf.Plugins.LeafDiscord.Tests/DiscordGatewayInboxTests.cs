using Leaf.Plugins.LeafDiscord;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordGatewayInboxTests
{
    [Fact]
    public async Task Enqueue_is_durable_and_idempotent_by_bot_channel_and_message()
    {
        var store = new MemoryEntityStore();
        var inbox = new DiscordGatewayInboxStore(store);
        var botId = Guid.NewGuid();

        var first = await inbox.EnqueueAsync(
            botId, "1562788496605847655", "1556368920787820668",
            "1539243753775042630");
        var duplicate = await inbox.EnqueueAsync(
            botId, "1562788496605847655", "1556368920787820668",
            "1539243753775042630");

        Assert.Equal(first.EntityId, duplicate.EntityId);
        Assert.Single(await inbox.PendingAsync(botId));

        await inbox.MarkCompletedAsync(first);

        Assert.Empty(await inbox.PendingAsync(botId));
    }

    [Fact]
    public async Task Scheduler_preserves_message_order_per_channel_while_other_channels_progress()
    {
        var inbox = new DiscordGatewayInboxStore(new MemoryEntityStore());
        var botId = Guid.NewGuid();
        var first = await inbox.EnqueueAsync(botId, "100", "10", "1");
        var second = await inbox.EnqueueAsync(botId, "101", "10", "1");
        var otherChannel = await inbox.EnqueueAsync(botId, "102", "11", "1");

        var heads = DiscordGatewayInboxStore.DueChannelHeads(
            await inbox.PendingAsync(botId), DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal([first.EntityId, otherChannel.EntityId],
            heads.Select(item => item.EntityId));
        Assert.DoesNotContain(heads, item => item.EntityId == second.EntityId);

        await inbox.MarkCompletedAsync(first);
        var next = DiscordGatewayInboxStore.DueChannelHeads(
            await inbox.PendingAsync(botId), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Contains(next, item => item.EntityId == second.EntityId);
    }

    [Fact]
    public async Task Announcement_edits_are_durable_and_deduplicated_by_revision()
    {
        var inbox = new DiscordGatewayInboxStore(new MemoryEntityStore());
        var botId = Guid.NewGuid();
        var original = await inbox.EnqueueAsync(botId, "300", "30", "1");
        var edit = await inbox.EnqueueAsync(
            botId, "300", "30", "1", "announcement-update", "announcement:300:update:2");
        var duplicate = await inbox.EnqueueAsync(
            botId, "300", "30", "1", "announcement-update", "announcement:300:update:2");

        Assert.NotEqual(original.EntityId, edit.EntityId);
        Assert.Equal(edit.EntityId, duplicate.EntityId);
        Assert.Equal("announcement-update", edit.Kind);
        Assert.Equal("announcement:300:update:2", edit.EventId);
        Assert.Equal(2, (await inbox.PendingAsync(botId)).Count);
    }

    [Fact]
    public async Task Retry_holds_later_messages_in_the_same_channel()
    {
        var inbox = new DiscordGatewayInboxStore(new MemoryEntityStore());
        var botId = Guid.NewGuid();
        var first = await inbox.EnqueueAsync(botId, "200", "20", null);
        _ = await inbox.EnqueueAsync(botId, "201", "20", null);
        await inbox.MarkRetryAsync(first, "transient", TimeSpan.FromMinutes(1));

        var heads = DiscordGatewayInboxStore.DueChannelHeads(
            await inbox.PendingAsync(botId), DateTimeOffset.UtcNow);

        Assert.Empty(heads);
    }
}
