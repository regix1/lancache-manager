using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Production <see cref="IPrefillContainerGateway"/> backed by a Docker.DotNet <see cref="DockerClient"/>.
/// Every member delegates one-to-one to the matching <c>DockerClient</c> call. Short calls, including the
/// open step of logs and exec, fail with <see cref="DockerTimeoutException"/> after <c>_shortCallLimit</c>
/// instead of Docker.DotNet's 100-second default; a pull fails after <c>_pullSilenceLimit</c> without a
/// progress message. A cancel of the caller's own token stays a cancel. <see cref="ConnectAsync"/> keeps a
/// client only after its version call answers.
/// </summary>
public sealed class DockerPrefillContainerGateway : IPrefillContainerGateway
{
    private static readonly TimeSpan _shortCallLimit = TimeSpan.FromSeconds(10);

    // Docker sends no pull message while it resolves the manifest or waits on a slow registry or disk, and
    // nothing promises a message every 10 seconds, so a pull ends only after a minute without one.
    private static readonly TimeSpan _pullSilenceLimit = TimeSpan.FromSeconds(60);

    private DockerClient? _client;
    private bool _disposed;

    public bool IsAvailable => _client != null;

    public async Task<VersionResponse> ConnectAsync(Uri dockerUri, CancellationToken cancellationToken)
    {
        var client = new DockerClientConfiguration(dockerUri).CreateClient();
        var version = await WithinLimitAsync(limitedToken => client.System.GetVersionAsync(limitedToken), cancellationToken);
        _client = client;
        return version;
    }

    private DockerClient Client => _client
        ?? throw new InvalidOperationException("Docker client is not connected. Check IsAvailable before invoking container operations.");

    private static async Task<T> WithinLimitAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_shortCallLimit);
        try
        {
            return await call(limit.Token);
        }
        catch (OperationCanceledException ex) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DockerTimeoutException($"Docker did not answer within {_shortCallLimit.TotalSeconds:0} seconds.", ex);
        }
    }

    private static async Task WithinLimitAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_shortCallLimit);
        try
        {
            await call(limit.Token);
        }
        catch (OperationCanceledException ex) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DockerTimeoutException($"Docker did not answer within {_shortCallLimit.TotalSeconds:0} seconds.", ex);
        }
    }

    public Task<IList<ContainerListResponse>> ListContainersAsync(ContainersListParameters parameters, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Containers.ListContainersAsync(parameters, limitedToken), cancellationToken);

    public Task<CreateContainerResponse> CreateContainerAsync(CreateContainerParameters parameters, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Containers.CreateContainerAsync(parameters, limitedToken), cancellationToken);

    public Task<bool> StartContainerAsync(string id, ContainerStartParameters? parameters, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Containers.StartContainerAsync(id, parameters, limitedToken), cancellationToken);

    public Task<bool> StopContainerAsync(string id, ContainerStopParameters parameters, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Containers.StopContainerAsync(id, parameters, limitedToken), cancellationToken);

    public Task KillContainerAsync(string id, ContainerKillParameters parameters, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Containers.KillContainerAsync(id, parameters, limitedToken), cancellationToken);

    public Task RemoveContainerAsync(string id, ContainerRemoveParameters parameters, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Containers.RemoveContainerAsync(id, parameters, limitedToken), cancellationToken);

    public Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Containers.InspectContainerAsync(id, limitedToken), cancellationToken);

    public Task<MultiplexedStream> GetContainerLogsAsync(string id, bool tty, ContainerLogsParameters parameters, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Containers.GetContainerLogsAsync(id, tty, parameters, limitedToken), cancellationToken);

    public Task RemoveVolumeAsync(string name, bool force, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Volumes.RemoveAsync(name, force, limitedToken), cancellationToken);

    public async Task CreateImageAsync(ImagesCreateParameters parameters, AuthConfig? authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken)
    {
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        silence.CancelAfter(_pullSilenceLimit);
        try
        {
            await Client.Images.CreateImageAsync(parameters, authConfig, new PullProgress(silence, progress), silence.Token);
        }
        catch (OperationCanceledException ex) when (silence.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DockerTimeoutException($"Docker reported no pull progress for {_pullSilenceLimit.TotalSeconds:0} seconds.", ex);
        }
    }

    // Docker.DotNet reports each pull message from its own read loop, so the limit restarts before the next
    // read and no report arrives after the pull has ended.
    private sealed class PullProgress(CancellationTokenSource silence, IProgress<JSONMessage> progress) : IProgress<JSONMessage>
    {
        public void Report(JSONMessage value)
        {
            silence.CancelAfter(_pullSilenceLimit);
            progress.Report(value);
        }
    }

    public Task<ImageInspectResponse> InspectImageAsync(string name, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Images.InspectImageAsync(name, limitedToken), cancellationToken);

    public Task<ContainerExecCreateResponse> ExecCreateContainerAsync(string id, ContainerExecCreateParameters parameters, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Exec.ExecCreateContainerAsync(id, parameters, limitedToken), cancellationToken);

    public Task<MultiplexedStream> StartAndAttachContainerExecAsync(string execId, bool tty, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Exec.StartAndAttachContainerExecAsync(execId, tty, limitedToken), cancellationToken);

    public Task<ContainerExecInspectResponse> InspectContainerExecAsync(string execId, CancellationToken cancellationToken)
        => WithinLimitAsync(limitedToken => Client.Exec.InspectContainerExecAsync(execId, limitedToken), cancellationToken);

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
