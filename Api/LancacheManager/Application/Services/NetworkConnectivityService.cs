using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;

namespace LancacheManager.Application.Services;

/// <summary>
/// Service that checks network/internet connectivity on startup and provides status
/// </summary>
public class NetworkConnectivityService : IHostedService
{
    private readonly ILogger<NetworkConnectivityService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly IHttpClientFactory _httpClientFactory;
    
    private bool _hasInternetAccess = true;
    private string? _lastError;
    private DateTime _lastCheck = DateTime.MinValue;
    private readonly object _lock = new();

    // Test URLs - Steam is primary since that's what we need access to
    private static readonly string[] TestUrls = new[]
    {
        "https://api.steampowered.com/",
        "https://store.steampowered.com/",
        "https://www.google.com/"
    };

    public NetworkConnectivityService(
        ILogger<NetworkConnectivityService> logger,
        IHubContext<DownloadHub> hubContext,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _hubContext = hubContext;
        _httpClientFactory = httpClientFactory;
    }

    public bool HasInternetAccess
    {
        get { lock (_lock) return _hasInternetAccess; }
    }

    public string? LastError
    {
        get { lock (_lock) return _lastError; }
    }

    public DateTime LastCheck
    {
        get { lock (_lock) return _lastCheck; }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Network connectivity service starting - checking internet access...");
        
        // Run connectivity check on startup
        await CheckConnectivityAsync(sendSignalREvent: true, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Check network connectivity by attempting to reach test URLs
    /// </summary>
    /// <param name="sendSignalREvent">Whether to send SignalR event on failure</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if internet is accessible, false otherwise</returns>
    public async Task<bool> CheckConnectivityAsync(bool sendSignalREvent = false, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        string? successUrl = null;
        string? lastErrorMessage = null;

        foreach (var url in TestUrls)
        {
            try
            {
                _logger.LogDebug("Testing connectivity to {Url}...", url);
                
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                // Any response (even 4xx/5xx) indicates network connectivity
                successUrl = url;
                _logger.LogDebug("Successfully connected to {Url} (Status: {StatusCode})", url, response.StatusCode);
                break;
            }
            catch (HttpRequestException ex)
            {
                lastErrorMessage = $"Failed to connect to {url}: {ex.Message}";
                _logger.LogDebug(lastErrorMessage);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                lastErrorMessage = $"Connection to {url} timed out";
                _logger.LogDebug(lastErrorMessage);
            }
            catch (Exception ex)
            {
                lastErrorMessage = $"Unexpected error connecting to {url}: {ex.Message}";
                _logger.LogDebug(lastErrorMessage);
            }
        }

        lock (_lock)
        {
            _lastCheck = DateTime.UtcNow;
            
            if (successUrl != null)
            {
                _hasInternetAccess = true;
                _lastError = null;
                _logger.LogInformation("Internet connectivity check passed (verified via {Url})", successUrl);
            }
            else
            {
                _hasInternetAccess = false;
                _lastError = lastErrorMessage ?? "Unable to connect to any test URL";
                _logger.LogError("Internet connectivity check FAILED: {Error}", _lastError);
            }
        }

        // Send SignalR event if connectivity failed and we're supposed to notify
        if (sendSignalREvent && !_hasInternetAccess)
        {
            await SendConnectivityErrorAsync();
        }

        return _hasInternetAccess;
    }

    /// <summary>
    /// Send a SignalR event notifying clients of connectivity failure
    /// </summary>
    private async Task SendConnectivityErrorAsync()
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("NetworkConnectivityError", new
            {
                hasInternetAccess = false,
                message = "No internet access detected. The Docker container may not have network connectivity. Steam login and PICS features require internet access.",
                error = _lastError,
                timestamp = DateTime.UtcNow,
                suggestion = "Check your Docker network configuration. You may need to set 'network_mode: host' in your docker-compose.yml."
            });
            
            _logger.LogInformation("Sent NetworkConnectivityError SignalR event to all clients");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send NetworkConnectivityError SignalR event");
        }
    }

    /// <summary>
    /// Get the current connectivity status as a response object
    /// </summary>
    public object GetStatus()
    {
        lock (_lock)
        {
            return new
            {
                hasInternetAccess = _hasInternetAccess,
                lastError = _lastError,
                lastCheck = _lastCheck,
                lastCheckUtc = _lastCheck.ToString("O")
            };
        }
    }
}
