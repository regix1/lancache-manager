using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services.Base;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that periodically checks directory permissions and broadcasts
/// changes via SignalR. This enables the frontend to auto-update when permissions
/// are restored (e.g., after Docker volume remount or PUID/PGID fix).
/// </summary>
public class DirectoryPermissionMonitorService : ScheduledBackgroundService
{
    private readonly IPathResolver _pathResolver;
    private readonly DatasourceService _datasourceService;
    private readonly ISignalRNotificationService _signalRNotificationService;
    private readonly IStateService _stateService;

    // Last known permission state per datasource: (cacheWritable, logsWritable)
    private readonly Dictionary<string, (bool CacheWritable, bool LogsWritable)> _lastKnownState = new();

    protected override string ServiceName => "DirectoryPermissionMonitor";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    public override bool DefaultRunOnStartup => true;

    public DirectoryPermissionMonitorService(
        ILogger<DirectoryPermissionMonitorService> logger,
        IConfiguration configuration,
        IPathResolver pathResolver,
        DatasourceService datasourceService,
        ISignalRNotificationService signalRNotificationService,
        IStateService stateService)
        : base(logger, configuration)
    {
        _pathResolver = pathResolver;
        _datasourceService = datasourceService;
        _signalRNotificationService = signalRNotificationService;
        _stateService = stateService;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Wait for setup to complete so datasource paths are configured
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);

        // Initialize last known state from current permissions
        var datasources = _datasourceService.GetDatasources();
        foreach (var ds in datasources)
        {
            var cacheAccess = GetWriteAccess(ds.CachePath);
            var logsAccess = GetWriteAccess(ds.LogPath);

            _lastKnownState[ds.Name] = (
                CacheWritable: cacheAccess == DirectoryWriteAccess.Writable,
                LogsWritable: logsAccess == DirectoryWriteAccess.Writable
            );

            // Surface a not-writable directory once at startup with an accurate reason. This is a
            // single message per datasource per boot, not the per-poll spam it replaces.
            if (cacheAccess != DirectoryWriteAccess.Writable)
            {
                _logger.LogWarning(
                    "Datasource '{Name}': cache directory is not writable at {Path}. {Reason}",
                    ds.Name, ds.CachePath, DescribeDenial(cacheAccess));
            }

            if (logsAccess != DirectoryWriteAccess.Writable)
            {
                _logger.LogWarning(
                    "Datasource '{Name}': logs directory is not writable at {Path}. {Reason}",
                    ds.Name, ds.LogPath, DescribeDenial(logsAccess));
            }
        }

        _logger.LogInformation("DirectoryPermissionMonitor initialized with {Count} datasource(s)", datasources.Count);
        await Task.CompletedTask;
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var datasources = _datasourceService.GetDatasources();
        var hasChanges = false;

        foreach (var ds in datasources)
        {
            var cacheAccess = GetWriteAccess(ds.CachePath);
            var logsAccess = GetWriteAccess(ds.LogPath);
            var currentCacheWritable = cacheAccess == DirectoryWriteAccess.Writable;
            var currentLogsWritable = logsAccess == DirectoryWriteAccess.Writable;

            if (_lastKnownState.TryGetValue(ds.Name, out var lastState))
            {
                if (lastState.CacheWritable != currentCacheWritable || lastState.LogsWritable != currentLogsWritable)
                {
                    hasChanges = true;

                    // Log once per transition per directory. Unchanged state is throttled: nothing is
                    // logged here on a poll where the writable flags did not move.
                    LogWriteAccessTransition(ds.Name, "cache", ds.CachePath, lastState.CacheWritable, cacheAccess);
                    LogWriteAccessTransition(ds.Name, "logs", ds.LogPath, lastState.LogsWritable, logsAccess);
                }
            }
            else
            {
                // New datasource not previously tracked
                hasChanges = true;
            }

            _lastKnownState[ds.Name] = (CacheWritable: currentCacheWritable, LogsWritable: currentLogsWritable);
        }

        if (hasChanges)
        {
            // Update cached permission flags on DatasourceService
            _datasourceService.RefreshPermissions();

            // Broadcast permission change to all connected clients
            await _signalRNotificationService.NotifyAllAsync(
                SignalREvents.DirectoryPermissionsChanged,
                new
                {
                    timestamp = DateTime.UtcNow,
                    datasources = datasources.Select(ds => new
                    {
                        name = ds.Name,
                        cacheWritable = _lastKnownState[ds.Name].CacheWritable,
                        logsWritable = _lastKnownState[ds.Name].LogsWritable
                    })
                });
        }
    }

    /// <summary>
    /// Resolves the typed write-access reason for a directory. The path resolvers expose this on the
    /// concrete base; if a resolver ever does not, fall back to the boolean writability check.
    /// </summary>
    private DirectoryWriteAccess GetWriteAccess(string directoryPath)
        => _pathResolver is PathResolverBase resolver
            ? resolver.GetDirectoryWriteAccess(directoryPath)
            : _pathResolver.IsDirectoryWritable(directoryPath)
                ? DirectoryWriteAccess.Writable
                : DirectoryWriteAccess.Indeterminate;

    /// <summary>
    /// Emits a single message when a directory's writability transitions. A loss of write access is a
    /// warning that names the actual cause; a recovery is informational.
    /// </summary>
    private void LogWriteAccessTransition(string datasourceName, string directoryKind, string path, bool wasWritable, DirectoryWriteAccess current)
    {
        var isWritable = current == DirectoryWriteAccess.Writable;
        if (wasWritable == isWritable)
        {
            return;
        }

        if (isWritable)
        {
            _logger.LogInformation(
                "Datasource '{Name}': {Kind} write access restored at {Path}.",
                datasourceName, directoryKind, path);
            return;
        }

        _logger.LogWarning(
            "Datasource '{Name}': {Kind} is no longer writable at {Path}. {Reason}",
            datasourceName, directoryKind, path, DescribeDenial(current));
    }

    /// <summary>
    /// Human-readable reason for a write denial, distinguishing a deliberately read-only mount from an
    /// ownership/mode denial so the message does not blame PUID/PGID when the mount is read-only by design.
    /// </summary>
    private static string DescribeDenial(DirectoryWriteAccess access) => access switch
    {
        DirectoryWriteAccess.ReadOnlyMount =>
            "The path is mounted read-only, so writes are disabled by design. No ownership change is needed.",
        DirectoryWriteAccess.OwnershipOrModeDenied =>
            "Write access is denied by ownership or file mode, commonly a PUID/PGID mismatch. Match the container's ownership to the lancache files.",
        DirectoryWriteAccess.DirectoryMissing =>
            "The directory no longer exists.",
        _ =>
            "Write access could not be determined."
    };
}
