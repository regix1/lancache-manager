using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;

namespace LancacheManager.Infrastructure.Services;

internal sealed class NginxReopenHintJsonConverter : JsonStringEnumConverter<NginxReopenHint>
{
    public NginxReopenHintJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

internal sealed class NginxReopenRequirementJsonConverter : JsonStringEnumConverter<NginxReopenRequirement>
{
    public NginxReopenRequirementJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

internal sealed class NginxReopenStatusJsonConverter : JsonStringEnumConverter<NginxReopenStatus>
{
    public NginxReopenStatusJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Service to signal nginx to reopen log files after log manipulation operations
/// This prevents containerized and bare-metal nginx from losing access to rewritten logs
/// </summary>
public class NginxLogRotationService
{
    private static readonly TimeSpan _bareMetalWarningThrottle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _availabilityCacheTtl = TimeSpan.FromSeconds(30);
    // One docker exec ... kill -USR1 normally returns within a second; 30 s lets a slow daemon
    // finish and stops a hung one from holding the log lock and every job queued behind it.
    private static readonly TimeSpan _writerSignalTimeout = TimeSpan.FromSeconds(30);

    private const string HostPidVisibleMarker = "nginx-pid-visible";
    private const int HostPidNotVisibleExitCode = 3;
    private const string NoNginxContainerFoundError = "No container with nginx found";
    private const string WindowsDockerUnsupportedMessage =
        "This manager cannot safely change logs held by a Docker writer while running natively on Windows. Run the manager in a Linux environment with the same log mounts, or use a static log source.";
    private const string HostNginxPidExpression =
        "$(cat /run/nginx.pid 2>/dev/null || cat /var/run/nginx.pid 2>/dev/null || " +
        "pgrep -f 'nginx[:] master' | head -1)";

    private readonly ILogger<NginxLogRotationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ProcessManager _processManager;
    private readonly IPathResolver _pathResolver;
    private readonly TimeProvider _timeProvider;
    private readonly object _bareMetalWarningLock = new();
    private readonly object _availabilityCacheLock = new();
    private readonly SemaphoreSlim _availabilityProbeLock = new(1, 1);
    private DateTimeOffset? _lastBareMetalWarning;
    private AvailabilityCacheEntry? _availability;

    protected virtual bool CanProbeHostWriters => OperatingSystem.IsLinux();
    protected virtual bool CanReplaceDockerLogs => !OperatingSystem.IsWindows();

    public NginxLogRotationService(
        ILogger<NginxLogRotationService> logger,
        IConfiguration configuration,
        ProcessManager processManager,
        IPathResolver pathResolver)
        : this(logger, configuration, processManager, pathResolver, TimeProvider.System)
    {
    }

