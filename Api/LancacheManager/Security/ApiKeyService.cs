using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LancacheManager.Infrastructure.Services.Interfaces;

namespace LancacheManager.Security;

public class ApiKeyService
{
    private readonly ILogger<ApiKeyService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly string _apiKeyPath;
    private string? _apiKey;
    private readonly object _keyLock = new object();

    public ApiKeyService(ILogger<ApiKeyService> logger, IConfiguration configuration, IPathResolver pathResolver)
    {
        _logger = logger;
        _pathResolver = pathResolver;

        // Get the API key path from configuration or use default
        var configPath = configuration["Security:ApiKeyPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            _apiKeyPath = Path.GetFullPath(configPath);
        }
        else
        {
            // Use the path resolver to get the data directory
            _apiKeyPath = Path.Combine(_pathResolver.GetDataDirectory(), "api_key.txt");
        }

        // Ensure data directory exists
        var dir = Path.GetDirectoryName(_apiKeyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            _logger.LogInformation("Creating data directory: {Directory}", dir);
            Directory.CreateDirectory(dir);
        }
    }

    public string GetOrCreateApiKey()
    {
        lock (_keyLock)
        {
            if (!string.IsNullOrEmpty(_apiKey))
            {
                return _apiKey;
            }

            if (File.Exists(_apiKeyPath))
            {
                try
                {
                    _apiKey = File.ReadAllText(_apiKeyPath).Trim();
                    if (!string.IsNullOrEmpty(_apiKey))
                    {
                        return _apiKey;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read API key from {Path}", _apiKeyPath);
                }
            }

            // Generate new API key
            _apiKey = GenerateApiKey();

            try
            {
                File.WriteAllText(_apiKeyPath, _apiKey);
                _logger.LogInformation("Generated and saved new API key to {Path}", _apiKeyPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save API key to {Path}", _apiKeyPath);
            }

            return _apiKey;
        }
    }

    public bool ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return false;
        }

        var validKey = GetOrCreateApiKey();
        return string.Equals(apiKey, validKey, StringComparison.Ordinal);
    }

