using System.Net.NetworkInformation;
using LancacheManager.Application.DTOs;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Application.Services;

/// <summary>
/// Background service that monitors network interface bandwidth in real-time.
/// Provides upload/download speeds from the OS network statistics.
/// </summary>
public class NetworkBandwidthService : BackgroundService
{
    private readonly ILogger<NetworkBandwidthService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;

    // Sampling configuration
    private const int SampleIntervalMs = 500; // Sample every 500ms
    private const int BroadcastIntervalMs = 500; // Broadcast every 500ms

    // Network interface tracking
    private NetworkInterface? _primaryInterface;
    private string _interfaceName = string.Empty;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSampleTime;
    private long _linkSpeedBps;

    // Peak tracking (reset on service restart)
    private double _peakDownloadBytesPerSecond;
    private double _peakUploadBytesPerSecond;

    // Current snapshot (thread-safe access)
    private NetworkBandwidthSnapshot _currentSnapshot = new();
    private readonly object _snapshotLock = new();

    public NetworkBandwidthService(
        ILogger<NetworkBandwidthService> logger,
        IHubContext<DownloadHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Get the current network bandwidth snapshot
    /// </summary>
    public NetworkBandwidthSnapshot GetCurrentSnapshot()
    {
        lock (_snapshotLock)
        {
            return _currentSnapshot;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app startup
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        // Find the primary network interface
        if (!TryFindPrimaryInterface())
        {
            _logger.LogWarning("Could not find a suitable network interface for bandwidth monitoring");
            UpdateSnapshot(new NetworkBandwidthSnapshot
            {
                IsAvailable = false,
                ErrorMessage = "No suitable network interface found"
            });
            return;
        }

        _logger.LogInformation("NetworkBandwidthService started - monitoring interface: {Interface}", _interfaceName);

        // Initialize counters
        InitializeCounters();

        var lastBroadcast = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Sample network stats
                SampleNetworkStats();

                // Broadcast if interval has passed
                var now = DateTime.UtcNow;
                if ((now - lastBroadcast).TotalMilliseconds >= BroadcastIntervalMs)
                {
                    await BroadcastSnapshot(stoppingToken);
                    lastBroadcast = now;
                }

                await Task.Delay(SampleIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in NetworkBandwidthService loop");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("NetworkBandwidthService stopped");
    }

    private bool TryFindPrimaryInterface()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            if (interfaces.Count == 0)
            {
                return false;
            }

            // Prefer interfaces with actual traffic or specific types
            // Priority: Ethernet > WiFi > Others
            _primaryInterface = interfaces
                .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 2 :
                                         ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                .ThenByDescending(ni =>
                {
                    try
                    {
                        var stats = ni.GetIPv4Statistics();
                        return stats.BytesReceived + stats.BytesSent;
                    }
                    catch
                    {
                        return 0;
                    }
                })
                .FirstOrDefault();

            if (_primaryInterface == null)
            {
                return false;
            }

            _interfaceName = _primaryInterface.Name;

            // Also check for common Docker bridge interfaces and skip them
            if (_interfaceName.StartsWith("docker") ||
                _interfaceName.StartsWith("br-") ||
                _interfaceName.StartsWith("veth"))
            {
                // Try to find a non-Docker interface
                var nonDockerInterface = interfaces
                    .Where(ni => !ni.Name.StartsWith("docker") &&
                                 !ni.Name.StartsWith("br-") &&
                                 !ni.Name.StartsWith("veth"))
                    .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 2 :
                                             ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                    .FirstOrDefault();

                if (nonDockerInterface != null)
                {
                    _primaryInterface = nonDockerInterface;
                    _interfaceName = _primaryInterface.Name;
                }
            }

            // Get link speed
            _linkSpeedBps = _primaryInterface.Speed;

            _logger.LogDebug("Selected network interface: {Name} ({Type}) - Link speed: {Speed} bps",
                _interfaceName, _primaryInterface.NetworkInterfaceType, _linkSpeedBps);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error finding network interface");
            return false;
        }
    }

    private void InitializeCounters()
    {
        try
        {
            if (_primaryInterface == null) return;

            var stats = _primaryInterface.GetIPv4Statistics();
            _lastBytesReceived = stats.BytesReceived;
            _lastBytesSent = stats.BytesSent;
            _lastSampleTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error initializing network counters");
        }
    }

    private void SampleNetworkStats()
    {
        try
        {
            if (_primaryInterface == null)
            {
                UpdateSnapshot(new NetworkBandwidthSnapshot
                {
                    IsAvailable = false,
                    ErrorMessage = "Network interface not available"
                });
                return;
            }

            // Re-fetch the interface to get fresh stats
            // NetworkInterface objects can become stale
            var currentInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.Name == _interfaceName);

            if (currentInterface == null)
            {
                UpdateSnapshot(new NetworkBandwidthSnapshot
                {
                    InterfaceName = _interfaceName,
                    IsAvailable = false,
                    ErrorMessage = "Network interface disconnected"
                });
                return;
            }

            var stats = currentInterface.GetIPv4Statistics();
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSampleTime).TotalSeconds;

            if (elapsed <= 0)
            {
                elapsed = 0.5; // Fallback to expected interval
            }

            var bytesReceived = stats.BytesReceived;
            var bytesSent = stats.BytesSent;

            // Calculate rates
            var downloadRate = (bytesReceived - _lastBytesReceived) / elapsed;
            var uploadRate = (bytesSent - _lastBytesSent) / elapsed;

            // Handle counter overflow or reset (rare but possible)
            if (downloadRate < 0) downloadRate = 0;
            if (uploadRate < 0) uploadRate = 0;

            // Track peak speeds
            if (downloadRate > _peakDownloadBytesPerSecond)
                _peakDownloadBytesPerSecond = downloadRate;
            if (uploadRate > _peakUploadBytesPerSecond)
                _peakUploadBytesPerSecond = uploadRate;

            // Update for next sample
            _lastBytesReceived = bytesReceived;
            _lastBytesSent = bytesSent;
            _lastSampleTime = now;

            UpdateSnapshot(new NetworkBandwidthSnapshot
            {
                TimestampUtc = now,
                InterfaceName = _interfaceName,
                DownloadBytesPerSecond = downloadRate,
                UploadBytesPerSecond = uploadRate,
                TotalBytesReceived = bytesReceived,
                TotalBytesSent = bytesSent,
                IsAvailable = true,
                LinkSpeedBps = _linkSpeedBps,
                PeakDownloadBytesPerSecond = _peakDownloadBytesPerSecond,
                PeakUploadBytesPerSecond = _peakUploadBytesPerSecond
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error sampling network stats");
            UpdateSnapshot(new NetworkBandwidthSnapshot
            {
                InterfaceName = _interfaceName,
                IsAvailable = false,
                ErrorMessage = $"Error reading stats: {ex.Message}"
            });
        }
    }

    private void UpdateSnapshot(NetworkBandwidthSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _currentSnapshot = snapshot;
        }
    }

    private async Task BroadcastSnapshot(CancellationToken ct)
    {
        NetworkBandwidthSnapshot snapshot;
        lock (_snapshotLock)
        {
            snapshot = _currentSnapshot;
        }

        // Always broadcast (even if 0) so UI shows current state
        try
        {
            await _hubContext.Clients.All.SendAsync("NetworkBandwidthUpdate", snapshot, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast network bandwidth update");
        }
    }
}
