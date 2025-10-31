using System.Security.Cryptography;
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

        return $"lm_admin_{key}"; // Prefix to identify as LancacheManager admin key
    }

    public void DisplayApiKey(IConfiguration configuration, DeviceAuthService? deviceAuthService = null)
    {
        var authEnabled = configuration.GetValue<bool>("Security:EnableAuthentication", true);

        // If authentication is disabled, don't display the API key
        if (!authEnabled)
        {
            Console.WriteLine("");
            Console.WriteLine("┌────────────────────────────────────────────────────────────────────────────┐");
            Console.WriteLine("│                          LANCACHE MANAGER                                  │");
            Console.WriteLine("└────────────────────────────────────────────────────────────────────────────┘");
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

        Console.WriteLine("");
        Console.WriteLine("┌────────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│                       LANCACHE MANAGER - API KEY                           │");
        Console.WriteLine("└────────────────────────────────────────────────────────────────────────────┘");
        Console.WriteLine("");
        Console.WriteLine("  ADMIN API KEY (Full Access)");
        Console.WriteLine($"  {apiKey}");
        Console.WriteLine("");
        Console.WriteLine($"  File: {_apiKeyPath}");
        Console.WriteLine("");

        // Show device usage if DeviceAuthService is available
        if (deviceAuthService != null)
        {
            var devices = deviceAuthService.GetAllDevices();
            var activeDevices = devices.Count(d => !d.IsExpired);
            Console.WriteLine($"  Admin Devices: {activeDevices} of {maxDevices} slots used");
            Console.WriteLine("");
        }
        else
        {
            Console.WriteLine($"  Admin Devices: Up to {maxDevices} devices can share this key");
            Console.WriteLine("");
        }

        Console.WriteLine("  IMPORTANT:");
        Console.WriteLine("  • Save this key securely - it provides full admin access");
        Console.WriteLine("  • Multiple admins can share the same key");
        Console.WriteLine("  • Guest mode available for temporary access (6 hours, read-only)");
        Console.WriteLine("");
        Console.WriteLine("────────────────────────────────────────────────────────────────────────────");
    }
}
