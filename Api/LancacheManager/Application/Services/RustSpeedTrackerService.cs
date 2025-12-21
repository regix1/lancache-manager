using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Application.DTOs;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Application.Services;

/// <summary>
/// Background service that runs the Rust speed tracker executable and broadcasts
/// speed snapshots via SignalR. Uses Rust for faster log parsing.
/// </summary>
public class RustSpeedTrackerService : BackgroundService
{
    private readonly ILogger<RustSpeedTrackerService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly DatasourceService _datasourceService;
    private readonly IHubContext<DownloadHub> _hubContext;
    private Process? _rustProcess;
    private DownloadSpeedSnapshot _currentSnapshot = new() { WindowSeconds = 2 };
    private readonly object _snapshotLock = new();

    public RustSpeedTrackerService(
        ILogger<RustSpeedTrackerService> logger,
        IPathResolver pathResolver,
        DatasourceService datasourceService,
        IHubContext<DownloadHub> hubContext)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _datasourceService = datasourceService;
        _hubContext = hubContext;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app startup
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var datasources = _datasourceService.GetDatasources();
        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured, RustSpeedTrackerService will not run");
            return;
        }

        var rustExecutablePath = _pathResolver.GetRustSpeedTrackerPath();
        if (!File.Exists(rustExecutablePath))
        {
            _logger.LogWarning("Rust speed tracker not found at {Path}, speed tracking disabled", rustExecutablePath);
            return;
        }

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

        _logger.LogInformation("RustSpeedTrackerService stopped");
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

                        // Broadcast via SignalR if there's activity
                        if (snapshot.HasActiveDownloads || snapshot.TotalBytesPerSecond > 0)
                        {
                            await _hubContext.Clients.All.SendAsync("DownloadSpeedUpdate", snapshot, stoppingToken);
                        }
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