    internal NginxLogRotationService(
        ILogger<NginxLogRotationService> logger,
        IConfiguration configuration,
        ProcessManager processManager,
        IPathResolver pathResolver,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _configuration = configuration;
        _processManager = processManager;
        _pathResolver = pathResolver;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns whether either reopen path is currently usable, checking the container path first
    /// and then the host signal path. The combined result is cached briefly because probing may
    /// spawn processes.
    /// </summary>
    /// <returns>
    /// False both when neither reopen path is usable and when the probe itself failed. Both mean
    /// we must not promise a reopen, so the caller takes the same branch.
    /// </returns>
    public async Task<bool> CanReopenNginxAsync()
    {
        try
        {
            return (await GetCachedAvailabilityAsync()).Available;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nginx reopen availability check failed");
            return false;
        }
    }

    /// <summary>
    /// Returns nginx reopen availability plus the detected remedy for a datasource layout.
    /// Probe evidence is shared with <see cref="CanReopenNginxAsync"/> and cached for the same TTL.
    /// </summary>
    public async Task<NginxReopenAvailability> GetNginxReopenAvailabilityAsync(string? datasourceLayout)
    {
        try
        {
            var detection = await GetCachedAvailabilityAsync();
            if (detection.Available)
            {
                return new NginxReopenAvailability(
                    true,
                    NginxReopenHint.None,
                    NginxReopenRequirement.Required,
                    NginxWriterProbe.CanCheckOnAction);
            }

            return new NginxReopenAvailability(
                false,
                detection.DetectedHint ?? GetLayoutFallbackHint(datasourceLayout),
                NginxReopenRequirement.Unknown,
                NginxWriterProbe.CanCheckOnAction);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nginx reopen availability check failed");
            return new NginxReopenAvailability(
                false,
                GetLayoutFallbackHint(datasourceLayout),
                NginxReopenRequirement.Unknown,
                NginxWriterProbe.CanCheckOnAction);
        }
    }

    /// <summary>
    /// Returns non-exclusive advisory evidence for one actual datasource. This method never opens
    /// deny-write handles, acquires leases, signals a writer, or waits for a writer to release a file.
    /// </summary>
    public async Task<NginxReopenAvailability> GetNginxReopenAvailabilityAsync(
        ResolvedDatasource datasource)
    {
        try
        {
            var paths = GetAffectedLogPaths(datasource);
            if (paths.Count > 0)
            {
                var writers = (await ResolveWritersAsync(paths, CancellationToken.None))
                    .SelectMany(pair => pair.Value)
                    .DistinctBy(writer => (writer.Kind, writer.Name, writer.ProcessId, writer.StartIdentity))
                    .ToList();
                if (writers.Any(writer => writer.Kind == NginxWriterKind.Docker) &&
                    !CanReplaceDockerLogs)
                {
                    return new NginxReopenAvailability(
                        false,
                        NginxReopenHint.UseLinuxManager,
                        NginxReopenRequirement.Required,
                        CheckOnAction: false);
                }
                if (writers.Count > 0)
                {
                    return new NginxReopenAvailability(
                        true,
                        NginxReopenHint.None,
                        NginxReopenRequirement.Required,
                        NginxWriterProbe.CanCheckOnAction);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "Datasource nginx writer advisory could not be resolved");
        }

        var detection = await GetCachedAvailabilityAsync();
        if (detection.Available)
        {
            return new NginxReopenAvailability(
                true,
                NginxReopenHint.None,
                NginxReopenRequirement.Required,
                NginxWriterProbe.CanCheckOnAction);
        }

        var checkOnAction = NginxWriterProbe.CanCheckOnAction && Directory.Exists(datasource.LogPath);
        return new NginxReopenAvailability(
            false,
            checkOnAction ? NginxReopenHint.None : detection.DetectedHint ?? NginxReopenHint.MountDockerSocket,
            NginxReopenRequirement.Unknown,
            checkOnAction);
    }

    private async Task<AvailabilityDetection> GetCachedAvailabilityAsync()
    {
        if (TryGetCachedAvailability(out var cached))
        {
            return cached;
        }

        await _availabilityProbeLock.WaitAsync();
        try
        {
            if (TryGetCachedAvailability(out cached))
            {
                return cached;
            }

            var detection = AvailabilityDetection.Unavailable();
            try
            {
                if (_configuration.GetValue<bool>("NginxLogRotation:Enabled", false))
                {
                    detection = await DetectAvailabilityAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nginx reopen availability detection failed");
            }

            lock (_availabilityCacheLock)
            {
                _availability = new AvailabilityCacheEntry(detection, _timeProvider.GetUtcNow());
            }

            return detection;
        }
        finally
        {
            _availabilityProbeLock.Release();
        }
    }

    private bool TryGetCachedAvailability(out AvailabilityDetection detection)
    {
        lock (_availabilityCacheLock)
        {
            if (_availability is not null &&
                _timeProvider.GetUtcNow() - _availability.CheckedAt < _availabilityCacheTtl)
            {
                detection = _availability.Detection;
                return true;
            }
        }

        detection = AvailabilityDetection.Unavailable();
        return false;
    }

    private async Task<AvailabilityDetection> DetectAvailabilityAsync()
    {
        DockerProbeResult dockerProbe;
        try
        {
            dockerProbe = await ProbeDockerReopenAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nginx reopen availability probe failed for Docker");
            dockerProbe = DockerProbeResult.Unknown;
        }

        if (dockerProbe.Available)
        {
            return AvailabilityDetection.AvailableResult;
        }

        HostProbeResult hostProbe;
        try
        {
            hostProbe = await ProbeHostSignalAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nginx reopen availability probe failed for host nginx");
            hostProbe = HostProbeResult.Unknown;
        }

        if (hostProbe.Available)
        {
            return AvailabilityDetection.AvailableResult;
        }

        if (hostProbe.Failure == HostProbeFailure.SignalDenied)
        {
            return AvailabilityDetection.Unavailable(NginxReopenHint.GrantSignalPrivilege);
        }

        if (dockerProbe.Failure == DockerProbeFailure.NoNginxContainer &&
            hostProbe.Failure == HostProbeFailure.PidNotVisible)
        {
            return AvailabilityDetection.Unavailable(NginxReopenHint.EnablePidHost);
        }

        return AvailabilityDetection.Unavailable();
    }

    private async Task<DockerProbeResult> ProbeDockerReopenAsync()
    {
        if (!_pathResolver.IsDockerSocketAvailable())
        {
            return new DockerProbeResult(false, DockerProbeFailure.SocketMissing);
        }

        var configuredName = _configuration.GetValue<string>("NginxLogRotation:ContainerName");
        if (!string.IsNullOrWhiteSpace(configuredName) &&
            !string.Equals(configuredName, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return await ContainerHasNginxAsync(configuredName, CancellationToken.None) == true
                ? DockerProbeResult.AvailableResult
                : DockerProbeResult.Unknown;
        }

        var (containerName, error) = await FindMonolithicContainerAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            // A detected container counts as available even when its nginx check did not answer (a sole LANCache-named
            // container stays the target for that reason); the reopen itself then names the timeout. A configured container in
            // that state answers Unknown above, which the page also offers as check-on-action.
            return DockerProbeResult.AvailableResult;
        }

        if (string.Equals(error, NoNginxContainerFoundError, StringComparison.Ordinal))
        {
            return new DockerProbeResult(false, DockerProbeFailure.NoNginxContainer);
        }

        if (error?.Contains("Docker socket", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new DockerProbeResult(false, DockerProbeFailure.SocketMissing);
        }

        return DockerProbeResult.Unknown;
    }

    private async Task<HostProbeResult> ProbeHostSignalAsync()
    {
        var result = await RunProcessAsync(
            CreateHostProbeStartInfo(),
            "host nginx signal probe");
        if (result.ExitCode == 0)
        {
            return HostProbeResult.AvailableResult;
        }

        if (result.ExitCode == HostPidNotVisibleExitCode ||
            string.IsNullOrWhiteSpace(result.Error) ||
            result.Error.Contains("no process", StringComparison.OrdinalIgnoreCase) ||
            result.Error.Contains("No such process", StringComparison.OrdinalIgnoreCase))
        {
            return new HostProbeResult(false, HostProbeFailure.PidNotVisible);
        }

        if (IndicatesSignalPermissionDenied(result.Error))
        {
            return new HostProbeResult(false, HostProbeFailure.SignalDenied);
        }

        return HostProbeResult.Unknown;
    }

    private static bool IndicatesSignalPermissionDenied(string error) =>
        error.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);

    private static NginxReopenHint GetLayoutFallbackHint(string? datasourceLayout) =>
        datasourceLayout is LogSourceLayout.LayoutBareMetal or LogSourceLayout.LayoutMixed
            ? NginxReopenHint.EnablePidHost
            : NginxReopenHint.MountDockerSocket;

    public static IReadOnlyList<string> GetAffectedLogPaths(ResolvedDatasource datasource)
    {
        datasource.RefreshLogSources();
        if (!Directory.Exists(datasource.LogPath))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(datasource.LogPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => LogSourceLayout.LogicalStem(Path.GetFileName(path)) is not null)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<NginxReopenCheck> PrepareReopenCheckAsync(
        IReadOnlyList<ResolvedDatasource> datasources,
        IReadOnlyList<string> affectedPaths,
        bool expectsPublication,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalNames = datasources.Select(datasource => datasource.Name).Distinct(
            StringComparer.OrdinalIgnoreCase).ToList();
        var existingPaths = affectedPaths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (existingPaths.Count == 0 && !expectsPublication)
        {
            return new NginxReopenCheck(
                canonicalNames,
                existingPaths,
                new Dictionary<string, NginxFileIdentity>(),
                Array.Empty<NginxWriterIdentity>(),
                NginxReopenRequirement.NotRequired,
                Array.Empty<NginxHeldProof>(),
                null,
                null,
                expectsPublication);
        }

        // With no log file there is no writer to find. A child that publishes still gets a check, one that
        // binds no file, so it refuses any log file that appears before it scans the folder.
        var writersByPath = existingPaths.Count == 0
            ? new Dictionary<string, IReadOnlyList<NginxWriterIdentity>>()
            : await ResolveWritersAsync(existingPaths, cancellationToken);
        var writers = existingPaths
            .SelectMany(path => writersByPath[path])
            .DistinctBy(writer => (writer.Kind, writer.Name, writer.ProcessId, writer.StartIdentity))
            .ToList();
        if (!CanReplaceDockerLogs && writers.Any(writer => writer.Kind == NginxWriterKind.Docker))
        {
            throw new ValidationException(WindowsDockerUnsupportedMessage)
            {
                StageKey = "management.nginxReopen.windowsDockerUnsupported"
            };
        }
        IReadOnlyList<NginxHeldProof> proofs = Array.Empty<NginxHeldProof>();
        var uncoveredPaths = existingPaths
            .Where(path => writersByPath[path].Count == 0)
            .ToList();
        if (uncoveredPaths.Count > 0 &&
            !NginxWriterProbe.TryAcquire(uncoveredPaths, out proofs, out var proofError))
        {
            throw new InvalidOperationException(
                $"Could not prove that the selected logs have no active writer: {proofError}");
        }

        string? checkPath = null;
        string? resultPath = null;
        try
        {
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var proofIdentities = proofs.ToDictionary(
                proof => proof.Path,
                proof => proof.Identity,
                pathComparer);
            var identities = existingPaths.ToDictionary(
                path => path,
                path => proofIdentities.TryGetValue(path, out var identity)
                    ? identity
                    : NginxWriterProbe.ReadIdentity(path),
                pathComparer);
            var operationsDirectory = _pathResolver.GetOperationsDirectory();
            Directory.CreateDirectory(operationsDirectory);
            var token = Guid.NewGuid().ToString("N");
            checkPath = Path.Combine(operationsDirectory, $"nginx_log_check_{token}.json");
            resultPath = Path.Combine(operationsDirectory, $"nginx_log_result_{token}.json");
            await WritePublicationCheckAsync(checkPath, valid: true, identities, cancellationToken);

            return new NginxReopenCheck(
                canonicalNames,
                existingPaths,
                identities,
                writers,
                writers.Count > 0 ? NginxReopenRequirement.Required : NginxReopenRequirement.NotRequired,
                proofs,
                checkPath,
                resultPath,
                expectsPublication);
        }
        catch
        {
            foreach (var proof in proofs)
            {
                proof.Dispose();
            }
            foreach (var path in new[]
            {
                checkPath,
                checkPath is null ? null : checkPath + ".new",
                resultPath,
                resultPath is null ? null : resultPath + ".new"
            })
            {
                if (!string.IsNullOrEmpty(path))
                {
                    File.Delete(path);
                }
            }
            throw;
        }
    }

    public void ValidateReopenCheck(NginxReopenCheck check)
    {
        foreach (var proof in check.Proofs)
        {
            if (!proof.Validate())
            {
                throw new InvalidOperationException($"The held no-writer proof is no longer valid for '{proof.Path}'");
            }
        }
        foreach (var pair in check.OriginalIdentities)
        {
            if (!File.Exists(pair.Key) || NginxWriterProbe.ReadIdentity(pair.Key) != pair.Value)
            {
                throw new InvalidOperationException($"The selected log identity changed before launch: '{pair.Key}'");
            }
        }
    }

    public static void AttachPublicationCheck(NginxReopenCheck check, ProcessStartInfo process)
    {
        if (check.CheckPath is null || check.ResultPath is null)
        {
            return;
        }

        process.Environment["LANCACHE_LOG_CHECK"] = check.CheckPath;
        process.Environment["LANCACHE_LOG_RESULT"] = check.ResultPath;
    }

    public async Task InvalidateReopenCheckAsync(
        NginxReopenCheck check,
        CancellationToken cancellationToken = default)
    {
        if (check.CheckPath is not null)
        {
            await WritePublicationCheckAsync(
                check.CheckPath,
                valid: false,
                check.OriginalIdentities,
                cancellationToken);
        }
    }

    public async Task<LogRotationResult> CompleteReopenCheckAsync(
        NginxReopenCheck check,
        bool physicalChange,
        CancellationToken cancellationToken = default)
    {
        if (!physicalChange && check.Requirement == NginxReopenRequirement.NotRequired)
        {
            return LogRotationResult.NotRequired();
        }

        string? completionError = null;
        if (physicalChange && check.Proofs.Any(proof => !proof.Validate()))
        {
            completionError = "The no-writer proof changed while the logs were being updated";
        }

        if (physicalChange && completionError is null && check.ExpectsPublication)
        {
            try
            {
                var publication = await ReadPublicationResultAsync(check, cancellationToken);
                if (!publication.Success)
                {
                    completionError = "The log child reported an incomplete publication";
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                completionError = error.Message;
            }
        }

        if (check.Requirement == NginxReopenRequirement.NotRequired)
        {
            return completionError is null
                ? LogRotationResult.NotRequired()
                : LogRotationResult.Failed(
                    completionError,
                    NginxReopenRequirement.NotRequired,
                    partialPhysicalEffects: physicalChange);
        }

        foreach (var writer in check.Writers)
        {
            using var signalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            signalTimeout.CancelAfter(_writerSignalTimeout);
            ProcessCommandResult signal;
            try
            {
                signal = await SignalWriterAsync(writer, signalTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return LogRotationResult.Failed(
                    $"Failed to reopen nginx writer '{writer.Name}' within {_writerSignalTimeout.TotalSeconds:0} seconds",
                    NginxReopenRequirement.Required,
                    partialPhysicalEffects: physicalChange);
            }
            if (signal.ExitCode != 0)
            {
                return LogRotationResult.Failed(
                    GetSignalError(writer, signal),
                    NginxReopenRequirement.Required,
                    partialPhysicalEffects: physicalChange);
            }
        }

        return completionError is null
            ? LogRotationResult.Succeeded()
            : LogRotationResult.Failed(
                completionError,
                NginxReopenRequirement.Required,
                partialPhysicalEffects: physicalChange);
    }

    private async Task WritePublicationCheckAsync(
        string checkPath,
        bool valid,
        IReadOnlyDictionary<string, NginxFileIdentity> identities,
        CancellationToken cancellationToken)
    {
        var check = new NginxPublicationCheckFile(
            valid,
            identities.Select(pair => new NginxPublicationExpectation(pair.Key, pair.Value)).ToList());
        var temporaryPath = checkPath + ".new";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(check, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            cancellationToken);
        File.Move(temporaryPath, checkPath, overwrite: true);
    }

    internal static async Task<NginxPublicationResult> ReadPublicationResultAsync(
        NginxReopenCheck check,
        CancellationToken cancellationToken)
    {
        if (check.ResultPath is null || !File.Exists(check.ResultPath))
        {
            throw new InvalidDataException("The log child did not publish its identity result");
        }

        await using var stream = File.OpenRead(check.ResultPath);
        var result = await JsonSerializer.DeserializeAsync<NginxPublicationResult>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken) ?? throw new InvalidDataException("The log child result was empty");
        if (result.Files.Count != check.AffectedPaths.Count)
        {
            throw new InvalidDataException("The log child result did not cover every selected file");
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var records = result.Files.ToDictionary(record => Path.GetFullPath(record.TargetPath), comparer);
        foreach (var path in check.AffectedPaths)
        {
            if (!records.TryGetValue(path, out var record) ||
                !check.OriginalIdentities.TryGetValue(path, out var original) ||
                record.OriginalIdentity != original)
            {
                throw new InvalidDataException($"The log child result did not match '{path}'");
            }

            if (record.Changed && !record.Deleted &&
                (record.TemporaryIdentity is null ||
                 record.PublishedIdentity != record.TemporaryIdentity ||
                 record.PublishedIdentity == original))
            {
                throw new InvalidDataException($"The log child reported an invalid publication for '{path}'");
            }
            if (!record.Changed && record.PublishedIdentity != original)
            {
                throw new InvalidDataException($"The unchanged log identity did not match '{path}'");
            }
            if (record.Deleted && File.Exists(path))
            {
                throw new InvalidDataException($"The deleted log still exists at '{path}'");
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<NginxWriterIdentity>>> ResolveWritersAsync(
        IReadOnlyList<string> affectedPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var writers = affectedPaths.ToDictionary(
            path => path,
            _ => new List<NginxWriterIdentity>(),
            pathComparer);
        var discoveryErrors = affectedPaths.ToDictionary(
            path => path,
            _ => new List<string>(),
            pathComparer);
        // Logs a container that did not answer could write: their step fails even when another writer was found,
        // because the writer nobody signals would keep writing to the replaced file.
        var unanswered = affectedPaths.ToDictionary(path => path, _ => new List<string>(), pathComparer);
        if (_pathResolver.IsDockerSocketAvailable())
        {
            var configured = _configuration.GetValue<string>("NginxLogRotation:ContainerName")
                ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(containerName => !string.Equals(containerName, "auto", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();

            var listed = await RunLimitedAsync(
                CreateDockerStartInfo("ps --filter status=running --format \"{{.Names}}\""),
                "docker nginx writer list",
                cancellationToken);
            var running = new List<string>();
            if (listed.ExitCode == 0)
            {
                running = listed.Output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                var message = $"docker nginx writer list failed with exit code {listed.ExitCode}";
                var error = listed.Error.Trim();
                foreach (var path in affectedPaths)
                {
                    discoveryErrors[path].Add(string.IsNullOrEmpty(error) ? message : $"{message}: {error}");
                }
            }

            var names = configured.Count == 0 ? running : configured;
            var inspectionNames = running
                .Concat(configured)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var mounts = new Dictionary<string, IReadOnlyList<(string Source, string Destination)>>(StringComparer.Ordinal);
            var timedOutInspections = new List<(string Name, string Error)>();
            foreach (var name in inspectionNames)
            {
                var inspect = await RunLimitedAsync(
                    CreateDockerStartInfo(
                        $"inspect --format \"{{{{range .Mounts}}}}{{{{println .Source \\\"|\\\" .Destination}}}}{{{{end}}}}\" {name}"),
                    "docker nginx mount inspection",
                    cancellationToken);
                if (inspect.TimedOut)
                {
                    // Its mounts are unknown here; the container list below, which answers while a stuck container's
                    // own commands hang, decides which logs it may write.
                    timedOutInspections.Add((name, inspect.Error));
                    continue;
                }
                if (inspect.ExitCode != 0)
                {
                    var message = $"docker nginx mount inspection for '{name}' failed with exit code {inspect.ExitCode}";
                    var error = inspect.Error.Trim();
                    foreach (var path in affectedPaths)
                    {
                        discoveryErrors[path].Add(string.IsNullOrEmpty(error) ? message : $"{message}: {error}");
                    }
                    continue;
                }

                var parsed = new List<(string Source, string Destination)>();
                var malformed = false;
                foreach (var line in inspect.Output.Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                    {
                        malformed = true;
                        break;
                    }
                    parsed.Add((parts[0], parts[1]));
                }
                if (malformed)
                {
                    foreach (var path in affectedPaths)
                    {
                        discoveryErrors[path].Add(
                            $"docker nginx mount inspection for '{name}' returned invalid output with exit code {inspect.ExitCode}");
                    }
                    continue;
                }
                mounts[name] = parsed;
            }

            var hostPaths = affectedPaths.ToDictionary(path => path, _ => (string?)null, pathComparer);
            if (CanProbeHostWriters)
            {
                var localNamespace = new ProcessStartInfo
                {
                    FileName = "sh",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                localNamespace.ArgumentList.Add("-c");
                localNamespace.ArgumentList.Add("readlink /proc/self/ns/mnt");
                var local = await RunLimitedAsync(localNamespace, "host mount namespace", cancellationToken);
                var currentNamespace = local.Output.Trim();
                var namespaceNumber = currentNamespace.Length > 6 &&
                    currentNamespace.StartsWith("mnt:[", StringComparison.Ordinal) &&
                    currentNamespace.EndsWith(']') &&
                    ulong.TryParse(currentNamespace.AsSpan(5, currentNamespace.Length - 6), out _);
                if (local.ExitCode != 0 || !namespaceNumber)
                {
                    var message = $"host mount namespace failed with exit code {local.ExitCode}";
                    var error = local.Error.Trim();
                    foreach (var path in affectedPaths)
                    {
                        discoveryErrors[path].Add(string.IsNullOrEmpty(error) ? message : $"{message}: {error}");
                    }
                }
                else
                {
                    var managerFound = false;
                    foreach (var (name, containerMounts) in mounts)
                    {
                        var relevantPaths = affectedPaths
                            .Where(path => containerMounts.Any(mount =>
                                IsWithinMount(path, mount.Destination, comparison)))
                            .ToList();
                        if (relevantPaths.Count == 0)
                        {
                            continue;
                        }

                        var process = CreateDockerStartInfo(string.Empty);
                        process.ArgumentList.Add("exec");
                        process.ArgumentList.Add(name);
                        process.ArgumentList.Add("sh");
                        process.ArgumentList.Add("-c");
                        process.ArgumentList.Add("readlink /proc/self/ns/mnt");
                        var candidate = await RunLimitedAsync(process, "docker manager mount namespace", cancellationToken);
                        var candidateNamespace = candidate.Output.Trim();
                        var candidateNumber = candidateNamespace.Length > 6 &&
                            candidateNamespace.StartsWith("mnt:[", StringComparison.Ordinal) &&
                            candidateNamespace.EndsWith(']') &&
                            ulong.TryParse(candidateNamespace.AsSpan(5, candidateNamespace.Length - 6), out _);
                        if (candidate.TimedOut)
                        {
                            // This may be the manager's own container: without its mapping a writer matched by host
                            // path could be missed, so the logs it mounts are not replaced.
                            foreach (var path in relevantPaths)
                            {
                                unanswered[path].Add($"Container '{name}': {candidate.Error}");
                            }
                            continue;
                        }
                        if (candidate.ExitCode != 0 || !candidateNumber)
                        {
                            var message =
                                $"docker manager mount namespace for '{name}' failed with exit code {candidate.ExitCode}";
                            var error = candidate.Error.Trim();
                            foreach (var path in relevantPaths)
                            {
                                discoveryErrors[path].Add(
                                    string.IsNullOrEmpty(error) ? message : $"{message}: {error}");
                            }
                            continue;
                        }
                        if (!string.Equals(candidateNamespace, currentNamespace, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        managerFound = true;
                        foreach (var path in relevantPaths)
                        {
                            var mount = containerMounts
                                .Where(item => IsWithinMount(path, item.Destination, comparison))
                                .OrderByDescending(item => item.Destination.Replace('\\', '/').TrimEnd('/').Length)
                                .FirstOrDefault();
                            var mapped = MapPath(path, mount.Destination, mount.Source);
                            if (mapped is null)
                            {
                                continue;
                            }
                            if (hostPaths[path] is { } existing &&
                                !string.Equals(existing, mapped, comparison))
                            {
                                throw new InvalidOperationException(
                                    $"Conflicting manager mount mappings were found for '{path}'");
                            }
                            hostPaths[path] = mapped;
                        }
                    }

                    if (!managerFound)
                    {
                        foreach (var path in affectedPaths)
                        {
                            hostPaths[path] = path;
                        }
                    }
                }
            }
            else
            {
                foreach (var path in affectedPaths)
                {
                    hostPaths[path] = path;
                }
            }

            if (timedOutInspections.Count > 0)
            {
                var mountList = await RunLimitedAsync(
                    CreateDockerStartInfo("ps --no-trunc --filter status=running --format \"{{.Names}}|{{.Mounts}}\""),
                    "docker nginx mount list",
                    cancellationToken);
                foreach (var (name, error) in timedOutInspections)
                {
                    // Each entry is a bind mount's host source or a named volume's name.
                    var entries = mountList.ExitCode == 0
                        ? mountList.Output
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(line => line.Split('|', 2, StringSplitOptions.TrimEntries))
                            .Where(parts => parts.Length == 2 && string.Equals(parts[0], name, StringComparison.Ordinal))
                            .SelectMany(parts => parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            .ToList()
                        : null;
                    foreach (var path in affectedPaths)
                    {
                        // A configured writer, or any container while Docker cannot list mounts, may write every log; a
                        // listed container only the logs under its mounts, so an unrelated one that hangs never stops a step.
                        var mayWrite = configured.Contains(name, StringComparer.Ordinal) || entries is null ||
                            entries.Any(entry => Path.IsPathRooted(entry)
                                ? IsWithinMount(path, entry, comparison) ||
                                  (hostPaths[path] is { } hostPath && IsWithinMount(hostPath, entry, comparison))
                                : mounts.Values.SelectMany(known => known).Any(mount =>
                                    mount.Source.Replace('\\', '/').Contains($"/volumes/{entry}/", comparison) &&
                                    (IsWithinMount(path, mount.Destination, comparison) ||
                                     IsWithinMount(path, mount.Source, comparison))));
                        if (mayWrite)
                        {
                            unanswered[path].Add($"Container '{name}': {error}");
                        }
                    }
                }
            }

            foreach (var name in names)
            {
                if (!mounts.TryGetValue(name, out var containerMounts))
                {
                    continue;
                }
                NginxWriterIdentity? writer;
                try
                {
                    writer = await ReadDockerWriterIdentityAsync(name, cancellationToken);
                }
                catch (TimeoutException timeout) when (!configured.Contains(name, StringComparer.Ordinal))
                {
                    foreach (var path in affectedPaths.Where(path => containerMounts.Any(mount =>
                                 IsWithinMount(path, mount.Source, comparison) ||
                                 IsWithinMount(path, mount.Destination, comparison) ||
                                 (hostPaths[path] is { } hostPath && IsWithinMount(hostPath, mount.Source, comparison)))))
                    {
                        unanswered[path].Add(timeout.Message);
                    }
                    continue;
                }
                if (writer is null)
                {
                    if (configured.Contains(name, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Configured nginx writer '{name}' could not be verified for the selected logs");
                    }
                    continue;
                }

                foreach (var path in affectedPaths)
                {
                    NginxWriterMatch match;
                    try
                    {
                        match = await ReadDockerWriterAsync(
                            name,
                            path,
                            hostPaths[path],
                            containerMounts,
                            writer,
                            discoveryErrors[path],
                            cancellationToken);
                    }
                    catch (TimeoutException timeout)
                    {
                        unanswered[path].Add(timeout.Message);
                        continue;
                    }
                    if (match.Matches)
                    {
                        writers[path].Add(match.Writer);
                    }
                }
            }
        }

        if (CanProbeHostWriters)
        {
            foreach (var path in affectedPaths)
            {
                var hosts = await ReadHostWritersAsync(new[] { path }, cancellationToken);
                writers[path].AddRange(hosts);
            }
        }

        foreach (var path in affectedPaths)
        {
            if (unanswered[path].Count > 0)
            {
                throw new TimeoutException(unanswered[path][0]);
            }

            var distinct = writers[path]
                .DistinctBy(writer => (writer.Kind, writer.Name, writer.ProcessId, writer.StartIdentity))
                .ToList();
            writers[path].Clear();
            writers[path].AddRange(distinct);
            if (writers[path].Count == 0 && discoveryErrors[path].Count > 0)
            {
                throw new InvalidOperationException(discoveryErrors[path][0]);
            }
        }

        return writers.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<NginxWriterIdentity>)pair.Value,
            pathComparer);
    }

    private async Task<NginxWriterMatch> ReadDockerWriterAsync(
        string containerName,
        string affectedPaths,
        string? hostPath,
        IReadOnlyList<(string Source, string Destination)> mounts,
        NginxWriterIdentity writer,
        List<string> discoveryErrors,
        CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!CanProbeHostWriters)
        {
            var mapped = mounts.Any(mount =>
                IsWithinMount(affectedPaths, mount.Source, comparer) ||
                IsWithinMount(affectedPaths, mount.Destination, comparer));
            return new NginxWriterMatch(writer, mapped);
        }

        var candidates = new List<string>();
        if (hostPath is not null)
        {
            var sourceMount = mounts
                .Where(mount => IsWithinMount(hostPath, mount.Source, comparer))
                .OrderByDescending(mount => mount.Source.Replace('\\', '/').TrimEnd('/').Length)
                .FirstOrDefault();
            var candidate = MapPath(hostPath, sourceMount.Source, sourceMount.Destination);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        var directMount = mounts
            .Where(mount => IsWithinMount(affectedPaths, mount.Destination, comparer))
            .OrderByDescending(mount => mount.Destination.Replace('\\', '/').TrimEnd('/').Length)
            .FirstOrDefault();
        if (MapPath(affectedPaths, directMount.Destination, directMount.Destination) is { } direct)
        {
            candidates.Add(direct);
        }

        var expected = NginxWriterProbe.ReadIdentity(affectedPaths);
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            var process = CreateDockerStartInfo(string.Empty);
            process.ArgumentList.Add("exec");
            process.ArgumentList.Add(containerName);
            process.ArgumentList.Add("sh");
            process.ArgumentList.Add("-c");
            process.ArgumentList.Add("stat -Lc '%d|%i' -- \"$1\"");
            process.ArgumentList.Add("nginx-file-identity");
            process.ArgumentList.Add(candidate);
            var result = await RunLimitedAsync(process, "docker nginx file identity", cancellationToken);
            if (result.TimedOut)
            {
                throw new TimeoutException($"Container '{containerName}' and '{affectedPaths}': {result.Error}");
            }
            if (result.ExitCode != 0)
            {
                var message =
                    $"docker nginx file identity for '{containerName}' and '{affectedPaths}' failed with exit code {result.ExitCode}";
                var error = result.Error.Trim();
                discoveryErrors.Add(string.IsNullOrEmpty(error) ? message : $"{message}: {error}");
                continue;
            }

            var parts = result.Output.Trim().Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !ulong.TryParse(parts[0], out var first) ||
                !ulong.TryParse(parts[1], out var second))
            {
                discoveryErrors.Add(
                    $"docker nginx file identity for '{containerName}' and '{affectedPaths}' returned invalid output with exit code {result.ExitCode}");
                continue;
            }
            if (new NginxFileIdentity(first, second) == expected)
            {
                return new NginxWriterMatch(writer, Matches: true);
            }
        }

        return new NginxWriterMatch(writer, Matches: false);
    }

    private async Task<NginxWriterIdentity?> ReadDockerWriterIdentityAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        var identities = await RunLimitedAsync(
            CreateDockerStartInfo(
                $"exec {containerName} sh -c \"pids=\\\"$(cat /run/nginx.pid 2>/dev/null; cat /var/run/nginx.pid 2>/dev/null; pgrep -f 'nginx[:] master' 2>/dev/null)\\\"; found=; for pid in $pids; do case \\\"$pid\\\" in ''|*[!0-9]*) continue;; esac; tr '\\0' ' ' </proc/$pid/cmdline 2>/dev/null | grep -q 'nginx: master' || continue; start=$(awk '{{print $22}}' /proc/$pid/stat 2>/dev/null) || continue; test -n \\\"$start\\\" || continue; printf '%s|%s\\n' \\\"$pid\\\" \\\"$start\\\"; found=1; done; test -n \\\"$found\\\"\""),
            "docker nginx writer identity",
            cancellationToken);
        if (identities.TimedOut)
        {
            // A container that does not answer may still be the writer, so it is never read as "no nginx".
            throw new TimeoutException($"Container '{containerName}': {identities.Error}");
        }
        if (identities.ExitCode != 0)
        {
            return null;
        }

        var candidates = identities.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ParseWriterIdentity(NginxWriterKind.Docker, containerName, line))
            .Where(writer => writer is not null)
            .Select(writer => writer!)
            .DistinctBy(writer => (writer.ProcessId, writer.StartIdentity));
        foreach (var candidate in candidates)
        {
            var ownership = await RunLimitedAsync(
                CreateDockerStartInfo(
                    $"exec {containerName} sh -c \"owner=$(readlink /proc/self/ns/mnt 2>/dev/null) || exit 5; test -n \\\"$owner\\\" || exit 5; candidate=$(readlink /proc/{candidate.ProcessId}/ns/mnt 2>/dev/null) || exit 6; test \\\"$candidate\\\" = \\\"$owner\\\" || exit 7; start=$(awk '{{print $22}}' /proc/{candidate.ProcessId}/stat 2>/dev/null) || exit 8; test \\\"$start\\\" = '{candidate.StartIdentity}'\""),
                "docker nginx writer ownership",
                cancellationToken);
            if (ownership.TimedOut)
            {
                throw new TimeoutException($"Container '{containerName}': {ownership.Error}");
            }
            if (ownership.ExitCode == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsWithinMount(
        string path,
        string root,
        StringComparison comparison)
    {
        var normalizedPath = path.Replace('\\', '/').TrimEnd('/');
        var normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + "/", comparison);
    }

    private static string? MapPath(string path, string fromRoot, string toRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.IsNullOrEmpty(fromRoot) || string.IsNullOrEmpty(toRoot) ||
            !IsWithinMount(path, fromRoot, comparison))
        {
            return null;
        }

        var normalizedPath = path.Replace('\\', '/').TrimEnd('/');
        var normalizedFrom = fromRoot.Replace('\\', '/').TrimEnd('/');
        var normalizedTo = toRoot.Replace('\\', '/').TrimEnd('/');
        var suffix = normalizedPath.Length == normalizedFrom.Length
            ? string.Empty
            : normalizedPath[normalizedFrom.Length..].TrimStart('/');
        return string.IsNullOrEmpty(suffix) ? normalizedTo : $"{normalizedTo}/{suffix}";
    }

    private async Task<IReadOnlyList<NginxWriterIdentity>> ReadHostWritersAsync(
        IReadOnlyList<string>? affectedPaths,
        CancellationToken cancellationToken)
    {
        var process = new ProcessStartInfo
        {
            FileName = "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.ArgumentList.Add("-c");
        process.ArgumentList.Add(
            "found=; for pid in $(pgrep -f 'nginx[:] master'); do matched=; " +
            "if [ \"$#\" -eq 0 ]; then matched=1; else " +
            "for descriptor in /proc/$pid/fd/*; do resolved=$(readlink \"$descriptor\" 2>/dev/null) || continue; " +
            "case \"$resolved\" in /*) ;; *) continue;; esac; " +
            "case \"$resolved\" in *\" (deleted)\") resolved=${resolved%\" (deleted)\"};; esac; " +
            "current=$(stat -Lc '%d|%i' -- \"/proc/$pid/root$resolved\" 2>/dev/null) || continue; " +
            "matches() { while [ \"$#\" -ge 3 ]; do target=$1; first=$2; second=$3; shift 3; " +
            "if [ \"$current\" = \"$first|$second\" ]; then return 0; fi; done; return 1; }; " +
            "if matches \"$@\"; then matched=1; break; fi; done; fi; " +
            "if [ -n \"$matched\" ]; then start=$(awk '{print $22}' /proc/$pid/stat 2>/dev/null) || continue; " +
            "printf '%s|%s\\n' \"$pid\" \"$start\"; found=1; fi; done; test -n \"$found\"");
        process.ArgumentList.Add("nginx-writer-check");
        if (affectedPaths is not null)
        {
            foreach (var path in affectedPaths)
            {
                process.ArgumentList.Add(path);
                var identity = NginxWriterProbe.ReadIdentity(path);
                process.ArgumentList.Add(identity.First.ToString());
                process.ArgumentList.Add(identity.Second.ToString());
            }
        }
        ProcessCommandResult result;
        using (var limit = Limit(cancellationToken))
        {
            try
            {
                result = await RunProcessAsync(process, "host nginx writer identity", limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A timeout cannot read as "no host writer": a log rewritten under a writer nobody signals loses lines.
                throw new TimeoutException(
                    $"host nginx writer identity did not finish within {_writerSignalTimeout.TotalSeconds:0} seconds");
            }
        }
        if (result.ExitCode != 0)
        {
            return Array.Empty<NginxWriterIdentity>();
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ParseWriterIdentity(NginxWriterKind.Host, "host", line))
            .Where(writer => writer is not null)
            .Select(writer => writer!)
            .ToList();
    }

    private static NginxWriterIdentity? ParseWriterIdentity(
        NginxWriterKind kind,
        string name,
        string output)
    {
        var parts = output.Trim().Split('|', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var processId) &&
            !string.IsNullOrEmpty(parts[1])
            ? new NginxWriterIdentity(kind, name, processId, parts[1])
            : null;
    }

    private async Task<ProcessCommandResult> SignalWriterAsync(
        NginxWriterIdentity writer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (writer.Kind == NginxWriterKind.Docker)
        {
            return await RunProcessAsync(
                CreateDockerStartInfo(
                    $"exec {writer.Name} sh -c \"start=$(awk '{{print $22}}' /proc/{writer.ProcessId}/stat 2>/dev/null); test \\\"$start\\\" = '{writer.StartIdentity}' && kill -USR1 {writer.ProcessId}\""),
                "docker nginx verified reopen",
                cancellationToken);
        }

        var process = new ProcessStartInfo
        {
            FileName = "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.ArgumentList.Add("-c");
        process.ArgumentList.Add(
            $"start=$(awk '{{print $22}}' /proc/{writer.ProcessId}/stat 2>/dev/null); " +
            $"test \"$start\" = '{writer.StartIdentity}' && kill -USR1 {writer.ProcessId}");
        return await RunProcessAsync(process, "host nginx verified reopen", cancellationToken);
    }

    private static string GetSignalError(
        NginxWriterIdentity writer,
        ProcessCommandResult result)
    {
        var message = $"Failed to reopen nginx writer '{writer.Name}' with exit code {result.ExitCode}";
        var error = result.Error.Trim();
        return string.IsNullOrEmpty(error) ? message : $"{message}: {error}";
    }

    // Each command stops after the writer-signal limit, so a hung Docker daemon cannot hold the log lock;
    // the run's token (X on its card) stops it at once.
    private static CancellationTokenSource Limit(CancellationToken cancellationToken)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_writerSignalTimeout);
        return limit;
    }

    // One command of a writer lookup, a container search or a reopen, stopped after 30 seconds. A timeout is marked
    // (TimedOut) and each caller decides what silence means: a writer read throws so the step names the container,
    // the nginx check answers "unknown", and a search moves on to the next container. A hung Docker daemon cannot hold
    // the log lock. The caller's own cancel throws.
    private async Task<ProcessCommandResult> RunLimitedAsync(
        ProcessStartInfo start,
        string label,
        CancellationToken cancellationToken)
    {
        using var limit = Limit(cancellationToken);
        try
        {
            return await RunProcessAsync(start, label, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProcessCommandResult
            {
                // No exit code: the command was stopped.
                ExitCode = -1,
                TimedOut = true,
                Error = $"{label} did not finish within {_writerSignalTimeout.TotalSeconds:0} seconds"
            };
        }
    }

    /// <summary>
    /// Signals nginx to reopen log files. A configured or auto-detected LANCache container is
    /// preferred; when none is found, the host nginx master is signaled locally. Each command stops
    /// after 30 seconds: a container that does not answer the nginx check is skipped, except a sole LANCache-named
    /// container, which stays the target so its writer read names the timeout; a writer that does not answer is
    /// named as not reopened, and a host writer read that does not answer fails the reopen. A cancel of
    /// <paramref name="cancellationToken"/> stops it.
    /// </summary>
    public async Task<LogRotationResult> ReopenNginxLogsAsync(CancellationToken cancellationToken)
    {
        void LogBareMetalFailure(string failureReason, Exception? exception = null)
        {
            var shouldLog = false;
            lock (_bareMetalWarningLock)
            {
                var now = _timeProvider.GetUtcNow();
                if (!_lastBareMetalWarning.HasValue ||
                    now - _lastBareMetalWarning.Value >= _bareMetalWarningThrottle)
                {
                    _lastBareMetalWarning = now;
                    shouldLog = true;
                }
            }

            if (!shouldLog)
            {
                return;
            }

            const string message =
                "Bare-metal nginx log reopen failed: {FailureReason}. Run the manager with " +
                "pid: host; CAP_KILL is granted by default, so root is not required. If signaling " +
                "is denied, ensure CAP_KILL was not removed (for example, via cap_drop), or " +
                "configure the host's logrotate to run 'nginx -s reopen' after rotation.";

            if (exception is null)
            {
                _logger.LogWarning(message, failureReason);
            }
            else
            {
                _logger.LogWarning(exception, message, failureReason);
            }
        }

        try
        {
            var enabled = _configuration.GetValue<bool>("NginxLogRotation:Enabled", false);

            if (!enabled)
            {
                _logger.LogDebug("Nginx log rotation is disabled in configuration");
                return LogRotationResult.Failed("Log rotation is disabled in configuration");
            }

            var configuredName = _configuration.GetValue<string>("NginxLogRotation:ContainerName");
            var configuredNames = configuredName?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(containerName => !string.Equals(containerName, "auto", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();
            var writers = new List<NginxWriterIdentity>();
            string? detectionError = null;
            if (configuredNames.Count > 0)
            {
                foreach (var containerName in configuredNames)
                {
                    var writer = await ReadDockerWriterIdentityAsync(containerName, cancellationToken);
                    if (writer is null)
                    {
                        return LogRotationResult.Failed(
                            $"Configured nginx writer '{containerName}' could not be verified");
                    }
                    writers.Add(writer);
                }
            }
            else
            {
                var (containerName, error) = await FindMonolithicContainerAsync(cancellationToken);
                detectionError = error;
                if (!string.IsNullOrEmpty(containerName))
                {
                    var writer = await ReadDockerWriterIdentityAsync(containerName, cancellationToken);
                    if (writer is null)
                    {
                        return LogRotationResult.Failed(
                            $"Detected nginx writer '{containerName}' could not be verified");
                    }
                    writers.Add(writer);
                }
            }

            if (writers.Count == 0)
            {
                var hosts = CanProbeHostWriters
                    ? await ReadHostWritersAsync(null, cancellationToken)
                    : Array.Empty<NginxWriterIdentity>();
                if (hosts.Count == 0)
                {
                    // Docker failed or did not answer: that is the cause to name, not a missing pid: host.
                    if (detectionError is { } dockerFailure &&
                        dockerFailure != NoNginxContainerFoundError &&
                        !dockerFailure.Contains("Docker socket", StringComparison.OrdinalIgnoreCase))
                    {
                        return LogRotationResult.Failed(dockerFailure);
                    }

                    const string failureReason =
                        "Host nginx is not visible to the manager; enable pid: host and preserve CAP_KILL";
                    LogBareMetalFailure(failureReason);
                    return LogRotationResult.Failed(
                        failureReason,
                        detectionError?.Contains("Docker socket", StringComparison.OrdinalIgnoreCase) == true);
                }
                writers.AddRange(hosts);
            }

            var failures = new List<string>();
            foreach (var writer in writers)
            {
                ProcessCommandResult signal;
                try
                {
                    using var limit = Limit(cancellationToken);
                    signal = await SignalWriterAsync(writer, limit.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The writers after it are still signaled; the partial result names this one.
                    failures.Add($"Failed to reopen nginx writer '{writer.Name}' within {_writerSignalTimeout.TotalSeconds:0} seconds");
                    continue;
                }
                if (signal.ExitCode != 0)
                {
                    failures.Add(GetSignalError(writer, signal));
                }
            }

            if (failures.Count == writers.Count)
            {
                return LogRotationResult.Failed(
                    string.Join("; ", failures),
                    detectionError?.Contains("Docker socket", StringComparison.OrdinalIgnoreCase) == true);
            }

            lock (_bareMetalWarningLock)
            {
                _lastBareMetalWarning = null;
            }

            if (failures.Count > 0)
            {
                // Writers that reopened keep their new file; the card names the ones that could not be signaled.
                return new LogRotationResult
                {
                    Success = true,
                    ErrorMessage = string.Join("; ", failures),
                    Requirement = NginxReopenRequirement.Required,
                    Status = NginxReopenStatus.Succeeded
                };
            }

            return LogRotationResult.Succeeded();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed while attempting best-effort nginx log reopen");
            return LogRotationResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Auto-detect the monolithic container by matching name/image hints,
    /// with nginx checks as a fallback when there are multiple candidates.
    /// </summary>
    /// <returns>A tuple of (container name, error message). If container is found, error is null.</returns>
    protected virtual async Task<(string? ContainerName, string? Error)> FindMonolithicContainerAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Attempting to auto-detect monolithic container...");

            // Check if docker socket is accessible
            if (!_pathResolver.IsDockerSocketAvailable())
            {
                var error = "Docker socket not mounted. Add /var/run/docker.sock:/var/run/docker.sock to your volumes.";
                _logger.LogDebug("Docker socket not found at /var/run/docker.sock; trying host nginx signaling");
                return (null, error);
            }

            // Get all running containers with names and images
            var processStartInfo = CreateDockerStartInfo(
                "ps --filter status=running --format \"{{.Names}}|{{.Image}}\"");

            var result = await RunLimitedAsync(processStartInfo, "docker ps", cancellationToken);

            if (result.ExitCode != 0)
            {
                var errorMsg = string.IsNullOrWhiteSpace(result.Error) ? "Unknown error" : result.Error.Trim();
                _logger.LogDebug(
                    "docker ps failed with exit code {ExitCode}: {Error}; trying host nginx signaling",
                    result.ExitCode,
                    errorMsg);
                return (null, $"Docker command failed: {errorMsg}");
            }

            var stdout = result.Output;

            static bool LooksLikeLancache(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                var lower = value.ToLowerInvariant();
                return lower.Contains("lancache") || lower.Contains("monolithic");
            }

            static bool LooksLikeMonolithic(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                return value.Contains("monolithic", StringComparison.OrdinalIgnoreCase);
            }

            static bool LooksLikeNonCache(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                var lower = value.ToLowerInvariant();
                // Sidecars that share the "lancache" name prefix but never run nginx: the
                // manager UI, the DNS resolver, and the database backend (e.g. lancache-db
                // on a postgres image). Excluding them keeps them out of the nginx search.
                return lower.Contains("dns")
                    || lower.Contains("manager")
                    || lower.Contains("postgres")
                    || lower.Contains("redis")
                    || lower.Contains("mariadb")
                    || lower.Contains("mysql");
            }

            var containers = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line))
                .Select(line =>
                {
                    var parts = line.Split('|', 2);
                    var name = parts[0].Trim();
                    var image = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    return (Name: name, Image: image);
                })
                .Where(container => !string.IsNullOrEmpty(container.Name))
                .ToList();

            var candidates = containers
                .Where(container => LooksLikeLancache(container.Name) || LooksLikeLancache(container.Image))
                .Where(container => !LooksLikeNonCache(container.Name) && !LooksLikeNonCache(container.Image))
                .ToList();

            var monolithicCandidates = candidates
                .Where(container => LooksLikeMonolithic(container.Name) || LooksLikeMonolithic(container.Image))
                .ToList();

            if (monolithicCandidates.Count == 1)
            {
                var match = monolithicCandidates[0];
                _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", match.Name);
                return (match.Name, null);
            }

            if (monolithicCandidates.Count > 1)
            {
                foreach (var candidate in monolithicCandidates)
                {
                    var hasNginx = await ContainerHasNginxAsync(candidate.Name, cancellationToken);
                    if (hasNginx == true)
                    {
                        _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", candidate.Name);
                        return (candidate.Name, null);
                    }
                }

                var fallbackName = monolithicCandidates[0].Name;
                _logger.LogWarning("Multiple monolithic containers matched; using {ContainerName} without nginx validation.", fallbackName);
                return (fallbackName, null);
            }

            if (candidates.Count == 1)
            {
                var match = candidates[0];
                // A single "lancache"-named match is ambiguous: a database or other sidecar
                // can share the prefix, so confirm nginx is actually present before claiming
                // it as the reopen target. If it is not, keep searching for a real nginx host.
                // A LANCache-named container that did not answer stays the target: its writer read then names the
                // timeout, and another container that runs nginx is never signaled in its place.
                if (await ContainerHasNginxAsync(match.Name, cancellationToken) != false)
                {
                    _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", match.Name);
                    return (match.Name, null);
                }

                _logger.LogDebug(
                    "Sole lancache-named container {ContainerName} has no nginx; continuing search.",
                    match.Name);
            }

            if (candidates.Count > 1)
            {
                foreach (var candidate in candidates)
                {
                    var hasNginx = await ContainerHasNginxAsync(candidate.Name, cancellationToken);
                    if (hasNginx == true)
                    {
                        _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", candidate.Name);
                        return (candidate.Name, null);
                    }
                }

                var fallbackName = candidates[0].Name;
                _logger.LogWarning("Multiple containers matched lancache; using {ContainerName} without nginx validation.", fallbackName);
                return (fallbackName, null);
            }

            // Fall back to any container with nginx installed, but still skip the known
            // sidecars so a manager/dns/database container is never picked as the target.
            foreach (var container in containers)
            {
                if (LooksLikeNonCache(container.Name) || LooksLikeNonCache(container.Image)) continue;

                var hasNginx = await ContainerHasNginxAsync(container.Name, cancellationToken);
                if (hasNginx != true) continue;

                // Found a container with nginx
                _logger.LogInformation("Found container with nginx: {ContainerName}", container.Name);
                return (container.Name, null);
            }

            // No suitable container found

            _logger.LogDebug("No suitable nginx container found; trying host nginx signaling");
            return (null, NoNginxContainerFoundError);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Container detection failed; trying host nginx signaling");
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Whether a container runs nginx; null when it did not answer within the limit, which is not "no nginx".
    /// </summary>
    private async Task<bool?> ContainerHasNginxAsync(string containerName, CancellationToken cancellationToken)
    {
        try
        {
            var processStartInfo = CreateDockerStartInfo(
                $"exec {containerName} sh -c \"which nginx || command -v nginx\"");

            var result = await RunLimitedAsync(processStartInfo, "docker exec nginx-check", cancellationToken);
            return result.TimedOut ? null : result.ExitCode == 0;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateDockerStartInfo(string arguments) => new()
    {
        FileName = "docker",
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    /// <summary>
    /// Shell prefix shared by the host probe and signal scripts: resolves the host nginx pid and
    /// exits with <see cref="HostPidNotVisibleExitCode"/> before either script acts on it, so both
    /// stay in lockstep instead of drifting the way the probe-only guard once did.
    /// </summary>
    private static string BuildHostPidResolutionScript() =>
        $"pid={HostNginxPidExpression}; " +
        $"if [ -z \"$pid\" ]; then exit {HostPidNotVisibleExitCode}; fi; ";

    private static ProcessStartInfo CreateHostProbeStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            BuildHostPidResolutionScript() +
            $"printf '{HostPidVisibleMarker}\\n'; kill -0 \"$pid\"");
        return startInfo;
    }

    /// <summary>
    /// Test seam for commands that production runs through the shared process manager.
    /// </summary>
    protected virtual Task<ProcessCommandResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        string label,
        CancellationToken cancellationToken = default) =>
        _processManager.RunAsync(startInfo, cancellationToken, label: label);

}
