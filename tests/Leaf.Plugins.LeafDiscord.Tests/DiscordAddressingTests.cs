using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Leaf.Plugins.LeafDiscord;
using Leaf.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafDiscord.Tests;

public sealed class DiscordAddressingTests
{
    private const ulong BotId = 900000000000000001;
    private const ulong AskerId = 300000000000000004;
    private const ulong OwnerId = 300000000000000003;

    [Fact]
    public void Reply_reference_carries_the_resolved_target_and_its_author()
    {
        var target = Message(1000, Author(AskerId, "Asker"), "Who handles this?");
        var reply = Message(1001, Author(OwnerId, "Owner Display"), "I will.",
            MessageType.Reply, reference: 1000, referenced: target);

        var reference = DiscordAddressings.ReplyReference(reply, BotId)!;

        Assert.Equal("1000", reference.MessageId);
        Assert.Equal(AskerId.ToString(), reference.AuthorId);
        Assert.Equal("Asker", reference.AuthorName);
        Assert.False(reference.AuthorIsBot);
        Assert.False(reference.AuthorIsThisBot);
        Assert.Equal("Who handles this?", reference.Content);
    }

    [Fact]
    public void Reply_reference_marks_this_bot_and_treats_webhooks_as_bots()
    {
        var ownAnswer = Message(1000, Author(BotId, "Nova", isBot: true), "Done.");
        var relayed = Message(1001, Author(555, "Relay"), "Relayed", source: MessageSource.Webhook);

        var toBot = DiscordAddressings.ReplyReference(
            Message(1002, Author(AskerId, "Asker"), "Thanks", MessageType.Reply, 1000, ownAnswer),
            BotId)!;
        var toWebhook = DiscordAddressings.ReplyReference(
            Message(1003, Author(AskerId, "Asker"), "Ok", MessageType.Reply, 1001, relayed),
            BotId)!;

        Assert.True(toBot.AuthorIsThisBot);
        Assert.True(toBot.AuthorIsBot);
        Assert.False(toWebhook.AuthorIsThisBot);
        Assert.True(toWebhook.AuthorIsBot);
    }

    [Fact]
    public void Unresolved_reply_keeps_only_its_id_and_non_replies_have_none()
    {
        var unresolved = DiscordAddressings.ReplyReference(
            Message(1001, Author(AskerId, "Asker"), "re", MessageType.Reply, reference: 1000),
            BotId)!;
        // A forward also carries a reference, but it is not a reply to anyone.
        var forward = DiscordAddressings.ReplyReference(
            Message(1002, Author(AskerId, "Asker"), "fwd", MessageType.Default, reference: 1000),
            BotId);

        Assert.Equal(new DiscordReplyReference("1000"), unresolved);
        Assert.Null(unresolved.AuthorIsThisBot);
        Assert.Null(forward);
        Assert.Null(DiscordAddressings.ReplyReference(
            Message(1003, Author(AskerId, "Asker"), "plain"), BotId));
    }

    [Fact]
    public void Gap_messages_carry_the_reply_target_and_the_own_bot_flag()
    {
        var target = Message(1000, Author(AskerId, "Asker"), "Question");
        var reply = Message(1001, Author(OwnerId, "Owner Display"), "Answer",
            MessageType.Reply, 1000, target);
        var own = Message(1002, Author(BotId, "Nova", isBot: true), "Mine");

        var gapReply = DiscordBotRuntime.ToGapMessage(reply, BotId)!;
        var gapOwn = DiscordBotRuntime.ToGapMessage(own, BotId)!;

        Assert.False(gapReply.AuthorIsThisBot);
        Assert.Equal("1000", gapReply.ReplyTo!.MessageId);
        Assert.Equal("Asker", gapReply.ReplyTo.AuthorName);
        Assert.True(gapOwn.AuthorIsThisBot);
        Assert.Null(gapOwn.ReplyTo);
        // Without the bot's ID nothing is claimed to be the bot's own.
        Assert.False(DiscordBotRuntime.ToGapMessage(own)!.AuthorIsThisBot);
    }

