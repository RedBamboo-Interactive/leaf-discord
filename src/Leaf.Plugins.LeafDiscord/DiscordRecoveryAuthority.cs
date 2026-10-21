using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafDiscord;

/// <summary>
/// Vouches for a Discord binding when RedCompute recovers a Discord turn it had already
/// accepted, typically after a rebuild stopped the session process. Without this, the kernel
/// only trusts discussion origins and every Discord turn queued across a restart is stuck.
/// </summary>
/// <remarks>
/// Answers from the canonical binding and bot configuration, never from the request alone.
/// A <c>/clear</c> advances the generation and attaches a new session, so input queued for
/// the old session is refused rather than replayed into the wrong conversation.
/// </remarks>
public sealed class DiscordRecoveryAuthority(
    DiscordBindingStore bindings,
    DiscordBotConfigurationStore configurations,
    DiscordAgentResolver agents) : IExternalConversationRecoveryAuthority
{
    public string PluginId => LeafDiscordPlugin.PluginId;

    public bool Handles(string conversationId)
        => conversationId.StartsWith("discord:", StringComparison.Ordinal);

    public async Task<ExternalConversationRecoveryDecision> ValidateAsync(
        ExternalConversationRecoveryRequest request, CancellationToken ct = default)
    {
        var binding = await bindings.FindByBindingIdAsync(request.ConversationId, ct);
        if (binding is null)
            return ExternalConversationRecoveryDecision.Block("recovery_origin_missing");
        if (!binding.Enabled)
            return ExternalConversationRecoveryDecision.Block("recovery_origin_revoked");
        if (binding.Generation != request.Generation
            || !string.Equals(binding.SessionId, request.SessionId, StringComparison.Ordinal))
            return ExternalConversationRecoveryDecision.Block("recovery_origin_superseded");
        if (!string.Equals(binding.OwnerId, request.OwnerUserId, StringComparison.Ordinal))
            return ExternalConversationRecoveryDecision.Block("recovery_origin_authority_changed");
        var agent = await agents.ResolveAsync(binding.AgentId, ct);
        if (agent is null || !string.Equals(agent.Id.ToString(), request.AgentEntityId,
                StringComparison.OrdinalIgnoreCase))
            return ExternalConversationRecoveryDecision.Block("recovery_origin_authority_changed");

        var config = await configurations.FindByApplicationIdAsync(binding.ApplicationId, ct);
        if (config is null || !config.Enabled)
            return ExternalConversationRecoveryDecision.Block("recovery_origin_revoked");
        try
        {
            DiscordBotService.ValidateExistingBindingTarget(binding, config.DmAllowlist);
        }
        catch (UnauthorizedAccessException)
        {
            return ExternalConversationRecoveryDecision.Block("recovery_origin_revoked");
        }
        return ExternalConversationRecoveryDecision.Allow();
    }
}
