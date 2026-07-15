namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Creates a fresh <see cref="IPrefillContainerGateway"/> for each prefill daemon service. Every daemon
/// (Steam / Epic / Xbox / Battle.net / Riot) owns its own gateway instance and disposes it, preserving
/// the previous one-Docker-client-per-service ownership model rather than sharing a single client. A
/// single factory is registered in DI; tests substitute a factory that hands back a recording fake.
/// </summary>
public interface IPrefillContainerGatewayFactory
{
    IPrefillContainerGateway Create();
}
