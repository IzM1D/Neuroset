using System.Collections.Concurrent;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Security;

public sealed class PermissionService(
    IToolPermissionRepository repository,
    IPermissionPrompt prompt,
    ISettingsRepository settingsRepository) : IPermissionService
{
    private readonly ConcurrentDictionary<string, PermissionDecision> _sessionDecisions = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PermissionDecision> AuthorizeAsync(PermissionRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings.FullAutonomyEnabled)
            return PermissionDecision.AlwaysAllow;

        var chatKey = $"{request.ConversationId:N}:{request.ToolName}:{request.Risk}";
        if (_sessionDecisions.TryGetValue(chatKey, out var cached)) return cached;

        var persisted = await repository.FindAsync(request.ToolName, request.Risk, request.ConversationId, cancellationToken).ConfigureAwait(false);
        if (persisted?.Policy == PermissionPolicy.Allow) return PermissionDecision.AlwaysAllow;
        if (persisted?.Policy == PermissionPolicy.Deny) return PermissionDecision.Deny;

        if (persisted is null && DefaultPolicy(request.ToolName, request.Risk) == PermissionPolicy.Allow)
            return PermissionDecision.AlwaysAllow;

        var decision = await prompt.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (decision == PermissionDecision.AllowForChat) _sessionDecisions[chatKey] = decision;
        if (decision == PermissionDecision.AlwaysAllow)
        {
            await repository.SaveAsync(new ToolPermissionRule
            {
                ToolName = request.ToolName,
                Risk = request.Risk,
                Policy = PermissionPolicy.Allow
            }, cancellationToken).ConfigureAwait(false);
        }
        return decision;
    }

    public static PermissionPolicy DefaultPolicy(string toolName, ToolRisk risk)
    {
        // image_generation is separately gated by explicit per-message authorization in the runtime and executor.
        if (toolName is "web_search" or "fetch_url" or "memory_search" or "notify_user" or "image_generation") return PermissionPolicy.Allow;
        return risk is ToolRisk.Safe or ToolRisk.NetworkRead ? PermissionPolicy.Allow : PermissionPolicy.Ask;
    }
}

public sealed class DenyPermissionPrompt : IPermissionPrompt
{
    public ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(PermissionDecision.Deny);
}
