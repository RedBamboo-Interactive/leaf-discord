using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafDiscord;

public sealed record DiscordDirectMessageRequest(
    string RecipientUserId,
    string Content,
    string IdempotencyKey);

public sealed record DiscordBindingMessageRequest(
    string Content,
    string IdempotencyKey,
    string? DeliveryContent = null,
    IReadOnlyList<DiscordBindingOutputAssetRequest>? OutputAssets = null,
    bool NotifyReplies = true,
    bool? LeadConversation = null,
    int? LeadForMinutes = null,
    bool Announcement = false);

public sealed record DiscordBindingOutputAssetRequest(
    string AssetId,
    string Description);

public sealed record DiscordBindingMessageEditRequest(
    string? Content = null,
    string? DeliveryContent = null,
    IReadOnlyList<DiscordBindingOutputAssetRequest>? OutputAssets = null);

public sealed record DiscordBindingPinRequest(string MessageUid);

public sealed record DiscordReactionRequest(string MessageId, string Emoji);

public sealed record DiscordGuildBindingRequest(string GuildId, string ChannelId);
public sealed record DiscordForumPostRequest(
    string GuildId,
    string ForumId,
    string Title,
    string Content,
    string IdempotencyKey);
public sealed record DiscordAvatarRequest(string AssetId);
public sealed record DiscordBehaviorRequest(
    string Scope,
    string ResponseMode,
    string? Instructions = null,
    string? GuildId = null,
    string? ConversationId = null);

