using Docker.DotNet;
using Docker.DotNet.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Thin seam over the Docker Engine operations the prefill daemon services perform. Extracted so the
/// container lifecycle (create / start / stop / kill / remove / inspect / list, volume + image + exec
/// operations) can be driven by a recording fake in tests, letting the startup reconcile orchestration
/// (cleanup -&gt; re-adopt -&gt; recreate) be exercised end-to-end without a live Docker daemon. Each
/// operation maps one-to-one onto the corresponding <c>DockerClient</c> call it replaced, so the
/// production adapter is a pure delegation with no behavior change. Docker.DotNet request/response
/// models are intentionally passed through unchanged - this is an extract-delegate seam, not a
/// domain-model abstraction.
/// </summary>
public interface IPrefillContainerGateway : IDisposable
{
    /// <summary>
    /// True once <see cref="Connect"/> has established an underlying client and it has not been dropped by
    /// <see cref="Reset"/>. Mirrors the previous <c>_dockerClient != null</c> availability check that gated
    /// every operation.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Creates the underlying Docker client for the given engine endpoint. Synchronous, matching the
    /// previous <c>new DockerClientConfiguration(uri).CreateClient()</c>; the connection is not verified
    /// here (callers probe with <see cref="GetVersionAsync"/>).
    /// </summary>
    void Connect(Uri dockerUri);

    /// <summary>
    /// Drops the underlying client so <see cref="IsAvailable"/> becomes false. Used on the connect-probe
    /// failure paths that previously set <c>_dockerClient = null</c>, disabling the feature for this run.
    /// </summary>
    void Reset();

    Task<VersionResponse> GetVersionAsync(CancellationToken cancellationToken);

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
