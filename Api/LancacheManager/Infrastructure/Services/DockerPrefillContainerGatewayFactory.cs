using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Default <see cref="IPrefillContainerGatewayFactory"/> handing each prefill daemon service its own
/// <see cref="DockerPrefillContainerGateway"/> instance.
/// </summary>
public sealed class DockerPrefillContainerGatewayFactory : IPrefillContainerGatewayFactory
{
    public IPrefillContainerGateway Create() => new DockerPrefillContainerGateway();
}
