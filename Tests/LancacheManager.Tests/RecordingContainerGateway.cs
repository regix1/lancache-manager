using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A single simulated Docker container in the <see cref="RecordingContainerGateway"/> inventory. Only the
/// fields the prefill daemon startup reconcile reads are modeled (name, running state, labels, env).
/// </summary>
internal sealed class FakeContainer
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public bool Running { get; set; }
    public Dictionary<string, string> Labels { get; init; } = new();
    public List<string> Env { get; init; } = new();

    public ContainerListResponse ToListResponse() => new()
    {
        ID = Id,
        Names = new List<string> { Name.StartsWith('/') ? Name : "/" + Name },
        Image = "test-image",
        State = Running ? "running" : "exited",
        Status = Running ? "Up 1 minute" : "Exited (0)",
        Labels = new Dictionary<string, string>(Labels)
    };

    public ContainerInspectResponse ToInspectResponse() => new()
    {
        ID = Id,
        Name = Name.StartsWith('/') ? Name : "/" + Name,
        State = new ContainerState { Running = Running, Status = Running ? "running" : "exited", ExitCode = 0 },
        Config = new Config { Env = new List<string>(Env), Labels = new Dictionary<string, string>(Labels) },
        HostConfig = new HostConfig(),
        NetworkSettings = new NetworkSettings()
    };
}

/// <summary>
/// In-memory <see cref="IPrefillContainerGateway"/> that simulates a Docker container inventory so the
/// prefill daemon startup reconcile (cleanup -&gt; re-adopt -&gt; recreate) can be driven end-to-end
/// without a live Docker daemon. Records every container operation (a docker spy) and supports
/// per-operation failure injection. Not thread-safe by design - the daemon drives it sequentially.
/// </summary>
internal sealed class RecordingContainerGateway : IPrefillContainerGateway
{
    private readonly List<FakeContainer> _containers = new();
    private Exception? _pendingCreateFailure;
    private Exception? _pendingRemoveFailure;

    public RecordingContainerGateway(bool available = true)
    {
        IsAvailable = available;
    }

    /// <summary>Ordered op log: e.g. "Create:steam-daemon-persistent", "Stop:{id}", "Remove:{id}".</summary>
    public List<string> Calls { get; } = new();

    public bool IsAvailable { get; private set; }
    public bool Disposed { get; private set; }

    public int CountOf(string opPrefix) => Calls.Count(c => c.StartsWith(opPrefix, StringComparison.Ordinal));
    public int DestructiveCallCount =>
        Calls.Count(c => c.StartsWith("Stop:", StringComparison.Ordinal)
                      || c.StartsWith("Kill:", StringComparison.Ordinal)
                      || c.StartsWith("Remove:", StringComparison.Ordinal)
                      || c.StartsWith("RemoveVolume:", StringComparison.Ordinal));

    public FakeContainer AddContainer(FakeContainer container)
    {
        _containers.Add(container);
        return container;
    }

    public bool ContainsContainer(string id) => _containers.Any(c => c.Id == id);
    public int ContainerCount => _containers.Count;
    public FakeContainer? FindByName(string name) =>
        _containers.FirstOrDefault(c => c.Name.TrimStart('/') == name.TrimStart('/'));

    /// <summary>Injects a failure on the next <see cref="CreateContainerAsync"/> call (cleared after it fires).</summary>
    public void FailNextCreateContainer(Exception failure) => _pendingCreateFailure = failure;

    /// <summary>Injects a failure on the next <see cref="RemoveContainerAsync"/> call (cleared after it fires). The
    /// op is still logged to <see cref="Calls"/> before it throws, matching a real Docker call that reached the daemon.</summary>
    public void FailNextRemoveContainer(Exception failure) => _pendingRemoveFailure = failure;

    public void Connect(Uri dockerUri) => IsAvailable = true;
    public void Reset() => IsAvailable = false;
    public void Dispose() => Disposed = true;

    public Task<Docker.DotNet.Models.VersionResponse> GetVersionAsync(CancellationToken cancellationToken)
        => Task.FromResult(new Docker.DotNet.Models.VersionResponse { Version = "test-engine" });

