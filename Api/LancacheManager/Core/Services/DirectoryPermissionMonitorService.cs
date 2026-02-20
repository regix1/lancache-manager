using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
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

    // Last known permission state per datasource: (cacheWritable, logsWritable)
    private readonly Dictionary<string, (bool CacheWritable, bool LogsWritable)> _lastKnownState = new();

    protected override string ServiceName => "DirectoryPermissionMonitor";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(10);
    protected override bool RunOnStartup => true;

    public DirectoryPermissionMonitorService(
        ILogger<DirectoryPermissionMonitorService> logger,
        IConfiguration configuration,
        IPathResolver pathResolver,
        DatasourceService datasourceService,
        ISignalRNotificationService signalRNotificationService)
        : base(logger, configuration)
    {
        _pathResolver = pathResolver;
        _datasourceService = datasourceService;
        _signalRNotificationService = signalRNotificationService;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Initialize last known state from current permissions
        var datasources = _datasourceService.GetDatasources();
        foreach (var ds in datasources)
        {
            _lastKnownState[ds.Name] = (
                CacheWritable: _pathResolver.IsDirectoryWritable(ds.CachePath),
                LogsWritable: _pathResolver.IsDirectoryWritable(ds.LogPath)
            );
        }

        Logger.LogInformation("DirectoryPermissionMonitor initialized with {Count} datasource(s)", datasources.Count);
        await Task.CompletedTask;
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var datasources = _datasourceService.GetDatasources();
        var hasChanges = false;

        foreach (var ds in datasources)
        {
            var currentCacheWritable = _pathResolver.IsDirectoryWritable(ds.CachePath);
            var currentLogsWritable = _pathResolver.IsDirectoryWritable(ds.LogPath);

            if (_lastKnownState.TryGetValue(ds.Name, out var lastState))
            {
                if (lastState.CacheWritable != currentCacheWritable || lastState.LogsWritable != currentLogsWritable)
                {
                    hasChanges = true;

                    Logger.LogInformation(
                        "Datasource '{Name}': Permissions changed - Cache: {OldCache} -> {NewCache}, Logs: {OldLogs} -> {NewLogs}",
                        ds.Name,
                        lastState.CacheWritable ? "writable" : "read-only",
                        currentCacheWritable ? "writable" : "read-only",
                        lastState.LogsWritable ? "writable" : "read-only",
                        currentLogsWritable ? "writable" : "read-only");
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
}
