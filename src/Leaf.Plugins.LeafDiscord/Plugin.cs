using System.Reflection;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Leaf.Plugins.LeafDiscord;

public sealed class LeafDiscordPlugin : ILeafPlugin
{
    public const string PluginId = "leaf-discord";

    public PluginManifest Manifest { get; } = LoadManifest();

    private static PluginManifest LoadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.json")
            ?? throw new InvalidOperationException("Embedded plugin.json is missing");
        using var reader = new StreamReader(stream);
        return PluginManifest.Parse(reader.ReadToEnd());
    }

    public void ConfigureServices(IServiceCollection services, PluginContext ctx)
    {
        services.AddSingleton<PromptInjectionDetector>();
        services.AddSingleton<DiscordEgressGuard>();
        services.AddSingleton<DiscordTypingLeases>();
        services.AddSingleton(sp => new LeafDiscordConfigStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordBotConfigurationStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId),
            sp.GetRequiredService<LeafDiscordConfigStore>()));
        services.AddSingleton(sp => new DiscordBindingStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordBehaviorStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordDeliveryStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordCommonsConfigurationStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordCommonsIngestStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordCommonsHostTurnStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordCommonsMembershipSuspensionStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordGatewayInboxStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordCommonsRelayService(
            sp.GetRequiredService<DiscordCommonsConfigurationStore>(),
            sp.GetRequiredService<DiscordCommonsIngestStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DiscordCommonsRelayService>>()));
        services.AddSingleton<DiscordCommonsSecretInstaller>();
        services.AddSingleton(sp => new DiscordAgentResolver(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordRuntimeOptionsResolver(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordUserIdentityResolver(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DiscordUserIdentityResolver>>()));
        services.AddSingleton(sp => new DiscordOwnerPresencePublisher(
            sp.GetRequiredKeyedService<IPluginEvents>(PluginId),
            sp.GetRequiredService<DiscordBindingStore>(),
            sp.GetRequiredService<DiscordUserIdentityResolver>()));
        services.AddSingleton(sp => new DiscordReplySubscriptionStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new DiscordReplyNotifier(
            sp.GetRequiredService<DiscordReplySubscriptionStore>(),
            sp.GetRequiredKeyedService<IPluginEvents>(PluginId),
            sp.GetRequiredService<PromptInjectionDetector>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DiscordReplyNotifier>>()));
        services.AddSingleton(sp => new DiscordConversationLeadStore(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton<IExternalConversationRecoveryAuthority>(sp => new DiscordRecoveryAuthority(
            sp.GetRequiredService<DiscordBindingStore>(),
            sp.GetRequiredService<DiscordBotConfigurationStore>(),
            sp.GetRequiredService<DiscordAgentResolver>()));
        services.AddSingleton<DiscordConversationCoordinator>();
        services.AddSingleton<DiscordAttachmentIngestor>();
        services.AddSingleton<DiscordRestTransport>();
        services.AddSingleton<DiscordBotService>();
        services.AddSingleton<DiscordSettingsProvider>();
        services.AddSingleton<IPluginSettingsProvider>(sp =>
            sp.GetRequiredService<DiscordSettingsProvider>());
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            try
            {
                return await next(context);
            }
            catch (DiscordSentinelRefusedException ex)
            {
                // The calling session wrote the content; it gets what the Sentinel did and why.
                return Results.Json(new
                {
                    error = ex.Unavailable ? "sentinel_unavailable" : "sentinel_held",
                    message = ex.Message,
                    sentinel = ex.Review,
                }, statusCode: ex.Unavailable
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status422UnprocessableEntity);
            }
        });
        DiscordEndpoints.Map(group);
    }

    public async Task OnStartupAsync(IPluginHost host, CancellationToken ct)
    {
        await host.GetRequiredService<DiscordBotConfigurationStore>()
            .EnsureLegacyMigratedAsync(ct);
        var configurations = host.GetRequiredService<DiscordBotConfigurationStore>();
        var bindings = await host.GetRequiredService<DiscordBindingStore>().ListAsync(ct);
        var behaviors = host.GetRequiredService<DiscordBehaviorStore>();
        foreach (var config in await configurations.ListAsync(ct))
            await behaviors.EnsureDefaultsAsync(config, bindings, ct);
        var lifetime = host.GetRequiredService<IHostApplicationLifetime>();
        host.GetRequiredService<DiscordCommonsRelayService>()
            .Start(lifetime.ApplicationStopping);
        var bot = host.GetRequiredService<DiscordBotService>();
        _ = Task.Run(() => bot.StartAsync(lifetime.ApplicationStopping), CancellationToken.None);
    }
}
