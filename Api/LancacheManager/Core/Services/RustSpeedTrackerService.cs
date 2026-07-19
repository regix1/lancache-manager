using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that runs the Rust speed tracker executable and broadcasts
/// speed snapshots via SignalR. Uses Rust for faster log parsing.
/// </summary>
public class RustSpeedTrackerService : ScheduledBackgroundService
{
    private readonly IPathResolver _pathResolver;
    private readonly DatasourceService _datasourceService;
    private readonly ISignalRNotificationService _notifications;
    private readonly ProcessManager _processManager;
    private readonly DatasourceCapabilityService _capabilityService;
    private bool _loggedNoTrackableDatasources;
    private string? _rustExecutablePath;
    private Process? _rustProcess;
    private DownloadSpeedSnapshot _currentSnapshot = new() { WindowSeconds = 2 };
    private readonly object _snapshotLock = new();
    private bool _previousHadActivity = false;
    private int _zeroStateBroadcastCountdown = 0; // Continue broadcasting zero-state for multiple cycles

    protected override string ServiceName => "RustSpeedTrackerService";
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(5);
    protected override TimeSpan Interval => TimeSpan.Zero;
    protected override TimeSpan ErrorRetryDelay => TimeSpan.FromSeconds(5);

    public RustSpeedTrackerService(
        ILogger<RustSpeedTrackerService> logger,
        IConfiguration configuration,
        IPathResolver pathResolver,
        DatasourceService datasourceService,
        ISignalRNotificationService notifications,
        ProcessManager processManager,
        DatasourceCapabilityService capabilityService)
        : base(logger, configuration)
    {
        _pathResolver = pathResolver;
        _datasourceService = datasourceService;
        _notifications = notifications;
        _processManager = processManager;
        _capabilityService = capabilityService;
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
                await RunTrackerAsync(rustExecutablePath, datasources, stoppingToken);
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

    private async Task RunTrackerAsync(
        string rustExecutablePath,
        IReadOnlyList<ResolvedDatasource> datasources,
        CancellationToken stoppingToken)
    {
        // Build log directory arguments. The tracker discovers and tails every log source in
        // each directory (the monolithic cachelog access.log AND per-service bare-metal
        // *-access.log files), so any datasource whose scheme supports live speed is passed its
        // directory. Datasources with no single trustworthy layout (Unknown/Mixed) are skipped.
        var logDirs = datasources
            .Where(d => d.Enabled && _capabilityService.GetCapabilities(d).CanTrackLiveSpeed)
            .Select(d => $"\"{d.LogPath}\"")
            .ToList();

        if (logDirs.Count == 0)
        {
            if (!_loggedNoTrackableDatasources)
            {
                _loggedNoTrackableDatasources = true;
                _logger.LogInformation(
                    "No datasource with trackable log sources; live speed tracking is idle");
            }
            // Idle without error spam; re-check periodically in case a source appears.
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            return;
        }

        var arguments = string.Join(" ", logDirs);

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

        _processManager.Track(_rustProcess);

        _logger.LogInformation("Rust speed tracker started with PID {Pid}", _rustProcess.Id);

        // Monitor stderr in background
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await _rustProcess.StandardError.ReadLineAsync(stoppingToken)) != null)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    _logger.LogInformation("[speed_tracker stderr] {Line}", line);
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

                        var hasActivity = snapshot.HasActiveDownloads;

                        // Broadcast logic with countdown for zero-state messages
                        // This ensures frontend receives multiple zero-state messages reliably
                        if (hasActivity)
                        {
                            // Active downloads - always broadcast and reset countdown
                            _zeroStateBroadcastCountdown = 5; // Will broadcast 5 more cycles after stopping
                            await _notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, snapshot);
                        }
                        else if (_zeroStateBroadcastCountdown > 0)
                        {
                            // No activity but countdown still running - broadcast zero-state
                            _zeroStateBroadcastCountdown--;
                            await _notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, snapshot);

                            // Send DownloadsRefresh on first transition to zero
                            // so frontend refreshes the active downloads list
                            if (_previousHadActivity)
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
            if (_rustProcess != null)
            {
                if (!_rustProcess.HasExited)
                {
                    _logger.LogInformation("Stopping Rust speed tracker");
                    _processManager.KillProcessTree(_rustProcess, "speed tracker stop");
                    await _processManager.WaitAfterKillAsync(_rustProcess, TimeSpan.FromSeconds(5));
                }

                _processManager.Untrack(_rustProcess);
                _rustProcess.Dispose();
                _rustProcess = null;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_rustProcess != null && !_rustProcess.HasExited)
        {
            _logger.LogInformation("Stopping Rust speed tracker process");
            _processManager.KillProcessTree(_rustProcess, "speed tracker service stop");
            await _processManager.WaitAfterKillAsync(_rustProcess, TimeSpan.FromSeconds(5));
        }

        await base.StopAsync(cancellationToken);
    }
}
