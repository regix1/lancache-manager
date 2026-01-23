using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Base;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that runs the Rust speed tracker executable and broadcasts
/// speed snapshots via SignalR. Uses Rust for faster log parsing.
/// </summary>
public class RustSpeedTrackerService : ScheduledBackgroundService
{
    private readonly ILogger<RustSpeedTrackerService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly DatasourceService _datasourceService;
    private readonly ISignalRNotificationService _notifications;
    private string? _rustExecutablePath;
    private Process? _rustProcess;
    private DownloadSpeedSnapshot _currentSnapshot = new() { WindowSeconds = 2 };
    private readonly object _snapshotLock = new();
    private bool _previousHadActivity = false;

    protected override string ServiceName => "RustSpeedTrackerService";
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(5);
    protected override TimeSpan Interval => TimeSpan.Zero;
    protected override TimeSpan ErrorRetryDelay => TimeSpan.FromSeconds(5);

    public RustSpeedTrackerService(
        ILogger<RustSpeedTrackerService> logger,
        IConfiguration configuration,
        IPathResolver pathResolver,
        DatasourceService datasourceService,
        ISignalRNotificationService notifications)
        : base(logger, configuration)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _datasourceService = datasourceService;
        _notifications = notifications;
    }

    /// <summary>
    /// Get the current speed snapshot
    /// </summary>
    public DownloadSpeedSnapshot GetCurrentSnapshot()
    {
        lock (_snapshotLock)
        {
            return _currentSnapshot;
        }
    }

    protected override bool IsEnabled()
    {
        var datasources = _datasourceService.GetDatasources();
        var hasEnabledDatasource = false;
        foreach (var datasource in datasources)
        {
            if (datasource.Enabled)
            {
                hasEnabledDatasource = true;
                break;
            }
        }

        if (!hasEnabledDatasource)
        {
            _logger.LogWarning("No enabled datasources configured, RustSpeedTrackerService will not run");
            return false;
        }

        _rustExecutablePath = _pathResolver.GetRustSpeedTrackerPath();
        if (!File.Exists(_rustExecutablePath))
        {
            _logger.LogWarning("Rust speed tracker not found at {Path}, speed tracking disabled", _rustExecutablePath);
            return false;
        }

        return true;
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var datasources = _datasourceService.GetDatasources();
        var rustExecutablePath = _rustExecutablePath ?? _pathResolver.GetRustSpeedTrackerPath();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSpeedTrackerAsync(rustExecutablePath, datasources, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RustSpeedTrackerService, restarting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task RunSpeedTrackerAsync(
        string rustExecutablePath,
        IReadOnlyList<ResolvedDatasource> datasources,
        CancellationToken stoppingToken)
    {
        var dbPath = _pathResolver.GetDatabasePath();

        // Build log directory arguments
        var logDirs = datasources
            .Where(d => d.Enabled)
            .Select(d => $"\"{d.LogPath}\"")
            .ToList();

        if (logDirs.Count == 0)
        {
            _logger.LogWarning("No enabled datasources, speed tracking disabled");
            return;
        }

        var arguments = $"\"{dbPath}\" {string.Join(" ", logDirs)}";

        _logger.LogInformation("Starting Rust speed tracker: {Path} {Args}", rustExecutablePath, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = rustExecutablePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(rustExecutablePath)
        };

        // Pass TZ environment variable to Rust
        var tz = Environment.GetEnvironmentVariable("TZ");
        if (!string.IsNullOrEmpty(tz))
        {
            startInfo.EnvironmentVariables["TZ"] = tz;
        }

        _rustProcess = Process.Start(startInfo);

        if (_rustProcess == null)
        {
            throw new Exception("Failed to start Rust speed tracker process");
        }

        _logger.LogInformation("Rust speed tracker started with PID {Pid}", _rustProcess.Id);

        // Monitor stderr in background
        _ = Task.Run(async () =>
        {
            while (!_rustProcess.StandardError.EndOfStream)
            {
                var line = await _rustProcess.StandardError.ReadLineAsync(stoppingToken);
                if (!string.IsNullOrEmpty(line))
                {
                    _logger.LogDebug("[speed_tracker stderr] {Line}", line);
                }
            }
        }, stoppingToken);

        // Read stdout for JSON speed snapshots
        try
        {
            while (!stoppingToken.IsCancellationRequested && !_rustProcess.HasExited)
            {
                var line = await _rustProcess.StandardOutput.ReadLineAsync(stoppingToken);

                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                try
                {
                    var snapshot = JsonSerializer.Deserialize<DownloadSpeedSnapshot>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (snapshot != null)
                    {
                        lock (_snapshotLock)
                        {
                            _currentSnapshot = snapshot;
                        }

                        var hasActivity = snapshot.HasActiveDownloads || snapshot.TotalBytesPerSecond > 0;

                        // Broadcast via SignalR if there's activity OR if we just transitioned to no activity
                        // This ensures the frontend gets the "zero" state when downloads stop
                        if (hasActivity || _previousHadActivity)
                        {
                            await _notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, snapshot);

                            // Also send DownloadsRefresh when transitioning to no activity
                            // so the frontend refreshes the active downloads list
                            if (_previousHadActivity && !hasActivity)
                            {
                                await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, null);
                            }
                        }

                        _previousHadActivity = hasActivity;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Failed to parse speed snapshot JSON: {Line}", line);
                }
            }
        }
        finally
        {
            if (!_rustProcess.HasExited)
            {
                _logger.LogInformation("Stopping Rust speed tracker");
                _rustProcess.Kill();
                await _rustProcess.WaitForExitAsync(stoppingToken);
            }
            _rustProcess.Dispose();
            _rustProcess = null;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_rustProcess != null && !_rustProcess.HasExited)
        {
            _logger.LogInformation("Stopping Rust speed tracker process");
            _rustProcess.Kill();
            await _rustProcess.WaitForExitAsync(cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }
}