public static class DiscordEndpoints
{
    private const int MaxTranscriptDiscordLookups = 50;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/config", async (
            IAuthContext auth, DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var config = (await configurations.ListAsync(ct)).FirstOrDefault();
            return config is null
                ? Results.NotFound(new
                {
                    error = "discord_bot_not_configured",
                    message = "Add a Discord bot configuration first.",
                })
                : Results.Ok(await ProjectConfigAsync(config, agents, ct));
        });

        group.MapPut("/config", async (
            LeafDiscordConfigUpdate update,
            IAuthContext auth,
            DiscordAgentResolver agents,
            DiscordRuntimeOptionsResolver runtimeOptions,
            DiscordBotConfigurationStore configurations,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            try
            {
                var current = (await configurations.ListAsync(ct)).FirstOrDefault();
                if (current is null)
                    return Results.NotFound(new
                    {
                        error = "discord_bot_not_configured",
                        message = "Add a Discord bot configuration first.",
                    });
                var agentReference = string.IsNullOrWhiteSpace(update.AgentId)
                    ? current.AgentId : update.AgentId.Trim();
                if (await agents.ResolveAsync(agentReference, ct) is null)
                    return Results.BadRequest(new
                    {
                        error = "invalid_agent",
                        message = $"Agent '{agentReference}' was not found",
                    });
                var sessionMode = string.IsNullOrWhiteSpace(update.SessionQualityMode)
                    ? current.SessionQualityMode : update.SessionQualityMode.Trim();
                if (await runtimeOptions.ResolveQualityModeAsync(sessionMode, ct) is null)
                    return Results.BadRequest(new
                    {
                        error = "invalid_quality_mode",
                        message = "The selected session quality mode was not found",
                    });
                return Results.Ok(await configurations.SaveAsync(
                    current.EntityId, update, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_config", message = ex.Message });
            }
        });

        group.MapGet("/status", async (DiscordBotService bot, CancellationToken ct) =>
            Results.Ok(await bot.GetStatusAsync(ct)));

        group.MapGet("/bots", async (
            IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var statuses = (await bot.GetStatusesAsync(ct))
                .ToDictionary(status => status.BotId);
            var configs = await configurations.ListAsync(ct);
            if (!string.IsNullOrWhiteSpace(auth.UserId))
                configs = await Task.WhenAll(configs.Select(config =>
                    configurations.EnsureOwnerAsync(config.EntityId, auth.UserId, ct)));
            var items = await Task.WhenAll(configs
                .Select(async config => new
                {
                    config = await ProjectConfigAsync(config, agents, ct),
                    status = statuses.GetValueOrDefault(config.EntityId),
                }));
            return Results.Ok(new { items });
        });

        group.MapPost("/bots", async (
            LeafDiscordConfigUpdate update,
            IAuthContext auth,
            DiscordAgentResolver agents,
            DiscordRuntimeOptionsResolver runtimeOptions,
            DiscordBotConfigurationStore configurations,
            DiscordBehaviorStore behaviors,
            DiscordBotService bot,
            IHostApplicationLifetime lifetime,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            try
            {
                await ValidateConfigurationAsync(
                    update.AgentId,
                    update.SessionQualityMode,
                    agents,
                    runtimeOptions,
                    ct);
                if (string.IsNullOrWhiteSpace(auth.UserId))
                    return Forbidden("An authenticated owner is required.");
                var created = await configurations.CreateAsync(update, ct, auth.UserId);
                await behaviors.EnsureDefaultsAsync(created, [], ct);
                await bot.RestartAsync(created.EntityId, lifetime.ApplicationStopping);
                return Results.Ok(await ProjectConfigAsync(created, agents, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_config", message = ex.Message });
            }
        });

        group.MapPut("/bots/{id:guid}", async (
            Guid id,
            LeafDiscordConfigUpdate update,
            IAuthContext auth,
            DiscordAgentResolver agents,
            DiscordRuntimeOptionsResolver runtimeOptions,
            DiscordBotConfigurationStore configurations,
            DiscordBotService bot,
            IHostApplicationLifetime lifetime,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var current = await configurations.GetAsync(id, ct);
            if (current is null) return Results.NotFound();
            try
            {
                if (!string.IsNullOrWhiteSpace(auth.UserId))
                    current = await configurations.EnsureOwnerAsync(id, auth.UserId, ct);
                await ValidateConfigurationAsync(
                    string.IsNullOrWhiteSpace(update.AgentId) ? current.AgentId : update.AgentId,
                    string.IsNullOrWhiteSpace(update.SessionQualityMode)
                        ? current.SessionQualityMode : update.SessionQualityMode,
                    agents,
                    runtimeOptions,
                    ct);
                var saved = await configurations.SaveAsync(id, update, ct);
                await bot.RestartAsync(id, lifetime.ApplicationStopping);
                return Results.Ok(await ProjectConfigAsync(saved, agents, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_config", message = ex.Message });
            }
        });

        group.MapPost("/restart", async (
            IAuthContext auth, DiscordBotService bot, IHostApplicationLifetime lifetime,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            await bot.RestartAsync(lifetime.ApplicationStopping);
            return Results.Ok(await bot.GetStatusAsync(ct));
        });

        group.MapPost("/bots/{id:guid}/restart", async (
            Guid id,
            IAuthContext auth,
            DiscordBotService bot,
            IHostApplicationLifetime lifetime,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            try
            {
                await bot.RestartAsync(id, lifetime.ApplicationStopping);
                var status = (await bot.GetStatusesAsync(ct))
                    .First(item => item.BotId == id);
                return Results.Ok(status);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        group.MapPut("/bots/{id:guid}/avatar", async (
            Guid id, DiscordAvatarRequest request, IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents, DiscordBotService bot,
            CancellationToken ct) =>
            await ApplyBotAvatarAsync(
                id, request.AssetId, auth, configurations, agents, bot, ct));

        group.MapDelete("/bots/{id:guid}/avatar", async (
            Guid id, IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents, DiscordBotService bot,
            CancellationToken ct) =>
            await ApplyBotAvatarAsync(
                id, null, auth, configurations, agents, bot, ct));

        group.MapPut("/bots/{id:guid}/guilds/{guildId}/avatar", async (
            Guid id, string guildId, DiscordAvatarRequest request, IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents, DiscordBotService bot,
            CancellationToken ct) =>
            await ApplyGuildAvatarAsync(
                id, guildId, request.AssetId, auth,
                configurations, agents, bot, ct));

        group.MapDelete("/bots/{id:guid}/guilds/{guildId}/avatar", async (
            Guid id, string guildId, IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents, DiscordBotService bot,
            CancellationToken ct) =>
            await ApplyGuildAvatarAsync(
                id, guildId, null, auth,
                configurations, agents, bot, ct));

        group.MapGet("/bots/{id:guid}/commands", async (
            Guid id, IAuthContext auth, DiscordBotConfigurationStore configurations,
            DiscordBotService bot, CancellationToken ct) =>
            await ReadBotCommandsAsync(id, auth, configurations.GetAsync, bot.GetCommandsAsync, ct));

        group.MapGet("/bots/{id:guid}/guild-channels", async (
            Guid id, IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordBindingStore bindings, DiscordBotService bot, CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            if (!CanManageBot(auth, config))
                return Forbidden("You cannot manage this Discord bot.");
            try
            {
                var existing = (await bindings.ListAsync(ct))
                    .Where(binding => string.Equals(
                        binding.ApplicationId, config.ApplicationId, StringComparison.Ordinal))
                    .ToDictionary(binding => binding.ConversationId, StringComparer.Ordinal);
                var channels = (await bot.ListGuildChannelsAsync(id, ct)).Select(channel => new
                {
                    channel.GuildId, channel.GuildName,
                    channel.ChannelId, channel.ChannelName,
                    bound = existing.TryGetValue(channel.ChannelId, out var binding),
                    bindingEntityId = binding?.EntityId,
                    sessionId = binding?.SessionId,
                });
                return Results.Ok(new { items = channels });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "discord_bot_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/bots/{id:guid}/guild-navigation", async (
            Guid id,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            try
            {
                return Results.Ok(new
                {
                    untrusted = true,
                    items = await bot.ListGuildNavigationAsync(id, ct),
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_bot_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/bots/{id:guid}/guilds/{guildId}/permissions", async (
            Guid id,
            string guildId,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            try
            {
                return Results.Ok(await bot.GetGuildPermissionReportAsync(id, guildId, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_discord_guild", message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = "discord_guild_not_found", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "discord_bot_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/bots/{id:guid}/guilds/{guildId}/channels/{channelId}/messages", async (
            Guid id,
            string guildId,
            string channelId,
            int? limit,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordCommonsConfigurationStore commons,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            if (!await bot.IsCommonsChannelAsync(id, guildId, channelId, ct))
                return Forbidden("That Discord channel is outside Leaf Community scope.");
            try
            {
                return Results.Ok(new
                {
                    guildId,
                    channelId,
                    trust = "untrusted_discord_content",
                    messages = await bot.ReadGuildChannelMessagesAsync(
                        id, guildId, channelId, limit ?? 50, ct),
                });
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or KeyNotFoundException
                                       or UnauthorizedAccessException)
            {
                return GuildReadError(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_guild_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/bots/{id:guid}/behaviors", async (
            Guid id,
            IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordBindingStore bindings,
            DiscordBehaviorStore behaviors,
            DiscordBotService bot,
            DiscordAgentResolver agents,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            if (!CanManageBot(auth, config))
                return Forbidden("You cannot manage this Discord bot.");
            var allBindings = (await bindings.ListAsync(ct))
                .Where(binding => string.Equals(
                    binding.ApplicationId, config.ApplicationId, StringComparison.Ordinal))
                .ToArray();
            await behaviors.EnsureDefaultsAsync(config, allBindings, ct);
            IReadOnlyList<JsonObject> navigation = [];
            string? visibilityError = null;
            try
            {
                navigation = await bot.ListGuildNavigationAsync(id, ct);
            }
            catch (InvalidOperationException ex)
            {
                visibilityError = ex.Message;
            }
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            return Results.Ok(new
            {
                bot = new
                {
                    id = config.EntityId,
                    config.ApplicationId,
                    config.AgentId,
                    agentName = agent?.Name ?? config.AgentId,
                    agentAvatarUrl = agent is null
                        ? null : JsonRead.String(agent.Data, "avatar"),
                },
                behaviors = await behaviors.ListAsync(config.EntityId, ct),
                navigation,
                visibilityError,
                dms = allBindings.Where(binding => binding.Kind == "dm").Select(binding => new
                {
                    binding.EntityId,
                    binding.ConversationId,
                    binding.ConversationName,
                    binding.CounterpartyUserId,
                    binding.Enabled,
                }),
            });
        });

        group.MapPut("/bots/{id:guid}/behaviors", async (
            Guid id,
            DiscordBehaviorRequest request,
            IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordBindingStore bindings,
            DiscordBehaviorStore behaviors,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            if (!CanManageBot(auth, config))
                return Forbidden("You cannot manage this Discord bot.");
            try
            {
                await ValidateBehaviorTargetAsync(config, request, bindings, ct);
                return Results.Ok(await behaviors.SaveAsync(config,
                    new DiscordBehaviorUpdate(
                        request.Scope,
                        request.ResponseMode,
                        request.Instructions,
                        request.GuildId,
                        request.ConversationId),
                    ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_discord_behavior",
                    message = ex.Message,
                });
            }
        });

        group.MapDelete("/bots/{id:guid}/behaviors/{scope}", async (
            Guid id,
            string scope,
            string? guildId,
            string? conversationId,
            IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordBindingStore bindings,
            DiscordBehaviorStore behaviors,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            if (!CanManageBot(auth, config))
                return Forbidden("You cannot manage this Discord bot.");
            try
            {
                await ValidateBehaviorTargetAsync(config,
                    new DiscordBehaviorRequest(
                        scope, DiscordResponseModes.Direct,
                        GuildId: guildId, ConversationId: conversationId),
                    bindings, ct);
                return Results.Ok(new
                {
                    disabled = await behaviors.DisableAsync(
                        config, scope, guildId, conversationId, ct),
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_discord_behavior",
                    message = ex.Message,
                });
            }
        });

        group.MapPost("/bots/{id:guid}/forum-posts", async (
            Guid id,
            DiscordForumPostRequest request,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            try
            {
                var result = await bot.CreateForumPostAsync(
                    id,
                    request.GuildId,
                    request.ForumId,
                    request.Title,
                    request.Content,
                    request.IdempotencyKey,
                    caller.BeneficiaryId,
                    ct);
                return Results.Ok(new
                {
                    ok = true,
                    result.GuildId,
                    result.ForumId,
                    result.PostId,
                    result.Title,
                    bindingEntityId = result.Binding.EntityId,
                    result.Binding.BindingId,
                    result.Binding.SessionId,
                    result.MessageUid,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_forum_post",
                    message = ex.Message,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new
                {
                    error = "discord_forum_not_found",
                    message = ex.Message,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new
                {
                    error = "discord_forum_not_allowed",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_forum_post_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/bots/{id:guid}/application-emojis", async (
            Guid id,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
            await ListApplicationEmojisAsync(
                id, http, configurations, agents, bot.ListApplicationEmojisAsync, ct));

        group.MapPost("/bots/{id:guid}/application-emojis", async (
            Guid id,
            DiscordApplicationEmojiCreateRequest request,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            IAssets assets,
            DiscordBotService bot,
            CancellationToken ct) =>
            await CreateApplicationEmojiAsync(
                id, request, http, configurations, agents, assets,
                bot.ListApplicationEmojisAsync, bot.CreateApplicationEmojiAsync, ct));

        group.MapDelete("/bots/{id:guid}/application-emojis/{emojiId}", async (
            Guid id,
            string emojiId,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
            await DeleteApplicationEmojiAsync(
                id, emojiId, http, configurations, agents,
                bot.ListApplicationEmojisAsync, bot.DeleteApplicationEmojiAsync, ct));

        group.MapGet("/bots/{id:guid}/commons", async (
            Guid id,
            IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordCommonsConfigurationStore commons,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth) || string.IsNullOrWhiteSpace(auth.UserId))
                return Forbidden("An authenticated owner or administrator is required.");
            var botConfig = await configurations.GetAsync(id, ct);
            if (botConfig is null) return Results.NotFound();
            if (!CanManageBot(auth, botConfig))
                return Forbidden("You cannot manage this Discord bot.");
            var config = await commons.GetAsync(id, ct);
            return config is null
                ? Results.NotFound()
                : Results.Ok(DiscordCommonsConfigurationStore.View(config));
        });

        group.MapPut("/bots/{id:guid}/commons", async (
            Guid id,
            DiscordCommonsConfigurationUpdate request,
            IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordCommonsConfigurationStore commons,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth) || string.IsNullOrWhiteSpace(auth.UserId))
                return Forbidden("An authenticated owner or administrator is required.");
            var botConfig = await configurations.GetAsync(id, ct);
            if (botConfig is null) return Results.NotFound();
            if (!CanManageBot(auth, botConfig))
                return Forbidden("You cannot manage this Discord bot.");
            try
            {
                var saved = await commons.SaveAsync(id, request, ct);
                return Results.Ok(DiscordCommonsConfigurationStore.View(saved));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_commons_configuration",
                    message = ex.Message,
                });
            }
        });

        group.MapPost("/bots/{id:guid}/guilds/{guildId}/channels", async (
            Guid id,
            string guildId,
            DiscordGuildChannelCreateRequest request,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            try
            {
                return Results.Ok(await bot.CreateGuildTextChannelAsync(
                    id, guildId, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_discord_channel_create",
                    message = ex.Message,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new
                {
                    error = "discord_channel_parent_not_found",
                    message = ex.Message,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new
                {
                    error = "discord_channel_create_forbidden",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (DiscordRestException ex)
            {
                return Results.Json(new
                {
                    error = "discord_channel_create_rejected",
                    message = ex.Message,
                }, statusCode: (int)ex.StatusCode);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_channel_create_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/bots/{id:guid}/guilds/{guildId}/categories", async (
            Guid id,
            string guildId,
            DiscordGuildCategoryCreateRequest request,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            try
            {
                return Results.Ok(await bot.CreateGuildCategoryAsync(id, guildId, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_discord_category_create", message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = "discord_guild_not_found", message = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = "discord_category_create_forbidden", message = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (DiscordRestException ex)
            {
                return Results.Json(new { error = "discord_category_create_rejected", message = ex.Message },
                    statusCode: (int)ex.StatusCode);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "discord_category_create_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/bots/{id:guid}/guilds/{guildId}/forums", async (
            Guid id,
            string guildId,
            DiscordGuildForumChannelCreateRequest request,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordCommonsConfigurationStore commons,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            var commonsConfig = await commons.GetAsync(id, ct);
            if (commonsConfig?.Scope(guildId) is not { } guildScope
                || !guildScope.Contains(null, request.ParentCategoryId))
                return Forbidden("That Discord category is outside Leaf Community scope.");
            try
            {
                return Results.Ok(await bot.CreateGuildForumChannelAsync(
                    id, guildId, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_discord_forum_create",
                    message = ex.Message,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new
                {
                    error = "discord_channel_parent_not_found",
                    message = ex.Message,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new
                {
                    error = "discord_forum_create_forbidden",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (DiscordRestException ex)
            {
                return Results.Json(new
                {
                    error = "discord_forum_create_rejected",
                    message = ex.Message,
                }, statusCode: (int)ex.StatusCode);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_forum_create_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // Moderation of Leaf Community Envoys in a guild this bot hosts: list them, and suspend,
        // unsuspend or approve one with the bot's host credential. Before this, a host suspension
        // (including legacy owner switch-offs that read as host) could not be lifted at all.
        group.MapGet("/bots/{id:guid}/guilds/{guildId}/commons/envoys", async (
            Guid id,
            string guildId,
            IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordCommonsRelayService relay,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth) || string.IsNullOrWhiteSpace(auth.UserId))
                return Forbidden("An authenticated owner or administrator is required.");
            var botConfig = await configurations.GetAsync(id, ct);
            if (botConfig is null) return Results.NotFound();
            if (!CanManageBot(auth, botConfig))
                return Forbidden("You cannot manage this Discord bot.");
            try
            {
                return Results.Ok(new { envoys = await relay.GetEnvoysAsync(id, guildId, ct) });
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapPost("/bots/{id:guid}/guilds/{guildId}/commons/envoys/{envoyId}/{action}", async (
            Guid id,
            string guildId,
            string envoyId,
            string action,
            IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordCommonsRelayService relay,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth) || string.IsNullOrWhiteSpace(auth.UserId))
                return Forbidden("An authenticated owner or administrator is required.");
            if (action is not (DiscordCommonsEnvoyActions.Approve or DiscordCommonsEnvoyActions.Suspend
                    or DiscordCommonsEnvoyActions.Unsuspend))
                return Results.BadRequest(new
                {
                    error = "invalid_envoy_action",
                    message = "The action must be approve, suspend or unsuspend.",
                });
            var botConfig = await configurations.GetAsync(id, ct);
            if (botConfig is null) return Results.NotFound();
            if (!CanManageBot(auth, botConfig))
                return Forbidden("You cannot manage this Discord bot.");
            try
            {
                await relay.SetEnvoyStatusAsync(id, guildId, envoyId, action, ct);
                return Results.Ok(new { envoyId, action });
            }
            catch (DiscordCommonsRelayException ex)
            {
                return Results.Problem(ex.Message, statusCode: (int)ex.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapPost("/bots/{id:guid}/guilds/{guildId}/channels/{channelId}/commons-webhook", async (
            Guid id,
            string guildId,
            string channelId,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            try
            {
                return Results.Ok(await bot.EnsureCommonsWebhookAsync(
                    id, guildId, channelId, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_commons_webhook",
                    message = ex.Message,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new
                {
                    error = "discord_channel_not_found",
                    message = ex.Message,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new
                {
                    error = "commons_webhook_forbidden",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (DiscordRestException ex)
            {
                return Results.Json(new
                {
                    error = "commons_webhook_rejected",
                    message = ex.Message,
                }, statusCode: (int)ex.StatusCode);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "commons_webhook_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/bots/{id:guid}/guilds/{guildId}/channels/{channelId}/commons-webhook/install-secret", async (
            Guid id,
            string guildId,
            string channelId,
            DiscordCommonsSecretInstallRequest request,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordCommonsConfigurationStore commons,
            DiscordCommonsSecretInstaller installer,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            if (!await bot.IsCommonsChannelAsync(id, guildId, channelId, ct))
                return Forbidden("That Discord channel is outside Leaf Community scope.");
            try
            {
                return Results.Ok(await installer.InstallAsync(id, channelId, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_commons_secret_install",
                    message = ex.Message,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new
                {
                    error = "commons_webhook_not_found",
                    message = ex.Message,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "commons_secret_install_failed",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapPatch("/bots/{id:guid}/guilds/{guildId}/channels/{channelId}", async (
            Guid id,
            string guildId,
            string channelId,
            DiscordChannelEditRequest request,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadPrivateLeafAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            var agent = await agents.ResolveAsync(config.AgentId, ct);
            if (!CanAddressBot(caller, config, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord bot.");
            try
            {
                return Results.Ok(await bot.EditGuildChannelAsync(
                    id, guildId, channelId, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_discord_channel_edit",
                    message = ex.Message,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new
                {
                    error = "discord_channel_not_found",
                    message = ex.Message,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new
                {
                    error = "discord_channel_edit_forbidden",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (DiscordRestException ex)
            {
                return Results.Json(new
                {
                    error = "discord_channel_edit_rejected",
                    message = ex.Message,
                }, statusCode: (int)ex.StatusCode);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_channel_edit_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/bots/{id:guid}/bindings", async (
            Guid id, DiscordGuildBindingRequest request, IAuthContext auth,
            DiscordBotConfigurationStore configurations,
            DiscordBotService bot, CancellationToken ct) =>
        {
            if (!IsAdmin(auth) || string.IsNullOrWhiteSpace(auth.UserId))
                return Forbidden("An authenticated owner or administrator is required.");
            var config = await configurations.GetAsync(id, ct);
            if (config is null) return Results.NotFound();
            if (!CanManageBot(auth, config))
                return Forbidden("You cannot manage this Discord bot.");
            if (string.IsNullOrWhiteSpace(config.OwnerUserId))
                config = await configurations.EnsureOwnerAsync(id, auth.UserId, ct);
            try
            {
                return Results.Ok(await bot.CreateGuildChannelBindingAsync(
                    id, request.GuildId, request.ChannelId, auth.UserId, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_channel", message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = "discord_channel_not_found", message = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = "discord_channel_not_allowed", message = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "discord_bot_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/bindings", async (
            string? agentId,
            DiscordBindingStore bindings, DiscordBotService bot,
            DiscordConversationLeadStore leads, CancellationToken ct) =>
        {
            var items = await bindings.ListAsync(ct);
            if (!string.IsNullOrWhiteSpace(agentId))
                items = items.Where(item => string.Equals(
                    item.AgentId, agentId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var now = DateTimeOffset.UtcNow;
            var projected = await Task.WhenAll(items.Select(async binding =>
                binding with
                {
                    IconUrl = await bot.GetConversationIconUrlAsync(binding, ct),
                    ConversationLead = DiscordConversationLeadStore.Project(
                        await leads.GetAsync(binding, ct), now),
                }));
            return Results.Ok(new { items = projected });
        });

        group.MapGet("/bindings/{id:guid}/lead", async (
            Guid id, HttpContext http, IAuthContext auth,
            DiscordBindingStore bindings, DiscordAgentResolver agents,
            DiscordConversationLeadStore leads, CancellationToken ct) =>
        {
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            if (ReadBindingMessageCaller(http) is { } caller)
            {
                var agent = await agents.ResolveAsync(binding.AgentId, ct);
                if (!CanAddressBinding(caller, binding, agent?.Id.ToString()))
                    return Forbidden("This Agent cannot address that Discord binding.");
            }
            else if (!IsAdmin(auth))
            {
                return Forbidden("Owner or administrator access is required.");
            }
            return Results.Ok(new
            {
                bindingId = binding.BindingId,
                conversationLead = DiscordConversationLeadStore.Project(
                    await leads.GetAsync(binding, ct), DateTimeOffset.UtcNow),
            });
        });

        group.MapDelete("/bindings/{id:guid}/lead", async (
            Guid id, HttpContext http, IAuthContext auth,
            DiscordBindingStore bindings, DiscordAgentResolver agents,
            DiscordConversationLeadStore leads, CancellationToken ct) =>
        {
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            var now = DateTimeOffset.UtcNow;
            // A discussion hands back only its own lead; the owner in the Leaf UI may end any.
            string? discussionId = null;
            if (ReadBindingMessageCaller(http) is { } caller)
            {
                var agent = await agents.ResolveAsync(binding.AgentId, ct);
                if (!CanAddressBinding(caller, binding, agent?.Id.ToString()))
                    return Forbidden("This Agent cannot address that Discord binding.");
                if (caller.DiscussionId is null)
                    return Forbidden("Only the leading discussion or the owner can end a lead.");
                discussionId = caller.DiscussionId;
            }
            else if (!IsAdmin(auth))
            {
                return Forbidden("Owner or administrator access is required.");
            }
            var current = await leads.GetAsync(binding, ct);
            if (discussionId is not null && current is not null && current.IsActive(now)
                && !string.Equals(current.DiscussionId, discussionId, StringComparison.Ordinal))
                return Results.Conflict(new
                {
                    error = "discord_conversation_led_elsewhere",
                    message = "Another Leaf discussion leads this Discord conversation.",
                    conversationLead = DiscordConversationLeadStore.Project(current, now),
                });
            var released = await leads.ReleaseAsync(binding, "released", now, discussionId, ct);
            return Results.Ok(new
            {
                bindingId = binding.BindingId,
                released = released is not null,
                conversationLead = DiscordConversationLeadStore.Project(released ?? current, now),
            });
        });

        group.MapGet("/bindings/{id:guid}/transcript", async (
            Guid id,
            IAuthContext auth,
            DiscordBindingStore bindings,
            DiscordDeliveryStore deliveries,
            DiscordUserIdentityResolver identities,
            DiscordBotService bot,
            DiscordConversationLeadStore leads,
            IEnumerable<IExternalAgentConversationProvider> providers,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            if (binding.SessionId is null || binding.ConversationHandleId is null)
                return Results.Ok(new { binding, messages = Array.Empty<ExternalConversationMessage>() });
            var provider = providers.FirstOrDefault(candidate => candidate.CanHandle(binding.AgentId));
            if (provider is null)
                return Results.Problem(
                    $"No external conversation provider handles Agent '{binding.AgentId}'",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            var handle = new ExternalConversationHandle(
                provider.ProviderId, binding.BindingId, binding.Generation,
                binding.ConversationHandleId, binding.SessionId);
            var page = await provider.ReadSettledAsync(handle, null, 500, ct);
            var discordLookups = 0;
            var messages = await DiscordTranscriptProjection.ProjectAsync(
                page.Messages,
                (externalId, token) => bot.GetUserProfileAsync(
                    binding, externalId, token),
                async (discordMessageId, token) =>
                {
                    var retained = await deliveries.FindInboundAttributionAsync(
                        binding, discordMessageId, token);
                    if (retained is not null) return retained;
                    // Bridge notices (an outbound Sentinel hold) carry no Discord message.
                    if (!ulong.TryParse(discordMessageId, out _)) return null;
                    // Turns admitted before attribution was retained: ask Discord, bounded
                    // per page so a long legacy gap cannot hammer the REST rate limit.
                    if (++discordLookups > MaxTranscriptDiscordLookups) return null;
                    var fetched = await bot.GetMessageAttributionAsync(
                        binding, discordMessageId, token);
                    if (fetched is null) return null;
                    var verified = await identities.ResolveAsync(
                        fetched.ExternalId, binding.OwnerId, token);
                    return verified is null ? fetched : fetched with
                    {
                        VerifiedDisplayName = verified.DisplayName,
                        IsConversationOwner = verified.IsConversationOwner,
                    };
                },
                ct,
                (messageUid, token) => deliveries.FindOutboundReviewAsync(binding, messageUid, token),
                (await leads.GetAsync(binding, ct))?.UnpostedHistory.ToHashSet(StringComparer.Ordinal));
            return Results.Ok(new { binding, messages, nextCursor = page.NextCursor });
        });

        group.MapPut("/bindings/{id:guid}/avatar", async (
            Guid id, DiscordAvatarRequest request, IAuthContext auth,
            DiscordBindingStore bindings,
            DiscordBotConfigurationStore configurations,
            DiscordBotService bot, CancellationToken ct) =>
            await ApplyChannelAvatarAsync(
                id, request.AssetId, auth, bindings, configurations, bot, ct));

        group.MapDelete("/bindings/{id:guid}/avatar", async (
            Guid id, IAuthContext auth,
            DiscordBindingStore bindings,
            DiscordBotConfigurationStore configurations,
            DiscordBotService bot, CancellationToken ct) =>
            await ApplyChannelAvatarAsync(
                id, null, auth, bindings, configurations, bot, ct));

        group.MapPost("/bindings/{id:guid}/clear", async (
            Guid id,
            IAuthContext auth,
            DiscordBindingStore bindings,
            DiscordConversationCoordinator conversations,
            CancellationToken ct) =>
        {
            if (!IsAdmin(auth))
                return Forbidden("Owner or administrator access is required.");
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            return Results.Ok(await conversations.ClearAsync(
                binding, $"leaf-owner:{Guid.NewGuid():N}", ct));
        });

        group.MapPost("/bindings/{id:guid}/messages", async (
            Guid id,
            DiscordBindingMessageRequest request,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            DiscordReplySubscriptionStore replySubscriptions,
            DiscordConversationLeadStore leads,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var caller = ReadBindingMessageCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion or binding-authorized Nova automation is required.");
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            if (!binding.Enabled || binding.SessionId is null
                || binding.ConversationHandleId is null)
                return Results.Conflict(new
                {
                    error = "discord_binding_session_unavailable",
                    message = "The Discord binding has no enabled existing Agent session",
                });
            var agent = await agents.ResolveAsync(binding.AgentId, ct);
            if (!CanAddressBinding(caller, binding, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord binding.");
            if (ValidateLeadRequest(request, caller) is { } invalidLead)
                return invalidLead;
            // Claim before sending, so the session also hears the message that takes the lead.
            // A send that fails puts the previous row back. A discussion that already leads is
            // renewed only after its message is delivered.
            DiscordConversationLead? claimed = null;
            JsonObject? previousLead = null;
            if (request.LeadConversation == true
                && !LeadsNow(await leads.GetAsync(binding, ct), caller, DateTimeOffset.UtcNow))
            {
                try
                {
                    (claimed, previousLead) = await leads.ClaimAsync(
                        binding, caller.DiscussionId!, caller.BeneficiaryId, caller.ActorEntityId,
                        request.LeadForMinutes is { } minutes
                            ? TimeSpan.FromMinutes(minutes)
                            : DiscordConversationLeadStore.DefaultWindow,
                        DateTimeOffset.UtcNow, ct);
                }
                catch (DiscordConversationLedElsewhereException ex)
                {
                    return Results.Conflict(new
                    {
                        error = "discord_conversation_led_elsewhere",
                        message = ex.Message,
                        conversationLead = DiscordConversationLeadStore.Project(
                            ex.Lead, DateTimeOffset.UtcNow),
                    });
                }
            }
            var sent = false;
            try
            {
                var result = await bot.SendExistingBindingMessageAsync(
                    binding,
                    request.Content,
                    request.IdempotencyKey,
                    request.DeliveryContent,
                    (request.OutputAssets ?? [])
                        .Select(asset => new ExternalConversationOutputAsset(
                            asset.AssetId, asset.Description))
                        .ToArray(),
                    new DiscordOutreachProvenance(
                        request.Announcement, caller.AppId, caller.ActorKind,
                        caller.ActorEntityId),
                    ct);
                sent = true;
                var replyCallback = await SubscribeRepliesAsync(
                    request.NotifyReplies, caller, result.Binding, result.MessageUid,
                    replySubscriptions,
                    loggers.CreateLogger("Leaf.Plugins.LeafDiscord.ReplyCallbacks"), ct);
                var conversationLead = await SettleLeadAsync(
                    request, caller, result.Binding, replyCallback, leads,
                    loggers.CreateLogger("Leaf.Plugins.LeafDiscord.ConversationLeads"), ct);
                return Results.Ok(new
                {
                    ok = true,
                    bindingId = result.Binding.BindingId,
                    conversationName = result.Binding.ConversationName,
                    sessionId = result.Binding.SessionId,
                    messageUid = result.MessageUid,
                    replyCallback = replyCallback is null ? null : new
                    {
                        discussionId = replyCallback.DiscussionId,
                        expiresAt = replyCallback.ExpiresAt,
                    },
                    conversationLead,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = "binding_not_allowed", message = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_message", message = ex.Message });
            }
            catch (DiscordBindingSessionChangedException ex)
            {
                return Results.Conflict(new
                {
                    error = "discord_binding_session_changed",
                    message = ex.Message,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "delivery_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            finally
            {
                if (!sent && claimed is not null)
                    await leads.RevertClaimAsync(binding, previousLead, CancellationToken.None);
            }
        });

        group.MapPost("/bindings/{id:guid}/messages/{messageUid}/edit", async (
            Guid id,
            string messageUid,
            DiscordBindingMessageEditRequest request,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadBindingMessageCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion or binding-authorized Nova automation is required.");
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            if (!binding.Enabled || binding.SessionId is null
                || binding.ConversationHandleId is null)
                return Results.Conflict(new
                {
                    error = "discord_binding_session_unavailable",
                    message = "The Discord binding has no enabled existing Agent session",
                });
            var agent = await agents.ResolveAsync(binding.AgentId, ct);
            if (!CanAddressBinding(caller, binding, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord binding.");
            try
            {
                var result = await bot.EditExistingBindingMessageAsync(
                    binding,
                    messageUid,
                    request.Content,
                    request.DeliveryContent,
                    request.OutputAssets?.Select(asset =>
                        new ExternalConversationOutputAsset(
                            asset.AssetId, asset.Description)).ToArray(),
                    ct);
                return Results.Ok(new
                {
                    ok = true,
                    edited = true,
                    bindingId = binding.BindingId,
                    messageUid = result.MessageUid,
                    discordMessageIds = result.DiscordMessageIds,
                    changedCount = result.ChangedCount,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = "binding_not_allowed", message = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_message", message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = "delivery_not_found", message = ex.Message });
            }
            catch (DiscordRestException ex)
            {
                return Results.Json(new { error = "discord_delivery_failed", message = ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "delivery_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapDelete("/bindings/{id:guid}/messages/{messageUid}", async (
            Guid id,
            string messageUid,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadBindingMessageCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion or binding-authorized Nova automation is required.");
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            if (!binding.Enabled || binding.SessionId is null
                || binding.ConversationHandleId is null)
                return Results.Conflict(new
                {
                    error = "discord_binding_session_unavailable",
                    message = "The Discord binding has no enabled existing Agent session",
                });
            var agent = await agents.ResolveAsync(binding.AgentId, ct);
            if (!CanAddressBinding(caller, binding, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord binding.");
            try
            {
                var result = await bot.DeleteExistingBindingMessageAsync(
                    binding, messageUid, ct);
                return Results.Ok(new
                {
                    ok = true,
                    deleted = true,
                    bindingId = binding.BindingId,
                    messageUid = result.MessageUid,
                    discordMessageIds = result.DiscordMessageIds,
                    changedCount = result.ChangedCount,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = "binding_not_allowed", message = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_message", message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = "delivery_not_found", message = ex.Message });
            }
            catch (DiscordRestException ex)
            {
                return Results.Json(new { error = "discord_delivery_failed", message = ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "delivery_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/bindings/{id:guid}/messages/pin", async (
            Guid id,
            DiscordBindingPinRequest request,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadBindingMessageCaller(http);
            if (caller is null)
                return Forbidden("A signed private Nova Agent discussion or binding-authorized Nova automation is required.");
            var binding = await bindings.GetAsync(id, ct);
            if (binding is null) return Results.NotFound();
            if (!binding.Enabled || binding.SessionId is null
                || binding.ConversationHandleId is null)
                return Results.Conflict(new
                {
                    error = "discord_binding_session_unavailable",
                    message = "The Discord binding has no enabled existing Agent session",
                });
            var agent = await agents.ResolveAsync(binding.AgentId, ct);
            if (!CanAddressBinding(caller, binding, agent?.Id.ToString()))
                return Forbidden("This Agent cannot address that Discord binding.");
            try
            {
                var discordMessageId = await bot.PinExistingBindingMessageAsync(
                    binding, request.MessageUid, ct);
                return Results.Ok(new
                {
                    ok = true,
                    pinned = true,
                    bindingId = binding.BindingId,
                    messageUid = request.MessageUid,
                    discordMessageId,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_message", message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = "delivery_not_found", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "delivery_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/outreach/dm", async (
            DiscordDirectMessageRequest request,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var caller = ReadDiscordAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed external Discord conversation session is required.");
            var source = await bindings.FindByBindingIdAsync(caller.BindingId, ct);
            if (source is null || source.SessionId != caller.SessionId
                || source.OwnerId != caller.BeneficiaryId)
                return Forbidden("The signed Discord session does not match the source binding.");
            var agent = await agents.ResolveAsync(source.AgentId, ct);
            if (agent is null || !string.Equals(
                    agent.Id.ToString(), caller.ActorEntityId, StringComparison.OrdinalIgnoreCase))
                return Forbidden("The signed Agent does not own this Discord session.");
            try
            {
                var result = await bot.SendDirectMessageAsync(
                    source,
                    request.RecipientUserId,
                    request.Content,
                    request.IdempotencyKey,
                    ct);
                return Results.Ok(new
                {
                    ok = true,
                    bindingId = result.Binding.BindingId,
                    conversationName = result.Binding.ConversationName,
                    sessionId = result.Binding.SessionId,
                    messageUid = result.MessageUid,
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = "recipient_not_allowed", message = ex.Message },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_outreach", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "outreach_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/bots/{id:guid}/dms", async (
            Guid id,
            DiscordOwnerDirectMessageRequest request,
            HttpContext http,
            DiscordBotConfigurationStore configurations,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            DiscordBindingStore bindings,
            DiscordReplySubscriptionStore replySubscriptions,
            ILoggerFactory loggers,
            CancellationToken ct) =>
            await OpenOwnerDirectMessageAsync(
                id, http, configurations, agents,
                async (config, ownerUserId, token) =>
                {
                    var result = await bot.OpenOwnerDirectMessageAsync(
                        config.EntityId, ownerUserId, request, token);
                    // OpenOwnerDirectMessageAsync already required a private discussion caller.
                    if (request.NotifyReplies
                        && ReadPrivateLeafAgentCaller(http) is { } caller
                        && await bindings.GetAsync(result.BindingEntityId, token) is { } binding)
                        await SubscribeRepliesAsync(
                            true, caller, binding, result.MessageUid, replySubscriptions,
                            loggers.CreateLogger("Leaf.Plugins.LeafDiscord.ReplyCallbacks"), token);
                    return result;
                },
                ct));

        group.MapPost("/reactions", async (
            DiscordReactionRequest request,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordDeliveryStore deliveries,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            DiscordConversationLeadStore leads,
            CancellationToken ct) =>
        {
            var caller = ReadDiscordAgentCaller(http);
            if (caller is null)
                return Forbidden("A signed external Discord conversation session is required.");
            var source = await bindings.FindByBindingIdAsync(caller.BindingId, ct);
            if (source is null || source.SessionId != caller.SessionId
                || source.OwnerId != caller.BeneficiaryId)
                return Forbidden("The signed Discord session does not match the source binding.");
            var agent = await agents.ResolveAsync(source.AgentId, ct);
            if (agent is null || !string.Equals(
                    agent.Id.ToString(), caller.ActorEntityId, StringComparison.OrdinalIgnoreCase))
                return Forbidden("The signed Agent does not own this Discord session.");
            if (await RejectWhileDiscussionLeadsAsync(source, leads, DateTimeOffset.UtcNow, ct)
                is { } leadConflict)
                return leadConflict;
            if (!await deliveries.ContainsAdmittedInboundAsync(
                    source.BindingId, source.Generation, request.MessageId, ct))
                return Results.NotFound(new
                {
                    error = "discord_message_not_in_binding",
                    message = "The message is not an admitted inbound message in this Discord session",
                });
            try
            {
                await bot.AddReactionAsync(source, request.MessageId, request.Emoji, ct);
                return Results.Ok(new { ok = true });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_reaction", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "reaction_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (DiscordRestException ex)
            {
                return Results.Json(new { error = "discord_reaction_failed", message = ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapGet("/guild/context", async (
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var scope = await ResolveDiscordGuildScopeAsync(http, bindings, agents, ct);
            if (scope is null)
                return Forbidden("A signed external Discord conversation session is required.");
            try
            {
                return Results.Ok(new
                {
                    bindingId = scope.Binding.BindingId,
                    guildId = scope.Binding.GuildId,
                    currentChannelId = scope.Binding.ConversationId,
                    serverMap = await bot.GetGuildNavigationAsync(scope.Binding, ct),
                });
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or KeyNotFoundException
                                       or UnauthorizedAccessException)
            {
                return GuildReadError(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_guild_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/guild/channels/{channelId}/messages", async (
            string channelId,
            int? limit,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var scope = await ResolveDiscordGuildScopeAsync(http, bindings, agents, ct);
            if (scope is null)
                return Forbidden("A signed external Discord conversation session is required.");
            try
            {
                var messages = await bot.ReadGuildChannelMessagesAsync(
                    scope.Binding, channelId, limit ?? 50, ct);
                return Results.Ok(new
                {
                    guildId = scope.Binding.GuildId,
                    channelId,
                    trust = "untrusted_discord_content",
                    messages,
                });
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or KeyNotFoundException
                                       or UnauthorizedAccessException)
            {
                return GuildReadError(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_guild_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/guild/forums/{forumId}/posts", async (
            string forumId,
            int? limit,
            bool? includeArchived,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var scope = await ResolveDiscordGuildScopeAsync(http, bindings, agents, ct);
            if (scope is null)
                return Forbidden("A signed external Discord conversation session is required.");
            try
            {
                var posts = await bot.ListForumPostsAsync(
                    scope.Binding, forumId, limit ?? 50, includeArchived ?? true, ct);
                return Results.Ok(new
                {
                    guildId = scope.Binding.GuildId,
                    forumId,
                    trust = "untrusted_discord_content",
                    posts,
                });
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or KeyNotFoundException
                                       or UnauthorizedAccessException)
            {
                return GuildReadError(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_guild_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/guild/forums/{forumId}/posts/{postId}/messages", async (
            string forumId,
            string postId,
            int? limit,
            HttpContext http,
            DiscordBindingStore bindings,
            DiscordAgentResolver agents,
            DiscordBotService bot,
            CancellationToken ct) =>
        {
            var scope = await ResolveDiscordGuildScopeAsync(http, bindings, agents, ct);
            if (scope is null)
                return Forbidden("A signed external Discord conversation session is required.");
            try
            {
                var messages = await bot.ReadForumPostMessagesAsync(
                    scope.Binding, forumId, postId, limit ?? 50, ct);
                return Results.Ok(new
                {
                    guildId = scope.Binding.GuildId,
                    forumId,
                    postId,
                    trust = "untrusted_discord_content",
                    messages,
                });
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or KeyNotFoundException
                                       or UnauthorizedAccessException)
            {
                return GuildReadError(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new
                {
                    error = "discord_guild_unavailable",
                    message = ex.Message,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/privacy", () => Results.Ok(new
        {
            audience = "explicitly-authorized-discord-participants",
            session = "normal-persistent-agent-session",
            capabilities = "normal-agent-workspace-and-tools",
            promptInjectionReview = "separate-isolated-advisory-sentinel",
            authority = "discord-participants-are-not-owner-delegates",
            disclosure = "no-owner-personal-or-unrelated-private-information",
            consequentialActions = "require-private-owner-confirmation",
            conversationalDmFollowup = "current-agent-session-to-configured-dm-allowlist-only",
            ownerDirectedSessionMessage = "private-leaf-agent-to-existing-owned-binding-only",
            guildContext = "always-on-bot-visible-server-map-plus-optional-presence",
            crossChannelRead = "current-external-session-same-application-and-guild-only",
            forumPosts = "human-created-posts-use-independent-thread-bindings",
            textChannelReaderPresence = "not-available-from-discord",
        }));
    }

    private static async Task ValidateBehaviorTargetAsync(
        LeafDiscordConfig config,
        DiscordBehaviorRequest request,
        DiscordBindingStore bindings,
        CancellationToken ct)
    {
        var scope = request.Scope?.Trim().ToLowerInvariant() ?? "";
        if (!DiscordBehaviorScopes.IsValid(scope))
            throw new ArgumentException("Discord behavior scope is invalid.");
        if (scope != DiscordBehaviorScopes.Dm) return;
        var conversationId = request.ConversationId?.Trim();
        var dm = (await bindings.ListAsync(ct)).FirstOrDefault(binding =>
            binding.Kind == "dm"
            && string.Equals(binding.ApplicationId, config.ApplicationId,
                StringComparison.Ordinal)
            && string.Equals(binding.ConversationId, conversationId,
                StringComparison.Ordinal));
        if (dm is null)
            throw new ArgumentException(
                "A DM override must target an existing DM visible to this Agent bot.");
    }

    private static async Task<IResult> ApplyBotAvatarAsync(
        Guid id, string? assetId, IAuthContext auth,
        DiscordBotConfigurationStore configurations,
        DiscordAgentResolver agents, DiscordBotService bot,
        CancellationToken ct)
    {
        if (!IsAdmin(auth))
            return Forbidden("Owner or administrator access is required.");
        var config = await configurations.GetAsync(id, ct);
        if (config is null) return Results.NotFound();
        if (!CanManageBot(auth, config))
            return Forbidden("You cannot manage this Discord bot.");
        if (assetId is not null && string.IsNullOrWhiteSpace(assetId))
            return Results.BadRequest(new
            {
                error = "discord_avatar_asset_required",
                message = "A RedLeaf Asset ID is required.",
            });
        try
        {
            var saved = await bot.SetBotAvatarAsync(id, assetId, ct);
            return Results.Ok(await ProjectConfigAsync(saved, agents, ct));
        }
        catch (ArgumentException ex)
        {
            return AvatarError("discord_avatar_invalid", ex, StatusCodes.Status400BadRequest);
        }
        catch (KeyNotFoundException ex)
        {
            return AvatarError("discord_avatar_target_not_found", ex, StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return AvatarError("discord_avatar_unavailable", ex,
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (DiscordRestException ex)
        {
            return AvatarError("discord_avatar_update_failed", ex,
                StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ApplyGuildAvatarAsync(
        Guid id, string guildId, string? assetId, IAuthContext auth,
        DiscordBotConfigurationStore configurations,
        DiscordAgentResolver agents, DiscordBotService bot,
        CancellationToken ct)
    {
        if (!IsAdmin(auth))
            return Forbidden("Owner or administrator access is required.");
        var config = await configurations.GetAsync(id, ct);
        if (config is null) return Results.NotFound();
        if (!CanManageBot(auth, config))
            return Forbidden("You cannot manage this Discord bot.");
        if (assetId is not null && string.IsNullOrWhiteSpace(assetId))
            return Results.BadRequest(new
            {
                error = "discord_avatar_asset_required",
                message = "A RedLeaf Asset ID is required.",
            });
        try
        {
            var saved = await bot.SetGuildAvatarAsync(id, guildId, assetId, ct);
            return Results.Ok(await ProjectConfigAsync(saved, agents, ct));
        }
        catch (ArgumentException ex)
        {
            return AvatarError("discord_avatar_invalid", ex, StatusCodes.Status400BadRequest);
        }
        catch (KeyNotFoundException ex)
        {
            return AvatarError("discord_avatar_target_not_found", ex, StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return AvatarError("discord_avatar_unavailable", ex,
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (DiscordRestException ex)
        {
            return AvatarError("discord_avatar_update_failed", ex,
                StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ApplyChannelAvatarAsync(
        Guid id, string? assetId, IAuthContext auth,
        DiscordBindingStore bindings,
        DiscordBotConfigurationStore configurations,
        DiscordBotService bot, CancellationToken ct)
    {
        if (!IsAdmin(auth))
            return Forbidden("Owner or administrator access is required.");
        var binding = await bindings.GetAsync(id, ct);
        if (binding is null) return Results.NotFound();
        var config = await configurations.FindByApplicationIdAsync(
            binding.ApplicationId, ct);
        if (config is null) return Results.NotFound();
        if (!CanManageBot(auth, config)
            || (!IsLocalAdministrator(auth)
                && !string.Equals(binding.OwnerId, auth.UserId,
                    StringComparison.OrdinalIgnoreCase)))
            return Forbidden("You cannot manage this Discord binding.");
        if (assetId is not null && string.IsNullOrWhiteSpace(assetId))
            return Results.BadRequest(new
            {
                error = "discord_avatar_asset_required",
                message = "A RedLeaf Asset ID is required.",
            });
        try
        {
            return Results.Ok(await bot.SetChannelAvatarAsync(binding, assetId, ct));
        }
        catch (ArgumentException ex)
        {
            return AvatarError("discord_avatar_invalid", ex, StatusCodes.Status400BadRequest);
        }
        catch (KeyNotFoundException ex)
        {
            return AvatarError("discord_avatar_target_not_found", ex, StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return AvatarError("discord_avatar_unavailable", ex,
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (DiscordRestException ex)
        {
            return AvatarError("discord_avatar_update_failed", ex,
                StatusCodes.Status502BadGateway);
        }
    }

    private static IResult AvatarError(string error, Exception exception, int statusCode)
        => Results.Json(new { error, message = exception.Message }, statusCode: statusCode);

    private static IResult GuildReadError(Exception exception)
        => exception switch
        {
            ArgumentException => Results.BadRequest(new
            {
                error = "discord_guild_read_invalid",
                message = exception.Message,
            }),
            KeyNotFoundException => Results.NotFound(new
            {
                error = "discord_guild_target_not_found",
                message = exception.Message,
            }),
            UnauthorizedAccessException => Results.Json(new
            {
                error = "discord_guild_read_forbidden",
                message = exception.Message,
            }, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(exception.Message),
        };

    internal static async Task<IResult> ReadBotCommandsAsync(
        Guid id, IAuthContext auth,
        Func<Guid, CancellationToken, Task<LeafDiscordConfig?>> getConfig,
        Func<Guid, CancellationToken, Task<DiscordCommandReport>> readCommands,
        CancellationToken ct)
    {
        if (!IsAdmin(auth) || string.IsNullOrWhiteSpace(auth.UserId))
            return Forbidden("Owner or administrator access is required.");
        var config = await getConfig(id, ct);
        if (config is null) return Results.NotFound();
        if (!CanManageBot(auth, config))
            return Forbidden("You cannot manage this Discord bot.");
        try
        {
            return Results.Ok(await readCommands(id, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException)
        {
            return Results.Json(new
            {
                error = "discord_bot_unavailable",
                message = "Discord bot is disconnected or its configured identity is unavailable.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception)
        {
            // Never emit/log Discord exception messages, response bodies, or credentials.
            return Results.Json(new
            {
                error = "discord_commands_read_failed",
                message = "Discord global application commands could not be read.",
            }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Owner-directed DM opening from a signed private Nova Agent discussion. Authority is
    /// the exact bot Agent and owner; the recipient must already be on the bot's DM allowlist.
    /// </summary>
    internal static async Task<IResult> OpenOwnerDirectMessageAsync(
        Guid id,
        HttpContext http,
        DiscordBotConfigurationStore configurations,
        DiscordAgentResolver agents,
        Func<LeafDiscordConfig, string, CancellationToken, Task<DiscordOwnerDirectMessageResult>> open,
        CancellationToken ct)
    {
        var caller = ReadPrivateLeafAgentCaller(http);
        if (caller is null)
            return Forbidden("A signed private Nova Agent discussion is required.");
        var config = await configurations.GetAsync(id, ct);
        if (config is null) return Results.NotFound();
        var agent = await agents.ResolveAsync(config.AgentId, ct);
        if (!MatchesBotOwnerAndAgent(caller, config, agent?.Id.ToString()))
            return Forbidden("This Agent cannot address that Discord bot.");
        if (!config.Enabled)
            return Results.Conflict(new
            {
                error = "discord_bot_disabled",
                message = "The Discord bot is disabled.",
            });
        try
        {
            var result = await open(config, config.OwnerUserId!, ct);
            return Results.Ok(new
            {
                ok = true,
                bindingEntityId = result.BindingEntityId,
                bindingId = result.BindingId,
                generation = result.Generation,
                sessionId = result.SessionId,
                deliveryId = result.DeliveryId,
                messageUid = result.MessageUid,
                channelId = result.ChannelId,
                discordMessageId = result.DiscordMessageId,
                binding = result.BindingCreated ? "created" : "reused",
                bindingCreated = result.BindingCreated,
                replayed = result.Replayed,
            });
        }
        catch (DiscordRecipientNotAllowlistedException ex)
        {
            return Results.Json(new { error = "recipient_not_allowlisted", message = ex.Message },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = "dm_not_allowed", message = ex.Message },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = "invalid_dm", message = ex.Message });
        }
        catch (DiscordIdempotencyConflictException ex)
        {
            return Results.Conflict(new { error = "idempotency_key_conflict", message = ex.Message });
        }
        catch (DiscordBindingUnavailableException ex)
        {
            return Results.Conflict(new { error = "discord_binding_disabled", message = ex.Message });
        }
        catch (DiscordDirectMessageRefusedException ex)
        {
            return Results.Json(new
            {
                error = "discord_dm_refused",
                message = ex.Message,
                discordStatus = (int)ex.DiscordStatusCode,
            }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (DiscordDirectMessageUnavailableException ex)
        {
            return Results.Json(new
            {
                error = "discord_dm_unavailable",
                message = ex.Message,
                retryAfterSeconds = ex.RetryAfter?.TotalSeconds,
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = "discord_bot_unavailable", message = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Lists the bot application's own emojis from the connected client. Authority is the
    /// same signed private Nova discussion, exact bot Agent, and owner as the other bot routes.
    /// </summary>
    internal static async Task<IResult> ListApplicationEmojisAsync(
        Guid id,
        HttpContext http,
        DiscordBotConfigurationStore configurations,
        DiscordAgentResolver agents,
        Func<Guid, CancellationToken, Task<IReadOnlyList<DiscordApplicationEmoji>>> list,
        CancellationToken ct)
    {
        if (await AuthorizeBotAgentAsync(id, http, configurations, agents, ct) is { } denied)
            return denied;
        try
        {
            return Results.Ok(new { items = await list(id, ct) });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return ApplicationEmojiDiscordError(ex, "listed");
        }
    }

    /// <summary>
    /// Uploads one application emoji from a RedLeaf image Asset. An existing emoji of the
    /// same name is returned with 409 instead of being duplicated.
    /// </summary>
    internal static async Task<IResult> CreateApplicationEmojiAsync(
        Guid id,
        DiscordApplicationEmojiCreateRequest request,
        HttpContext http,
        DiscordBotConfigurationStore configurations,
        DiscordAgentResolver agents,
        IAssets assets,
        Func<Guid, CancellationToken, Task<IReadOnlyList<DiscordApplicationEmoji>>> list,
        Func<Guid, string, DiscordApplicationEmojiImage, CancellationToken, Task<DiscordApplicationEmoji>> create,
        CancellationToken ct)
    {
        if (await AuthorizeBotAgentAsync(id, http, configurations, agents, ct) is { } denied)
            return denied;
        string name;
        DiscordApplicationEmojiImage image;
        try
        {
            name = DiscordApplicationEmojis.ValidateName(request.Name);
            image = await DiscordApplicationEmojis.ReadImageAsync(request.AssetId, assets, ct);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = "invalid_application_emoji", message = ex.Message });
        }
        catch (DiscordApplicationEmojiAssetNotFoundException ex)
        {
            return Results.NotFound(new { error = "application_emoji_asset_not_found", message = ex.Message });
        }

        var gate = DiscordApplicationEmojis.CreateGate(id);
        await gate.WaitAsync(ct);
        try
        {
            var existing = (await list(id, ct)).FirstOrDefault(emoji =>
                string.Equals(emoji.Name, name, StringComparison.Ordinal));
            if (existing is not null)
                return Results.Conflict(new
                {
                    error = "application_emoji_exists",
                    message = $"An application emoji named '{name}' already exists.",
                    emoji = existing,
                });
            return Results.Ok(await create(id, name, image, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return ApplicationEmojiDiscordError(ex, "created");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Deletes one of the application's own emojis so it can be replaced. The emoji must be
    /// in this application's list; the create gate is held so a delete cannot race a create.
    /// </summary>
    internal static async Task<IResult> DeleteApplicationEmojiAsync(
        Guid id,
        string emojiId,
        HttpContext http,
        DiscordBotConfigurationStore configurations,
        DiscordAgentResolver agents,
        Func<Guid, CancellationToken, Task<IReadOnlyList<DiscordApplicationEmoji>>> list,
        Func<Guid, ulong, CancellationToken, Task> delete,
        CancellationToken ct)
    {
        if (await AuthorizeBotAgentAsync(id, http, configurations, agents, ct) is { } denied)
            return denied;
        if (!ulong.TryParse(emojiId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedId)
            || parsedId == 0)
            return Results.BadRequest(new
            {
                error = "invalid_application_emoji",
                message = "Emoji ID must be a Discord snowflake.",
            });

        var gate = DiscordApplicationEmojis.CreateGate(id);
        await gate.WaitAsync(ct);
        try
        {
            var canonicalId = parsedId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!(await list(id, ct)).Any(emoji =>
                    string.Equals(emoji.Id, canonicalId, StringComparison.Ordinal)))
                return Results.NotFound(new
                {
                    error = "application_emoji_not_found",
                    message = "No application emoji with that ID belongs to this bot.",
                });
            await delete(id, parsedId, ct);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return ApplicationEmojiDiscordError(ex, "deleted");
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<IResult?> AuthorizeBotAgentAsync(
        Guid id,
        HttpContext http,
        DiscordBotConfigurationStore configurations,
        DiscordAgentResolver agents,
        CancellationToken ct)
    {
        var caller = ReadPrivateLeafAgentCaller(http);
        if (caller is null)
            return Forbidden("A signed private Nova Agent discussion is required.");
        var config = await configurations.GetAsync(id, ct);
        if (config is null) return Results.NotFound();
        var agent = await agents.ResolveAsync(config.AgentId, ct);
        return CanAddressBot(caller, config, agent?.Id.ToString())
            ? null
            : Forbidden("This Agent cannot address that Discord bot.");
    }

    // Never emit Discord exception messages or response bodies; only bounded codes.
    private static IResult ApplicationEmojiDiscordError(Exception exception, string action)
        => exception switch
        {
            KeyNotFoundException => Results.NotFound(),
            InvalidOperationException => Results.Json(new
            {
                error = "discord_bot_unavailable",
                message = "Discord bot is disconnected or its configured identity is unavailable.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable),
            Discord.Net.HttpException http => Results.Json(new
            {
                error = "discord_application_emoji_rejected",
                message = $"Discord refused the application emoji request ({(int)http.HttpCode}).",
                discordCode = (int?)http.DiscordCode,
            }, statusCode: StatusCodes.Status502BadGateway),
            _ => Results.Json(new
            {
                error = "discord_application_emoji_failed",
                message = $"Discord application emojis could not be {action}.",
            }, statusCode: StatusCodes.Status502BadGateway),
        };

    private static bool IsAdmin(IAuthContext auth)
        => auth.IsInRole("admin") || auth.IsInRole("owner");

    internal static bool CanManageBot(IAuthContext auth, LeafDiscordConfig config)
        => !string.IsNullOrWhiteSpace(auth.UserId)
           && (IsLocalAdministrator(auth)
               || string.IsNullOrWhiteSpace(config.OwnerUserId)
               || string.Equals(config.OwnerUserId, auth.UserId,
                   StringComparison.OrdinalIgnoreCase));

    private static bool IsLocalAdministrator(IAuthContext auth)
        => IsAdmin(auth)
           && string.Equals(auth.UserId, "local-user",
               StringComparison.OrdinalIgnoreCase);

    private static async Task<LeafDiscordConfigView> ProjectConfigAsync(
        LeafDiscordConfig config,
        DiscordAgentResolver agents,
        CancellationToken ct)
    {
        var agent = await agents.ResolveAsync(config.AgentId, ct);
        return LeafDiscordConfigStore.View(config) with
        {
            AgentName = agent?.Name,
            AgentAvatarUrl = agent is null ? null : JsonRead.String(agent.Data, "avatar"),
        };
    }

    private static async Task ValidateConfigurationAsync(
        string? agentId,
        string? sessionMode,
        DiscordAgentResolver agents,
        DiscordRuntimeOptionsResolver runtimeOptions,
        CancellationToken ct)
    {
        if (await agents.ResolveAsync(agentId, ct) is null)
            throw new ArgumentException($"Agent '{agentId}' was not found");
        if (await runtimeOptions.ResolveQualityModeAsync(sessionMode, ct) is null)
            throw new ArgumentException(
                $"Session quality mode '{sessionMode}' was not found");
    }

    internal static DiscordAgentCaller? ReadDiscordAgentCaller(HttpContext http)
    {
        if (!string.Equals(http.User.FindFirst("token_use")?.Value,
                "execution", StringComparison.OrdinalIgnoreCase))
            return null;
        var raw = http.User.FindFirst("execution_identity")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var actor = root.GetProperty("actor");
            var beneficiary = root.GetProperty("beneficiary");
            if (actor.GetProperty("kind").GetString() != "agent"
                || beneficiary.GetProperty("kind").GetString() != "user")
                return null;
            var actorEntityId = actor.GetProperty("entityId").GetString();
            var beneficiaryId = beneficiary.GetProperty("id").GetString();
            string? bindingId = null;
            string? sessionId = null;
            foreach (var item in root.GetProperty("context").EnumerateArray())
            {
                var kind = item.GetProperty("kind").GetString();
                if (kind == "external-conversation") bindingId = item.GetProperty("id").GetString();
                if (kind == "session") sessionId = item.GetProperty("id").GetString();
            }
            return actorEntityId is not null && beneficiaryId is not null
                   && bindingId is not null && sessionId is not null
                ? new DiscordAgentCaller(actorEntityId, beneficiaryId, bindingId, sessionId)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
                                   or KeyNotFoundException)
        {
            return null;
        }
    }

    private static async Task<DiscordGuildAgentScope?> ResolveDiscordGuildScopeAsync(
        HttpContext http,
        DiscordBindingStore bindings,
        DiscordAgentResolver agents,
        CancellationToken ct)
    {
        var caller = ReadDiscordAgentCaller(http);
        if (caller is null) return null;
        var source = await bindings.FindByBindingIdAsync(caller.BindingId, ct);
        if (source is null) return null;
        var agent = await agents.ResolveAsync(source.AgentId, ct);
        return CanReadGuildScope(caller, source, agent?.Id.ToString())
            ? new DiscordGuildAgentScope(caller, source)
            : null;
    }

    internal static bool CanReadGuildScope(
        DiscordAgentCaller caller,
        DiscordBinding source,
        string? resolvedAgentEntityId)
        => source.Enabled
           && string.Equals(source.SessionId, caller.SessionId,
               StringComparison.Ordinal)
           && string.Equals(source.OwnerId, caller.BeneficiaryId,
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(resolvedAgentEntityId, caller.ActorEntityId,
               StringComparison.OrdinalIgnoreCase)
           && !string.Equals(source.Kind, "dm", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(source.GuildId);

    internal static bool CanAddressBinding(
        LeafAgentCaller caller,
        DiscordBinding binding,
        string? resolvedAgentEntityId)
        => string.Equals(binding.OwnerId, caller.BeneficiaryId,
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(resolvedAgentEntityId, caller.ActorEntityId,
               StringComparison.OrdinalIgnoreCase)
           && (caller.AutomationId is null
               || binding.AuthorizedAutomationIds.Contains(caller.AutomationId));

    internal static bool CanAddressBot(
        LeafAgentCaller caller,
        LeafDiscordConfig config,
        string? resolvedAgentEntityId)
        => config.Enabled && MatchesBotOwnerAndAgent(caller, config, resolvedAgentEntityId);

    internal static bool MatchesBotOwnerAndAgent(
        LeafAgentCaller caller,
        LeafDiscordConfig config,
        string? resolvedAgentEntityId)
        => !string.IsNullOrWhiteSpace(config.OwnerUserId)
           && string.Equals(config.OwnerUserId, caller.BeneficiaryId,
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(resolvedAgentEntityId, caller.ActorEntityId,
               StringComparison.OrdinalIgnoreCase);

    internal static IResult Forbidden(string message)
        => Results.Json(new { error = "forbidden", message },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// A message sent from a private discussion subscribes that discussion to the binding's
    /// replies. Automations have no discussion to report back to. The message is already
    /// delivered, so a subscription failure is logged and reported as absent, never as a
    /// send failure the caller might retry into a duplicate.
    /// </summary>
    internal static async Task<DiscordReplySubscription?> SubscribeRepliesAsync(
        bool notifyReplies,
        LeafAgentCaller caller,
        DiscordBinding binding,
        string messageUid,
        DiscordReplySubscriptionStore replySubscriptions,
        ILogger logger,
        CancellationToken ct)
    {
        if (!notifyReplies || caller.DiscussionId is null) return null;
        try
        {
            return await replySubscriptions.SubscribeAsync(
                binding, caller.DiscussionId, caller.BeneficiaryId,
                caller.ActorEntityId, messageUid, DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Discord reply callback could not be recorded for binding {BindingId}",
                binding.BindingId);
            return null;
        }
    }

    internal static IResult? ValidateLeadRequest(
        DiscordBindingMessageRequest request, LeafAgentCaller caller)
    {
        var maxMinutes = (int)DiscordConversationLeadStore.MaxWindow.TotalMinutes;
        if (request.LeadForMinutes is { } minutes)
        {
            if (request.LeadConversation != true)
                return Results.BadRequest(new
                {
                    error = "invalid_lead_window",
                    message = "leadForMinutes applies only with leadConversation: true.",
                });
            if (minutes < 1 || minutes > maxMinutes)
                return Results.BadRequest(new
                {
                    error = "invalid_lead_window",
                    message = $"leadForMinutes must be between 1 and {maxMinutes}.",
                });
        }
        if (request.LeadConversation != true) return null;
        if (caller.DiscussionId is null)
            return Results.BadRequest(new
            {
                error = "lead_requires_discussion",
                message = "Only a signed private discussion can lead a Discord conversation; it must receive the replies.",
            });
        if (!request.NotifyReplies)
            return Results.BadRequest(new
            {
                error = "lead_requires_reply_callback",
                message = "A leading discussion must receive the replies; leadConversation cannot be combined with notifyReplies: false.",
            });
        return null;
    }

    /// <summary>
    /// After a delivered send: the leading discussion renews its lead, or hands it back with
    /// <c>leadConversation: false</c>. A lead whose discussion could not be subscribed to the
    /// replies is released at once, so nobody is left unanswered. The message is already
    /// delivered, so a failure here is logged and reported as an unknown state, never as a
    /// send failure the caller might retry into a duplicate.
    /// </summary>
    internal static async Task<DiscordConversationLeadStatus?> SettleLeadAsync(
        DiscordBindingMessageRequest request,
        LeafAgentCaller caller,
        DiscordBinding binding,
        DiscordReplySubscription? replyCallback,
        DiscordConversationLeadStore leads,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var lead = await leads.GetAsync(binding, ct);
            if (LeadsNow(lead, caller, now) && caller.DiscussionId is { } discussionId)
            {
                lead = request.LeadConversation == false
                    ? await leads.ReleaseAsync(binding, "released", now, discussionId, ct)
                    : request.NotifyReplies && replyCallback is null
                        ? await leads.ReleaseAsync(
                            binding, "reply_callback_unavailable", now, discussionId, ct)
                        : await leads.RenewAsync(binding, discussionId,
                            request.LeadForMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                            now, ct);
            }
            return DiscordConversationLeadStore.Project(lead ?? await leads.GetAsync(binding, ct), now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Discord conversation lead could not be settled for binding {BindingId}",
                binding.BindingId);
            return null;
        }
    }

    /// <summary>
    /// A reaction from the binding's own session is a voice too: while a private discussion
    /// leads the conversation, that discussion is the only one.
    /// </summary>
    internal static async Task<IResult?> RejectWhileDiscussionLeadsAsync(
        DiscordBinding binding, DiscordConversationLeadStore leads, DateTimeOffset now,
        CancellationToken ct)
        => await leads.GetAsync(binding, ct) is { } lead && lead.IsActive(now)
            ? Results.Conflict(new
            {
                error = "discord_conversation_owner_leads",
                message = "A private Leaf discussion leads this Discord conversation; it answers here until it hands back.",
                conversationLead = DiscordConversationLeadStore.Project(lead, now),
            })
            : null;

    private static bool LeadsNow(
        DiscordConversationLead? lead, LeafAgentCaller caller, DateTimeOffset now)
        => lead is not null && lead.IsActive(now) && caller.DiscussionId is not null
           && string.Equals(lead.DiscussionId, caller.DiscussionId, StringComparison.Ordinal);

    internal static LeafAgentCaller? ReadBindingMessageCaller(HttpContext http)
        => ReadLeafAgentCaller(http, allowAuthorizedAutomation: true);

    internal static LeafAgentCaller? ReadPrivateLeafAgentCaller(HttpContext http)
        => ReadLeafAgentCaller(http, allowAuthorizedAutomation: false);

    private static LeafAgentCaller? ReadLeafAgentCaller(
        HttpContext http, bool allowAuthorizedAutomation)
    {
        if (!string.Equals(http.User.FindFirst("token_use")?.Value,
                "execution", StringComparison.OrdinalIgnoreCase))
            return null;
        var raw = http.User.FindFirst("execution_identity")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var appId = root.GetProperty("app").GetProperty("id").GetString();
            if (appId != "nova")
                return null;
            var actor = root.GetProperty("actor");
            var beneficiary = root.GetProperty("beneficiary");
            var actorKind = actor.GetProperty("kind").GetString();
            if (actorKind != "agent"
                || beneficiary.GetProperty("kind").GetString() != "user")
                return null;
            var actorEntityId = actor.GetProperty("entityId").GetString();
            var beneficiaryId = beneficiary.GetProperty("id").GetString();
            var hasPrivateDiscussion = false;
            string? discussionId = null;
            var hasExternalConversation = false;
            var automationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.GetProperty("context").EnumerateArray())
            {
                var kind = item.GetProperty("kind").GetString();
                if (kind == "discussion")
                {
                    hasPrivateDiscussion = true;
                    if (item.TryGetProperty("id", out var discussion)
                        && discussion.ValueKind == JsonValueKind.String
                        && discussion.GetString() is { Length: > 0 and <= 100 } value)
                        discussionId ??= value;
                }
                if (kind == "external-conversation") hasExternalConversation = true;
                if (kind == "automation"
                    && item.TryGetProperty("id", out var automationId)
                    && automationId.ValueKind == JsonValueKind.String
                    && automationId.GetString() is { Length: > 0 } id)
                    automationIds.Add(id);
            }
            if (actorEntityId is null || beneficiaryId is null || hasExternalConversation)
                return null;
            if (hasPrivateDiscussion)
                return new LeafAgentCaller(actorEntityId, beneficiaryId)
                {
                    DiscussionId = discussionId,
                    AppId = appId,
                    ActorKind = actorKind,
                };
            return allowAuthorizedAutomation && automationIds.Count == 1
                ? new LeafAgentCaller(actorEntityId, beneficiaryId,
                    automationIds.Single())
                {
                    AppId = appId,
                    ActorKind = actorKind,
                }
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
                                   or KeyNotFoundException)
        {
            return null;
        }
    }
}

internal sealed record DiscordAgentCaller(
    string ActorEntityId,
    string BeneficiaryId,
    string BindingId,
    string SessionId);

internal sealed record DiscordGuildAgentScope(
    DiscordAgentCaller Caller,
    DiscordBinding Binding);

internal sealed record LeafAgentCaller(
    string ActorEntityId,
    string BeneficiaryId,
    string? AutomationId = null)
{
    /// <summary>The signed private discussion the call came from, when there is one.</summary>
    public string? DiscussionId { get; init; }

    /// <summary>The signed execution app and actor kind (only <c>nova</c> agents are read).</summary>
    public string AppId { get; init; } = "nova";
    public string ActorKind { get; init; } = "agent";
}