    public Task<IList<ContainerListResponse>> ListContainersAsync(ContainersListParameters parameters, CancellationToken cancellationToken)
    {
        Calls.Add("List");
        IEnumerable<FakeContainer> query = _containers;

        if (parameters.All == false)
        {
            query = query.Where(c => c.Running);
        }

        if (parameters.Filters != null)
        {
            if (parameters.Filters.TryGetValue("name", out var nameFilters) && nameFilters.Count > 0)
            {
                // Docker's name filter is a substring match.
                query = query.Where(c => nameFilters.Keys.Any(n => c.Name.Contains(n, StringComparison.Ordinal)));
            }

            if (parameters.Filters.TryGetValue("label", out var labelFilters) && labelFilters.Count > 0)
            {
                query = query.Where(c => labelFilters.Keys.All(l => MatchesLabel(c, l)));
            }
        }

        IList<ContainerListResponse> result = query.Select(c => c.ToListResponse()).ToList();
        return Task.FromResult(result);
    }

    private static bool MatchesLabel(FakeContainer container, string labelFilter)
    {
        var eq = labelFilter.IndexOf('=');
        if (eq < 0)
        {
            return container.Labels.ContainsKey(labelFilter);
        }

        var key = labelFilter[..eq];
        var value = labelFilter[(eq + 1)..];
        return container.Labels.TryGetValue(key, out var actual) && actual == value;
    }

    public Task<CreateContainerResponse> CreateContainerAsync(CreateContainerParameters parameters, CancellationToken cancellationToken)
    {
        Calls.Add($"Create:{parameters.Name}");

        if (_pendingCreateFailure != null)
        {
            var failure = _pendingCreateFailure;
            _pendingCreateFailure = null;
            throw failure;
        }

        var id = Guid.NewGuid().ToString("N");
        _containers.Add(new FakeContainer
        {
            Id = id,
            Name = parameters.Name ?? id,
            Running = false,
            Labels = parameters.Labels != null ? new Dictionary<string, string>(parameters.Labels) : new(),
            Env = parameters.Env != null ? new List<string>(parameters.Env) : new()
        });

        return Task.FromResult(new CreateContainerResponse { ID = id });
    }

    public Task<bool> StartContainerAsync(string id, ContainerStartParameters? parameters, CancellationToken cancellationToken)
    {
        Calls.Add($"Start:{id}");
        var container = _containers.FirstOrDefault(c => c.Id == id);
        if (container != null)
        {
            container.Running = true;
        }

        return Task.FromResult(true);
    }

    public Task<bool> StopContainerAsync(string id, ContainerStopParameters parameters, CancellationToken cancellationToken)
    {
        Calls.Add($"Stop:{id}");
        var container = _containers.FirstOrDefault(c => c.Id == id);
        if (container != null)
        {
            container.Running = false;
        }

        return Task.FromResult(true);
    }

    public Task KillContainerAsync(string id, ContainerKillParameters parameters, CancellationToken cancellationToken)
    {
        Calls.Add($"Kill:{id}");
        var container = _containers.FirstOrDefault(c => c.Id == id);
        if (container != null)
        {
            container.Running = false;
        }

        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(string id, ContainerRemoveParameters parameters, CancellationToken cancellationToken)
    {
        Calls.Add($"Remove:{id}");
        if (_pendingRemoveFailure != null)
        {
            var failure = _pendingRemoveFailure;
            _pendingRemoveFailure = null;
            return Task.FromException(failure);
        }
        _containers.RemoveAll(c => c.Id == id);
        return Task.CompletedTask;
    }

    public Task<ContainerInspectResponse> InspectContainerAsync(string id, CancellationToken cancellationToken)
    {
        Calls.Add($"Inspect:{id}");
        var container = _containers.FirstOrDefault(c => c.Id == id)
            ?? throw new DockerContainerNotFoundException(System.Net.HttpStatusCode.NotFound, $"No such container: {id}");
        return Task.FromResult(container.ToInspectResponse());
    }

    public Task<MultiplexedStream> GetContainerLogsAsync(string id, bool tty, ContainerLogsParameters parameters, CancellationToken cancellationToken)
    {
        // Only reached on a socket-connect failure diagnostic path, which the orchestration fakes never hit.
        Calls.Add($"Logs:{id}");
        throw new NotSupportedException("Container log streaming is not simulated by the recording gateway.");
    }

    public Task RemoveVolumeAsync(string name, bool force, CancellationToken cancellationToken)
    {
        Calls.Add($"RemoveVolume:{name}");
        return Task.CompletedTask;
    }

    public Task CreateImageAsync(ImagesCreateParameters parameters, AuthConfig? authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken)
    {
        // Simulates a successful image pull.
        Calls.Add("CreateImage");
        return Task.CompletedTask;
    }

    public Task<ImageInspectResponse> InspectImageAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add($"InspectImage:{name}");
        return Task.FromResult(new ImageInspectResponse { ID = "sha256:testimageid0000" });
    }

