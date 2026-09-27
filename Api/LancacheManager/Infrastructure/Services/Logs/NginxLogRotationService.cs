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
                var writers = await ResolveWritersAsync(paths, CancellationToken.None);
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
            return await ContainerHasNginxAsync(configuredName)
                ? DockerProbeResult.AvailableResult
                : DockerProbeResult.Unknown;
        }

        var (containerName, error) = await FindMonolithicContainerAsync();
        if (!string.IsNullOrWhiteSpace(containerName))
        {
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
        if (existingPaths.Count == 0)
        {
            if (expectsPublication)
            {
                throw new InvalidOperationException(
                    "No existing log file was available to bind to the replacement check");
            }

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

        var writers = await ResolveWritersAsync(existingPaths, cancellationToken);
        if (!CanReplaceDockerLogs && writers.Any(writer => writer.Kind == NginxWriterKind.Docker))
        {
            throw new ValidationException(WindowsDockerUnsupportedMessage)
            {
                StageKey = "management.nginxReopen.windowsDockerUnsupported"
            };
        }
        IReadOnlyList<NginxHeldProof> proofs = Array.Empty<NginxHeldProof>();
        if (writers.Count == 0 &&
            !NginxWriterProbe.TryAcquire(existingPaths, out proofs, out var proofError))
        {
            throw new InvalidOperationException(
                $"Could not prove that the selected logs have no active writer: {proofError}");
        }

        string? checkPath = null;
        string? resultPath = null;
        try
        {
            var identities = proofs.Count > 0
                ? proofs.ToDictionary(proof => proof.Path, proof => proof.Identity,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                : existingPaths.ToDictionary(path => path, NginxWriterProbe.ReadIdentity,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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

    public async Task<NginxReopenChecks> PrepareReopenChecksAsync(
        IReadOnlyList<ResolvedDatasource> datasources,
        bool expectsPublication,
        CancellationToken cancellationToken = default)
    {
        var prepared = new List<KeyValuePair<string, NginxReopenCheck>>();
        try
        {
            foreach (var datasource in datasources)
            {
                var check = await PrepareReopenCheckAsync(
                    new[] { datasource },
                    GetAffectedLogPaths(datasource),
                    expectsPublication,
                    cancellationToken);
                prepared.Add(KeyValuePair.Create(datasource.Name, check));
            }
            return new NginxReopenChecks(prepared);
        }
        catch
        {
            foreach (var pair in prepared)
            {
                await pair.Value.DisposeAsync();
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
        if (!physicalChange)
        {
            return LogRotationResult.NotRequired();
        }

        string? completionError = null;
        if (check.Proofs.Any(proof => !proof.Validate()))
        {
            completionError = "The no-writer proof changed while the logs were being updated";
        }

        if (completionError is null && check.ExpectsPublication)
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
                    partialPhysicalEffects: true);
        }

        foreach (var writer in check.Writers)
        {
            if (!await SignalWriterAsync(writer, cancellationToken))
            {
                return LogRotationResult.Failed(
                    $"Failed to reopen nginx writer '{writer.Name}'",
                    NginxReopenRequirement.Required,
                    partialPhysicalEffects: true);
            }
        }

        return completionError is null
            ? LogRotationResult.Succeeded()
            : LogRotationResult.Failed(
                completionError,
                NginxReopenRequirement.Required,
                partialPhysicalEffects: true);
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

    private static async Task<NginxPublicationResult> ReadPublicationResultAsync(
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

    private async Task<IReadOnlyList<NginxWriterIdentity>> ResolveWritersAsync(
        IReadOnlyList<string> affectedPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writers = new List<NginxWriterIdentity>();
        if (_pathResolver.IsDockerSocketAvailable())
        {
            var configured = _configuration.GetValue<string>("NginxLogRotation:ContainerName")
                ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(containerName => !string.Equals(containerName, "auto", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();
            var names = configured;
            if (names.Count == 0)
            {
                var listed = await RunProcessAsync(
                    CreateDockerStartInfo("ps --filter status=running --format \"{{.Names}}\""),
                    "docker nginx writer list");
                if (listed.ExitCode == 0)
                {
                    names = listed.Output
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                }
            }

            foreach (var name in names)
            {
                var match = await ReadDockerWriterAsync(name, affectedPaths);
                if (match is { Matches: true })
                {
                    writers.Add(match.Writer);
                }
                else if (match is null && configured.Contains(name, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Configured nginx writer '{name}' could not be verified for the selected logs");
                }
            }
        }

        var hosts = CanProbeHostWriters
            ? await ReadHostWritersAsync(affectedPaths)
            : Array.Empty<NginxWriterIdentity>();
        foreach (var host in hosts)
        {
            writers.Add(host);
        }

        return writers
            .DistinctBy(writer => (writer.Kind, writer.Name, writer.ProcessId, writer.StartIdentity))
            .ToList();
    }

    private async Task<NginxWriterMatch?> ReadDockerWriterAsync(
        string containerName,
        IReadOnlyList<string> affectedPaths)
    {
        var inspect = await RunProcessAsync(
            CreateDockerStartInfo(
                $"inspect --format \"{{{{range .Mounts}}}}{{{{println .Source \\\"|\\\" .Destination}}}}{{{{end}}}}\" {containerName}"),
            "docker nginx mount inspection");
        if (inspect.ExitCode != 0)
        {
            return null;
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var mapped = inspect.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .Any(parts => affectedPaths.Any(path =>
                IsWithinMount(path, parts[0], comparer) ||
                IsWithinMount(path, parts[1], comparer)));
        var writer = await ReadDockerWriterIdentityAsync(containerName);
        if (writer is null)
        {
            return null;
        }
        return new NginxWriterMatch(writer, mapped);
    }

    private async Task<NginxWriterIdentity?> ReadDockerWriterIdentityAsync(string containerName)
    {
        var identity = await RunProcessAsync(
            CreateDockerStartInfo(
                $"exec {containerName} sh -c \"pid=$(cat /run/nginx.pid 2>/dev/null || cat /var/run/nginx.pid 2>/dev/null || pgrep -f 'nginx[:] master' | head -1); test -n \\\"$pid\\\" || exit 3; tr '\\0' ' ' </proc/$pid/cmdline | grep -q 'nginx: master' || exit 4; start=$(awk '{{print $22}}' /proc/$pid/stat); printf '%s|%s\\n' \\\"$pid\\\" \\\"$start\\\"\""),
            "docker nginx writer identity");
        return identity.ExitCode == 0
            ? ParseWriterIdentity(NginxWriterKind.Docker, containerName, identity.Output)
            : null;
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

    private async Task<IReadOnlyList<NginxWriterIdentity>> ReadHostWritersAsync(
        IReadOnlyList<string>? affectedPaths = null)
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
            "for descriptor in /proc/$pid/fd/*; do resolved=$(readlink -f \"$descriptor\" 2>/dev/null) || continue; " +
            "for target in \"$@\"; do if [ \"$resolved\" = \"$target\" ]; then matched=1; break 2; fi; done; done; fi; " +
            "if [ -n \"$matched\" ]; then start=$(awk '{print $22}' /proc/$pid/stat 2>/dev/null) || continue; " +
            "printf '%s|%s\\n' \"$pid\" \"$start\"; found=1; fi; done; test -n \"$found\"");
        process.ArgumentList.Add("nginx-writer-check");
        if (affectedPaths is not null)
        {
            foreach (var path in affectedPaths)
            {
                process.ArgumentList.Add(path);
            }
        }
        var result = await RunProcessAsync(process, "host nginx writer identity");
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

    private async Task<bool> SignalWriterAsync(
        NginxWriterIdentity writer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (writer.Kind == NginxWriterKind.Docker)
        {
            var result = await RunProcessAsync(
                CreateDockerStartInfo(
                    $"exec {writer.Name} sh -c \"start=$(awk '{{print $22}}' /proc/{writer.ProcessId}/stat 2>/dev/null); test \\\"$start\\\" = '{writer.StartIdentity}' && kill -USR1 {writer.ProcessId}\""),
                "docker nginx verified reopen");
            return result.ExitCode == 0;
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
        var hostResult = await RunProcessAsync(process, "host nginx verified reopen");
        return hostResult.ExitCode == 0;
    }

    /// <summary>
    /// Signals nginx to reopen log files. A configured or auto-detected LANCache container is
    /// preferred; when none is found, the host nginx master is signaled locally.
    /// </summary>
    public async Task<LogRotationResult> ReopenNginxLogsAsync()
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
                    var writer = await ReadDockerWriterIdentityAsync(containerName);
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
                var (containerName, error) = await FindMonolithicContainerAsync();
                detectionError = error;
                if (!string.IsNullOrEmpty(containerName))
                {
                    var writer = await ReadDockerWriterIdentityAsync(containerName);
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
                    ? await ReadHostWritersAsync()
                    : Array.Empty<NginxWriterIdentity>();
                if (hosts.Count == 0)
                {
                    const string failureReason =
                        "Host nginx is not visible to the manager; enable pid: host and preserve CAP_KILL";
                    LogBareMetalFailure(failureReason);
                    return LogRotationResult.Failed(
                        failureReason,
                        detectionError?.Contains("Docker socket", StringComparison.OrdinalIgnoreCase) == true);
                }
                writers.AddRange(hosts);
            }

            foreach (var writer in writers)
            {
                if (!await SignalWriterAsync(writer, CancellationToken.None))
                {
                    return LogRotationResult.Failed(
                        $"Failed to reopen nginx writer '{writer.Name}'",
                        detectionError?.Contains("Docker socket", StringComparison.OrdinalIgnoreCase) == true);
                }
            }

            lock (_bareMetalWarningLock)
            {
                _lastBareMetalWarning = null;
            }
            return LogRotationResult.Succeeded();
        }
        catch (Exception ex)
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
    protected virtual async Task<(string? ContainerName, string? Error)> FindMonolithicContainerAsync()
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

            var result = await RunProcessAsync(processStartInfo, "docker ps");

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
                    var hasNginx = await ContainerHasNginxAsync(candidate.Name);
                    if (hasNginx)
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
                if (await ContainerHasNginxAsync(match.Name))
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
                    var hasNginx = await ContainerHasNginxAsync(candidate.Name);
                    if (hasNginx)
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

                var hasNginx = await ContainerHasNginxAsync(container.Name);
                if (!hasNginx) continue;

                // Found a container with nginx
                _logger.LogInformation("Found container with nginx: {ContainerName}", container.Name);
                return (container.Name, null);
            }

            // No suitable container found

            _logger.LogDebug("No suitable nginx container found; trying host nginx signaling");
            return (null, NoNginxContainerFoundError);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Container detection failed; trying host nginx signaling");
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Check if a container has nginx by trying to execute nginx -v
    /// </summary>
    private async Task<bool> ContainerHasNginxAsync(string containerName)
    {
        try
        {
            var processStartInfo = CreateDockerStartInfo(
                $"exec {containerName} sh -c \"which nginx || command -v nginx\"");

            var result = await RunProcessAsync(processStartInfo, "docker exec nginx-check");
            return result.ExitCode == 0;
        }
        catch
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
    protected virtual Task<ProcessCommandResult> RunProcessAsync(ProcessStartInfo startInfo, string label) =>
        _processManager.RunAsync(startInfo, label: label);

}
