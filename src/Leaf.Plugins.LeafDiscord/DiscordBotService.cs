using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordBotStatus(
    string State,
    bool Configured,
    bool Enabled,
    string? BotUser,
    int Guilds,
    DateTimeOffset? ConnectedAt,
    string? LastError)
{
    public Guid BotId { get; init; }
    public string ApplicationId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string? AgentName { get; init; }
    public string? AgentAvatarUrl { get; init; }
    public string? ProfileWarning { get; init; }
}

public sealed record DiscordDirectMessageResult(
    DiscordBinding Binding,
    string MessageUid);

public sealed record DiscordMessageMutationResult(
    string MessageUid,
    IReadOnlyList<string> DiscordMessageIds,
    int ChangedCount);

public sealed record DiscordUserProfile(
    string ExternalId,
    string DisplayName,
    string Username,
    string AvatarUrl);

public sealed record DiscordGuildChannel(
    string GuildId,
    string GuildName,
    string ChannelId,
    string ChannelName);

public sealed record DiscordGuildPermissionReport(
    string GuildId,
    string GuildName,
    bool ManageChannels,
    bool ManageWebhooks,
    bool ManageRoles,
    IReadOnlyList<string> Missing);

public sealed record DiscordCommonsWebhookResult(
    string GuildId,
    string ChannelId,
    string ChannelName,
    string Kind,
    bool Reused);

public sealed record DiscordForumPostResult(
    DiscordBinding Binding,
    string MessageUid,
    string GuildId,
    string ForumId,
    string PostId,
    string Title);

internal sealed record DiscordAvatarPayload(
    string AssetId, string DataUri, string Fingerprint);