    public Task<ContainerExecCreateResponse> ExecCreateContainerAsync(string id, ContainerExecCreateParameters parameters, CancellationToken cancellationToken)
        => throw new NotSupportedException("Container exec is not simulated by the recording gateway.");

    public Task<MultiplexedStream> StartAndAttachContainerExecAsync(string execId, bool tty, CancellationToken cancellationToken)
        => throw new NotSupportedException("Container exec is not simulated by the recording gateway.");

    public Task<ContainerExecInspectResponse> InspectContainerExecAsync(string execId, CancellationToken cancellationToken)
        => throw new NotSupportedException("Container exec is not simulated by the recording gateway.");
}

/// <summary>
/// <see cref="IPrefillContainerGatewayFactory"/> that always hands back one specific gateway instance, so a
/// test can seed and assert against it directly.
/// </summary>
internal sealed class SingleContainerGatewayFactory : IPrefillContainerGatewayFactory
{
    private readonly IPrefillContainerGateway _gateway;

    public SingleContainerGatewayFactory(IPrefillContainerGateway gateway)
    {
        _gateway = gateway;
    }

    public IPrefillContainerGateway Create() => _gateway;
}

/// <summary>
/// <see cref="IPrefillContainerGatewayFactory"/> that hands back a gateway reporting Docker as
/// unavailable, so a daemon constructed with it behaves exactly like the previous
/// <c>_dockerClient == null</c> state (every container operation is skipped by its availability guard).
/// The default for daemon-service tests that never drive a container operation.
/// </summary>
internal sealed class UnavailableContainerGatewayFactory : IPrefillContainerGatewayFactory
{
    public IPrefillContainerGateway Create() => new RecordingContainerGateway(available: false);
}

/// <summary>
/// Fake <see cref="IDaemonClient"/> used as the reconnect/create-path client in the orchestration tests.
/// It "connects" successfully so <see cref="PrefillDaemonServiceBase.ConnectAndRegisterSessionAsync"/>
/// can register/reactivate the session, records logout attempts (to prove re-adopt leaves the login
/// intact), and fails loudly on any member a startup-reconcile flow should never touch.
/// </summary>
internal sealed class FakeReconnectDaemonClient : IDaemonClient
{
    public event Func<CredentialChallenge, Task>? OnCredentialChallenge { add { } remove { } }
    public event Func<DaemonStatus, Task>? OnStatusUpdate { add { } remove { } }
    public event Func<SocketPrefillProgress, Task>? OnProgressUpdate { add { } remove { } }
    public event Func<string, Task>? OnError { add { } remove { } }
    public event Func<Task>? OnDisconnected { add { } remove { } }

    public bool Connected { get; private set; }
    public bool Disposed { get; private set; }
    public int LogoutCount { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    // The reconnect flow reconciles once with live status; returning null leaves the conservative default.
    public Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<DaemonStatus?>(null);

    public Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
    {
        LogoutCount++;
        return Task.FromResult(true);
    }

    public void Dispose() => Disposed = true;

    public Task<CommandResponse> SendCommandAsync(string type, Dictionary<string, string>? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<CredentialChallenge?> GetAutoLoginChallengeAsync(string sessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<bool> ProvideAutoLoginAsync(string sessionId, string username, string refreshToken, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<bool> ProvideEpicAutoLoginAsync(string sessionId, string refreshToken, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<bool> ProvideXboxAutoLoginAsync(string sessionId, string refreshToken, string deviceKeyPkcs8, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task CancelLoginAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task CancelPrefillAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<List<CdnInfo>> GetCdnInfoAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task SetSelectedAppsAsync(List<string> appIds, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<PrefillResult> PrefillAsync(bool all = false, bool recent = false, bool recentlyPurchased = false, int? top = null, bool force = false, List<string>? operatingSystems = null, int? maxConcurrency = null, List<CachedDepotInput>? cachedDepots = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<CacheStatusResult> CheckCacheStatusAsync(List<CachedDepotInput> cachedDepots, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public void ClearPendingChallenges() { }
}
