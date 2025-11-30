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

    private (string? url, string? port) GetConnectionUrl()
    {
        // Get host's LAN IP via host.docker.internal (requires extra_hosts in docker-compose)
        var hostIp = GetHostLanIp();

        // Get the mapped port via Docker socket
        var hostPort = GetHostPort();

        if (!string.IsNullOrEmpty(hostIp) && !string.IsNullOrEmpty(hostPort))
        {
            return ($"http://{hostIp}:{hostPort}", hostPort);
        }

        if (!string.IsNullOrEmpty(hostPort))
        {
            return (null, hostPort);
        }

        return (null, null);
    }

    private string? GetHostLanIp()
    {
        // The ONLY reliable way to get the host's actual LAN IP from inside a container
        // Requires: extra_hosts: ["host.docker.internal:host-gateway"] in docker-compose
        try
        {
            var hostEntry = Dns.GetHostEntry("host.docker.internal");
            var ipv4 = hostEntry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return ipv4?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private string? GetHostPort()
    {
        var dockerSocket = "/var/run/docker.sock";
        if (!File.Exists(dockerSocket))
        {
            return null;
        }

        try
        {
            var containerIdentifier = Environment.GetEnvironmentVariable("HOSTNAME");
            if (string.IsNullOrEmpty(containerIdentifier))
            {
                return null;
            }

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

            var response = client.GetAsync($"/containers/{containerIdentifier}/json").Result;
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = response.Content.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Get host port from port bindings (look for port 80 mapping)
            if (root.TryGetProperty("NetworkSettings", out var networkSettings) &&
                networkSettings.TryGetProperty("Ports", out var ports) &&
                ports.TryGetProperty("80/tcp", out var portBindings) &&
                portBindings.ValueKind == JsonValueKind.Array &&
                portBindings.GetArrayLength() > 0)
            {
                var binding = portBindings[0];
                if (binding.TryGetProperty("HostPort", out var hostPortElement))
                {
                    return hostPortElement.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get host port from Docker");
            return null;
        }
    }

    public void DisplayApiKey(IConfiguration configuration, DeviceAuthService? deviceAuthService = null)
    {
        var authEnabled = configuration.GetValue<bool>("Security:EnableAuthentication", true);

        var (connectionUrl, detectedPort) = GetConnectionUrl();

        // Helper to display the web interface line
        void DisplayWebInterface()
        {
            if (!string.IsNullOrEmpty(connectionUrl))
            {
                Console.WriteLine($"  Web Interface: {connectionUrl}");
            }
            else if (!string.IsNullOrEmpty(detectedPort))
            {
                Console.WriteLine($"  Web Interface: http://<your-server-ip>:{detectedPort}");
                Console.WriteLine("      Add extra_hosts: [\"host.docker.internal:host-gateway\"] to auto-detect IP");
            }
            else
            {
                Console.WriteLine("  Web Interface: Unable to detect");
                Console.WriteLine("      Ensure docker.sock is mounted and extra_hosts configured");
            }
        }

        // If authentication is disabled, don't display the API key
        if (!authEnabled)
        {
            Console.WriteLine("");
            Console.WriteLine("┌────────────────────────────────────────────────────────────────────────────┐");
            Console.WriteLine("│                          LANCACHE MANAGER                                  │");
            Console.WriteLine("└────────────────────────────────────────────────────────────────────────────┘");
            Console.WriteLine("");
            DisplayWebInterface();
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
        DisplayWebInterface();
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
