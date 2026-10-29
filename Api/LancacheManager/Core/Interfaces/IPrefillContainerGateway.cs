using Docker.DotNet;
using Docker.DotNet.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Thin seam over the Docker Engine operations the prefill daemon services perform. Extracted so the
/// container lifecycle (create / start / stop / kill / remove / inspect / list, volume + image + exec
/// operations) can be driven by a recording fake in tests, letting the startup reconcile orchestration
/// (cleanup -&gt; re-adopt -&gt; recreate) be exercised end-to-end without a live Docker daemon. Each
/// operation maps one-to-one onto a <c>DockerClient</c> call. A call Docker does not answer in time
/// throws <see cref="DockerTimeoutException"/>; a cancel of the caller's own token still surfaces as a
/// cancel. Docker.DotNet request/response
/// models are intentionally passed through unchanged - this is an extract-delegate seam, not a
/// domain-model abstraction.
/// </summary>
public interface IPrefillContainerGateway : IDisposable
{
    /// <summary>
    /// True once <see cref="ConnectAsync"/> has connected a client whose version check answered. Every
    /// other operation needs it.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Creates a Docker client for the given engine endpoint and checks it with the version call. The
    /// client is kept, and <see cref="IsAvailable"/> turns true, only when the version answers; a failed
    /// check throws and leaves the gateway as it was.
    /// </summary>
    Task<VersionResponse> ConnectAsync(Uri dockerUri, CancellationToken cancellationToken);

    Task<IList<ContainerListResponse>> ListContainersAsync(ContainersListParameters parameters, CancellationToken cancellationToken);

    Task<CreateContainerResponse> CreateContainerAsync(CreateContainerParameters parameters, CancellationToken cancellationToken);

    Task<bool> StartContainerAsync(string id, ContainerStartParameters? parameters, CancellationToken cancellationToken);

    Task<bool> StopContainerAsync(string id, ContainerStopParameters parameters, CancellationToken cancellationToken);

    Task KillContainerAsync(string id, ContainerKillParameters parameters, CancellationToken cancellationToken);

    Task RemoveContainerAsync(string id, ContainerRemoveParameters parameters, CancellationToken cancellationToken);

    Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken);

    Task<MultiplexedStream> GetContainerLogsAsync(string id, bool tty, ContainerLogsParameters parameters, CancellationToken cancellationToken);

    Task RemoveVolumeAsync(string name, bool force, CancellationToken cancellationToken);

    Task CreateImageAsync(ImagesCreateParameters parameters, AuthConfig? authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken);

    Task<ImageInspectResponse> InspectImageAsync(string name, CancellationToken cancellationToken);

    Task<ContainerExecCreateResponse> ExecCreateContainerAsync(string id, ContainerExecCreateParameters parameters, CancellationToken cancellationToken);

    Task<MultiplexedStream> StartAndAttachContainerExecAsync(string execId, bool tty, CancellationToken cancellationToken);

    Task<ContainerExecInspectResponse> InspectContainerExecAsync(string execId, CancellationToken cancellationToken);
}

/// <summary>
/// A short Docker call that Docker did not answer within the gateway's limit. It says nothing about the
/// container the call named, so a caller never removes, erases or caches anything because of it; an HTTP
/// request answers it as Docker being unavailable.
/// </summary>
public sealed class DockerTimeoutException : TimeoutException
{
    public DockerTimeoutException(string message, Exception innerException) : base(message, innerException) { }
}