    public (string oldKey, string newKey) ForceRegenerateApiKey()
    {
        lock (_keyLock)
        {
            var oldKey = GetOrCreateApiKey();

            string newKey;
            do
            {
                newKey = GenerateApiKey();
            }
            while (newKey == oldKey);

            _apiKey = newKey;

            try
            {
                File.WriteAllText(_apiKeyPath, _apiKey);
                _logger.LogInformation("Generated and saved regenerated API key to {Path}", _apiKeyPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save regenerated API key to {Path}", _apiKeyPath);
            }

            return (oldKey, newKey);
        }
    }

    private string GenerateApiKey()
    {
        // Generate a secure random API key
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        // Convert to base64 and make URL-safe
        var key = Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

        return $"lm_{key}"; // Prefix to identify as LancacheManager key
    }

    private (string? url, bool dockerSocketAvailable) GetConnectionUrl()
    {
        // Try to auto-detect from Docker socket
        var (hostIp, hostPort, socketAvailable) = GetDockerHostInfo();

        if (!string.IsNullOrEmpty(hostIp) && !string.IsNullOrEmpty(hostPort))
        {
            return ($"http://{hostIp}:{hostPort}", socketAvailable);
        }

        // If we got the IP but not the port (socket not available), we can't show full URL
        return (null, socketAvailable);
    }

    private (string? hostIp, string? hostPort, bool socketAvailable) GetDockerHostInfo()
    {
        var dockerSocket = "/var/run/docker.sock";
        var socketAvailable = File.Exists(dockerSocket);

        if (!socketAvailable)
        {
            return (null, null, false);
        }

        try
        {
            // Get container identifier - could be container ID or container name
            var containerIdentifier = Environment.GetEnvironmentVariable("HOSTNAME")
                ?? GetContainerIdFromCgroup();
            if (string.IsNullOrEmpty(containerIdentifier))
            {
                return (null, null, true);
            }

            // Query Docker API for container info
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var endpoint = new UnixDomainSocketEndPoint(dockerSocket);
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };

            using var client = new HttpClient(handler);
            client.BaseAddress = new Uri("http://localhost");
            client.Timeout = TimeSpan.FromSeconds(5);

            // Docker API accepts both container ID and container name
            var response = client.GetAsync($"/containers/{containerIdentifier}/json").Result;
            if (!response.IsSuccessStatusCode)
            {
                return (null, null, true);
            }

            var json = response.Content.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Get host port from port bindings (look for port 80 mapping)
            string? hostPort = null;
            if (root.TryGetProperty("NetworkSettings", out var networkSettings) &&
                networkSettings.TryGetProperty("Ports", out var ports) &&
                ports.TryGetProperty("80/tcp", out var portBindings) &&
                portBindings.ValueKind == JsonValueKind.Array &&
                portBindings.GetArrayLength() > 0)
            {
                var binding = portBindings[0];
                if (binding.TryGetProperty("HostPort", out var hostPortElement))
                {
                    hostPort = hostPortElement.GetString();
                }
            }

            // Get host IP from gateway
            string? hostIp = null;
            if (networkSettings.TryGetProperty("Networks", out var networks))
            {
                foreach (var network in networks.EnumerateObject())
                {
                    if (network.Value.TryGetProperty("Gateway", out var gateway))
                    {
                        var gatewayIp = gateway.GetString();
                        if (!string.IsNullOrEmpty(gatewayIp) && gatewayIp != "")
                        {
                            hostIp = gatewayIp;
                            break;
                        }
                    }
                }
            }

            return (hostIp, hostPort, true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get Docker host info");
            return (null, null, true);
        }
    }

    private string? GetContainerIdFromCgroup()
    {
        try
        {
            // Try to read container ID from cgroup (Linux)
            var cgroupPath = "/proc/self/cgroup";
            if (!File.Exists(cgroupPath))
            {
                return null;
            }

            var lines = File.ReadAllLines(cgroupPath);
            foreach (var line in lines)
            {
                // Format: hierarchy-ID:controller-list:cgroup-path
                // Docker container IDs appear in the cgroup path
                var parts = line.Split(':');
                if (parts.Length >= 3)
                {
                    var cgroupPathPart = parts[2];
                    // Look for docker container ID pattern (64 hex chars)
                    var dockerIndex = cgroupPathPart.IndexOf("/docker/", StringComparison.OrdinalIgnoreCase);
                    if (dockerIndex >= 0)
                    {
                        var idStart = dockerIndex + 8;
                        if (cgroupPathPart.Length >= idStart + 12)
                        {
                            return cgroupPathPart.Substring(idStart, Math.Min(64, cgroupPathPart.Length - idStart));
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    public void DisplayApiKey(IConfiguration configuration, DeviceAuthService? deviceAuthService = null)
    {
        var authEnabled = configuration.GetValue<bool>("Security:EnableAuthentication", true);

        var (connectionUrl, dockerSocketAvailable) = GetConnectionUrl();

        // If authentication is disabled, don't display the API key
        if (!authEnabled)
        {
            Console.WriteLine("");
            Console.WriteLine("┌────────────────────────────────────────────────────────────────────────────┐");
            Console.WriteLine("│                          LANCACHE MANAGER                                  │");
            Console.WriteLine("└────────────────────────────────────────────────────────────────────────────┘");
            Console.WriteLine("");
            if (!string.IsNullOrEmpty(connectionUrl))
            {
                Console.WriteLine($"  Web Interface: {connectionUrl}");
            }
            else if (!dockerSocketAvailable)
            {
                Console.WriteLine("  Web Interface: Mount docker.sock to auto-detect URL");
            }
            else
            {
                Console.WriteLine("  Web Interface: Unable to detect (check docker.sock permissions)");
            }
            Console.WriteLine("");
            Console.WriteLine("  [!] AUTHENTICATION: DISABLED");
            Console.WriteLine("      Full access available without API key");
            Console.WriteLine("");
            Console.WriteLine("  [i] To enable authentication:");
            Console.WriteLine("      Set Security__EnableAuthentication=true in docker-compose.yml");
            Console.WriteLine("");
            Console.WriteLine("  Note: Guest mode still available for temporary read-only access (6 hours)");
            Console.WriteLine("");
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────");
            return;
        }

        // Authentication is enabled - display the API key
        var apiKey = GetOrCreateApiKey();
        var maxDevices = configuration.GetValue<int>("Security:MaxAdminDevices", 3);

        // Get PUID/PGID from environment (set by entrypoint.sh)
        var puid = Environment.GetEnvironmentVariable("LANCACHE_PUID") ?? "N/A";
        var pgid = Environment.GetEnvironmentVariable("LANCACHE_PGID") ?? "N/A";

        Console.WriteLine("");
        Console.WriteLine("┌────────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│                            LANCACHE MANAGER                                │");
        Console.WriteLine("└────────────────────────────────────────────────────────────────────────────┘");
        Console.WriteLine("");
        if (!string.IsNullOrEmpty(connectionUrl))
        {
            Console.WriteLine($"  Web Interface: {connectionUrl}");
        }
        else if (!dockerSocketAvailable)
        {
            Console.WriteLine("  Web Interface: Mount docker.sock to auto-detect URL");
        }
        else
        {
            Console.WriteLine("  Web Interface: Unable to detect (check docker.sock permissions)");
        }
        Console.WriteLine("");
        Console.WriteLine($"  Running as UID: {puid} / GID: {pgid}");
        Console.WriteLine("");
        Console.WriteLine("  API KEY (Full Access)");
        Console.WriteLine($"  {apiKey}");
        Console.WriteLine("");
        Console.WriteLine($"  File: {_apiKeyPath}");
        Console.WriteLine("");

        // Show device usage if DeviceAuthService is available
        if (deviceAuthService != null)
        {
            var devices = deviceAuthService.GetAllDevices();
            var activeDevices = devices.Count(d => !d.IsExpired);
            Console.WriteLine($"  Registered Devices: {activeDevices} of {maxDevices} slots used");
            Console.WriteLine("");
        }
        else
        {
            Console.WriteLine($"  Registered Devices: Up to {maxDevices} devices can share this key");
            Console.WriteLine("");
        }

        Console.WriteLine("  IMPORTANT:");
        Console.WriteLine("  • Save this key securely - it provides full access");
        Console.WriteLine("  • Multiple users can share the same key");
        Console.WriteLine("  • Guest mode available for temporary access (6 hours, read-only)");
        Console.WriteLine("");
        Console.WriteLine("────────────────────────────────────────────────────────────────────────────");
    }
}
