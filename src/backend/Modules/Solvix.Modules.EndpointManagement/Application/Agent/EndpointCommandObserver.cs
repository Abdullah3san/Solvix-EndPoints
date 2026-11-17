namespace Solvix.Modules.EndpointManagement.Application.Agent;

using Solvix.SharedKernel.Endpoints;

/// <summary>Hands finished patch, restart and configuration commands back to <see cref="IAgentPatchingService"/>.</summary>
internal sealed class EndpointCommandObserver(string prefix, IAgentPatchingService patching) : IRemoteCommandObserver
{
    public string CommandTypePrefix => prefix;

    public Task OnCommandFinishedAsync(RemoteCommandState state, CancellationToken cancellationToken) =>
        patching.OnCommandFinishedAsync(state, cancellationToken);
}