    [Fact]
    public void Excerpt_is_bounded_without_splitting_a_surrogate_pair()
    {
        Assert.Equal("short", DiscordAddressings.Excerpt("short"));
        var exact = new string('x', DiscordAddressings.MaxExcerptCharacters);
        Assert.Equal(exact, DiscordAddressings.Excerpt(exact));

        var trimmed = DiscordAddressings.Excerpt(new string('x', 2_000));
        Assert.Equal(DiscordAddressings.MaxExcerptCharacters, trimmed.Length);
        Assert.EndsWith("…", trimmed);

        var emoji = new string('x', DiscordAddressings.MaxExcerptCharacters - 2) + "😀😀😀";
        var safe = DiscordAddressings.Excerpt(emoji);
        Assert.True(safe.Length <= DiscordAddressings.MaxExcerptCharacters);
        Assert.False(char.IsHighSurrogate(safe[^2]));
    }

    [Fact]
    public async Task Record_resolves_only_a_human_reply_author_through_the_resolver()
    {
        var entities = new MemoryEntityStore();
        var owner = await entities.UpsertBySlugAsync("user", "owner", "Owner Example",
            new JsonObject { [DiscordUserIdentityResolver.UserField] = OwnerId.ToString() });
        var identities = new DiscordUserIdentityResolver(
            entities, NullLogger<DiscordUserIdentityResolver>.Instance);
        var asked = new List<string>();
        Task<DiscordVerifiedLeafUser?> Resolve(string authorId, CancellationToken ct)
        {
            asked.Add(authorId);
            return identities.ResolveAsync(authorId, owner.Id.ToString(), ct);
        }

        var toOwner = await DiscordAddressings.ToRecordAsync(new DiscordAddressing(
            false, false, false,
            new DiscordReplyReference("1000", OwnerId.ToString(), "Display Name Is Not Identity",
                false, false, new string('y', 900))), Resolve);
        var toBot = await DiscordAddressings.ToRecordAsync(new DiscordAddressing(
            false, true, false,
            new DiscordReplyReference("1001", OwnerId.ToString(), "Nova", true, true, "Done")),
            Resolve);
        var unresolved = await DiscordAddressings.ToRecordAsync(
            new DiscordAddressing(true, false, true, new DiscordReplyReference("1002")), Resolve);
        var plain = await DiscordAddressings.ToRecordAsync(
            new DiscordAddressing(true, false, false, null), Resolve);

        Assert.Equal([OwnerId.ToString()], asked);
        var identity = toOwner["reply_to"]!["author_verified_leaf_identity"]!.AsObject();
        Assert.Equal("Owner Example", identity["display_name"]!.GetValue<string>());
        Assert.True(identity["is_conversation_owner"]!.GetValue<bool>());
        Assert.Equal(DiscordAddressings.MaxExcerptCharacters,
            toOwner["reply_to"]!["excerpt"]!.GetValue<string>().Length);

        Assert.True(toBot["replies_to_bot"]!.GetValue<bool>());
        Assert.True(toBot["reply_to"]!["author_is_this_bot"]!.GetValue<bool>());
        Assert.Null(toBot["reply_to"]!["author_verified_leaf_identity"]);

        Assert.True(unresolved["mentioned"]!.GetValue<bool>());
        Assert.True(unresolved["open_invitation"]!.GetValue<bool>());
        Assert.Equal(
            """{"message_id":"1002","author_name":null,"author_is_this_bot":null,"author_verified_leaf_identity":null,"excerpt":null}""",
            unresolved["reply_to"]!.ToJsonString());

        Assert.Equal(
            """{"schema":"leaf-discord-addressing/v1","mentioned":true,"replies_to_bot":false,"open_invitation":false,"reply_to":null}""",
            plain.ToJsonString());
    }

    [Fact]
    public void Bridge_status_reports_the_owner_link_under_its_public_name()
    {
        var status = new DiscordBotStatus("connected", true, true, "nova", 1, null, null)
        {
            OwnerDiscordLinked = true,
        };

        var json = JsonSerializer.SerializeToNode(status, JsonSerializerOptions.Web)!;

        Assert.True(json["ownerDiscordLinked"]!.GetValue<bool>());
        Assert.False(new DiscordBotStatus("stopped", false, false, null, 0, null, null)
            .OwnerDiscordLinked);
    }

