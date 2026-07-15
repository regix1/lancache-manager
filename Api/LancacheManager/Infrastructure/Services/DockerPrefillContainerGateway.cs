using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Production <see cref="IPrefillContainerGateway"/> backed by a Docker.DotNet <see cref="DockerClient"/>.
/// Every member delegates one-to-one to the exact <c>DockerClient</c> call the prefill daemon services
/// previously made inline, so this adapter carries no behavior of its own. The client is created lazily by
/// <see cref="Connect"/> (matching the previous startup flow) and dropped by <see cref="Reset"/> on a
/// failed connect probe.
/// </summary>
public sealed class DockerPrefillContainerGateway : IPrefillContainerGateway
{
    private DockerClient? _client;
    private bool _disposed;

    public bool IsAvailable => _client != null;

    public void Connect(Uri dockerUri)
    {
        _client = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    public void Reset()
    {
        _client = null;
    }

    private DockerClient Client => _client
        ?? throw new InvalidOperationException("Docker client is not connected. Check IsAvailable before invoking container operations.");

    public Task<VersionResponse> GetVersionAsync(CancellationToken cancellationToken)
        => Client.System.GetVersionAsync(cancellationToken);

    public Task<IList<ContainerListResponse>> ListContainersAsync(ContainersListParameters parameters, CancellationToken cancellationToken)
        => Client.Containers.ListContainersAsync(parameters, cancellationToken);

    public Task<CreateContainerResponse> CreateContainerAsync(CreateContainerParameters parameters, CancellationToken cancellationToken)
        => Client.Containers.CreateContainerAsync(parameters, cancellationToken);

    public Task<bool> StartContainerAsync(string id, ContainerStartParameters? parameters, CancellationToken cancellationToken)
        => Client.Containers.StartContainerAsync(id, parameters, cancellationToken);

    public Task<bool> StopContainerAsync(string id, ContainerStopParameters parameters, CancellationToken cancellationToken)
        => Client.Containers.StopContainerAsync(id, parameters, cancellationToken);

    public Task KillContainerAsync(string id, ContainerKillParameters parameters, CancellationToken cancellationToken)
        => Client.Containers.KillContainerAsync(id, parameters, cancellationToken);

    public Task RemoveContainerAsync(string id, ContainerRemoveParameters parameters, CancellationToken cancellationToken)
        => Client.Containers.RemoveContainerAsync(id, parameters, cancellationToken);

    public Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken)
        => Client.Containers.InspectContainerAsync(id, cancellationToken);

    public Task<MultiplexedStream> GetContainerLogsAsync(string id, bool tty, ContainerLogsParameters parameters, CancellationToken cancellationToken)
        => Client.Containers.GetContainerLogsAsync(id, tty, parameters, cancellationToken);

    public Task RemoveVolumeAsync(string name, bool force, CancellationToken cancellationToken)
        => Client.Volumes.RemoveAsync(name, force, cancellationToken);

    public Task CreateImageAsync(ImagesCreateParameters parameters, AuthConfig? authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken)
        => Client.Images.CreateImageAsync(parameters, authConfig, progress, cancellationToken);

    public Task<ImageInspectResponse> InspectImageAsync(string name, CancellationToken cancellationToken)
        => Client.Images.InspectImageAsync(name, cancellationToken);

    public Task<ContainerExecCreateResponse> ExecCreateContainerAsync(string id, ContainerExecCreateParameters parameters, CancellationToken cancellationToken)
        => Client.Exec.ExecCreateContainerAsync(id, parameters, cancellationToken);

    public Task<MultiplexedStream> StartAndAttachContainerExecAsync(string execId, bool tty, CancellationToken cancellationToken)
        => Client.Exec.StartAndAttachContainerExecAsync(execId, tty, cancellationToken);

    public Task<ContainerExecInspectResponse> InspectContainerExecAsync(string execId, CancellationToken cancellationToken)
        => Client.Exec.InspectContainerExecAsync(execId, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _client = null;
    }
}