/// <summary>
/// Supervises one isolated Discord Gateway runtime per configured Agent bot.
/// Bindings route by immutable Discord application ID, never by whichever client
/// happened to connect first.
/// </summary>
public sealed class DiscordBotService(
    DiscordBotConfigurationStore configurations,
    DiscordBindingStore bindings,
    DiscordBehaviorStore behaviors,
    DiscordDeliveryStore deliveries,
    DiscordConversationCoordinator conversations,
    DiscordAgentResolver agents,
    DiscordAttachmentIngestor attachmentIngestor,
    DiscordRestTransport rest,
    DiscordTypingLeases typing,
    DiscordOwnerPresencePublisher ownerPresence,
    DiscordCommonsRelayService commons,
    DiscordCommonsConfigurationStore commonsConfigurations,
    DiscordCommonsHostTurnStore commonsHostTurns,
    DiscordGatewayInboxStore gatewayInbox,
    DiscordReplyNotifier replyNotifier,
    IAssets assets,
    ILoggerFactory loggerFactory,
    IEnumerable<IDiscordExplicitAction> explicitActions) : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DiscordBotRuntime> runtimes = new();

    public async Task<IReadOnlyList<DiscordBotStatus>> GetStatusesAsync(
        CancellationToken ct = default)
    {
        var configs = await configurations.ListAsync(ct);
        var result = new List<DiscordBotStatus>(configs.Count);
        foreach (var config in configs)
        {
            var status = runtimes.TryGetValue(config.EntityId, out var runtime)
                ? await runtime.GetStatusAsync(ct)
                : new DiscordBotStatus(
                    config.Enabled && DescribeConfigurationIssue(config) is not null
                        ? "not-configured"
                        : config.Enabled ? "stopped" : "disabled",
                    !string.IsNullOrWhiteSpace(config.BotToken),
                    config.Enabled,
                    null,
                    0,
                    null,
                    config.Enabled ? DescribeConfigurationIssue(config) : null);
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            result.Add(status with
            {
                BotId = config.EntityId,
                ApplicationId = config.ApplicationId,
                AgentId = config.AgentId,
                AgentName = agent?.Name,
                AgentAvatarUrl = agent is null ? null : JsonRead.String(agent.Data, "avatar"),
            });
        }
        return result;
    }

    public async Task<DiscordBotStatus> GetStatusAsync(CancellationToken ct = default)
        => (await GetStatusesAsync(ct)).FirstOrDefault()
           ?? new DiscordBotStatus("not-configured", false, false, null, 0, null, null);

    internal static string? DescribeConfigurationIssue(LeafDiscordConfig config)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(config.ApplicationId)) missing.Add("application ID");
        if (string.IsNullOrWhiteSpace(config.BotToken)) missing.Add("bot token");
        if (string.IsNullOrWhiteSpace(config.AgentId)) missing.Add("Agent");
        return missing.Count == 0
            ? null
            : $"Discord bot cannot start because the {string.Join(", ", missing)} "
              + (missing.Count == 1 ? "is" : "are") + " missing.";
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        await lifecycle.WaitAsync(stoppingToken);
        try
        {
            foreach (var config in await configurations.ListAsync(stoppingToken))
                await GetOrCreateRuntime(config).StartAsync(stoppingToken);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task RestartAsync(CancellationToken stoppingToken)
    {
        await lifecycle.WaitAsync(stoppingToken);
        try
        {
            foreach (var runtime in runtimes.Values)
                await runtime.StopAsync();
            foreach (var config in await configurations.ListAsync(stoppingToken))
                await GetOrCreateRuntime(config).StartAsync(stoppingToken);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task RestartAsync(Guid botId, CancellationToken stoppingToken)
    {
        var config = await configurations.GetAsync(botId, stoppingToken)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        await lifecycle.WaitAsync(stoppingToken);
        try
        {
            var runtime = GetOrCreateRuntime(config);
            await runtime.StopAsync();
            await runtime.StartAsync(stoppingToken);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task<DiscordUserProfile?> GetUserProfileAsync(
        DiscordBinding binding, string externalId, CancellationToken ct = default)
    {
        try
        {
            return await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
                .GetUserProfileAsync(externalId, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the author of one message in the binding's own Discord conversation. Used only
    /// for transcript turns admitted before the bridge retained inbound attribution.
    /// </summary>
    public async Task<DiscordInboundAttribution?> GetMessageAttributionAsync(
        DiscordBinding binding, string messageId, CancellationToken ct = default)
    {
        try
        {
            return await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
                .GetMessageAttributionAsync(binding, messageId, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string?> GetConversationIconUrlAsync(
        DiscordBinding binding, CancellationToken ct = default)
    {
        try
        {
            return await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
                .GetConversationIconUrlAsync(binding, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<DiscordDirectMessageResult> SendDirectMessageAsync(
        DiscordBinding source,
        string recipientUserId,
        string content,
        string idempotencyKey,
        CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(source.ApplicationId, ct))
            .SendDirectMessageAsync(recipientUserId, content, idempotencyKey, ct);

    public async Task<DiscordDirectMessageResult> SendExistingBindingMessageAsync(
        DiscordBinding binding,
        string content,
        string idempotencyKey,
        CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
            .SendExistingBindingMessageAsync(binding, content, idempotencyKey, ct);

    public async Task<DiscordDirectMessageResult> SendExistingBindingMessageAsync(
        DiscordBinding binding,
        string content,
        string idempotencyKey,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset> outputAssets,
        CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
            .SendExistingBindingMessageAsync(
                binding, content, idempotencyKey, deliveryContent, outputAssets, ct);

    public async Task AddReactionAsync(
        DiscordBinding binding, string messageId, string emoji, CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
            .AddReactionAsync(binding, messageId, emoji, ct);

    public async Task<string> PinExistingBindingMessageAsync(
        DiscordBinding binding, string messageUid, CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
            .PinExistingBindingMessageAsync(binding, messageUid, ct);

    public async Task<DiscordMessageMutationResult> EditExistingBindingMessageAsync(
        DiscordBinding binding,
        string messageUid,
        string? content,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset>? outputAssets,
        CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
            .EditExistingBindingMessageAsync(
                binding, messageUid, content, deliveryContent, outputAssets, ct);

    public async Task<DiscordMessageMutationResult> DeleteExistingBindingMessageAsync(
        DiscordBinding binding, string messageUid, CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
            .DeleteExistingBindingMessageAsync(binding, messageUid, ct);

    public async Task<DiscordCommandReport> GetCommandsAsync(
        Guid botId, CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(botId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.GetCommandsAsync(config, ct);
    }

    internal async Task<IReadOnlyList<DiscordApplicationEmoji>> ListApplicationEmojisAsync(
        Guid botId, CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(botId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.ListApplicationEmojisAsync(config, ct);
    }

    internal async Task<DiscordApplicationEmoji> CreateApplicationEmojiAsync(
        Guid botId, string name, DiscordApplicationEmojiImage image,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(botId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.CreateApplicationEmojiAsync(config, name, image, ct);
    }

    internal async Task DeleteApplicationEmojiAsync(
        Guid botId, ulong emojiId, CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(botId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        await runtime.DeleteApplicationEmojiAsync(config, emojiId, ct);
    }

    public async Task<IReadOnlyList<DiscordGuildChannel>> ListGuildChannelsAsync(
        Guid botId, CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return runtime.ListGuildChannels();
    }

    public async Task<IReadOnlyList<JsonObject>> ListGuildNavigationAsync(
        Guid botId, CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return runtime.ListGuildNavigation();
    }

    public async Task<DiscordForumPostResult> CreateForumPostAsync(
        Guid botId,
        string guildId,
        string forumId,
        string title,
        string content,
        string idempotencyKey,
        string ownerUserId,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.CreateForumPostAsync(
            guildId, forumId, title, content, idempotencyKey, ownerUserId, ct);
    }

    public async Task<DiscordOwnerDirectMessageResult> OpenOwnerDirectMessageAsync(
        Guid botId,
        string ownerUserId,
        DiscordOwnerDirectMessageRequest request,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.OpenOwnerDirectMessageAsync(ownerUserId, request, ct);
    }

    public async Task<DiscordChannelEditResult> EditGuildChannelAsync(
        Guid botId,
        string guildId,
        string channelId,
        DiscordChannelEditRequest request,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.EditGuildChannelAsync(
            guildId, channelId, request, ct);
    }

    public async Task<DiscordGuildChannelCreateResult> CreateGuildTextChannelAsync(
        Guid botId,
        string guildId,
        DiscordGuildChannelCreateRequest request,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.CreateGuildTextChannelAsync(guildId, request, ct);
    }

    public async Task<DiscordGuildChannelCreateResult> CreateGuildCategoryAsync(
        Guid botId,
        string guildId,
        DiscordGuildCategoryCreateRequest request,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.CreateGuildCategoryAsync(guildId, request, ct);
    }

    public async Task<DiscordGuildPermissionReport> GetGuildPermissionReportAsync(
        Guid botId, string guildId, CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return runtime.GetGuildPermissionReport(guildId);
    }

    public async Task<DiscordGuildChannelCreateResult> CreateGuildForumChannelAsync(
        Guid botId,
        string guildId,
        DiscordGuildForumChannelCreateRequest request,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.CreateGuildForumChannelAsync(guildId, request, ct);
    }

    public async Task<DiscordCommonsWebhookResult> EnsureCommonsWebhookAsync(
        Guid botId,
        string guildId,
        string channelId,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.EnsureCommonsWebhookAsync(guildId, channelId, ct);
    }

    public async Task<bool> IsCommonsChannelAsync(
        Guid botId, string guildId, string channelId, CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.IsCommonsChannelAsync(guildId, channelId, ct);
    }

    public async Task<DiscordBinding> CreateGuildChannelBindingAsync(
        Guid botId, string guildId, string channelId, string ownerUserId,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        var channel = runtime.ResolveGuildChannel(guildId, channelId);
        if (await runtime.IsNativeSuppressedAsync(guildId, channelId, ct))
            throw new UnauthorizedAccessException(
                "Native Agent bindings are disabled in Leaf Community channels.");
        var behavior = await behaviors.ResolveAsync(
            config, "guild_channel", channel.GuildId, channel.ChannelId,
            null, DiscordResponseModes.Direct, ct);
        var binding = await bindings.GetOrCreateAsync(new CreateDiscordBinding(
            config.ApplicationId, channel.ChannelId, "guild_channel", channel.GuildId, null,
            config.AgentId, ownerUserId, config.AudienceProfile, behavior.ResponseMode,
            channel.ChannelName, channel.GuildName, null,
            config.SessionQualityMode), ct);
        return await conversations.EnsureReadyAsync(binding, ct);
    }

    internal async Task<JsonObject> GetGuildNavigationAsync(
        DiscordBinding source, CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(source.ApplicationId, ct))
            .GetGuildNavigationAsync(source, ct);

    internal async Task<IReadOnlyList<DiscordGuildMessageView>> ReadGuildChannelMessagesAsync(
        DiscordBinding source, string channelId, int limit,
        CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(source.ApplicationId, ct))
            .ReadGuildChannelMessagesAsync(source, channelId, limit, ct);

    internal async Task<IReadOnlyList<DiscordGuildMessageView>> ReadGuildChannelMessagesAsync(
        Guid botId, string guildId, string channelId, int limit,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        return await (await ResolveRuntimeAsync(config.ApplicationId, ct))
            .ReadGuildChannelMessagesAsync(guildId, channelId, limit, ct);
    }

    internal async Task<IReadOnlyList<DiscordForumPostView>> ListForumPostsAsync(
        DiscordBinding source, string forumId, int limit, bool includeArchived,
        CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(source.ApplicationId, ct))
            .ListForumPostsAsync(source, forumId, limit, includeArchived, ct);

    internal async Task<IReadOnlyList<DiscordGuildMessageView>> ReadForumPostMessagesAsync(
        DiscordBinding source, string forumId, string postId, int limit,
        CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(source.ApplicationId, ct))
            .ReadForumPostMessagesAsync(source, forumId, postId, limit, ct);

    public async Task<LeafDiscordConfig> SetBotAvatarAsync(
        Guid botId, string? avatarAssetId, CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.SetBotAvatarAsync(avatarAssetId, ct);
    }

    public async Task<LeafDiscordConfig> SetGuildAvatarAsync(
        Guid botId, string guildId, string? avatarAssetId,
        CancellationToken ct = default)
    {
        var config = await configurations.GetAsync(botId, ct)
            ?? throw new KeyNotFoundException("Discord bot configuration was not found.");
        var runtime = runtimes.TryGetValue(config.EntityId, out var current)
            ? current
            : throw new InvalidOperationException("Discord bot is not running.");
        return await runtime.SetGuildAvatarAsync(guildId, avatarAssetId, ct);
    }

    public async Task<DiscordBinding> SetChannelAvatarAsync(
        DiscordBinding binding, string? avatarAssetId,
        CancellationToken ct = default)
        => await (await ResolveRuntimeAsync(binding.ApplicationId, ct))
            .SetChannelAvatarAsync(binding, avatarAssetId, ct);

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            foreach (var runtime in runtimes.Values)
                await runtime.StopAsync();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private DiscordBotRuntime GetOrCreateRuntime(LeafDiscordConfig config)
        => runtimes.GetOrAdd(config.EntityId, id => new DiscordBotRuntime(
            id,
            configurations,
            bindings,
            behaviors,
            deliveries,
            conversations,
            agents,
            attachmentIngestor,
            rest,
            typing,
            ownerPresence,
            commons,
            commonsConfigurations,
            commonsHostTurns,
            gatewayInbox,
            replyNotifier,
            assets,
            loggerFactory.CreateLogger<DiscordBotRuntime>(),
            explicitActions));

    private async Task<DiscordBotRuntime> ResolveRuntimeAsync(
        string applicationId, CancellationToken ct)
    {
        var config = await configurations.FindByApplicationIdAsync(applicationId, ct)
            ?? throw new InvalidOperationException(
                $"Discord application '{applicationId}' is not configured.");
        return runtimes.TryGetValue(config.EntityId, out var runtime)
            ? runtime
            : throw new InvalidOperationException(
                $"Discord bot for application '{applicationId}' is not running.");
    }

    internal static void ValidateApplicationId(string configuredApplicationId, ulong botUserId)
        => DiscordBotRuntime.ValidateApplicationId(configuredApplicationId, botUserId);

    internal static DiscordTransportStatus ReconcileTransportStatus(
        string cachedState, string? cachedError, ConnectionState? observedState)
        => DiscordBotRuntime.ReconcileTransportStatus(cachedState, cachedError, observedState);

    internal static string DescribeDisconnect(Exception exception)
        => DiscordBotRuntime.DescribeDisconnect(exception);

    internal static DiscordSocketConfig CreateSocketConfig(
        string presenceContextMode, bool commonsMembershipEvents = false)
        => DiscordBotRuntime.CreateSocketConfig(presenceContextMode, commonsMembershipEvents);

    internal static void ValidateExistingBindingTarget(
        DiscordBinding binding, IReadOnlySet<string> dmAllowlist)
        => DiscordBotRuntime.ValidateExistingBindingTarget(binding, dmAllowlist);

    internal static CreateDiscordBinding? BuildExistingDmBackfill(
        DiscordBinding existing,
        string authenticatedAuthorId,
        string authenticatedAuthorName,
        bool authorAllowed)
        => DiscordBotRuntime.BuildExistingDmBackfill(
            existing, authenticatedAuthorId, authenticatedAuthorName, authorAllowed);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var runtime in runtimes.Values)
            await runtime.DisposeAsync();
        runtimes.Clear();
        lifecycle.Dispose();
    }
}

internal sealed class DiscordBotRuntime(
    Guid configId,
    DiscordBotConfigurationStore configStore,
    DiscordBindingStore bindings,
    DiscordBehaviorStore behaviors,
    DiscordDeliveryStore deliveries,
    DiscordConversationCoordinator conversations,
    DiscordAgentResolver agents,
    DiscordAttachmentIngestor attachmentIngestor,
    DiscordRestTransport rest,
    DiscordTypingLeases typing,
    DiscordOwnerPresencePublisher ownerPresence,
    DiscordCommonsRelayService commons,
    DiscordCommonsConfigurationStore commonsConfigurations,
    DiscordCommonsHostTurnStore commonsHostTurns,
    DiscordGatewayInboxStore gatewayInbox,
    DiscordReplyNotifier replyNotifier,
    IAssets assets,
    ILogger logger,
    IEnumerable<IDiscordExplicitAction> explicitActions) : IAsyncDisposable
{
    private static readonly TimeSpan InitialReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UserProfileCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaintenanceRecoveryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaintenanceFailureBackoff = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan SelectiveTypingDelay = TimeSpan.FromSeconds(2);
    private const int GuildStartupBackfillLimit = 25;
    private const int DmStartupBackfillLimit = 50;
    private static readonly TimeSpan GuildStartupBackfillMaxAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DmStartupBackfillMaxAge = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly DiscordMaintenanceQueue maintenance = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> outboxGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> forumPostGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> channelEditGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> channelCreateGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> settledPeerTurns = new(StringComparer.Ordinal);
    // One outbound review runner per binding. A request while it runs makes it loop once more,
    // and the runner leaves only after seeing no request under the same lock, so none is lost.
    private readonly Dictionary<string, Task> outboundReviewRunners = new(StringComparer.Ordinal);
    private readonly HashSet<string> outboundReviewRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ulong, CachedUserProfile> userProfiles = new();
    private readonly DiscordOwnerDirectMessenger ownerDirectMessages =
        new(bindings, behaviors, deliveries, conversations, rest, assets);
    private DiscordSocketClient? client;
    private Func<Task>? clientConnectedHandler;
    private Func<Task>? clientReadyHandler;
    private Func<Exception, Task>? clientDisconnectedHandler;
    private Func<SocketUser, SocketPresence, SocketPresence, Task>? clientPresenceHandler;
    private Func<SocketUser, SocketVoiceState, SocketVoiceState, Task>? clientVoiceHandler;
    private Action<DiscordBinding>? conversationReconciledHandler;
    private Action<DiscordBinding, DateTimeOffset>? conversationRetryHandler;
    private string? maintenanceApplicationId;
    private CancellationTokenSource? runCts;
    private Task? outboxLoop;
    private Task? gatewayInboxLoop;
    private Task? presenceLoop;
    private Task? commonsRoleLoop;
    private Task? commonsWebhookLoop;
    private Task? commonsMembershipLoop;
    private DiscordCommonsMembershipReconciler? commonsMembership;
    // Set when Discord refused the member intents; cleared by an explicit restart.
    private bool commonsMemberIntentsDisallowed;
    private string state = "stopped";
    private DateTimeOffset? connectedAt;
    private string? lastError;
    private string? profileWarning;

    public async Task<DiscordBotStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);
        var current = client;
        var reported = ReconcileTransportStatus(
            state, lastError, current?.ConnectionState);
        return new DiscordBotStatus(
            reported.State,
            !string.IsNullOrWhiteSpace(config.BotToken),
            config.Enabled,
            current?.CurrentUser?.Username,
            current?.Guilds.Count ?? 0,
            connectedAt,
            reported.LastError)
        {
            ProfileWarning = profileWarning,
        };
    }

    public async Task<IReadOnlyList<DiscordApplicationEmoji>> ListApplicationEmojisAsync(
        LeafDiscordConfig config, CancellationToken ct)
    {
        var current = ConnectedApplicationClient(config);
        var emotes = await current.GetApplicationEmotesAsync(
            new RequestOptions { CancelToken = ct });
        return emotes.Select(DiscordApplicationEmojis.Project).ToArray();
    }

    public async Task<DiscordApplicationEmoji> CreateApplicationEmojiAsync(
        LeafDiscordConfig config, string name, DiscordApplicationEmojiImage image,
        CancellationToken ct)
    {
        var current = ConnectedApplicationClient(config);
        using var stream = new MemoryStream(image.Bytes, writable: false);
        using var upload = new Image(stream);
        var created = await current.CreateApplicationEmoteAsync(
            name, upload, new RequestOptions { CancelToken = ct });
        return DiscordApplicationEmojis.Project(created);
    }

    public async Task DeleteApplicationEmojiAsync(
        LeafDiscordConfig config, ulong emojiId, CancellationToken ct)
    {
        var current = ConnectedApplicationClient(config);
        await current.DeleteApplicationEmoteAsync(
            emojiId, new RequestOptions { CancelToken = ct });
    }

    // Application emojis belong to the connected application, so its identity must
    // be the configured one before anything is listed or uploaded.
    private DiscordSocketClient ConnectedApplicationClient(LeafDiscordConfig config)
    {
        var current = client;
        if (current is null || current.ConnectionState != ConnectionState.Connected
            || current.CurrentUser is null)
            throw new InvalidOperationException("Discord bot is not connected.");
        DiscordCommandDiagnostics.ValidateIdentity(config, configId, current.CurrentUser.Id);
        return current;
    }

    public async Task<DiscordCommandReport> GetCommandsAsync(
        LeafDiscordConfig config, CancellationToken ct)
    {
        var current = client;
        if (current is null || current.ConnectionState != ConnectionState.Connected
            || current.CurrentUser is null)
            throw new InvalidOperationException("Discord bot is not connected.");
        DiscordCommandDiagnostics.ValidateIdentity(config, configId, current.CurrentUser.Id);
        // Use REST directly: Gateway connectivity and cached commands are not registration evidence.
        var commands = await ((IDiscordClient)current.Rest).GetGlobalApplicationCommandsAsync(
            options: new RequestOptions { CancelToken = ct });
        if (commands.Any(command => command.ApplicationId.ToString() != config.ApplicationId))
            throw new InvalidOperationException("Discord command application does not match its configuration.");
        var registered = commands.Select(command => new DiscordRegisteredCommand(
            command.Name, command.Type.ToString(),
            command.ContextTypes?.Select(context => context.ToString()).ToArray())).ToArray();
        return DiscordCommandDiagnostics.Compare(config, explicitActions, registered);
    }

    public IReadOnlyList<DiscordGuildChannel> ListGuildChannels()
    {
        var current = client
            ?? throw new InvalidOperationException("Discord bot is not connected.");
        return current.Guilds
            .SelectMany(guild => guild.TextChannels
                .Where(channel => CanUse(guild, channel))
                .Select(channel => new DiscordGuildChannel(
                    guild.Id.ToString(), guild.Name,
                    channel.Id.ToString(), channel.Name)))
            .OrderBy(channel => channel.GuildName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(channel => channel.ChannelName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<JsonObject> ListGuildNavigation()
    {
        var current = client
            ?? throw new InvalidOperationException("Discord bot is not connected.");
        var capturedAt = DateTimeOffset.UtcNow;
        return current.Guilds
            .OrderBy(guild => guild.Name, StringComparer.OrdinalIgnoreCase)
            .Select(guild => BuildGuildNavigation(guild, "", capturedAt))
            .ToArray();
    }

    public DiscordGuildPermissionReport GetGuildPermissionReport(string guildId)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0)
            throw new ArgumentException("Discord guild ID must be a snowflake.");
        var guild = client?.GetGuild(parsedGuildId)
            ?? throw new KeyNotFoundException("Discord guild was not found for this bot.");
        var permissions = guild.CurrentUser.GuildPermissions;
        var missing = new List<string>();
        if (!permissions.ManageChannels) missing.Add("Manage Channels");
        if (!permissions.ManageWebhooks) missing.Add("Manage Webhooks");
        if (!permissions.ManageRoles) missing.Add("Manage Roles");
        return new DiscordGuildPermissionReport(
            guildId, guild.Name,
            permissions.ManageChannels,
            permissions.ManageWebhooks,
            permissions.ManageRoles,
            missing);
    }

    public async Task<DiscordForumPostResult> CreateForumPostAsync(
        string guildId,
        string forumId,
        string title,
        string content,
        string idempotencyKey,
        string ownerUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 100)
            throw new ArgumentException("Discord forum post title must contain 1 to 100 characters.");
        if (string.IsNullOrWhiteSpace(content) || content.Trim().Length > 2_000)
            throw new ArgumentException("Discord forum starter must contain 1 to 2,000 characters.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new ArgumentException("The Leaf owner user ID is required.");
        title = title.Trim();
        var guarded = conversations.PrepareOutreach(content.Trim(), null, []);
        var hiddenInPost = guarded.HiddenCharacters
            || !ReferenceEquals(DiscordEgressGuard.StripHiddenCharacters(title), title);
        content = guarded.DeliveryContent;
        idempotencyKey = idempotencyKey.Trim();
        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var config = await GetConfigAsync(ct);
        var forum = ResolveWritableForum(activeClient, guildId, forumId);
        if (await IsNativeSuppressedAsync(guildId, forum.Id.ToString(), ct))
            throw new UnauthorizedAccessException(
                "Native Agent forum posts are disabled in Leaf Community channels.");
        var gateKey = $"{config.ApplicationId}:{guildId}:{forumId}:{idempotencyKey}";
        var gate = forumPostGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var operation = await deliveries.ReserveForumPostAsync(
                config.ApplicationId, guildId, forumId, idempotencyKey,
                title, content, ct);
            var thread = await FindForumPostAsync(
                forum, operation.DiscordId, title, content,
                activeClient.CurrentUser.Id, operation.CreatedAt, ct);
            if (thread is null && operation.State == "sent")
                throw new InvalidOperationException(
                    "The recorded Discord forum post is no longer visible to this bot.");
            if (thread is null)
                await conversations.ReviewOutgoingOrThrowAsync(
                    new DiscordOutgoingContext(ownerUserId, config.AgentId,
                        $"{config.ApplicationId}:{guildId}:{forumId}",
                        "a new post in a Discord server forum, readable by every member who can see it"),
                    $"{title}\n\n{content}", [], hiddenInPost,
                    $"forum:{config.ApplicationId}:{idempotencyKey}", ct);
            thread ??= await forum.CreatePostAsync(
                title,
                forum.DefaultAutoArchiveDuration,
                text: content,
                options: new RequestOptions { CancelToken = ct },
                allowedMentions: AllowedMentions.None);

            var binding = await bindings.GetOrCreateAsync(new CreateDiscordBinding(
                config.ApplicationId,
                thread.Id.ToString(),
                "thread",
                guildId,
                forumId,
                config.AgentId,
                ownerUserId,
                config.AudienceProfile,
                (await behaviors.ResolveAsync(
                    config, "thread", guildId, thread.Id.ToString(), forumId,
                    DiscordResponseModes.Direct, ct)).ResponseMode,
                title,
                forum.Guild.Name,
                null,
                config.SessionQualityMode), ct);
            binding = await conversations.EnsureReadyAsync(binding, ct);
            var recorded = await conversations.RecordDeliveredProactiveAsync(
                binding, idempotencyKey, content, thread.Id.ToString(), ct);
            await deliveries.MarkSentAsync(operation, thread.Id.ToString(), ct);
            return new DiscordForumPostResult(
                recorded.Binding, recorded.MessageUid, guildId, forumId,
                thread.Id.ToString(), title);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordChannelEditResult> EditGuildChannelAsync(
        string guildId,
        string channelId,
        DiscordChannelEditRequest request,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0
            || !ulong.TryParse(channelId, out var parsedChannelId) || parsedChannelId == 0)
            throw new ArgumentException("Discord guild and channel IDs must be snowflakes.");

        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var guild = activeClient.GetGuild(parsedGuildId)
            ?? throw new KeyNotFoundException("Discord guild was not found for this bot.");
        var channel = guild.GetChannel(parsedChannelId)
            ?? throw new KeyNotFoundException("Discord channel was not found in that guild.");
        var kind = EditableChannelKind(channel);
        var permissions = guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.ManageChannel)
            throw new UnauthorizedAccessException(
                "The Discord bot needs View Channel and Manage Channels for that channel.");

        var patch = DiscordChannelEdits.BuildPatch(request, kind);
        if (request.ParentCategoryId is not null)
        {
            var categoryId = ulong.Parse(request.ParentCategoryId);
            var category = guild.GetCategoryChannel(categoryId)
                ?? throw new KeyNotFoundException(
                    "Discord parent category was not found in that guild.");
            if (!guild.CurrentUser.GetPermissions(category).ViewChannel)
                throw new UnauthorizedAccessException(
                    "The Discord bot cannot view the requested parent category.");
        }

        var config = await GetConfigAsync(ct);
        var botToken = config.BotToken
            ?? throw new InvalidOperationException("Discord bot token is unavailable.");
        var gateKey = $"{config.ApplicationId}:{guildId}:{channelId}";
        await ReviewChannelSettingsAsync(config, guildId, patch, ct);
        var gate = channelEditGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var response = await rest.ModifyGuildChannelAsync(
                botToken,
                channelId,
                patch,
                "Leaf owner requested this channel edit through Nova",
                ct);
            var result = ProjectEditedChannel(guild, channel, kind, response);
            try
            {
                var binding = await bindings.FindAsync(
                    config.ApplicationId, channelId, ct);
                if (binding is not null
                    && !string.Equals(binding.ConversationName, result.ChannelName,
                        StringComparison.Ordinal))
                    await bindings.UpdateConversationMetadataAsync(
                        binding, result.ChannelName, guild.Name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Discord channel {ChannelId} was edited but its Leaf binding metadata could not be refreshed",
                    channelId);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordGuildChannelCreateResult> CreateGuildTextChannelAsync(
        string guildId,
        DiscordGuildChannelCreateRequest request,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0)
            throw new ArgumentException("Discord guild ID must be a snowflake.");

        var payload = DiscordChannelEdits.BuildTextChannelCreatePayload(request);
        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var guild = activeClient.GetGuild(parsedGuildId)
            ?? throw new KeyNotFoundException("Discord guild was not found for this bot.");
        if (!guild.CurrentUser.GuildPermissions.ManageChannels)
            throw new UnauthorizedAccessException(
                "The Discord bot needs Manage Channels for that server.");

        if (request.ParentCategoryId is not null)
        {
            var categoryId = ulong.Parse(request.ParentCategoryId);
            var category = guild.GetCategoryChannel(categoryId)
                ?? throw new KeyNotFoundException(
                    "Discord parent category was not found in that guild.");
            var permissions = guild.CurrentUser.GetPermissions(category);
            if (!permissions.ViewChannel || !permissions.SendMessages)
                throw new UnauthorizedAccessException(
                    "The Discord bot must be able to view and send messages in the requested parent category.");
        }

        var config = await GetConfigAsync(ct);
        var botToken = config.BotToken
            ?? throw new InvalidOperationException("Discord bot token is unavailable.");
        await ReviewChannelSettingsAsync(config, guildId, payload, ct);
        var gateKey = $"{config.ApplicationId}:{guildId}";
        var gate = channelCreateGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var response = await rest.CreateGuildChannelAsync(
                botToken,
                guildId,
                payload,
                "Leaf owner requested this channel creation through Nova",
                ct);
            return ProjectCreatedTextChannel(guild, response);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordGuildChannelCreateResult> CreateGuildCategoryAsync(
        string guildId,
        DiscordGuildCategoryCreateRequest request,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0)
            throw new ArgumentException("Discord guild ID must be a snowflake.");
        var payload = DiscordChannelEdits.BuildCategoryCreatePayload(request);
        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var guild = activeClient.GetGuild(parsedGuildId)
            ?? throw new KeyNotFoundException("Discord guild was not found for this bot.");
        if (!guild.CurrentUser.GuildPermissions.ManageChannels)
            throw new UnauthorizedAccessException(
                "The Discord bot needs Manage Channels for that server.");
        var config = await GetConfigAsync(ct);
        var botToken = config.BotToken
            ?? throw new InvalidOperationException("Discord bot token is unavailable.");
        await ReviewChannelSettingsAsync(config, guildId, payload, ct);
        var gateKey = $"{config.ApplicationId}:{guildId}";
        var gate = channelCreateGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var response = await rest.CreateGuildChannelAsync(
                botToken, guildId, payload,
                "Leaf owner requested this category creation through Nova", ct);
            return ProjectCreatedCategory(guild, response);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordGuildChannelCreateResult> CreateGuildForumChannelAsync(
        string guildId,
        DiscordGuildForumChannelCreateRequest request,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0)
            throw new ArgumentException("Discord guild ID must be a snowflake.");
        var payload = DiscordChannelEdits.BuildForumChannelCreatePayload(request);
        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var guild = activeClient.GetGuild(parsedGuildId)
            ?? throw new KeyNotFoundException("Discord guild was not found for this bot.");
        if (!guild.CurrentUser.GuildPermissions.ManageChannels)
            throw new UnauthorizedAccessException(
                "The Discord bot needs Manage Channels for that server.");
        if (request.ParentCategoryId is not null)
        {
            var categoryId = ulong.Parse(request.ParentCategoryId);
            var category = guild.GetCategoryChannel(categoryId)
                ?? throw new KeyNotFoundException(
                    "Discord parent category was not found in that guild.");
            var permissions = guild.CurrentUser.GetPermissions(category);
            if (!permissions.ViewChannel || !permissions.SendMessages)
                throw new UnauthorizedAccessException(
                    "The Discord bot must be able to view and send messages in the requested parent category.");
        }

        var config = await GetConfigAsync(ct);
        var botToken = config.BotToken
            ?? throw new InvalidOperationException("Discord bot token is unavailable.");
        await ReviewChannelSettingsAsync(config, guildId, payload, ct);
        var gateKey = $"{config.ApplicationId}:{guildId}";
        var gate = channelCreateGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var response = await rest.CreateGuildChannelAsync(
                botToken,
                guildId,
                payload,
                "Leaf owner requested this forum creation through Nova",
                ct);
            return ProjectCreatedForumChannel(guild, response);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordCommonsWebhookResult> EnsureCommonsWebhookAsync(
        string guildId,
        string channelId,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0
            || !ulong.TryParse(channelId, out var parsedChannelId) || parsedChannelId == 0)
            throw new ArgumentException("Discord guild and channel IDs must be snowflakes.");
        var commonsConfig = await commonsConfigurations.GetAsync(configId, ct)
            ?? throw new InvalidOperationException("Discord Commons is not configured.");
        if (!await IsCommonsChannelAsync(guildId, channelId, ct))
            throw new UnauthorizedAccessException(
                "That Discord channel is outside the configured Leaf Community scope.");
        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var guild = activeClient.GetGuild(parsedGuildId)
            ?? throw new KeyNotFoundException("Discord guild was not found for this bot.");
        var channel = guild.GetChannel(parsedChannelId)
            ?? throw new KeyNotFoundException("Discord channel was not found in that guild.");
        var kind = channel switch
        {
            SocketTextChannel => "text",
            SocketForumChannel => "forum",
            _ => throw new ArgumentException(
                "Commons webhooks are supported only for text and forum channels."),
        };
        var permissions = guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.ManageWebhooks)
            throw new UnauthorizedAccessException(
                "The Discord bot needs View Channel, Send Messages, and Manage Webhooks for that channel.");
        if (commonsConfig.WebhookUrls.ContainsKey(channelId))
            return new DiscordCommonsWebhookResult(
                guildId, channelId, channel.Name, kind, true);

        var config = await GetConfigAsync(ct);
        var botToken = config.BotToken
            ?? throw new InvalidOperationException("Discord bot token is unavailable.");
        var gateKey = $"commons-webhook:{config.ApplicationId}:{channelId}";
        var gate = channelCreateGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            commonsConfig = await commonsConfigurations.GetAsync(configId, ct)
                ?? throw new InvalidOperationException("Discord Commons is not configured.");
            if (commonsConfig.WebhookUrls.ContainsKey(channelId))
                return new DiscordCommonsWebhookResult(
                    guildId, channelId, channel.Name, kind, true);
            var created = await rest.CreateWebhookAsync(
                botToken, channelId, "Nova Commons Relay", null, ct);
            var webhookUrl = $"https://discord.com/api/webhooks/{Uri.EscapeDataString(created.Id)}/{Uri.EscapeDataString(created.Token)}";
            _ = await commonsConfigurations.SetWebhookUrlAsync(
                configId, channelId, webhookUrl, ct);
            return new DiscordCommonsWebhookResult(
                guildId, channelId, channel.Name, kind, false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<bool> IsCommonsChannelAsync(
        string guildId, string channelId, CancellationToken ct = default)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId)
            || !ulong.TryParse(channelId, out var parsedChannelId))
            return false;
        var channel = client?.GetGuild(parsedGuildId)?.GetChannel(parsedChannelId);
        var location = channel is null ? null : CommonsLocation(channel);
        return location is not null && await commons.IsCommonsLocationAsync(
            configId, location.Value.GuildId, location.Value.ChannelId,
            location.Value.CategoryId, ct);
    }

    internal async Task<bool> IsNativeSuppressedAsync(
        string guildId, string channelId, CancellationToken ct = default)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId)
            || !ulong.TryParse(channelId, out var parsedChannelId))
            return false;
        var channel = client?.GetGuild(parsedGuildId)?.GetChannel(parsedChannelId);
        var location = channel is null ? null : CommonsLocation(channel);
        var scope = location is null
            ? null
            : await commons.GetScopeAsync(configId, location.Value.GuildId, ct);
        return scope is not null
            && scope.Contains(location!.Value.ChannelId, location.Value.CategoryId)
            && scope.SuppressesNativeAgent;
    }

    private static string EditableChannelKind(SocketGuildChannel channel)
        => channel switch
        {
            SocketCategoryChannel => "category",
            SocketForumChannel => "forum",
            SocketStageChannel => "stage",
            SocketVoiceChannel => "voice",
            SocketTextChannel => "text",
            _ => throw new ArgumentException(
                $"Discord channel type '{channel.GetType().Name}' is not editable through this capability."),
        };

    private static DiscordChannelEditResult ProjectEditedChannel(
        SocketGuild guild,
        SocketGuildChannel original,
        string kind,
        JsonObject response)
    {
        var returnedId = response["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Discord channel edit response has no id.");
        if (!string.Equals(returnedId, original.Id.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Discord returned a different channel after the edit.");
        var name = response["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Discord channel edit response has no name.");
        return new DiscordChannelEditResult(
            guild.Id.ToString(),
            guild.Name,
            returnedId,
            name,
            kind,
            response["parent_id"]?.GetValue<string>(),
            response["topic"]?.GetValue<string>(),
            response["position"]?.GetValue<int>() ?? original.Position,
            response["nsfw"]?.GetValue<bool>(),
            response["rate_limit_per_user"]?.GetValue<int>());
    }

    private static DiscordGuildChannelCreateResult ProjectCreatedTextChannel(
        SocketGuild guild,
        JsonObject response)
    {
        var channelId = response["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Discord channel creation response has no id.");
        if (!ulong.TryParse(channelId, out var parsedChannelId) || parsedChannelId == 0)
            throw new InvalidOperationException(
                "Discord channel creation response has an invalid id.");
        var channelName = response["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Discord channel creation response has no name.");
        if (response["type"]?.GetValue<int>() is not 0)
            throw new InvalidOperationException(
                "Discord did not create a text channel.");
        return new DiscordGuildChannelCreateResult(
            guild.Id.ToString(),
            guild.Name,
            channelId,
            channelName,
            "text",
            response["parent_id"]?.GetValue<string>(),
            response["topic"]?.GetValue<string>(),
            response["position"]?.GetValue<int>() ?? 0,
            response["nsfw"]?.GetValue<bool>() ?? false,
            response["rate_limit_per_user"]?.GetValue<int>() ?? 0);
    }

    private static DiscordGuildChannelCreateResult ProjectCreatedCategory(
        SocketGuild guild,
        JsonObject response)
    {
        var channelId = response["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Discord category creation response has no id.");
        if (!ulong.TryParse(channelId, out var parsedChannelId) || parsedChannelId == 0)
            throw new InvalidOperationException(
                "Discord category creation response has an invalid id.");
        var channelName = response["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Discord category creation response has no name.");
        if (response["type"]?.GetValue<int>() is not 4)
            throw new InvalidOperationException("Discord did not create a category.");
        return new DiscordGuildChannelCreateResult(
            guild.Id.ToString(), guild.Name, channelId, channelName, "category",
            null, null, response["position"]?.GetValue<int>() ?? 0, false, 0);
    }

    private static DiscordGuildChannelCreateResult ProjectCreatedForumChannel(
        SocketGuild guild,
        JsonObject response)
    {
        if (response["type"]?.GetValue<int>() is not 15)
            throw new InvalidOperationException("Discord did not create a forum channel.");
        var channelId = response["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Discord channel creation response has no id.");
        if (!ulong.TryParse(channelId, out var parsedChannelId) || parsedChannelId == 0)
            throw new InvalidOperationException(
                "Discord channel creation response has an invalid id.");
        return new DiscordGuildChannelCreateResult(
            guild.Id.ToString(),
            guild.Name,
            channelId,
            response["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    "Discord channel creation response has no name."),
            "forum",
            response["parent_id"]?.GetValue<string>(),
            response["topic"]?.GetValue<string>(),
            response["position"]?.GetValue<int>() ?? 0,
            response["nsfw"]?.GetValue<bool>() ?? false,
            response["rate_limit_per_user"]?.GetValue<int>() ?? 0);
    }

    private static SocketForumChannel ResolveWritableForum(
        DiscordSocketClient current, string guildId, string forumId)
    {
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0
            || !ulong.TryParse(forumId, out var parsedForumId) || parsedForumId == 0)
            throw new ArgumentException("Discord guild and forum IDs must be snowflakes.");
        var guild = current.GetGuild(parsedGuildId)
            ?? throw new KeyNotFoundException("Discord guild was not found for this bot.");
        var forum = guild.GetChannel(parsedForumId) as SocketForumChannel
            ?? throw new KeyNotFoundException("Discord forum was not found in that guild.");
        var permissions = guild.CurrentUser.GetPermissions(forum);
        if (!permissions.ViewChannel || !permissions.SendMessages
            || !permissions.ReadMessageHistory || !permissions.SendMessagesInThreads)
            throw new UnauthorizedAccessException(
                "The Discord bot cannot create and continue posts in that forum.");
        return forum;
    }

    private static async Task<IThreadChannel?> FindForumPostAsync(
        SocketForumChannel forum,
        string? expectedPostId,
        string title,
        string content,
        ulong botUserId,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        IEnumerable<IThreadChannel> threads = await forum.GetActiveThreadsAsync(options);
        threads = threads.Concat(await forum.GetPublicArchivedThreadsAsync(100, null, options));
        var candidates = threads
            .GroupBy(thread => thread.Id)
            .Select(group => group.First())
            .Where(thread => expectedPostId is not null
                ? string.Equals(thread.Id.ToString(), expectedPostId, StringComparison.Ordinal)
                : string.Equals(thread.Name, title, StringComparison.Ordinal)
                  && thread.CreatedAt >= createdAt.AddMinutes(-1))
            .ToArray();
        var matches = new List<IThreadChannel>();
        foreach (var thread in candidates)
        {
            var starter = await thread.GetMessageAsync(
                thread.Id, CacheMode.AllowDownload, options);
            if (starter is not null && starter.Author.Id == botUserId
                && string.Equals(starter.Content, content, StringComparison.Ordinal))
                matches.Add(thread);
        }
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                "Discord forum-post recovery found multiple matching posts."),
        };
    }

    public DiscordGuildChannel ResolveGuildChannel(string guildId, string channelId)
    {
        var current = client
            ?? throw new InvalidOperationException("Discord bot is not connected.");
        if (!ulong.TryParse(guildId, out var parsedGuildId)
            || !ulong.TryParse(channelId, out var parsedChannelId))
            throw new ArgumentException("Discord guild and channel IDs must be snowflakes.");
        var guild = current.GetGuild(parsedGuildId)
            ?? throw new KeyNotFoundException("Discord guild was not found for this bot.");
        var channel = guild.GetTextChannel(parsedChannelId)
            ?? throw new KeyNotFoundException("Discord text channel was not found in that guild.");
        if (!CanUse(guild, channel))
            throw new UnauthorizedAccessException(
                "The Discord bot cannot view and send messages in that channel.");
        return new DiscordGuildChannel(
            guild.Id.ToString(), guild.Name, channel.Id.ToString(), channel.Name);
    }

    public Task<JsonObject> GetGuildNavigationAsync(
        DiscordBinding source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var guild = ResolveGuild(source);
        return Task.FromResult(BuildGuildNavigation(
            guild, source.ConversationId, DateTimeOffset.UtcNow));
    }

    public async Task<IReadOnlyList<DiscordGuildMessageView>> ReadGuildChannelMessagesAsync(
        DiscordBinding source, string channelId, int limit,
        CancellationToken ct = default)
    {
        var guild = ResolveGuild(source);
        return await ReadGuildChannelMessagesAsync(guild, channelId, limit, ct);
    }

    public async Task<IReadOnlyList<DiscordGuildMessageView>> ReadGuildChannelMessagesAsync(
        string guildId, string channelId, int limit,
        CancellationToken ct = default)
    {
        var current = client
            ?? throw new InvalidOperationException("Discord bot is not connected.");
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0)
            throw new ArgumentException("Discord guild ID must be a snowflake.");
        var guild = current.GetGuild(parsedGuildId)
            ?? throw new UnauthorizedAccessException(
                "The requested guild is unavailable to this Discord bot.");
        return await ReadGuildChannelMessagesAsync(guild, channelId, limit, ct);
    }

    private static async Task<IReadOnlyList<DiscordGuildMessageView>> ReadGuildChannelMessagesAsync(
        SocketGuild guild, string channelId, int limit, CancellationToken ct)
    {
        var target = ResolveActiveGuildChannel(guild, channelId);
        if (target is SocketForumChannel)
            throw new ArgumentException(
                "Forum parents contain posts rather than messages; list the forum posts first.");
        if (target is not IMessageChannel messages)
            throw new ArgumentException("The selected Discord channel has no readable messages.");
        EnsureReadable(guild, target);
        return await ReadMessagesAsync(messages, limit, ct);
    }

    public async Task<IReadOnlyList<DiscordForumPostView>> ListForumPostsAsync(
        DiscordBinding source, string forumId, int limit, bool includeArchived,
        CancellationToken ct = default)
    {
        var guild = ResolveGuild(source);
        var forum = ResolveForum(guild, forumId);
        EnsureReadable(guild, forum);
        limit = BoundedReadLimit(limit);
        var options = new RequestOptions { CancelToken = ct };
        IEnumerable<IThreadChannel> threads = await forum.GetActiveThreadsAsync(options);
        if (includeArchived)
            threads = threads.Concat(await forum.GetPublicArchivedThreadsAsync(
                limit, null, options));

        var tagNames = forum.Tags.ToDictionary(tag => tag.Id);
        return threads
            .GroupBy(thread => thread.Id)
            .Select(group => group.First())
            .OrderByDescending(thread => thread.CreatedAt)
            .Take(limit)
            .Select(thread => ProjectForumPost(thread, tagNames))
            .ToArray();
    }

    public async Task<IReadOnlyList<DiscordGuildMessageView>> ReadForumPostMessagesAsync(
        DiscordBinding source, string forumId, string postId, int limit,
        CancellationToken ct = default)
    {
        var guild = ResolveGuild(source);
        var forum = ResolveForum(guild, forumId);
        EnsureReadable(guild, forum);
        if (!ulong.TryParse(postId, out var parsedPostId) || parsedPostId == 0)
            throw new ArgumentException("Discord post ID must be a snowflake.");
        var options = new RequestOptions { CancelToken = ct };
        IThreadChannel? thread = (await forum.GetActiveThreadsAsync(options))
            .FirstOrDefault(item => item.Id == parsedPostId);
        if (thread is null)
            thread = (await forum.GetPublicArchivedThreadsAsync(100, null, options))
                .FirstOrDefault(item => item.Id == parsedPostId);
        if (thread is null)
            throw new KeyNotFoundException(
                "Discord forum post was not found or is not visible to this bot.");
        return await ReadMessagesAsync(thread, limit, ct);
    }

    private static bool CanUse(SocketGuild guild, SocketTextChannel channel)
    {
        var permissions = guild.CurrentUser.GetPermissions(channel);
        return permissions.ViewChannel && permissions.SendMessages;
    }

    private SocketGuild ResolveGuild(DiscordBinding source)
    {
        var current = client
            ?? throw new InvalidOperationException("Discord bot is not connected.");
        if (source.Kind == "dm" || string.IsNullOrWhiteSpace(source.GuildId)
            || !ulong.TryParse(source.GuildId, out var guildId))
            throw new UnauthorizedAccessException(
                "Guild navigation is available only from a bound guild conversation.");
        return current.GetGuild(guildId)
            ?? throw new UnauthorizedAccessException(
                "The source binding guild is unavailable to this Discord bot.");
    }

    private static SocketGuildChannel ResolveActiveGuildChannel(
        SocketGuild guild, string channelId)
    {
        if (!ulong.TryParse(channelId, out var parsedChannelId) || parsedChannelId == 0)
            throw new ArgumentException("Discord channel ID must be a snowflake.");
        return guild.GetChannel(parsedChannelId)
            ?? guild.ThreadChannels.FirstOrDefault(thread => thread.Id == parsedChannelId)
            ?? throw new KeyNotFoundException(
                "Discord channel was not found or is not active in this guild.");
    }

    private static SocketForumChannel ResolveForum(SocketGuild guild, string forumId)
        => ResolveActiveGuildChannel(guild, forumId) as SocketForumChannel
           ?? throw new KeyNotFoundException("Discord forum was not found in this guild.");

    private static void EnsureReadable(SocketGuild guild, SocketGuildChannel channel)
    {
        var permissionTarget = channel is SocketThreadChannel thread
            ? thread.ParentChannel
            : channel;
        var permissions = guild.CurrentUser.GetPermissions(permissionTarget);
        if (!permissions.ViewChannel || !permissions.ReadMessageHistory)
            throw new UnauthorizedAccessException(
                "The Discord bot cannot view message history in that channel.");
    }

    private static int BoundedReadLimit(int limit)
        => Math.Clamp(limit <= 0 ? 50 : limit, 1, 100);

    private static async Task<IReadOnlyList<DiscordGuildMessageView>> ReadMessagesAsync(
        IMessageChannel channel, int limit, CancellationToken ct)
    {
        var options = new RequestOptions { CancelToken = ct };
        var messages = await channel.GetMessagesAsync(
            BoundedReadLimit(limit), CacheMode.AllowDownload, options).FlattenAsync();
        return messages
            .OrderBy(message => message.Id)
            .Select(message => new DiscordGuildMessageView(
                message.Id.ToString(),
                message.Timestamp,
                message.EditedTimestamp,
                message.Author.Id.ToString(),
                message.Author.GlobalName ?? message.Author.Username,
                message.Author.Username,
                message.Author.IsBot,
                message.Source.ToString(),
                message.Content,
                message.Attachments.Select(attachment =>
                    new DiscordGuildMessageAttachmentView(
                        attachment.Id.ToString(),
                        attachment.Filename,
                        attachment.ContentType,
                        attachment.Size)).ToArray()))
            .ToArray();
    }

    private static DiscordForumPostView ProjectForumPost(
        IThreadChannel thread,
        IReadOnlyDictionary<ulong, ForumTag> availableTags)
        => new(
            thread.Id.ToString(),
            thread.Name,
            thread.CreatedAt,
            thread.IsArchived,
            thread.IsLocked,
            thread.MessageCount,
            thread.AppliedTags.Select(tagId =>
            {
                var found = availableTags.TryGetValue(tagId, out var tag);
                return new DiscordGuildForumTagSnapshot(
                    tagId.ToString(),
                    found ? tag.Name : tagId.ToString(),
                    found ? tag.Emoji?.Name : null,
                    found && tag.IsModerated);
            }).ToArray());

    private static JsonObject BuildGuildNavigation(
        SocketGuild guild, string currentChannelId, DateTimeOffset capturedAt)
    {
        var categories = guild.CategoryChannels
            .Where(category => guild.CurrentUser.GetPermissions(category).ViewChannel)
            .Select(category => new DiscordGuildCategorySnapshot(
                category.Id.ToString(), category.Name, category.Position))
            .ToArray();
        var channels = guild.Channels
            .Concat<SocketGuildChannel>(guild.ThreadChannels)
            .GroupBy(channel => channel.Id)
            .Select(group => group.First())
            .Where(channel => channel is not SocketCategoryChannel)
            .Select(channel => ProjectGuildChannel(guild, channel))
            .Where(channel => channel is not null)
            .Select(channel => channel!)
            .ToArray();
        return DiscordGuildNavigationBuilder.Build(
            guild.Id.ToString(), guild.Name, currentChannelId,
            categories, channels, capturedAt);
    }

    private static DiscordGuildChannelSnapshot? ProjectGuildChannel(
        SocketGuild guild, SocketGuildChannel channel)
    {
        var permissionTarget = channel is SocketThreadChannel thread
            ? thread.ParentChannel
            : channel;
        var permissions = guild.CurrentUser.GetPermissions(permissionTarget);
        if (!permissions.ViewChannel) return null;

        var kind = channel switch
        {
            SocketThreadChannel post when post.ParentChannel is SocketForumChannel => "forum_post",
            SocketThreadChannel => "thread",
            SocketForumChannel => "forum",
            SocketStageChannel => "stage",
            SocketVoiceChannel => "voice",
            SocketTextChannel => "text",
            _ => "other",
        };
        var parentId = channel switch
        {
            SocketThreadChannel post => post.ParentChannel.Id.ToString(),
            SocketForumChannel forum when forum.CategoryId is { } id => id.ToString(),
            SocketTextChannel text when text.CategoryId is { } id => id.ToString(),
            SocketVoiceChannel voice when voice.CategoryId is { } id => id.ToString(),
            SocketStageChannel stage when stage.CategoryId is { } id => id.ToString(),
            _ => null,
        };
        var topic = channel switch
        {
            SocketForumChannel forum => forum.Topic,
            SocketTextChannel text => text.Topic,
            _ => null,
        };
        var tags = channel is SocketForumChannel forumChannel
            ? forumChannel.Tags.Select(tag => new DiscordGuildForumTagSnapshot(
                tag.Id.ToString(), tag.Name, tag.Emoji?.Name, tag.IsModerated)).ToArray()
            : [];
        var canPost = channel is SocketForumChannel
            ? permissions.SendMessages
            : channel is SocketTextChannel && permissions.SendMessages;
        var canReply = channel is SocketThreadChannel
            ? permissions.SendMessagesInThreads
            : canPost;

        return new DiscordGuildChannelSnapshot(
            channel.Id.ToString(), channel.Name, kind, parentId, topic,
            channel.Position, permissions.ReadMessageHistory, canPost, canReply, tags);
    }

    public async Task<DiscordUserProfile?> GetUserProfileAsync(
        string externalId, CancellationToken ct = default)
    {
        if (!ulong.TryParse(externalId, out var userId)) return null;
        if (userProfiles.TryGetValue(userId, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Profile;
        var current = client;
        if (current is null) return null;
        try
        {
            IUser? user = current.GetUser(userId);
            user ??= await ((IDiscordClient)current).GetUserAsync(
                userId, CacheMode.AllowDownload, new RequestOptions { CancelToken = ct });
            if (user is null) return null;
            var profile = new DiscordUserProfile(
                externalId,
                user.GlobalName ?? user.Username,
                user.Username,
                user.GetDisplayAvatarUrl(ImageFormat.Auto, 128));
            userProfiles[userId] = new CachedUserProfile(
                profile, DateTimeOffset.UtcNow.Add(UserProfileCacheTtl));
            return profile;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve Discord profile {UserId}", externalId);
            return null;
        }
    }

    public async Task<DiscordInboundAttribution?> GetMessageAttributionAsync(
        DiscordBinding binding, string messageId, CancellationToken ct = default)
    {
        var current = client;
        if (current is null
            || !ulong.TryParse(binding.ConversationId, out var channelId)
            || !ulong.TryParse(messageId, out var id))
            return null;
        try
        {
            var options = new RequestOptions { CancelToken = ct };
            var channel = current.GetChannel(channelId) as IMessageChannel
                ?? await current.GetChannelAsync(channelId, options) as IMessageChannel;
            if (channel is null) return null;
            var message = await channel.GetMessageAsync(id, CacheMode.AllowDownload, options);
            if (message is null) return null;
            // Without attachments the admitted display text is exactly the trimmed content;
            // attachment lines carry ingest-time hashes that cannot be reconstructed here.
            return new DiscordInboundAttribution(
                message.Author.Id.ToString(),
                message.Author.GlobalName ?? message.Author.Username,
                message.Author.Username,
                AttachmentCount: message.Attachments.Count,
                ContentLength: message.Attachments.Count == 0
                    ? message.Content.Trim().Length
                    : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read Discord message {MessageId}", messageId);
            return null;
        }
    }

    public async Task<string?> GetConversationIconUrlAsync(
        DiscordBinding binding, CancellationToken ct = default)
    {
        var current = client;
        if (current is null) return null;
        if (binding.Kind == "dm")
        {
            var userId = binding.CounterpartyUserId;
            if (string.IsNullOrWhiteSpace(userId)
                && ulong.TryParse(binding.ConversationId, out var channelId)
                && current.GetChannel(channelId) is IDMChannel dm)
                userId = dm.Recipient.Id.ToString();
            return string.IsNullOrWhiteSpace(userId)
                ? null
                : (await GetUserProfileAsync(userId, ct))?.AvatarUrl;
        }

        return ulong.TryParse(binding.GuildId, out var guildId)
            ? current.GetGuild(guildId)?.IconUrl
            : null;
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        await lifecycle.WaitAsync(stoppingToken);
        DiscordSocketClient? startingClient = null;
        var commonsMembershipEvents = false;
        var retryWithoutMemberIntents = false;
        try
        {
            if (client is not null) return;
            var config = await GetConfigAsync(stoppingToken);
            if (!config.Ready)
            {
                state = config.Enabled ? "not-configured" : "disabled";
                lastError = config.Enabled
                    ? DiscordBotService.DescribeConfigurationIssue(config)
                    : null;
                return;
            }

            state = "connecting";
            lastError = null;
            // Leaf Community membership reconciliation reacts to leaves, kicks and bans at once.
            commonsMembershipEvents = !commonsMemberIntentsDisallowed
                && await HasReadyCommonsAsync(stoppingToken);
            var next = new DiscordSocketClient(CreateSocketConfig(
                config.PresenceContextMode, commonsMembershipEvents));
            next.Log += OnLogAsync;
            Func<Task> connectedHandler = () => OnConnectedAsync(next);
            Func<Exception, Task> disconnectedHandler =
                exception => OnDisconnectedAsync(next, exception);
            clientConnectedHandler = connectedHandler;
            clientDisconnectedHandler = disconnectedHandler;
            next.Connected += connectedHandler;
            next.Disconnected += disconnectedHandler;
            var ready = new TaskCompletionSource<SocketSelfUser>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task CaptureInitialReadyAsync()
            {
                if (next.CurrentUser is { } current)
                    ready.TrySetResult(current);
                else
                    ready.TrySetException(new InvalidOperationException(
                        "Discord Gateway became ready without an authenticated bot user."));
                return Task.CompletedTask;
            }
            Task CaptureInitialDisconnectAsync(Exception exception)
            {
                ready.TrySetException(new InvalidOperationException(
                    "Discord Gateway disconnected before becoming ready.", exception));
                return Task.CompletedTask;
            }
            next.Ready += CaptureInitialReadyAsync;
            next.Disconnected += CaptureInitialDisconnectAsync;
            startingClient = next;
            client = next;
            SocketSelfUser readyUser;
            try
            {
                await next.LoginAsync(TokenType.Bot, config.BotToken);
                await next.StartAsync();
                try
                {
                    readyUser = await ready.Task.WaitAsync(InitialReadyTimeout, stoppingToken);
                }
                catch (TimeoutException ex)
                {
                    throw new InvalidOperationException(
                        "Discord Gateway did not become ready within 30 seconds.", ex);
                }
            }
            finally
            {
                next.Ready -= CaptureInitialReadyAsync;
                next.Disconnected -= CaptureInitialDisconnectAsync;
            }
            ValidateApplicationId(config.ApplicationId, readyUser.Id);
            if (next.ConnectionState != ConnectionState.Connected)
                throw new InvalidOperationException("Discord Gateway disconnected during startup.");

            Func<Task> readyHandler = () => OnReadyAsync(next);
            clientReadyHandler = readyHandler;
            next.Ready += readyHandler;
            next.MessageReceived += OnMessageAsync;
            next.MessageUpdated += OnMessageUpdatedAsync;
            next.ReactionAdded += OnReactionAddedAsync;
            next.SlashCommandExecuted += OnSlashCommandAsync;
            next.UserLeft += OnGuildMemberLeftAsync;
            next.UserBanned += OnGuildMemberBannedAsync;
            Func<SocketUser, SocketPresence, SocketPresence, Task> presenceHandler =
                (_, _, _) => PublishOwnerPresenceAsync(next);
            Func<SocketUser, SocketVoiceState, SocketVoiceState, Task> voiceHandler =
                (_, _, _) => PublishOwnerPresenceAsync(next);
            clientPresenceHandler = presenceHandler;
            clientVoiceHandler = voiceHandler;
            next.PresenceUpdated += presenceHandler;
            next.UserVoiceStateUpdated += voiceHandler;
            conversations.Start();
            maintenanceApplicationId = config.ApplicationId;
            conversationReconciledHandler = binding =>
            {
                if (string.Equals(binding.ApplicationId, maintenanceApplicationId,
                        StringComparison.Ordinal))
                {
                    settledPeerTurns[binding.BindingId] = 0;
                    maintenance.RequestDrain(binding.BindingId);
                }
            };
            conversations.Reconciled += conversationReconciledHandler;
            // Sentinel-blocked work retries at its due time even when the maintenance loop is
            // too busy to reach its idle recovery sweep.
            conversationRetryHandler = (binding, dueAt) =>
            {
                if (!string.Equals(binding.ApplicationId, maintenanceApplicationId,
                        StringComparison.Ordinal))
                    return;
                var token = runCts?.Token ?? CancellationToken.None;
                var delay = dueAt - DateTimeOffset.UtcNow;
                _ = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, token)
                    .ContinueWith(task =>
                    {
                        if (!task.IsCanceled) maintenance.RequestFull(binding.BindingId);
                    }, TaskScheduler.Default);
            };
            conversations.RetryDue += conversationRetryHandler;
            runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            outboxLoop = RunOutboxLoopAsync(runCts.Token);
            gatewayInboxLoop = Task.Run(
                () => RunGatewayInboxLoopAsync(next, runCts.Token),
                CancellationToken.None);
            presenceLoop = RunPresenceLoopAsync(next, runCts.Token);
            commonsRoleLoop = RunCommonsRoleLoopAsync(next, runCts.Token);
            commonsWebhookLoop = RunCommonsWebhookLoopAsync(next, runCts.Token);
            commonsMembership = new DiscordCommonsMembershipReconciler(
                new DiscordCommonsMembershipGateway(
                    configId, commonsConfigurations, commons, rest, next,
                    () => ReferenceEquals(client, next),
                    async ct => (await GetConfigAsync(ct)).BotToken
                        ?? throw new InvalidOperationException("Discord bot token is unavailable."),
                    logger),
                logger);
            var membership = commonsMembership;
            var membershipToken = runCts.Token;
            commonsMembershipLoop = Task.Run(
                () => membership.RunAsync(membershipToken), CancellationToken.None);
            await OnReadyAsync(next);
        }
        catch (Exception ex) when (commonsMembershipEvents && DisallowsIntents(ex))
        {
            // Server Members Intent is off in the Developer Portal. Connect without the member
            // events; the periodic reconcile still runs and logs its own failures.
            logger.LogWarning(
                "Discord refused the Leaf Community member intents for bot configuration {ConfigId}; "
                + "reconnecting without leave and ban events", configId);
            commonsMemberIntentsDisallowed = true;
            retryWithoutMemberIntents = true;
            DetachConversationReconciledHandler();
            if (startingClient is not null) await DisposeClientAsync(startingClient);
            client = null;
            state = "stopped";
        }
        catch (Exception ex)
        {
            state = "error";
            lastError = ex.Message;
            logger.LogError(ex, "Discord bot failed to start");
            DetachConversationReconciledHandler();
            if (startingClient is not null) await DisposeClientAsync(startingClient);
            client = null;
        }
        finally
        {
            lifecycle.Release();
        }
        if (retryWithoutMemberIntents) await StartAsync(stoppingToken);
    }

    private async Task<bool> HasReadyCommonsAsync(CancellationToken ct)
    {
        try
        {
            return (await commonsConfigurations.GetAsync(configId, ct))?.Ready == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Leaf Community configuration could not be read for bot configuration {ConfigId}: {ErrorType}",
                configId, ex.GetType().Name);
            return false;
        }
    }

    internal static bool DisallowsIntents(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is WebSocketClosedException { CloseCode: 4014 })
                return true;
        return false;
    }

    // Leaves, kicks and bans only queue work; the membership loop calls Discord and the relay.
    private Task OnGuildMemberLeftAsync(SocketGuild guild, SocketUser user)
    {
        commonsMembership?.NotifyDeparture(guild.Id.ToString(), user.Id.ToString());
        return Task.CompletedTask;
    }

    private Task OnGuildMemberBannedAsync(SocketUser user, SocketGuild guild)
    {
        commonsMembership?.NotifyDeparture(guild.Id.ToString(), user.Id.ToString());
        return Task.CompletedTask;
    }

    internal static void ValidateApplicationId(string configuredApplicationId, ulong botUserId)
    {
        if (!string.Equals(configuredApplicationId, botUserId.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The configured Discord application ID does not match the bot token.");
    }

    public async Task<DiscordDirectMessageResult> SendDirectMessageAsync(
        string recipientUserId,
        string content,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (!ulong.TryParse(recipientUserId, out var recipientId) || recipientId == 0)
            throw new ArgumentException("Recipient Discord user ID must be a snowflake.");
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Discord DM content is required.");
        if (content.Length > 32_000)
            throw new ArgumentException("Discord DM content exceeds the 32,000 character safety limit.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");

        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var config = await GetConfigAsync(ct);
        if (!config.DmAllowlist.Contains(recipientUserId))
            throw new UnauthorizedAccessException(
                "Recipient is not in the configured Discord DM allowlist.");

        IUser? recipient = activeClient.GetUser(recipientId);
        recipient ??= await activeClient.Rest.GetUserAsync(recipientId);
        if (recipient is null)
            throw new InvalidOperationException("Discord user could not be resolved.");
        var dm = await recipient.CreateDMChannelAsync();
        var ownerId = config.OwnerUserId
            ?? await agents.ResolveOwnerUserIdAsync(config.AgentId, ct)
            ?? throw new InvalidOperationException(
                $"Discord could not resolve the owner of Agent '{config.AgentId}'.");
        var binding = await bindings.GetOrCreateAsync(new CreateDiscordBinding(
            config.ApplicationId,
            dm.Id.ToString(),
            "dm",
            null,
            null,
            config.AgentId,
            ownerId,
            config.AudienceProfile,
            "direct",
            recipient.GlobalName ?? recipient.Username,
            null,
            recipientUserId,
            config.SessionQualityMode), ct);
        var sent = await conversations.SendProactiveAsync(
            binding, idempotencyKey.Trim(), content.Trim(), ct);
        await DrainBindingAsync(sent.Binding, ct);
        return new DiscordDirectMessageResult(sent.Binding, sent.MessageUid);
    }

    public async Task<DiscordOwnerDirectMessageResult> OpenOwnerDirectMessageAsync(
        string ownerUserId,
        DiscordOwnerDirectMessageRequest request,
        CancellationToken ct = default)
    {
        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var config = await GetConfigAsync(ct);
        if (!config.Enabled)
            throw new InvalidOperationException("Discord bot is disabled.");
        return await ownerDirectMessages.OpenAsync(config, ownerUserId, request, ct);
    }

    public async Task<DiscordDirectMessageResult> SendExistingBindingMessageAsync(
        DiscordBinding binding,
        string content,
        string idempotencyKey,
        CancellationToken ct = default)
        => await SendExistingBindingMessageAsync(
            binding, content, idempotencyKey, null, [], ct);

    public async Task<DiscordDirectMessageResult> SendExistingBindingMessageAsync(
        DiscordBinding binding,
        string content,
        string idempotencyKey,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset> outputAssets,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Discord message content is required.");
        if (content.Length > 32_000)
            throw new ArgumentException("Discord message content exceeds the 32,000 character safety limit.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
        if (deliveryContent is not null && deliveryContent.Length > 32_000)
            throw new ArgumentException(
                "Discord delivery content exceeds the 32,000 character safety limit.");
        if (outputAssets.Count > 10)
            throw new ArgumentException("A Discord message can contain at most 10 output Assets.");
        if (outputAssets.Count > 0 && deliveryContent is null)
            throw new ArgumentException(
                "Discord output Assets require explicit delivery content.");

        var activeClient = client;
        if (activeClient is null || activeClient.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Discord bridge is not connected.");
        var config = await GetConfigAsync(ct);
        ValidateExistingBindingTarget(binding, config.DmAllowlist);

        var sent = await conversations.SendExistingProactiveAsync(
            binding, idempotencyKey.Trim(), content.Trim(), deliveryContent,
            outputAssets, ct);
        await DrainBindingAsync(sent.Binding, ct);
        return new DiscordDirectMessageResult(sent.Binding, sent.MessageUid);
    }

    internal static void ValidateExistingBindingTarget(
        DiscordBinding binding,
        IReadOnlySet<string> dmAllowlist)
    {
        if (binding.Kind != "dm") return;
        if (string.IsNullOrWhiteSpace(binding.CounterpartyUserId)
            || !dmAllowlist.Contains(binding.CounterpartyUserId))
            throw new UnauthorizedAccessException(
                "The Discord DM counterpart is not in the configured allowlist.");
    }

    public async Task RestartAsync(CancellationToken stoppingToken)
    {
        await StopAsync();
        commonsMemberIntentsDisallowed = false;
        await StartAsync(stoppingToken);
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            DetachConversationReconciledHandler();
            runCts?.Cancel();
            // A loop that already faulted must not keep the runtime from stopping, or the
            // fault would block every restart that could recover it.
            await ObserveBackgroundLoopAsync(outboxLoop, "outbox");
            await ObserveBackgroundLoopAsync(gatewayInboxLoop, "gateway inbox");
            await ObserveBackgroundLoopAsync(presenceLoop, "presence");
            await ObserveBackgroundLoopAsync(commonsRoleLoop, "Public Agent role reconciliation");
            await ObserveBackgroundLoopAsync(commonsWebhookLoop, "Leaf Community lazy webhooks");
            await ObserveBackgroundLoopAsync(commonsMembershipLoop, "Leaf Community membership");
            if (client is not null)
            {
                await DisposeClientAsync(client);
            }
            client = null;
            runCts?.Dispose();
            runCts = null;
            outboxLoop = null;
            gatewayInboxLoop = null;
            presenceLoop = null;
            commonsRoleLoop = null;
            commonsWebhookLoop = null;
            commonsMembershipLoop = null;
            commonsMembership?.Dispose();
            commonsMembership = null;
            connectedAt = null;
            state = "stopped";
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task ObserveBackgroundLoopAsync(Task? loop, string name)
    {
        if (loop is null) return;
        try
        {
            await loop;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Discord {Loop} loop had faulted before stop for bot configuration {ConfigId}",
                name, configId);
        }
    }

    private Task OnConnectedAsync(DiscordSocketClient source)
    {
        if (!ReferenceEquals(client, source)) return Task.CompletedTask;
        state = "connected";
        connectedAt = DateTimeOffset.UtcNow;
        lastError = null;
        return Task.CompletedTask;
    }

    private async Task OnReadyAsync(DiscordSocketClient source)
    {
        if (!ReferenceEquals(client, source)) return;
        await OnConnectedAsync(source);
        await SynchronizeConfiguredAvatarsAsync(source);
        await RegisterCommandsAsync();
        await BackfillBindingsAsync();
        await PublishOwnerPresenceAsync(source, forceRefresh: true);
        commonsMembership?.RequestFullReconcile();
    }

    internal static DiscordSocketConfig CreateSocketConfig(
        string presenceContextMode, bool commonsMembershipEvents = false)
    {
        var mode = DiscordPresenceContextModes.Normalize(presenceContextMode);
        var intents = GatewayIntents.Guilds
            | GatewayIntents.GuildMessages
            | GatewayIntents.GuildMessageReactions
            | GatewayIntents.DirectMessages
            | GatewayIntents.MessageContent;
        if (mode is DiscordPresenceContextModes.Active or DiscordPresenceContextModes.Roster)
            intents |= GatewayIntents.GuildPresences | GatewayIntents.GuildVoiceStates;
        if (mode == DiscordPresenceContextModes.Roster)
            intents |= GatewayIntents.GuildMembers;
        // Leaves and kicks need Server Members Intent; bans need only the moderation intent.
        if (commonsMembershipEvents)
            intents |= GatewayIntents.GuildMembers | GatewayIntents.GuildBans;
        return new DiscordSocketConfig
        {
            GatewayIntents = intents,
            AlwaysDownloadUsers = mode == DiscordPresenceContextModes.Roster,
            MessageCacheSize = 0,
            LogGatewayIntentWarnings = true,
        };
    }

    private async Task SynchronizeConfiguredAvatarsAsync(DiscordSocketClient source)
    {
        try
        {
            var config = await GetConfigAsync();
            if (config.AvatarManaged)
            {
                var payload = await ReadAvatarAsync(config.AvatarAssetId);
                var fingerprint = payload?.Fingerprint ?? "none";
                if (!string.Equals(
                        fingerprint, config.AvatarFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await rest.ModifyCurrentUserAvatarAsync(
                        config.BotToken!, payload?.DataUri);
                    await configStore.MarkAvatarSynchronizedAsync(
                        config.EntityId, fingerprint);
                }
            }

            foreach (var (guildId, assetId) in config.GuildAvatarAssetIds)
            {
                if (!source.Guilds.Any(guild => guild.Id.ToString() == guildId))
                    continue;
                var payload = await ReadAvatarAsync(assetId)
                    ?? throw new InvalidOperationException(
                        $"Guild avatar Asset '{assetId}' could not be resolved.");
                if (config.GuildAvatarFingerprints.TryGetValue(guildId, out var fingerprint)
                    && string.Equals(
                        fingerprint, payload.Fingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                await rest.ModifyCurrentGuildMemberAvatarAsync(
                    config.BotToken!, guildId, payload.DataUri);
                await configStore.MarkGuildAvatarSynchronizedAsync(
                    config.EntityId, guildId, payload.Fingerprint);
            }
            profileWarning = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            profileWarning = $"Configured avatar synchronization failed: {ex.Message}";
            logger.LogWarning(ex,
                "Discord configured avatar synchronization failed for bot {BotConfigurationId}",
                configId);
        }
    }

    public async Task<LeafDiscordConfig> SetBotAvatarAsync(
        string? avatarAssetId, CancellationToken ct = default)
    {
        var payload = await ReadAvatarAsync(avatarAssetId, ct);
        var saved = await configStore.SetAvatarAsync(
            configId, payload?.AssetId, ct);
        await rest.ModifyCurrentUserAvatarAsync(
            saved.BotToken!, payload?.DataUri, ct);
        await configStore.MarkAvatarSynchronizedAsync(
            configId, payload?.Fingerprint ?? "none", ct);
        profileWarning = null;
        return await GetConfigAsync(ct);
    }

    public async Task<LeafDiscordConfig> SetGuildAvatarAsync(
        string guildId, string? avatarAssetId, CancellationToken ct = default)
    {
        var current = client
            ?? throw new InvalidOperationException("Discord bot is not connected.");
        if (!ulong.TryParse(guildId, out var parsedGuildId) || parsedGuildId == 0)
            throw new ArgumentException("Discord guild ID must be a snowflake.");
        if (current.GetGuild(parsedGuildId) is null)
            throw new KeyNotFoundException("Discord guild was not found for this bot.");
        var config = await GetConfigAsync(ct);
        var payload = await ReadAvatarAsync(avatarAssetId, ct);
        await rest.ModifyCurrentGuildMemberAvatarAsync(
            config.BotToken!, guildId, payload?.DataUri, ct);
        var saved = await configStore.SetGuildAvatarAsync(
            configId, guildId, payload?.AssetId, ct);
        if (payload is not null)
        {
            await configStore.MarkGuildAvatarSynchronizedAsync(
                configId, guildId, payload.Fingerprint, ct);
            saved = await GetConfigAsync(ct);
        }
        profileWarning = null;
        return saved;
    }

    public async Task<DiscordBinding> SetChannelAvatarAsync(
        DiscordBinding binding, string? avatarAssetId,
        CancellationToken ct = default)
    {
        if (binding.Kind != "guild_channel")
            throw new ArgumentException(
                "Channel avatars are supported only for guild-channel bindings.");
        _ = ResolveGuildChannel(binding.GuildId ?? "", binding.ConversationId);
        var config = await GetConfigAsync(ct);
        if (string.IsNullOrWhiteSpace(avatarAssetId))
        {
            if (!string.IsNullOrWhiteSpace(binding.WebhookId))
                await rest.DeleteWebhookAsync(config.BotToken!, binding.WebhookId, ct);
            return await bindings.ClearChannelAvatarAsync(binding, ct);
        }

        var payload = await ReadAvatarAsync(avatarAssetId, ct)
            ?? throw new ArgumentException("A channel avatar Asset is required.");
        var name = client?.CurrentUser?.Username
            ?? throw new InvalidOperationException("Discord bot is not connected.");
        if (!string.IsNullOrWhiteSpace(binding.WebhookId)
            && !string.IsNullOrWhiteSpace(binding.WebhookToken))
        {
            await rest.ModifyWebhookAsync(
                config.BotToken!, binding.WebhookId, name, payload.DataUri, ct);
            return await bindings.SetChannelAvatarAsync(
                binding, payload.AssetId,
                binding.WebhookId, binding.WebhookToken, ct);
        }

        var webhook = await rest.CreateWebhookAsync(
            config.BotToken!, binding.ConversationId, name, payload.DataUri, ct);
        return await bindings.SetChannelAvatarAsync(
            binding, payload.AssetId, webhook.Id, webhook.Token, ct);
    }

    private async Task<DiscordAvatarPayload?> ReadAvatarAsync(
        string? avatarAssetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(avatarAssetId)) return null;
        var assetId = CanonicalAssetId(avatarAssetId)
            ?? throw new ArgumentException(
                "Avatar must identify a canonical RedLeaf Asset.");
        var file = await assets.ReadAsync(assetId, ct);
        if (file is null)
            throw new ArgumentException("Avatar Asset was not found.");
        var contentType = file.ContentType.Trim().ToLowerInvariant();
        if (contentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
            throw new ArgumentException(
                "Avatar Asset must be a PNG, JPEG, GIF, or WebP image.");
        if (file.Bytes.Length == 0 || file.Bytes.Length > 10 * 1024 * 1024)
            throw new ArgumentException(
                "Avatar Asset must contain between 1 byte and 10 MiB.");
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(file.Bytes)).ToLowerInvariant();
        return new DiscordAvatarPayload(
            assetId,
            $"data:{contentType};base64,{Convert.ToBase64String(file.Bytes)}",
            fingerprint);
    }

    internal static string? CanonicalAssetId(string? avatar)
    {
        if (string.IsNullOrWhiteSpace(avatar)) return null;
        var value = avatar.Trim();
        const string prefix = "/api/assets/";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = Uri.UnescapeDataString(value[prefix.Length..]);
        else if (value.Contains("://", StringComparison.Ordinal)
                 || value.Contains('/') || value.Contains('\\'))
            return null;
        return string.IsNullOrWhiteSpace(value)
            || value.Contains("..", StringComparison.Ordinal)
            ? null
            : value;
    }

    private Task OnDisconnectedAsync(
        DiscordSocketClient source, Exception exception)
    {
        if (!ReferenceEquals(client, source)) return Task.CompletedTask;
        state = "reconnecting";
        lastError = DescribeDisconnect(exception);
        logger.LogWarning(exception, "Discord Gateway disconnected");
        return Task.CompletedTask;
    }

    internal static DiscordTransportStatus ReconcileTransportStatus(
        string cachedState,
        string? cachedError,
        ConnectionState? observedState)
    {
        if (observedState == ConnectionState.Connected)
            return new DiscordTransportStatus("connected", null);

        if (cachedState == "connected"
            && observedState is ConnectionState.Connecting
                or ConnectionState.Disconnecting
                or ConnectionState.Disconnected)
        {
            return new DiscordTransportStatus(
                "reconnecting",
                cachedError ?? "Discord Gateway is disconnected.");
        }

        return new DiscordTransportStatus(cachedState, cachedError);
    }

    internal static string DescribeDisconnect(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not WebSocketClosedException closed) continue;
            var reason = string.IsNullOrWhiteSpace(closed.Reason)
                ? null
                : closed.Reason.ReplaceLineEndings(" ").Trim();
            if (reason?.Length > 160) reason = reason[..160] + "...";
            return reason is null
                ? $"Discord Gateway closed with code {closed.CloseCode}."
                : $"Discord Gateway closed with code {closed.CloseCode}: {reason}";
        }

        return string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
    }

    private Task OnLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical or LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        logger.Log(level, message.Exception, "Discord: {Message}", message.Message);
        return Task.CompletedTask;
    }

    private Task OnMessageAsync(SocketMessage raw)
    {
        return raw is IUserMessage message
            ? EnqueueGatewayMessageAsync(message)
            : Task.CompletedTask;
    }

    private Task OnMessageUpdatedAsync(
        Cacheable<IMessage, ulong> _, SocketMessage after, ISocketMessageChannel __)
    {
        if (after is not IUserMessage message) return Task.CompletedTask;
        var location = CommonsLocation(message.Channel);
        if (location is null || !string.Equals(
                DiscordCommonsRelayService.ChannelName(location.Value.Channel),
                "announcements", StringComparison.Ordinal))
            return Task.CompletedTask;
        var editedAt = message.EditedTimestamp ?? DateTimeOffset.UtcNow;
        return EnqueueGatewayMessageAsync(
            message,
            "announcement-update",
            $"announcement:{message.Id}:update:{editedAt.ToUnixTimeMilliseconds()}");
    }

    private async Task EnqueueGatewayMessageAsync(
        IUserMessage message, string kind = "message", string? eventId = null)
    {
        var started = Stopwatch.GetTimestamp();
        var guildId = (message.Channel as SocketGuildChannel)?.Guild.Id.ToString();
        await gatewayInbox.EnqueueAsync(
            configId,
            message.Id.ToString(),
            message.Channel.Id.ToString(),
            guildId,
            kind,
            eventId);
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed >= TimeSpan.FromSeconds(1))
            logger.LogWarning(
                "Discord gateway inbox enqueue for message {MessageId} took {ElapsedMs} ms",
                message.Id, elapsed.TotalMilliseconds);
    }

    private async Task ProcessMessageAsync(IUserMessage message)
    {
        var commonsLocation = CommonsLocation(message.Channel);
        var commonsScope = commonsLocation is null
            ? null
            : await commons.GetScopeAsync(configId, commonsLocation.Value.GuildId);
        var inCommonsScope = commonsLocation is not null
            && commonsScope is not null
            && commonsScope.Contains(
                commonsLocation.Value.ChannelId, commonsLocation.Value.CategoryId);
        var announcements = commonsLocation is not null && string.Equals(
            DiscordCommonsRelayService.ChannelName(commonsLocation.Value.Channel),
            "announcements", StringComparison.Ordinal);
        var inCommonsTransport = inCommonsScope
            && CanPostInCommonsLocation(message.Channel);
        if (inCommonsScope && (inCommonsTransport || announcements))
        {
            await ForwardCommonsMessageAsync(message, commonsLocation!.Value);
            if (announcements) return;
            if (commonsScope!.SuppressesNativeAgent) return;
            if (message.Source == MessageSource.Webhook)
            {
                await TryAdmitCommonsEnvoyMessageAsync(
                    message, commonsLocation.Value, commonsScope);
                return;
            }
        }
        if (message.Author.IsBot || message.Source == MessageSource.Webhook) return;
        var current = client?.CurrentUser;
        if (current is null) return;
        var config = await GetConfigAsync();
        var conversationId = message.Channel.Id.ToString();
        var binding = await FindOrCreateBindingAsync(message, config);
        if (binding is null || !binding.Enabled) return;
        if (binding.Kind == "dm" && !config.DmAllowlist.Contains(message.Author.Id.ToString()))
        {
            await bindings.MarkReceivedAsync(binding, message.Id.ToString());
            return;
        }
        var behavior = await behaviors.ResolveAsync(config, binding);
        if (behavior.ResponseMode == DiscordResponseModes.ContextOnly)
        {
            await bindings.MarkReceivedAsync(binding, message.Id.ToString());
            return;
        }
        var mentioned = MentionsBot(message, current.Id);
        var repliesToBot = message.ReferencedMessage?.Author.Id == current.Id;
        if (!ShouldAdmitMessage(
                binding.Kind, behavior.ResponseMode, mentioned, repliesToBot))
        {
            await bindings.MarkReceivedAsync(binding, message.Id.ToString());
            return;
        }

        var startTypingImmediately = ShouldStartTypingImmediately(
            binding.Kind, behavior.ResponseMode, mentioned, repliesToBot);
        var typingAdmission = typing.Begin(
            binding.BindingId,
            () => message.Channel.EnterTypingState(),
            startTypingImmediately);
        if (!startTypingImmediately)
            typing.ActivateAfter(typingAdmission, SelectiveTypingDelay);
        try
        {
            var attachmentStarted = Stopwatch.GetTimestamp();
            var submittedAttachments = await attachmentIngestor.IngestAsync(
                config, message.Id.ToString(), message.Attachments.Select(attachment =>
                    new DiscordAttachmentSource(
                        attachment.Id.ToString(), attachment.Filename, attachment.ContentType,
                        attachment.Size, attachment.Url)).ToArray());
            LogSlowMessageStep(message.Id, "attachment ingest", attachmentStarted);
            var attachmentLines = submittedAttachments.Select(attachment =>
                $"[Discord attachment: {attachment.Name}, {attachment.MediaType}, {attachment.Size} bytes, sha256 {attachment.Sha256}, quarantined asset {attachment.ScopeToken}]");
            var content = string.Join('\n', new[] { message.Content }.Concat(attachmentLines)).Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                typing.Cancel(typingAdmission);
                return;
            }
            var guildContext = BuildGuildContext(message, config);
            var submitStarted = Stopwatch.GetTimestamp();
            var admission = await conversations.SubmitAsync(binding, message.Id.ToString(),
                new ExternalRequestor(message.Author.Id.ToString(),
                    message.Author.GlobalName ?? message.Author.Username, message.Author.Username),
                content, submittedAttachments, guildContext: guildContext,
                behaviorContext: behavior.ToTrustedMetadata(),
                channelGap: ChannelGapSource(message, config));
            LogSlowMessageStep(message.Id, "Agent session admission", submitStarted);
            if (admission is null)
                typing.Cancel(typingAdmission);
            else if (admission.Disposition == ExternalConversationDispositions.RefusedSentinelUnavailable)
            {
                // Kept for a later retry; the static notice (if any) goes out now.
                typing.Cancel(typingAdmission);
                maintenance.RequestDrain(binding.BindingId);
            }
            else
            {
                await commonsHostTurns.CompletePendingAsync(
                    binding, "human-joined");
                typing.MarkAdmitted(typingAdmission, admission.MessageUid);
                maintenance.RequestFull(binding.BindingId);
                // The Discord session answers on its own; this only tells any private
                // discussion that messaged this binding that someone replied.
                await replyNotifier.NotifyAsync(
                    binding, message.Id.ToString(),
                    new ExternalRequestor(message.Author.Id.ToString(),
                        message.Author.GlobalName ?? message.Author.Username,
                        message.Author.Username),
                    message.Content ?? "", submittedAttachments.Count, admission.MessageUid,
                    mentioned, repliesToBot, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            typing.Cancel(typingAdmission);
            maintenance.RequestFull(binding.BindingId);
            logger.LogError(ex, "Discord message {MessageId} failed", message.Id);
            try
            {
                await conversations.EnqueueNoticeAsync(binding, message.Id.ToString(),
                    "I could not admit that message. The failure has been recorded in Leaf.");
                maintenance.RequestDrain(binding.BindingId);
            }
            catch (Exception noticeError)
            {
                logger.LogError(noticeError,
                    "Discord failure notice could not be queued for message {MessageId}", message.Id);
            }
        }
    }

    private static bool CanPostInCommonsLocation(IMessageChannel channel)
    {
        if (channel is not SocketGuildChannel guildChannel) return false;
        var permissions = guildChannel.Guild.CurrentUser.GetPermissions(guildChannel);
        return permissions.ViewChannel && channel switch
        {
            SocketThreadChannel => permissions.SendMessagesInThreads,
            SocketTextChannel => permissions.SendMessages,
            SocketForumChannel => permissions.SendMessages,
            _ => false,
        };
    }

    internal static bool ShouldForwardAnnouncement(
        MessageSource source, bool authorIsBot, ulong authorId, ulong? nativeBotId)
        => source != MessageSource.Webhook
           && (!authorIsBot || nativeBotId is not null && authorId == nativeBotId.Value);

    private async Task TryAdmitCommonsEnvoyMessageAsync(
        IUserMessage message,
        (string GuildId, string ChannelId, string? CategoryId, string Channel, string? ThreadRef) location,
        DiscordCommonsGuildScope scope)
    {
        if (scope.SuppressesNativeAgent) return;
        var verified = await commons.GetHostMessageAsync(
            configId, location.GuildId, message.Id.ToString());
        if (verified is null || string.IsNullOrWhiteSpace(verified.ThreadRef)) return;
        var config = await GetConfigAsync();
        var binding = await FindOrCreateBindingAsync(message, config);
        if (binding is null) return;
        if (!binding.Enabled)
        {
            await ApplyVerifiedPeerBindingPolicyAsync(
                bindings, binding, binding.ResponseMode, message.Id.ToString());
            return;
        }
        var behavior = await behaviors.ResolveAsync(config, binding);
        if (!await ApplyVerifiedPeerBindingPolicyAsync(
                bindings, binding, behavior.ResponseMode, message.Id.ToString()))
            return;
        var participantId = $"host:{config.ApplicationId}";
        var relayContext = new JsonObject
        {
            ["schema"] = "leaf-community-peer/v1",
            ["source"] = "commons_relay",
            ["trust"] = "relay_verified",
            ["verified"] = true,
            ["sender_kind"] = "peer-agent",
            ["envoy_id"] = verified.EnvoyId,
            ["persona_name"] = verified.PersonaName,
            ["owner_public_name"] = verified.OwnerPublicName,
            ["thread_ref"] = verified.ThreadRef,
            ["scenario"] = verified.Scenario,
            ["audience"] = "public-discord",
            ["authority"] = "peer-agent-requestor-only",
        };
        var hostTurn = await commonsHostTurns.SetPendingAsync(
            binding, location.ChannelId, location.Channel,
            verified, participantId);
        var admission = await conversations.SubmitAsync(
            binding,
            message.Id.ToString(),
            new ExternalRequestor($"envoy:{verified.EnvoyId}", verified.PersonaName),
            verified.Content,
            [],
            guildContext: BuildGuildContext(message, config),
            behaviorContext: behavior.ToTrustedMetadata(),
            relayContext: relayContext,
            channelGap: ChannelGapSource(message, config));
        if (admission is null
            || admission.Disposition == ExternalConversationDispositions.RefusedSentinelUnavailable)
        {
            await commonsHostTurns.CompleteAsync(hostTurn, "not-admitted");
            if (admission is not null) maintenance.RequestDrain(binding.BindingId);
            return;
        }
        typing.MarkAdmitted(
            typing.Begin(binding.BindingId, () => message.Channel.EnterTypingState(), true),
            admission.MessageUid);
        maintenance.RequestFull(binding.BindingId);
    }

    /// <summary>
    /// Reads the channel messages before <paramref name="message"/> that the binding's session
    /// has not seen, bounded by <see cref="DiscordChannelGaps"/>. Gap attachments go through
    /// the same quarantine ingestor and size limit as the current message's attachments.
    /// </summary>
    private DiscordChannelGapSource? ChannelGapSource(IUserMessage message, LeafDiscordConfig config)
    {
        if (message.Channel is not IGuildChannel) return null;
        var channel = message.Channel;
        return async (binding, ct) =>
        {
            var started = Stopwatch.GetTimestamp();
            var gap = await DiscordChannelGaps.ReadAsync(
                binding,
                async (limit, cursor, token) =>
                {
                    var read = new List<(ulong, DiscordGapMessage?)>();
                    await foreach (var page in channel.GetMessagesAsync(
                                       message.Id, Direction.Before, limit,
                                       CacheMode.AllowDownload,
                                       new RequestOptions { CancelToken = token }))
                    {
                        read.AddRange(page.Select(item => (item.Id, ToGapMessage(item))));
                        if (cursor is { } delivered && page.Any(item => item.Id <= delivered))
                            break;
                    }
                    return read;
                },
                deliveries,
                async (gapMessage, attachment, token) => (await attachmentIngestor.IngestAsync(
                    config, gapMessage.Id, [attachment], token)).Single(),
                config.MaxAttachmentBytes,
                ct);
            LogSlowMessageStep(message.Id, "channel gap", started);
            return gap;
        };
    }

    internal static DiscordGapMessage? ToGapMessage(IMessage message)
    {
        // A thread started from a channel message shows it as a reference to that message.
        if (message.Type == MessageType.ThreadStarterMessage)
            return message is IUserMessage { ReferencedMessage: { } starter }
                ? ToGapMessage(starter)
                : null;
        if (message.Type is not (MessageType.Default or MessageType.Reply)) return null;
        if (string.IsNullOrWhiteSpace(message.Content) && message.Attachments.Count == 0)
            return null;
        return new DiscordGapMessage(
            message.Id.ToString(),
            message.Author.Id.ToString(),
            message.Author.GlobalName ?? message.Author.Username,
            message.Author.IsBot,
            message.Source == MessageSource.Webhook,
            message.Timestamp,
            message.Content ?? "",
            message.Attachments.Select(attachment => new DiscordAttachmentSource(
                attachment.Id.ToString(), attachment.Filename, attachment.ContentType,
                attachment.Size, attachment.Url)).ToArray());
    }

    private void LogSlowMessageStep(ulong messageId, string step, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed >= TimeSpan.FromMilliseconds(500))
            logger.LogInformation(
                "Discord background processing step {Step} for message {MessageId} took {ElapsedMs} ms",
                step, messageId, elapsed.TotalMilliseconds);
    }

    private async Task RunGatewayInboxLoopAsync(
        DiscordSocketClient source, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pending = await gatewayInbox.PendingAsync(configId, ct);
            var due = DiscordGatewayInboxStore.DueChannelHeads(
                pending, DateTimeOffset.UtcNow);
            if (due.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            foreach (var item in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ProcessGatewayInboxItemAsync(source, item, ct);
                    await gatewayInbox.MarkCompletedAsync(item, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound)
                {
                    // A message deleted before the worker refetched it cannot ever be
                    // admitted. Retrying it would permanently block later messages in
                    // this binding, so settle the durable coordinate as gone.
                    await gatewayInbox.MarkCompletedAsync(item, ct);
                    logger.LogInformation(
                        "Discord gateway inbox message {MessageId} disappeared before processing",
                        item.MessageId);
                }
                catch (Exception ex)
                {
                    var safeError = ex.GetType().Name;
                    await gatewayInbox.MarkRetryAsync(
                        item,
                        safeError,
                        DiscordGatewayInboxStore.RetryDelay(item.Attempts),
                        ct);
                    logger.LogWarning(ex,
                        "Discord gateway inbox message {MessageId} will be retried",
                        item.MessageId);
                }
            }
        }
    }

    private async Task ProcessGatewayInboxItemAsync(
        DiscordSocketClient source,
        DiscordGatewayInboxItem item,
        CancellationToken ct)
    {
        if (!ulong.TryParse(item.ChannelId, out var channelId)
            || !ulong.TryParse(item.MessageId, out var messageId))
            return;
        var options = new RequestOptions { CancelToken = ct };
        var channel = source.GetChannel(channelId) as IMessageChannel
            ?? await source.GetChannelAsync(channelId, options) as IMessageChannel;
        if (channel is null) return;
        var message = await channel.GetMessageAsync(
            messageId, CacheMode.AllowDownload, options) as IUserMessage;
        if (message is null) return;

        var started = Stopwatch.GetTimestamp();
        if (item.Kind == "announcement-update")
            await ProcessAnnouncementUpdateAsync(message, item.EventId);
        else
            await ProcessMessageAsync(message);
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed >= TimeSpan.FromSeconds(3))
            logger.LogInformation(
                "Discord background processing for message {MessageId} completed in {ElapsedMs} ms",
                item.MessageId, elapsed.TotalMilliseconds);
    }

    private async Task ProcessAnnouncementUpdateAsync(
        IUserMessage message, string eventId)
    {
        var location = CommonsLocation(message.Channel);
        if (location is null || !string.Equals(
                DiscordCommonsRelayService.ChannelName(location.Value.Channel),
                "announcements", StringComparison.Ordinal))
            return;
        var scope = await commons.GetScopeAsync(configId, location.Value.GuildId);
        if (scope is null || !scope.Contains(
                location.Value.ChannelId, location.Value.CategoryId))
            return;
        await ForwardCommonsMessageAsync(
            message, location.Value, "update", eventId);
    }

    private async Task ForwardCommonsMessageAsync(
        IUserMessage message,
        (string GuildId, string ChannelId, string? CategoryId, string Channel, string? ThreadRef) location,
        string announcementOperation = "create",
        string? eventId = null)
    {
        if (string.Equals(
                DiscordCommonsRelayService.ChannelName(location.Channel),
                "announcements", StringComparison.Ordinal))
        {
            if (!ShouldForwardAnnouncement(
                    message.Source, message.Author.IsBot, message.Author.Id,
                    client?.CurrentUser.Id))
                return;
            var nativeHost = message.Author.IsBot
                && message.Author.Id == client?.CurrentUser.Id;
            var id = eventId ?? $"announcement:{message.Id}:create";
            await commons.EnqueueAsync(configId, location.GuildId, id,
                DiscordCommonsRelayService.BuildAnnouncementPayload(
                    id,
                    message.Id.ToString(),
                    announcementOperation,
                    nativeHost ? "native_host" : "human",
                    message.Author.Id.ToString(),
                    message.Author is SocketGuildUser announcementMember
                        ? announcementMember.DisplayName
                        : message.Author.GlobalName ?? message.Author.Username,
                    message.Content,
                    location.ChannelId,
                    location.Channel,
                    (message.EditedTimestamp ?? message.Timestamp).ToUnixTimeMilliseconds()));
            return;
        }
        if (message.Source == MessageSource.Webhook)
        {
            await commons.EnqueueAsync(configId, location.GuildId, message.Id.ToString(), new JsonObject
            {
                ["type"] = "webhook_echo",
                ["eventId"] = message.Id.ToString(),
                ["messageId"] = message.Id.ToString(),
                ["channelId"] = location.ChannelId,
                ["channel"] = location.Channel,
                ["threadRef"] = location.ThreadRef,
                ["timestamp"] = message.Timestamp.ToUnixTimeMilliseconds(),
            });
            return;
        }
        if (message.Author.IsBot) return;
        var mentions = message.MentionedUserIds
            .Select(value => value.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var mentionRoles = message.MentionedRoleIds
            .Select(value => value.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await commons.EnqueueAsync(configId, location.GuildId, message.Id.ToString(),
            DiscordCommonsRelayService.BuildHumanMessagePayload(
                message.Id.ToString(),
                message.Author.Id.ToString(),
                message.Author is SocketGuildUser member
                    ? member.DisplayName
                    : message.Author.GlobalName ?? message.Author.Username,
                message.Content,
                mentions,
                mentionRoles,
                location.ChannelId,
                location.Channel,
                location.ThreadRef,
                message.Reference?.MessageId.ToString(),
                message.Timestamp.ToUnixTimeMilliseconds(),
                message.Attachments.Select(attachment => new DiscordCommonsRelayService.DiscordCommonsAttachment(
                    attachment.Id.ToString(), attachment.Filename, attachment.ContentType, attachment.Size,
                    attachment.Width, attachment.Height, attachment.Url, attachment.Description)).ToArray()));
    }

    private async Task OnReactionAddedAsync(
        Cacheable<IUserMessage, ulong> _,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction)
    {
        try
        {
            if (!string.Equals(reaction.Emote.Name, "🛑", StringComparison.Ordinal)) return;
            if (client?.CurrentUser.Id == reaction.UserId) return;
            var resolved = client?.GetChannel(channel.Id);
            var location = resolved is null ? null : CommonsLocation(resolved);
            if (location?.ThreadRef is null) return;
            if (!await commons.IsCommonsLocationAsync(
                    configId, location.Value.GuildId, location.Value.ChannelId,
                    location.Value.CategoryId)) return;
            if ((resolved as SocketGuildChannel)?.Guild.GetUser(reaction.UserId)?.IsBot == true)
                return;
            var eventId = $"reaction:{reaction.MessageId}:{reaction.UserId}:stop";
            await commons.EnqueueAsync(configId, location.Value.GuildId, eventId, new JsonObject
            {
                ["type"] = "stop_reaction",
                ["eventId"] = eventId,
                ["actorId"] = reaction.UserId.ToString(),
                ["threadRef"] = location.Value.ThreadRef,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Commons stop reaction forwarding failed");
        }
    }

    private static (string GuildId, string ChannelId, string? CategoryId, string Channel, string? ThreadRef)? CommonsLocation(
        IChannel channel)
        => channel switch
        {
            SocketThreadChannel thread => (
                thread.Guild.Id.ToString(),
                thread.ParentChannel.Id.ToString(),
                (thread.ParentChannel as INestedChannel)?.CategoryId?.ToString(),
                DiscordCommonsRelayService.ChannelName(thread.ParentChannel.Name),
                thread.Id.ToString()),
            SocketGuildChannel guildChannel => (
                guildChannel.Guild.Id.ToString(),
                guildChannel.Id.ToString(),
                (guildChannel as INestedChannel)?.CategoryId?.ToString(),
                DiscordCommonsRelayService.ChannelName(guildChannel.Name),
                null),
            _ => null,
        };

    public async Task AddReactionAsync(
        DiscordBinding binding, string messageId, string emoji, CancellationToken ct = default)
    {
        if (!ulong.TryParse(messageId, out var parsedMessageId))
            throw new ArgumentException("Discord message id must be a snowflake", nameof(messageId));
        // Validate before checking transport availability so malformed public requests are a
        // deterministic 400 and never become Discord's opaque 10014 "Unknown Emoji" response.
        var reaction = DiscordReactionEmoji.Parse(emoji);
        var config = await GetConfigAsync(ct);
        if (client is null || string.IsNullOrWhiteSpace(config.BotToken))
            throw new InvalidOperationException("Discord is not connected");
        if (!ulong.TryParse(binding.ConversationId, out var channelId))
            throw new InvalidOperationException("Discord binding channel is invalid");
        try
        {
            // Use the connected Discord.Net client's reaction operation. Its IEmote-aware route
            // owns Discord's Unicode/custom identifier encoding and shares the gateway client's
            // authenticated REST and rate-limit state.
            await client.Rest.AddReactionAsync(channelId, parsedMessageId, reaction,
                new RequestOptions { CancelToken = ct });
        }
        catch (HttpException ex)
        {
            var discordCode = ex.DiscordCode is null ? "" : $" (Discord code {ex.DiscordCode})";
            throw new DiscordRestException(ex.HttpCode,
                $"Discord REST {(int)ex.HttpCode}: {ex.Reason}{discordCode}");
        }
    }

    public async Task<string> PinExistingBindingMessageAsync(
        DiscordBinding binding, string messageUid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageUid) || messageUid.Length > 256)
            throw new ArgumentException("A bounded canonical message UID is required", nameof(messageUid));
        var config = await GetConfigAsync(ct);
        if (client is null || string.IsNullOrWhiteSpace(config.BotToken))
            throw new InvalidOperationException("Discord is not connected");
        ValidateExistingBindingTarget(binding, config.DmAllowlist);
        var delivery = await deliveries.FindSentOutboundAsync(
            binding.BindingId, binding.Generation, messageUid.Trim(), ct)
            ?? throw new KeyNotFoundException(
                "No sent Discord delivery exists for this binding and canonical message UID.");
        await rest.PinMessageAsync(
            config.BotToken, binding.ConversationId, delivery.DiscordId!, ct);
        return delivery.DiscordId!;
    }

    public async Task<DiscordMessageMutationResult> DeleteExistingBindingMessageAsync(
        DiscordBinding binding, string messageUid, CancellationToken ct = default)
    {
        ValidateMessageUid(messageUid);
        messageUid = messageUid.Trim();
        var config = await GetConfigAsync(ct);
        ValidateExistingBindingTarget(binding, config.DmAllowlist);

        var gate = outboxGates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var owned = await deliveries.FindOwnedOutboundAsync(
                binding.BindingId, binding.Generation, messageUid, ct);
            if (owned.Count == 0)
                throw new KeyNotFoundException(
                    "No bot-authored Discord delivery exists for this binding, generation, and canonical message UID.");
            if (owned.All(item => item.State == "deleted"))
                return new DiscordMessageMutationResult(
                    messageUid, owned.Select(item => item.DiscordId!).ToArray(), 0);
            if (client is null || client.ConnectionState != ConnectionState.Connected
                               || string.IsNullOrWhiteSpace(config.BotToken))
                throw new InvalidOperationException("Discord is not connected");

            var changed = 0;
            foreach (var delivery in owned)
            {
                if (delivery.State == "deleted") continue;
                try
                {
                    await DeleteDiscordMessageAsync(binding, config, delivery.DiscordId!, ct);
                }
                catch (DiscordRestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Discord already converged to the requested state. Persist that
                    // fact so every later retry is a local no-op.
                }
                await deliveries.MarkDeletedAsync(delivery, ct);
                changed++;
            }
            return new DiscordMessageMutationResult(
                messageUid, owned.Select(item => item.DiscordId!).ToArray(), changed);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordMessageMutationResult> EditExistingBindingMessageAsync(
        DiscordBinding binding,
        string messageUid,
        string? content,
        string? deliveryContent,
        IReadOnlyList<ExternalConversationOutputAsset>? outputAssets,
        CancellationToken ct = default)
    {
        ValidateMessageUid(messageUid);
        if (content is null && deliveryContent is null && outputAssets is null)
            throw new ArgumentException(
                "A Discord correction must replace text, attachments, or both.");
        if (content is { Length: > 32_000 })
            throw new ArgumentException(
                "Discord correction content exceeds the 32,000 character safety limit.");
        if (deliveryContent is { Length: > 32_000 })
            throw new ArgumentException(
                "Discord correction delivery content exceeds the 32,000 character safety limit.");
        if (outputAssets is { Count: > 10 })
            throw new ArgumentException(
                "A Discord message can contain at most 10 output Assets.");
        if (outputAssets is { Count: > 0 } && content is not null && deliveryContent is null)
            throw new ArgumentException(
                "Discord output Assets accompanying canonical content require explicit delivery content.");

        var prepared = conversations.InspectCorrection(
            content, deliveryContent, outputAssets);
        var replacesContent = content is not null || deliveryContent is not null;
        if (replacesContent || prepared.OutputAssets is { Count: > 0 })
            await conversations.ReviewOutgoingOrThrowAsync(
                await conversations.OutgoingContextAsync(binding, ct),
                prepared.DeliveryContent ?? "", prepared.OutputAssets ?? [], prepared.HiddenCharacters,
                $"correction:{binding.BindingId}:{messageUid}:{DiscordBindingStore.Hash(prepared.DeliveryContent ?? "")}",
                ct);
        var chunks = replacesContent
            ? DiscordMessageChunker.Split(prepared.DeliveryContent ?? "")
            : [];
        var replacementCount = replacesContent
            ? Math.Max(1, chunks.Count)
            : 0;
        if (prepared.OutputAssets is not null)
            replacementCount = Math.Max(1, replacementCount);

        messageUid = messageUid.Trim();
        var config = await GetConfigAsync(ct);
        if (client is null || client.ConnectionState != ConnectionState.Connected
                           || string.IsNullOrWhiteSpace(config.BotToken))
            throw new InvalidOperationException("Discord is not connected");
        ValidateExistingBindingTarget(binding, config.DmAllowlist);

        var gate = outboxGates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var owned = await deliveries.FindOwnedOutboundAsync(
                binding.BindingId, binding.Generation, messageUid, ct);
            if (owned.Count == 0)
                throw new KeyNotFoundException(
                    "No bot-authored Discord delivery exists for this binding, generation, and canonical message UID.");
            var active = owned.Where(item => item.State != "deleted").ToArray();
            if (active.Length == 0)
                throw new InvalidOperationException("The bot-authored Discord message was deleted.");
            if (replacesContent && replacementCount > active.Length)
                throw new ArgumentException(
                    "The corrected text needs more Discord messages than the original delivery.");

            ResolvedOutboundPayload? resolvedAssets = null;
            if (prepared.OutputAssets is not null)
            {
                var payload = new JsonObject
                {
                    ["content"] = replacesContent && chunks.Count > 0 ? chunks[0] : "",
                    ["assets"] = new JsonArray(prepared.OutputAssets.Select(asset =>
                        (JsonNode?)new JsonObject
                        {
                            ["asset_id"] = asset.AssetId,
                            ["description"] = asset.Description,
                        }).ToArray()),
                };
                resolvedAssets = await ResolveOutboundPayloadAsync(
                    active[0] with { Payload = payload }, config, assets, ct);
            }

            var keptCount = replacesContent ? replacementCount : active.Length;
            var changed = 0;
            for (var index = 0; index < keptCount; index++)
            {
                string? correctedContent = replacesContent
                    ? index < chunks.Count ? chunks[index] : ""
                    : null;
                IReadOnlyList<DiscordOutboundFile>? correctedFiles = null;
                if (resolvedAssets is not null)
                {
                    correctedFiles = index == 0 ? resolvedAssets.Files : [];
                    if (index == 0
                        && (!string.IsNullOrEmpty(resolvedAssets.Content) || replacesContent))
                        correctedContent = resolvedAssets.Content;
                }
                await EditDiscordMessageAsync(
                    binding, config, active[index].DiscordId!, correctedContent,
                    correctedFiles, ct);
                await deliveries.MarkEditedAsync(active[index], ct);
                changed++;
            }

            if (replacesContent)
            {
                for (var index = keptCount; index < active.Length; index++)
                {
                    try
                    {
                        await DeleteDiscordMessageAsync(
                            binding, config, active[index].DiscordId!, ct);
                    }
                    catch (DiscordRestException ex) when (
                        ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                    }
                    await deliveries.MarkDeletedAsync(active[index], ct);
                    changed++;
                }
            }

            return new DiscordMessageMutationResult(
                messageUid, owned.Select(item => item.DiscordId!).ToArray(), changed);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EditDiscordMessageAsync(
        DiscordBinding binding,
        LeafDiscordConfig config,
        string discordMessageId,
        string? content,
        IReadOnlyList<DiscordOutboundFile>? files,
        CancellationToken ct)
    {
        if (string.Equals(binding.DeliveryMode, "webhook", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(binding.WebhookId)
            && !string.IsNullOrWhiteSpace(binding.WebhookToken))
        {
            await rest.EditWebhookMessageAsync(
                binding.WebhookId, binding.WebhookToken, discordMessageId,
                content, files, ct);
            return;
        }
        await rest.EditMessageAsync(
            config.BotToken!, binding.ConversationId, discordMessageId,
            content, files, ct);
    }

    private async Task DeleteDiscordMessageAsync(
        DiscordBinding binding,
        LeafDiscordConfig config,
        string discordMessageId,
        CancellationToken ct)
    {
        if (string.Equals(binding.DeliveryMode, "webhook", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(binding.WebhookId)
            && !string.IsNullOrWhiteSpace(binding.WebhookToken))
        {
            await rest.DeleteWebhookMessageAsync(
                binding.WebhookId, binding.WebhookToken, discordMessageId, ct);
            return;
        }
        await rest.DeleteMessageAsync(
            config.BotToken!, binding.ConversationId, discordMessageId, ct);
    }

    private static void ValidateMessageUid(string messageUid)
    {
        if (string.IsNullOrWhiteSpace(messageUid) || messageUid.Length > 256)
            throw new ArgumentException(
                "A bounded canonical message UID is required", nameof(messageUid));
    }

    private async Task<DiscordBinding?> FindOrCreateBindingAsync(
        IUserMessage message, LeafDiscordConfig config)
    {
        var existing = await bindings.FindAsync(
            config.ApplicationId, message.Channel.Id.ToString());
        if (existing is not null)
        {
            var dmBackfill = BuildExistingDmBackfill(
                existing,
                message.Author.Id.ToString(),
                message.Author.GlobalName ?? message.Author.Username,
                config.DmAllowlist.Contains(message.Author.Id.ToString()));
            return dmBackfill is null
                ? existing
                : await bindings.GetOrCreateAsync(dmBackfill);
        }

        string kind;
        string? guildId;
        string? parentConversationId;
        string conversationName;
        string? guildName;
        string? counterpartyUserId;
        string responseMode;
        switch (message.Channel)
        {
            case IDMChannel:
                if (!config.DmAllowlist.Contains(message.Author.Id.ToString()))
                    return null;
                kind = "dm";
                guildId = null;
                parentConversationId = null;
                conversationName = message.Author.GlobalName ?? message.Author.Username;
                guildName = null;
                counterpartyUserId = message.Author.Id.ToString();
                responseMode = "direct";
                break;
            case SocketThreadChannel thread:
                kind = "thread";
                guildId = thread.Guild.Id.ToString();
                parentConversationId = thread.ParentChannel.Id.ToString();
                conversationName = thread.Name;
                guildName = thread.Guild.Name;
                counterpartyUserId = null;
                responseMode = "participate";
                break;
            case SocketGuildChannel channel:
                kind = "guild_channel";
                guildId = channel.Guild.Id.ToString();
                parentConversationId = null;
                conversationName = channel.Name;
                guildName = channel.Guild.Name;
                counterpartyUserId = null;
                responseMode = "participate";
                break;
            default:
                return null;
        }

        var ownerId = config.OwnerUserId
            ?? await agents.ResolveOwnerUserIdAsync(config.AgentId)
            ?? throw new InvalidOperationException(
                $"Discord could not resolve the owner of Agent '{config.AgentId}'.");
        var behavior = await behaviors.ResolveAsync(
            config, kind, guildId, message.Channel.Id.ToString(), parentConversationId,
            responseMode, CancellationToken.None);
        return await bindings.GetOrCreateAsync(new CreateDiscordBinding(
            config.ApplicationId,
            message.Channel.Id.ToString(),
            kind,
            guildId,
            parentConversationId,
            config.AgentId,
            ownerId,
            config.AudienceProfile,
            behavior.ResponseMode,
            conversationName,
            guildName,
            counterpartyUserId,
            config.SessionQualityMode));
    }

    /// <summary>
    /// Discord gives every bot a managed integration role with the bot's own name, and
    /// mention autocomplete offers it beside the bot user. Pinging that role addresses
    /// the bot exactly like a user mention; other roles the bot merely holds do not.
    /// </summary>
    internal static bool MentionsBot(
        IEnumerable<ulong> mentionedUserIds,
        IEnumerable<ulong> mentionedRoleIds,
        ulong botUserId,
        Func<ulong, ulong?> roleBotId)
        => mentionedUserIds.Contains(botUserId)
            || mentionedRoleIds.Any(roleId => roleBotId(roleId) == botUserId);

    private bool MentionsBot(IMessage message, ulong botUserId)
    {
        var guild = message.Channel is IGuildChannel channel
            ? client?.GetGuild(channel.GuildId)
            : null;
        return MentionsBot(message.MentionedUserIds, message.MentionedRoleIds, botUserId,
            roleId => guild?.GetRole(roleId)?.Tags?.BotId);
    }

    internal static bool ShouldAdmitMessage(
        string bindingKind,
        string responseMode,
        bool mentioned,
        bool repliesToBot)
    {
        if (responseMode == DiscordResponseModes.ContextOnly) return false;
        if (bindingKind == "dm") return true;
        return responseMode is DiscordResponseModes.Selective
                or DiscordResponseModes.Participate
            || mentioned
            || repliesToBot;
    }

    internal static async Task<bool> ApplyVerifiedPeerBindingPolicyAsync(
        DiscordBindingStore bindings,
        DiscordBinding binding,
        string responseMode,
        string discordMessageId,
        CancellationToken ct = default)
    {
        if (binding.Enabled && responseMode != DiscordResponseModes.ContextOnly)
            return true;
        await bindings.MarkReceivedAsync(binding, discordMessageId, ct);
        return false;
    }

    internal static bool ShouldStartTypingImmediately(
        string bindingKind,
        string responseMode,
        bool mentioned,
        bool repliesToBot)
        => bindingKind == "dm"
           || responseMode == DiscordResponseModes.Participate
           || mentioned
           || repliesToBot;

    internal static DiscordStartupBackfillPolicy GetStartupBackfillPolicy(string bindingKind)
        => bindingKind == "dm"
            ? new(DmStartupBackfillLimit, DmStartupBackfillMaxAge, false)
            : new(GuildStartupBackfillLimit, GuildStartupBackfillMaxAge, true);

    internal static bool ShouldAdmitStartupBackfillMessage(
        string bindingKind,
        DateTimeOffset messageTimestamp,
        DateTimeOffset observedAt,
        bool mentioned,
        bool repliesToBot)
    {
        var policy = GetStartupBackfillPolicy(bindingKind);
        if (messageTimestamp < observedAt - policy.MaxAge) return false;
        return !policy.RequiresDirectAddress || mentioned || repliesToBot;
    }

    internal static CreateDiscordBinding? BuildExistingDmBackfill(
        DiscordBinding existing,
        string authenticatedAuthorId,
        string authenticatedAuthorName,
        bool authorAllowed)
    {
        if (existing.Kind != "dm"
            || !string.IsNullOrWhiteSpace(existing.CounterpartyUserId)
            || !authorAllowed
            || string.IsNullOrWhiteSpace(authenticatedAuthorId))
            return null;

        return new CreateDiscordBinding(
            existing.ApplicationId,
            existing.ConversationId,
            existing.Kind,
            existing.GuildId,
            existing.ParentConversationId,
            existing.AgentId,
            existing.OwnerId,
            existing.AudienceProfile,
            existing.ResponseMode,
            authenticatedAuthorName,
            existing.GuildName,
            authenticatedAuthorId,
            existing.SessionQualityMode);
    }

    private async Task BackfillBindingsAsync()
    {
        if (client is null) return;
        var config = await GetConfigAsync();
        foreach (var binding in await bindings.ListAsync())
        {
            try
            {
                if (!string.Equals(
                        binding.ApplicationId, config.ApplicationId, StringComparison.Ordinal)
                    || !binding.Enabled
                    || !ulong.TryParse(binding.LastReceivedSnowflake, out var cursor)
                    || !ulong.TryParse(binding.ConversationId, out var channelId))
                    continue;
                var channel = client.GetChannel(channelId) as IMessageChannel
                    ?? await client.GetChannelAsync(channelId) as IMessageChannel;
                if (channel is null) continue;

                var policy = GetStartupBackfillPolicy(binding.Kind);
                var observedAt = DateTimeOffset.UtcNow;
                var batch = (await channel.GetMessagesAsync(
                        policy.MessageLimit, CacheMode.AllowDownload).FlattenAsync())
                    .Where(message => message.Id > cursor)
                    .OrderBy(message => message.Id)
                    .ToArray();
                if (batch.Length == 0) continue;

                var admitted = 0;
                foreach (var message in batch.OfType<IUserMessage>())
                {
                    var mentioned = MentionsBot(message, client.CurrentUser.Id);
                    var repliesToBot = message.ReferencedMessage?.Author.Id == client.CurrentUser.Id;
                    if (!ShouldAdmitStartupBackfillMessage(
                            binding.Kind, message.Timestamp, observedAt, mentioned, repliesToBot))
                        continue;
                    admitted++;
                    await ProcessMessageAsync(message);
                }

                await bindings.MarkReceivedAsync(binding, batch[^1].Id.ToString());
                logger.LogInformation(
                    "Discord startup backfill admitted {AdmittedCount} of {InspectedCount} recent messages for {BindingId}; cursor advanced through {NewestMessageId}",
                    admitted, batch.Length, binding.BindingId, batch[^1].Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discord startup backfill failed for {BindingId}", binding.BindingId);
            }
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        try
        {
            var config = await GetConfigAsync();
            if (await TryHandleCommonsCommandAsync(command)) return;
            var action = explicitActions.SingleOrDefault(handler =>
                handler.CommandName == command.Data.Name && handler.Handles(config.EntityId, config.AgentId));
            if (action is not null)
            {
                await command.DeferAsync(ephemeral: true);
                var verifiedMember = command.GuildId is { } guildId
                    && command.User is SocketGuildUser member && member.Guild.Id == guildId;
                var request = new VerifiedDiscordAction(config.EntityId, config.AgentId,
                    command.Id.ToString(), command.User.Id.ToString(), command.GuildId?.ToString(),
                    verifiedMember, command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value?.ToString() ?? "open");
                var result = await action.ExecuteAsync(request, async (privateContent, ct) => {
                    try {
                        await command.User.SendMessageAsync(privateContent,
                            options: new RequestOptions { CancelToken = ct }, allowedMentions: AllowedMentions.None);
                        return true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
                });
                await command.FollowupAsync(result, ephemeral: true, allowedMentions: AllowedMentions.None);
                return;
            }
            var binding = command.ChannelId is { } channelId
                ? await bindings.FindAsync(config.ApplicationId, channelId.ToString())
                : null;
            switch (command.Data.Name)
            {
                case "clear":
                    if (binding is null)
                    {
                        await command.RespondAsync("This conversation is not bound to an Agent.", ephemeral: true);
                        return;
                    }
                    if (!config.ClearAllowlist.Contains(command.User.Id.ToString()))
                    {
                        await command.RespondAsync("You are not allowed to clear this shared conversation.", ephemeral: true);
                        return;
                    }
                    var advanced = await conversations.ClearAsync(binding, command.Id.ToString());
                    await command.RespondAsync($"Context cleared. This is generation {advanced.Generation}.");
                    return;
                case "status":
                    var status = await GetStatusAsync();
                    await command.RespondAsync(
                        $"Leaf Discord: {status.State}. Binding: {(binding is null ? "not configured" : $"generation {binding.Generation}")}",
                        ephemeral: true);
                    return;
                case "privacy":
                    await command.RespondAsync(
                        "This is a normal persistent Agent session with its workspace and tools so it can troubleshoot properly. Discord content is treated as untrusted and checked by a separate Sentinel. The Agent will not disclose its owner's private information or accept Discord participants as owner delegates.",
                        ephemeral: true);
                    return;
                default:
                    await command.RespondAsync(
                        "Talk to me normally in a bound support channel. Use /clear for a fresh context, /status for health, and /privacy for the boundary.",
                        ephemeral: true);
                    return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord slash command {Command} failed", command.Data.Name);
            if (!command.HasResponded)
                await command.RespondAsync("That command failed safely. Check Leaf for details.", ephemeral: true);
        }
    }

    private async Task<bool> TryHandleCommonsCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not ("ask" or "pause" or "resume")) return false;
        var channel = command.ChannelId is { } channelId
            ? client?.GetChannel(channelId)
            : null;
        var location = channel is null ? null : CommonsLocation(channel);
        if (location is null || !await commons.IsCommonsLocationAsync(
                configId, location.Value.GuildId, location.Value.ChannelId,
                location.Value.CategoryId))
        {
            await command.RespondAsync(
                "That Leaf Community command is available only in a configured community channel.",
                ephemeral: true);
            return true;
        }
        if (location?.ThreadRef is null)
        {
            await command.RespondAsync(
                "Use this command inside a Leaf Community forum thread.", ephemeral: true);
            return true;
        }
        var eventId = command.Id.ToString();
        var actorId = command.User.Id.ToString();
        if (command.Data.Name == "ask")
        {
            var envoy = command.Data.Options.FirstOrDefault(option => option.Name == "envoy")
                ?.Value?.ToString()?.Trim();
            var content = command.Data.Options.FirstOrDefault(option => option.Name == "question")
                ?.Value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(envoy) || string.IsNullOrWhiteSpace(content))
            {
                await command.RespondAsync(
                    "Both an Envoy and a question are required.", ephemeral: true);
                return true;
            }
            await commons.EnqueueAsync(configId, location.Value.GuildId, eventId, new JsonObject
            {
                ["type"] = "ask",
                ["eventId"] = eventId,
                ["authorId"] = actorId,
                ["displayName"] = command.User is SocketGuildUser member
                    ? member.DisplayName
                    : command.User.GlobalName ?? command.User.Username,
                ["envoy"] = envoy,
                ["content"] = content,
                ["channel"] = location.Value.Channel,
                ["threadRef"] = location.Value.ThreadRef,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            await command.RespondAsync("Your question was queued for Leaf Community.", ephemeral: true);
            return true;
        }
        await commons.EnqueueAsync(configId, location.Value.GuildId, eventId, new JsonObject
        {
            ["type"] = command.Data.Name,
            ["eventId"] = eventId,
            ["actorId"] = actorId,
            ["threadRef"] = location.Value.ThreadRef,
        });
        await command.RespondAsync(
            command.Data.Name == "pause"
            ? "This Leaf Community thread was queued to pause."
            : "This Leaf Community thread was queued to resume.",
            ephemeral: true);
        return true;
    }

    private async Task RegisterCommandsAsync()
    {
        if (client is null) return;
        var commands = new[]
        {
            new SlashCommandBuilder().WithName("clear").WithDescription("Start a fresh context generation").Build(),
            new SlashCommandBuilder().WithName("status").WithDescription("Show Leaf Discord connection and binding health").Build(),
            new SlashCommandBuilder().WithName("privacy").WithDescription("Explain the Agent's Discord privacy boundary").Build(),
            new SlashCommandBuilder().WithName("help").WithDescription("Show Leaf Discord commands").Build(),
        };
        var config = await GetConfigAsync();
        var contributed = explicitActions.Where(handler => handler.Handles(config.EntityId, config.AgentId))
            .Select(handler => new SlashCommandBuilder().WithName(handler.CommandName)
                .WithDescription(handler.Description)
                .AddOption(new SlashCommandOptionBuilder().WithName("action")
                    .WithDescription("Open, recover/revoke access, or join/leave this server's private atlas")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(false)
                    .AddChoice("Open my atlas", "open").AddChoice("Recover and rotate access", "recover")
                    .AddChoice("Revoke all access", "revoke").AddChoice("Join this server's atlas", "join")
                    .AddChoice("Leave this server's atlas", "leave")).Build());
        try
        {
            await client.BulkOverwriteGlobalApplicationCommandsAsync(
                commands.Concat(contributed).ToArray());
            var commonsCommands = new ApplicationCommandProperties[]
            {
                new SlashCommandBuilder().WithName("ask")
                    .WithDescription("Ask a specific Agent in this community thread")
                    .AddOption("envoy", ApplicationCommandOptionType.String,
                        "Agent ID or community name", isRequired: true)
                    .AddOption("question", ApplicationCommandOptionType.String,
                        "Question for the Envoy", isRequired: true)
                    .Build(),
                new SlashCommandBuilder().WithName("pause")
                    .WithDescription("Pause Agent replies in this community thread").Build(),
                new SlashCommandBuilder().WithName("resume")
                    .WithDescription("Resume Agent replies in this community thread").Build(),
            };
            var commonsConfig = await commons.GetConfigurationAsync(configId);
            var commonsScopes = commonsConfig?.GuildScopes.Values
                ?? Array.Empty<DiscordCommonsGuildScope>();
            foreach (var scope in commonsScopes)
            {
                if (!ulong.TryParse(scope.GuildId, out var parsedGuildId)) continue;
                var guild = client.GetGuild(parsedGuildId);
                if (guild is not null)
                    await guild.BulkOverwriteApplicationCommandAsync(
                        scope.WholeGuild ? commonsCommands : []);
            }
        }
        catch (HttpException ex)
        {
            logger.LogWarning(ex, "Discord application command registration failed");
        }
    }

    private Task RunOutboxLoopAsync(CancellationToken ct)
        => RunSupervisedMaintenanceAsync(
            recoverySweep => RunMaintenanceSweepAsync(recoverySweep, ct),
            () => maintenance.WaitAsync(MaintenanceRecoveryInterval, ct),
            () => Task.Delay(MaintenanceFailureBackoff, ct),
            ex => logger.LogError(ex,
                "Discord maintenance sweep failed for bot configuration {ConfigId}; retrying",
                configId),
            ct);

    // The outbox loop is the only sender for queued deliveries. An exception escaping it
    // ends the task silently and stops all background delivery for the bot until restart,
    // so every sweep is isolated and a failed sweep is followed by a full recovery sweep,
    // which re-requests the bindings whose pending requests the failure consumed.
    internal static async Task RunSupervisedMaintenanceAsync(
        Func<bool, Task> sweep,
        Func<Task<bool>> waitForRequest,
        Func<Task> backoff,
        Action<Exception> reportFailure,
        CancellationToken ct)
    {
        var recoverySweep = true;
        while (true)
        {
            try
            {
                await sweep(recoverySweep);
                recoverySweep = !await waitForRequest();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                reportFailure(ex);
                await backoff();
                recoverySweep = true;
            }
        }
    }

    private async Task RunMaintenanceSweepAsync(bool recoverySweep, CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);
        if (recoverySweep)
        {
            foreach (var binding in await bindings.ListAsync(ct))
            {
                if (binding.Enabled && string.Equals(
                        binding.ApplicationId, config.ApplicationId,
                        StringComparison.Ordinal))
                    maintenance.RetryFull(binding.BindingId);
            }
        }
        foreach (var request in maintenance.TakePending())
        {
            var binding = await bindings.FindByBindingIdAsync(request.BindingId, ct);
            if (binding is null) continue;
            if (!binding.Enabled || !string.Equals(
                    binding.ApplicationId, config.ApplicationId, StringComparison.Ordinal))
                continue;
            if (ShouldDeferLegacyOwnerMaintenance(config, binding))
                continue;
            var activeBinding = string.IsNullOrWhiteSpace(config.OwnerUserId)
                ? binding
                : await bindings.RepairLegacyOwnerAsync(
                    binding, config.OwnerUserId, ct);

            var retryFull = false;
            if (request.FullReconciliation)
            {
                try
                {
                    await conversations.RetryPendingAsync(activeBinding, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    retryFull = true;
                    logger.LogError(ex, "Discord {Phase} failed for {BindingId}",
                        "inbound retry", activeBinding.BindingId);
                }

                try
                {
                    if (!await conversations.ReconcileAsync(activeBinding, ct))
                        retryFull = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    retryFull = true;
                    logger.LogError(ex, "Discord {Phase} failed for {BindingId}",
                        "transcript reconciliation", activeBinding.BindingId);
                }

                if (retryFull)
                    maintenance.RetryFull(activeBinding.BindingId);
            }

            try
            {
                await DrainBindingAsync(activeBinding, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                maintenance.RetryDrain(activeBinding.BindingId);
                logger.LogError(ex, "Discord {Phase} failed for {BindingId}",
                    "outbound drain", activeBinding.BindingId);
            }
        }
    }

    private void DetachConversationReconciledHandler()
    {
        if (conversationReconciledHandler is not null)
            conversations.Reconciled -= conversationReconciledHandler;
        conversationReconciledHandler = null;
        if (conversationRetryHandler is not null)
            conversations.RetryDue -= conversationRetryHandler;
        conversationRetryHandler = null;
        maintenanceApplicationId = null;
    }

    private async Task RunPresenceLoopAsync(
        DiscordSocketClient source,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(ct))
            await PublishOwnerPresenceAsync(source, forceRefresh: true, ct);
    }

    private async Task RunCommonsRoleLoopAsync(
        DiscordSocketClient source,
        CancellationToken ct)
    {
        await ReconcileCommonsRolesAsync(source, ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(ct))
            await ReconcileCommonsRolesAsync(source, ct);
    }

    private async Task RunCommonsWebhookLoopAsync(
        DiscordSocketClient source,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var lastCatalogSync = DateTimeOffset.MinValue;
        do
        {
            try
            {
                var syncCatalog = DateTimeOffset.UtcNow - lastCatalogSync
                    >= TimeSpan.FromMinutes(2);
                await ReconcileCommonsTransportAsync(source, syncCatalog, ct);
                if (syncCatalog) lastCatalogSync = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Leaf Community channel/webhook reconciliation failed for {ConfigId}",
                    configId);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ReconcileCommonsTransportAsync(
        DiscordSocketClient source,
        bool syncCatalog,
        CancellationToken ct)
    {
        if (!ReferenceEquals(client, source)) return;
        var commonsConfig = await commonsConfigurations.GetAsync(configId, ct);
        if (commonsConfig is not { Ready: true }) return;
        foreach (var scope in commonsConfig.GuildScopes.Values.Where(candidate =>
                     !candidate.SuppressesNativeAgent))
        {
            if (!ulong.TryParse(scope.GuildId, out var guildId)) continue;
            var guild = source.GetGuild(guildId);
            if (guild is null) continue;
            if (syncCatalog)
            {
                var channels = new JsonArray();
                foreach (var channel in guild.Channels)
                {
                    var kind = CommonsCatalogChannelKind(
                        channel is SocketThreadChannel,
                        channel is SocketTextChannel,
                        channel is SocketForumChannel);
                    if (kind is null) continue;
                    var categoryId = (channel as INestedChannel)?.CategoryId?.ToString();
                    var channelId = channel.Id.ToString();
                    var structurallyIncluded = scope.WholeGuild
                        || scope.ChannelIds.Contains(channelId)
                        || (!string.IsNullOrWhiteSpace(categoryId)
                            && scope.CategoryIds.Contains(categoryId));
                    if (!structurallyIncluded) continue;
                    var permissions = guild.CurrentUser.GetPermissions(channel);
                    var canPost = permissions.ViewChannel
                        && permissions.SendMessages
                        && permissions.ManageWebhooks;
                    var name = DiscordCommonsRelayService.ChannelName(channel.Name);
                    var purpose = channel switch
                    {
                        SocketTextChannel text => text.Topic ?? "",
                        SocketForumChannel forum => forum.Topic ?? "",
                        _ => "",
                    };
                    var showcaseEligible = LooksLikeShowcaseChannel(name, purpose);
                    channels.Add(new JsonObject
                    {
                        ["id"] = channelId,
                        ["name"] = name,
                        ["kind"] = kind,
                        ["canPost"] = canPost,
                        ["readOnly"] = !canPost
                            || scope.ExcludedChannelIds.Contains(channelId)
                            || string.Equals(name, "announcements", StringComparison.Ordinal),
                        ["showcaseEligible"] = showcaseEligible,
                    });
                }
                await commons.SyncChannelsAsync(configId, scope.GuildId, channels, ct);
                await commons.SetHostParticipantAsync(
                    configId,
                    scope.GuildId,
                    $"host:{(await GetConfigAsync(ct)).ApplicationId}",
                    guild.CurrentUser.DisplayName,
                    guild.CurrentUser.Id.ToString(),
                    guild.CurrentUser.Roles.Where(role => !role.IsEveryone)
                        .Select(role => role.Id.ToString()).ToArray(),
                    ct);
            }
            foreach (var channelId in await commons.PendingWebhookChannelsAsync(
                         configId, scope.GuildId, ct))
            {
                _ = await EnsureCommonsWebhookAsync(scope.GuildId, channelId, ct);
                var refreshed = await commonsConfigurations.GetAsync(configId, ct);
                if (refreshed?.WebhookUrls.TryGetValue(channelId, out var webhookUrl) == true)
                    await commons.RegisterChannelWebhookAsync(
                        configId, scope.GuildId, channelId, webhookUrl, ct);
            }
        }
    }

    internal static string? CommonsCatalogChannelKind(
        bool isThread, bool isText, bool isForum)
    {
        if (isThread) return null;
        if (isForum) return "forum";
        return isText ? "text" : null;
    }

    internal static bool LooksLikeShowcaseChannel(string name, string topic)
    {
        const string topicMarker = "[leaf-community:showcase]";
        if (topic.Contains(topicMarker, StringComparison.OrdinalIgnoreCase)) return true;

        const string word = "showcase";
        for (var start = 0; start < name.Length;)
        {
            var index = name.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var beforeIsBoundary = index == 0 || !char.IsLetterOrDigit(name[index - 1]);
            var after = index + word.Length;
            var afterIsBoundary = after == name.Length || !char.IsLetterOrDigit(name[after]);
            if (beforeIsBoundary && afterIsBoundary) return true;
            start = index + 1;
        }
        return false;
    }

    private async Task ReconcileCommonsRolesAsync(
        DiscordSocketClient source,
        CancellationToken ct)
    {
        if (!ReferenceEquals(client, source)) return;
        var config = await commonsConfigurations.GetAsync(configId, ct);
        if (config is not { Ready: true }) return;
        var envoyCount = 0;
        try
        {
            var botConfig = await GetConfigAsync(ct);
            var botToken = botConfig.BotToken
                ?? throw new InvalidOperationException("Discord bot token is unavailable.");
            foreach (var guildId in config.GuildScopes.Keys)
            {
                if (!ulong.TryParse(guildId, out var parsedGuildId)) continue;
                var guild = source.GetGuild(parsedGuildId)
                    ?? throw new InvalidOperationException(
                        "A configured public Agent guild is not visible to the Discord bot.");
                var envoys = await commons.GetEnvoysAsync(configId, guildId, ct);
                envoyCount = envoys.Count;
                DiscordCommonsRolePolicy.EnsureCanReconcile(
                    guild.CurrentUser.GuildPermissions.ManageRoles, envoys);

                var members = await rest.ListGuildMembersAsync(botToken, guildId, ct);
                foreach (var envoy in envoys)
                {
                    var owner = members.FirstOrDefault(member =>
                        !member.IsBot && string.Equals(
                            member.UserId, envoy.OwnerDiscordUserId, StringComparison.Ordinal));
                    if (owner is not null && !string.Equals(
                            owner.DisplayName, envoy.OwnerDisplayName, StringComparison.Ordinal))
                        await commons.SyncOwnerDisplayNameAsync(
                            configId, guildId, envoy.Id, owner.DisplayName, ct);
                }
                await commons.PushReservedNamesAsync(configId, guildId, members, ct);
                envoys = await commons.GetEnvoysAsync(configId, guildId, ct);
                DiscordCommonsRolePolicy.EnsureCanReconcile(true, envoys);

                var roles = (await rest.ListGuildRolesAsync(botToken, guildId, ct)).ToList();
                foreach (var envoy in envoys)
                {
                    var recorded = string.IsNullOrWhiteSpace(envoy.DiscordRoleId)
                        ? null
                        : roles.FirstOrDefault(role => role.Id == envoy.DiscordRoleId);
                    if (envoy.Status != "active")
                    {
                        if (recorded is not null && !recorded.Managed)
                            await rest.DeleteGuildRoleAsync(botToken, guildId, recorded.Id, ct);
                        if (envoy.DiscordRoleId is not null)
                            await commons.SetDiscordRoleAsync(
                                configId, guildId, envoy.Id, null, ct);
                        continue;
                    }

                    var desiredName = DiscordCommonsRolePolicy.RoleName(envoy);
                    var role = recorded ?? roles.FirstOrDefault(candidate =>
                        !candidate.Managed && string.Equals(
                            candidate.Name, desiredName, StringComparison.Ordinal));
                    if (role is null)
                    {
                        role = await rest.CreateGuildRoleAsync(
                            botToken, guildId, desiredName, ct);
                        roles.Add(role);
                    }
                    else if (DiscordCommonsRolePolicy.NeedsUpdate(role, desiredName))
                    {
                        role = await rest.ModifyGuildRoleAsync(
                            botToken, guildId, role.Id, desiredName, ct);
                    }
                    if (!string.Equals(envoy.DiscordRoleId, role.Id, StringComparison.Ordinal))
                        await commons.SetDiscordRoleAsync(
                            configId, guildId, envoy.Id, role.Id, ct);
                }
            }
            await commonsConfigurations.SetRoleReconciliationAsync(
                configId,
                new DiscordCommonsRoleReconciliation(
                    "ready", null, DateTimeOffset.UtcNow, envoyCount),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var state = ex is UnauthorizedAccessException
                ? "missing-permission" : "error";
            await commonsConfigurations.SetRoleReconciliationAsync(
                configId,
                new DiscordCommonsRoleReconciliation(
                    state, ex.Message, DateTimeOffset.UtcNow, envoyCount),
                ct);
            logger.LogWarning(
                "Public Agent role reconciliation failed for {ConfigId}: {ErrorType}",
                configId, ex.GetType().Name);
        }
    }

    private async Task PublishOwnerPresenceAsync(
        DiscordSocketClient source,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (!ReferenceEquals(client, source)) return;
        try
        {
            await ownerPresence.PublishAsync(
                await GetConfigAsync(ct), source, forceRefresh, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Discord owner presence projection failed for bot configuration {ConfigId}",
                configId);
        }
    }

    private static JsonObject? BuildGuildContext(
        IUserMessage message, LeafDiscordConfig config)
    {
        if (message.Channel is not SocketGuildChannel channel)
            return null;
        var members = config.PresenceContextMode == DiscordPresenceContextModes.Off
            ? []
            : channel.Guild.Users.Select(user =>
            new DiscordGuildContextMember(
                user.Id.ToString(),
                user.DisplayName,
                user.Status.ToString(),
                user.Activities.Select(activity => new DiscordGuildContextActivity(
                    activity.Type.ToString(),
                    activity is SpotifyGame spotify ? spotify.TrackTitle : activity.Name,
                    activity is SpotifyGame listening
                        ? string.Join(", ", listening.Artists)
                        : activity.Details,
                    activity is SpotifyGame album
                        ? album.AlbumTitle
                        : activity is RichGame rich ? rich.State : null)).ToArray(),
                user.VoiceChannel?.Name,
                user.IsBot)).ToArray();
        var capturedAt = DateTimeOffset.UtcNow;
        return DiscordGuildContextBuilder.Build(
            config.PresenceContextMode,
            channel.Guild.Id.ToString(),
            channel.Guild.Name,
            channel.Id.ToString(),
            channel.Name,
            members,
            channel.Guild.Users.Count >= channel.Guild.MemberCount,
            capturedAt,
            BuildGuildNavigation(
                channel.Guild, channel.Id.ToString(), capturedAt));
    }

    internal static bool ShouldDeferLegacyOwnerMaintenance(
        LeafDiscordConfig config, DiscordBinding binding)
        => string.IsNullOrWhiteSpace(config.OwnerUserId)
            && string.Equals(
                binding.OwnerId, "local-user", StringComparison.OrdinalIgnoreCase);

    internal static async Task RunBindingMaintenanceAsync(
        Func<Task> retryInbound,
        Func<Task> reconcileTranscript,
        Func<Task> drainOutbound,
        Action<string, Exception> reportFailure,
        CancellationToken ct = default)
    {
        await RunPhaseAsync("inbound retry", retryInbound, reportFailure, ct);
        await RunPhaseAsync("transcript reconciliation", reconcileTranscript, reportFailure, ct);
        await RunPhaseAsync("outbound drain", drainOutbound, reportFailure, ct);
    }

    private static async Task RunPhaseAsync(
        string phase,
        Func<Task> operation,
        Action<string, Exception> reportFailure,
        CancellationToken ct)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            reportFailure(phase, ex);
        }
    }

    private async Task DrainBindingAsync(DiscordBinding binding, CancellationToken ct)
    {
        if (await IsCommonsBindingAsync(binding, ct)) return;
        var gate = outboxGates.GetOrAdd(binding.BindingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (client is null || !ulong.TryParse(binding.ConversationId, out _)) return;
            var config = await GetConfigAsync(ct);
            if (string.IsNullOrWhiteSpace(config.BotToken)) return;
            var useWebhook = string.Equals(
                    binding.DeliveryMode, "webhook", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(binding.WebhookId)
                && !string.IsNullOrWhiteSpace(binding.WebhookToken);
            var pending = await deliveries.PendingOutboundAsync(binding.BindingId, ct);
            for (var index = 0; index < pending.Count; index++)
            {
                var delivery = pending[index];
                if (delivery.Generation != binding.Generation) continue;
                if (delivery.State == DiscordDeliveryStore.ReviewingState)
                {
                    // Nothing after a reply under review may overtake it. The review runs off
                    // this loop and requests another drain when the reply leaves review.
                    RequestOutboundReview(binding, ct);
                    break;
                }
                if (delivery.Kind == DiscordDeliveryStore.MessageKind
                    && delivery.OutboundReview is null && delivery.ReviewExempt is null)
                {
                    // Safety net: a model-written message never leaves without a review, even one
                    // queued before the outbound pass existed or by a path that forgot it.
                    if (delivery.MessageUid is not null)
                        await deliveries.MarkUnreviewedForReviewAsync(binding, delivery.MessageUid, ct);
                    RequestOutboundReview(binding, ct);
                    break;
                }
                if (delivery.Kind == DiscordDeliveryStore.ProgressKind
                    && await commonsHostTurns.GetPendingAsync(binding, ct) is not null)
                {
                    await deliveries.MarkFailedAsync(
                        delivery, "relay_peer_turn_progress_suppressed", ct);
                    continue;
                }
                if (delivery.Kind == DiscordDeliveryStore.ProgressKind)
                {
                    var batch = new List<DiscordDelivery> { delivery };
                    while (index + 1 < pending.Count
                           && pending[index + 1].Generation == delivery.Generation
                           && pending[index + 1].Kind == DiscordDeliveryStore.ProgressKind
                           && string.Equals(
                               pending[index + 1].MessageUid,
                               delivery.MessageUid,
                               StringComparison.Ordinal))
                        batch.Add(pending[++index]);

                    var latest = batch[^1];
                    var progressContent = latest.Payload?["content"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(progressContent))
                    {
                        foreach (var item in batch)
                            await deliveries.MarkFailedAsync(
                                item, "Queued progress payload is missing", ct);
                        continue;
                    }
                    try
                    {
                        var receipt = await deliveries.FindProgressReceiptAsync(latest, ct);
                        if (receipt is null)
                        {
                            receipt = useWebhook
                                ? await rest.SendWebhookAsync(
                                    binding.WebhookId!, binding.WebhookToken!,
                                    progressContent, ct)
                                : await rest.SendIdempotentAsync(
                                    config.BotToken,
                                    binding.ConversationId,
                                    progressContent,
                                    DiscordRestTransport.Nonce(latest),
                                    ct);
                            // Discord hides "is typing" as soon as the bot posts a message.
                            // The progress message is not the answer, so show it again now
                            // rather than after the indicator's next periodic trigger.
                            typing.Refresh(binding.BindingId);
                        }
                        else
                        {
                            if (useWebhook)
                                await rest.EditWebhookMessageAsync(
                                    binding.WebhookId!, binding.WebhookToken!,
                                    receipt, progressContent, ct);
                            else
                                await rest.EditMessageAsync(
                                    config.BotToken,
                                    binding.ConversationId,
                                    receipt,
                                    progressContent,
                                    ct);
                        }
                        foreach (var item in batch)
                            await deliveries.MarkSentAsync(item, receipt, ct);
                    }
                    catch (DiscordRestException ex) when ((int)ex.StatusCode == 429
                                                           || (int)ex.StatusCode >= 500)
                    {
                        foreach (var item in batch)
                            await deliveries.MarkRetryAsync(item, ex.Message,
                                ex.RetryAfter ?? TimeSpan.FromSeconds(
                                    Math.Min(60, Math.Pow(2, Math.Min(item.Attempts, 5)))), ct);
                        break;
                    }
                    catch (DiscordRestException ex)
                    {
                        foreach (var item in batch)
                            await deliveries.MarkFailedAsync(item, ex.Message, ct);
                        break;
                    }
                    continue;
                }

                var outbound = await ResolveOutboundPayloadAsync(delivery, config, assets, ct);
                if (string.IsNullOrEmpty(outbound.Content) && outbound.Files.Count == 0)
                {
                    await deliveries.MarkFailedAsync(delivery, "Queued outbound payload is missing", ct);
                    continue;
                }
                var hostTurn = await commonsHostTurns.GetPendingAsync(binding, ct);
                if (hostTurn is { State: "pending" })
                {
                    var authorization = await commons.AuthorizeHostPostAsync(
                        configId,
                        hostTurn.GuildId,
                        hostTurn.ParticipantId,
                        hostTurn.ThreadRef,
                        [hostTurn.SourceEnvoyId],
                        hostTurn.SourceMessageId,
                        ct);
                    if (!authorization.Allowed)
                    {
                        foreach (var blocked in pending.Skip(index).Where(item =>
                                     item.Kind == DiscordDeliveryStore.MessageKind))
                            await deliveries.MarkFailedAsync(
                                blocked,
                                $"relay_{authorization.Reason ?? "governor_rejected"}",
                                ct);
                        await commonsHostTurns.CompleteAsync(hostTurn, "blocked", ct);
                        break;
                    }
                    await commonsHostTurns.CompleteAsync(hostTurn, "authorized", ct);
                    hostTurn = hostTurn with { State = "authorized" };
                }
                try
                {
                    var sent = useWebhook
                        ? await rest.SendWebhookMessageAsync(
                            binding.WebhookId!, binding.WebhookToken!, outbound.Content,
                            outbound.Files, ct)
                        : await rest.SendIdempotentMessageAsync(config.BotToken,
                            binding.ConversationId, outbound.Content,
                            DiscordRestTransport.Nonce(delivery), outbound.Files, ct);
                    var sentId = sent.Id;
                    await deliveries.MarkSentAsync(delivery, sentId, ct);
                    if (hostTurn is not null)
                    {
                        await commons.EnqueueAsync(
                            configId,
                            hostTurn.GuildId,
                            $"host-post:{sentId}",
                            DiscordCommonsRelayService.BuildHostParticipantPostPayload(
                                sentId, hostTurn.ParticipantId, outbound.Content,
                                hostTurn.SourceEnvoyId, hostTurn.Channel, hostTurn.ChannelId,
                                hostTurn.ThreadRef, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                sent.Attachments),
                            ct);
                        await commonsHostTurns.CompleteAsync(hostTurn, "posted", ct);
                    }
                }
                catch (DiscordRestException ex) when ((int)ex.StatusCode == 429
                                                       || (int)ex.StatusCode >= 500)
                {
                    await deliveries.MarkRetryAsync(delivery, ex.Message,
                        ex.RetryAfter ?? TimeSpan.FromSeconds(
                            Math.Min(60, Math.Pow(2, Math.Min(delivery.Attempts, 5)))), ct);
                    break;
                }
                catch (DiscordRestException ex)
                {
                    await deliveries.MarkFailedAsync(delivery, ex.Message, ct);
                    break;
                }
            }
            if (settledPeerTurns.TryRemove(binding.BindingId, out _)
                && await commonsHostTurns.GetPendingAsync(binding, ct) is
                    { State: "pending" } settledTurn
                && !await deliveries.HasPendingOutboundMessageAsync(
                    binding.BindingId, binding.Generation, ct))
                await commonsHostTurns.CompleteAsync(settledTurn, "no-reply", ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Channel names, topics and tags written by a private Nova session are public text:
    /// they get the outbound pass like any message.</summary>
    private async Task ReviewChannelSettingsAsync(
        LeafDiscordConfig config, string guildId, JsonObject settings, CancellationToken ct)
    {
        var text = string.Join("\n", ChannelSettingsText(settings, ""));
        if (text.Length == 0) return;
        await conversations.ReviewOutgoingOrThrowAsync(
            new DiscordOutgoingContext(config.OwnerUserId, config.AgentId,
                $"{config.ApplicationId}:{guildId}",
                "the name, topic and settings of a channel in a Discord server, visible to every member who can see it"),
            text, [], !ReferenceEquals(DiscordEgressGuard.StripHiddenCharacters(text), text),
            $"channel:{config.ApplicationId}:{guildId}:{DiscordBindingStore.Hash(text)}", ct);
    }

    internal static IEnumerable<string> ChannelSettingsText(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var (key, child) in value)
                    foreach (var line in ChannelSettingsText(child, path.Length == 0 ? key : $"{path}.{key}"))
                        yield return line;
                break;
            case JsonArray items:
                for (var i = 0; i < items.Count; i++)
                    foreach (var line in ChannelSettingsText(items[i], $"{path}[{i}]"))
                        yield return line;
                break;
            case JsonValue leaf when leaf.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text):
                yield return $"{path}: {text}";
                break;
        }
    }

    private void RequestOutboundReview(DiscordBinding binding, CancellationToken ct)
    {
        lock (outboundReviewRunners)
        {
            outboundReviewRequests.Add(binding.BindingId);
            if (outboundReviewRunners.ContainsKey(binding.BindingId)) return;
            // A review never borrows the identity of whatever request kicked the drain: it runs
            // as the binding's own background work and states its provenance explicitly.
            outboundReviewRunners[binding.BindingId] = RunDetached(
                () => RunOutboundReviewsAsync(binding, ct));
        }
    }

    /// <summary>
    /// Starts bridge-owned background work without flowing an HTTP request, signed execution
    /// scope, or any other ambient AsyncLocal state from the caller.
    /// </summary>
    internal static Task RunDetached(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed())
            return Task.Run(work, CancellationToken.None);
        using (ExecutionContext.SuppressFlow())
            return Task.Run(work, CancellationToken.None);
    }

    private async Task RunOutboundReviewsAsync(DiscordBinding binding, CancellationToken ct)
    {
        while (true)
        {
            lock (outboundReviewRunners)
            {
                if (!outboundReviewRequests.Remove(binding.BindingId))
                {
                    outboundReviewRunners.Remove(binding.BindingId);
                    return;
                }
            }
            try
            {
                if (await conversations.ReviewPendingOutboundAsync(binding, ct))
                    maintenance.RequestDrain(binding.BindingId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (outboundReviewRunners) outboundReviewRunners.Remove(binding.BindingId);
                return;
            }
            catch (Exception ex)
            {
                // The rows stay in review; the recovery sweep drains the binding and asks again.
                logger.LogError(ex, "Discord outbound review failed for {BindingId}", binding.BindingId);
                maintenance.RetryDrain(binding.BindingId);
                lock (outboundReviewRunners)
                {
                    outboundReviewRequests.Remove(binding.BindingId);
                    outboundReviewRunners.Remove(binding.BindingId);
                }
                return;
            }
        }
    }

    private async Task<bool> IsCommonsBindingAsync(
        DiscordBinding binding, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(binding.GuildId)
            || !ulong.TryParse(binding.ConversationId, out var channelId))
            return false;
        var location = client?.GetChannel(channelId) is { } channel
            ? CommonsLocation(channel)
            : null;
        var scope = location is null
            ? null
            : await commons.GetScopeAsync(configId, location.Value.GuildId, ct);
        return scope is not null
            && scope.Contains(location!.Value.ChannelId, location.Value.CategoryId)
            && scope.SuppressesNativeAgent;
    }

    internal static async Task<ResolvedOutboundPayload> ResolveOutboundPayloadAsync(
        DiscordDelivery delivery, LeafDiscordConfig config, IAssets assetStore,
        CancellationToken ct = default)
    {
        var content = delivery.Payload?["content"]?.GetValue<string>() ?? "";
        var files = new List<DiscordOutboundFile>();
        if (delivery.Payload?["assets"] is not JsonArray assetsPayload)
            return new ResolvedOutboundPayload(content, files);

        var unavailable = new List<string>();
        foreach (var (assetNode, index) in assetsPayload.Select((node, index) => (node, index)))
        {
            var asset = assetNode as JsonObject;
            var assetId = asset?["asset_id"]?.GetValue<string>();
            var description = asset?["description"]?.GetValue<string>();
            description = string.IsNullOrWhiteSpace(description) ? "Shared image" : description;
            if (string.IsNullOrWhiteSpace(assetId))
            {
                unavailable.Add(description);
                continue;
            }

            var file = await assetStore.ReadAsync(assetId, ct);
            if (file is null
                || file.Bytes.LongLength > config.MaxAttachmentBytes
                || !TryImageExtension(file.ContentType, out var extension))
            {
                unavailable.Add(description);
                continue;
            }

            files.Add(new DiscordOutboundFile(
                $"shared-image-{index + 1}{extension}",
                file.ContentType,
                description.Length <= 1024 ? description : description[..1024],
                file.Bytes));
        }

        foreach (var description in unavailable)
            content = AppendUnavailableImage(content, description);
        return new ResolvedOutboundPayload(content, files);
    }

    private static string AppendUnavailableImage(string content, string description)
    {
        var note = $"[Image unavailable: {description}]";
        if (string.IsNullOrEmpty(content)) return note[..Math.Min(note.Length, 2_000)];
        var available = 2_000 - note.Length - 2;
        return available <= 0
            ? note[..Math.Min(note.Length, 2_000)]
            : $"{content[..Math.Min(content.Length, available)]}\n\n{note}";
    }

    private static bool TryImageExtension(string contentType, out string extension)
    {
        extension = contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => "",
        };
        return extension.Length > 0;
    }

    internal sealed record ResolvedOutboundPayload(
        string Content, IReadOnlyList<DiscordOutboundFile> Files);

    private async Task DisposeClientAsync(DiscordSocketClient target)
    {
        var connectedHandler = ReferenceEquals(client, target)
            ? clientConnectedHandler
            : null;
        var readyHandler = ReferenceEquals(client, target)
            ? clientReadyHandler
            : null;
        var disconnectedHandler = ReferenceEquals(client, target)
            ? clientDisconnectedHandler
            : null;
        target.Log -= OnLogAsync;
        if (connectedHandler is not null) target.Connected -= connectedHandler;
        if (readyHandler is not null) target.Ready -= readyHandler;
        if (disconnectedHandler is not null) target.Disconnected -= disconnectedHandler;
        target.MessageReceived -= OnMessageAsync;
        target.MessageUpdated -= OnMessageUpdatedAsync;
        target.ReactionAdded -= OnReactionAddedAsync;
        target.SlashCommandExecuted -= OnSlashCommandAsync;
        target.UserLeft -= OnGuildMemberLeftAsync;
        target.UserBanned -= OnGuildMemberBannedAsync;
        if (clientPresenceHandler is not null)
            target.PresenceUpdated -= clientPresenceHandler;
        if (clientVoiceHandler is not null)
            target.UserVoiceStateUpdated -= clientVoiceHandler;
        if (ReferenceEquals(client, target))
        {
            clientConnectedHandler = null;
            clientReadyHandler = null;
            clientDisconnectedHandler = null;
            clientPresenceHandler = null;
            clientVoiceHandler = null;
        }
        try { await target.StopAsync(); } catch { }
        try { await target.LogoutAsync(); } catch { }
        try { await target.DisposeAsync(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lifecycle.Dispose();
        maintenance.Dispose();
        foreach (var gate in outboxGates.Values) gate.Dispose();
    }

    private async Task<LeafDiscordConfig> GetConfigAsync(CancellationToken ct = default)
        => await configStore.GetAsync(configId, ct)
           ?? throw new InvalidOperationException(
               $"Discord bot configuration '{configId}' no longer exists.");

    private sealed record CachedUserProfile(
        DiscordUserProfile Profile,
        DateTimeOffset ExpiresAt);
}

internal readonly record struct DiscordStartupBackfillPolicy(
    int MessageLimit,
    TimeSpan MaxAge,
    bool RequiresDirectAddress);

internal sealed record DiscordTransportStatus(
    string State,
    string? LastError);
