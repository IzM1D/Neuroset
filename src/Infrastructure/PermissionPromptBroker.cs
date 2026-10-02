using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Infrastructure;

public sealed class PermissionPromptBroker : IPermissionPrompt
{
    public Func<PermissionRequest, CancellationToken, ValueTask<PermissionDecision>>? Handler { get; set; }
    public ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
        => Handler?.Invoke(request, cancellationToken) ?? ValueTask.FromResult(PermissionDecision.Deny);
}