    [Fact]
    public async Task Owner_link_monitor_warns_once_per_loss_and_reports_restoration()
    {
        var (entities, owner, monitor, log, _) = await MonitorAsync();
        var botId = Guid.NewGuid();

        Assert.True(await monitor.CheckAsync(botId, owner.Id.ToString()));
        Assert.Empty(log.Entries);

        await entities.PatchAsync(owner.Id,
            new JsonObject { [DiscordUserIdentityResolver.UserField] = null });
        Assert.False(await monitor.CheckAsync(botId, owner.Id.ToString()));
        Assert.False(await monitor.CheckAsync(botId, owner.Id.ToString()));
        Assert.False(await monitor.CheckAsync(botId, owner.Id.ToString()));
        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(DiscordUserIdentityResolver.UserField, warning.Message);

        await entities.PatchAsync(owner.Id,
            new JsonObject { [DiscordUserIdentityResolver.UserField] = OwnerId.ToString() });
        Assert.True(await monitor.CheckAsync(botId, owner.Id.ToString()));
        Assert.Equal(LogLevel.Information, log.Entries[^1].Level);

        await entities.PatchAsync(owner.Id,
            new JsonObject { [DiscordUserIdentityResolver.UserField] = null });
        Assert.False(await monitor.CheckAsync(botId, owner.Id.ToString()));
        Assert.Equal(2, log.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task Owner_link_monitor_treats_a_missing_owner_as_unlinked()
    {
        var (_, _, monitor, log, _) = await MonitorAsync();

        Assert.False(await monitor.CheckAsync(Guid.NewGuid(), null));
        Assert.False(await monitor.CheckAsync(Guid.NewGuid(), "local-user"));
        Assert.Equal(2, log.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task Owner_link_observation_reads_at_most_once_per_interval()
    {
        var (entities, owner, monitor, log, clock) = await MonitorAsync();
        var botId = Guid.NewGuid();
        Assert.True(await monitor.ObserveAsync(botId, owner.Id.ToString()));

        await entities.PatchAsync(owner.Id,
            new JsonObject { [DiscordUserIdentityResolver.UserField] = null });
        Assert.True(await monitor.ObserveAsync(botId, owner.Id.ToString()));
        Assert.Empty(log.Entries);

        clock.Now += DiscordOwnerLinkMonitor.RecheckInterval;
        Assert.False(await monitor.ObserveAsync(botId, owner.Id.ToString()));
        Assert.Single(log.Entries);
    }

    private static async Task<(MemoryEntityStore, LeafEntity, DiscordOwnerLinkMonitor, ListLogger,
        ManualClock)> MonitorAsync()
    {
        var entities = new MemoryEntityStore();
        var owner = await entities.UpsertBySlugAsync("user", "owner", "Owner Example",
            new JsonObject { [DiscordUserIdentityResolver.UserField] = OwnerId.ToString() });
        var log = new ListLogger();
        var clock = new ManualClock();
        var monitor = new DiscordOwnerLinkMonitor(
            new DiscordUserIdentityResolver(entities, NullLogger<DiscordUserIdentityResolver>.Instance),
            log, clock);
        return (entities, owner, monitor, log, clock);
    }

    private static IUser Author(ulong id, string name, bool isBot = false)
        => FakeDiscordObject.Create<IUser>(new()
        {
            ["Id"] = id,
            ["Username"] = name,
            ["GlobalName"] = name,
            ["IsBot"] = isBot,
        });

    private static IUserMessage Message(
        ulong id, IUser author, string content,
        MessageType type = MessageType.Default,
        ulong? reference = null,
        IUserMessage? referenced = null,
        MessageSource source = MessageSource.User)
        => FakeDiscordObject.Create<IUserMessage>(new()
        {
            ["Id"] = id,
            ["Author"] = author,
            ["Content"] = content,
            ["Type"] = type,
            ["Source"] = source,
            ["Reference"] = reference is { } target ? new MessageReference(target) : null,
            ["ReferencedMessage"] = referenced,
            ["Timestamp"] = DateTimeOffset.UnixEpoch,
            ["Attachments"] = Array.Empty<IAttachment>(),
        });

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ListLogger : ILogger<DiscordOwnerLinkMonitor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>A Discord.Net interface whose members return preset values, or defaults.</summary>
public class FakeDiscordObject : DispatchProxy
{
    private Dictionary<string, object?> values = [];

    public static T Create<T>(Dictionary<string, object?> values) where T : class
    {
        var proxy = Create<T, FakeDiscordObject>();
        ((FakeDiscordObject)(object)proxy).values = values;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name.StartsWith("get_", StringComparison.Ordinal)
            ? targetMethod.Name[4..]
            : targetMethod.Name;
        if (values.TryGetValue(name, out var value)) return value;
        var type = targetMethod.ReturnType;
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
