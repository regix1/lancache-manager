using System.Security.Cryptography;
using LancacheManager.Infrastructure.Services.Interfaces;

namespace LancacheManager.Security;

public class ApiKeyService
{
    private readonly ILogger<ApiKeyService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly string _primaryKeyPath;
    private readonly string _secondaryKeyPath;
    private string? _primaryKey;
    private string? _secondaryKey;
    private readonly object _keyLock = new object();

    public ApiKeyService(ILogger<ApiKeyService> logger, IConfiguration configuration, IPathResolver pathResolver)
    {
        _logger = logger;
        _pathResolver = pathResolver;

        // Get the primary API key path from configuration or use default
        var configPath = configuration["Security:ApiKeyPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            _primaryKeyPath = Path.GetFullPath(configPath);
        }
        else
        {
            // Use the path resolver to get the data directory
            _primaryKeyPath = Path.Combine(_pathResolver.GetDataDirectory(), "api_key.txt");
        }

        // User API key is always in data directory
        _secondaryKeyPath = Path.Combine(_pathResolver.GetDataDirectory(), "user_api_key.txt");

        // Ensure data directory exists
        var dir = Path.GetDirectoryName(_primaryKeyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            _logger.LogInformation("Creating data directory: {Directory}", dir);
            Directory.CreateDirectory(dir);
        }
    }

    public string GetOrCreateApiKey()
    {
        return GetOrCreatePrimaryKey();
    }

    public string GetOrCreatePrimaryKey()
    {
        lock (_keyLock)
        {
            if (!string.IsNullOrEmpty(_primaryKey))
            {
                return _primaryKey;
            }

            if (File.Exists(_primaryKeyPath))
            {
                try
                {
                    _primaryKey = File.ReadAllText(_primaryKeyPath).Trim();
                    if (!string.IsNullOrEmpty(_primaryKey))
                    {
                        return _primaryKey;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read ADMIN API key from {Path}", _primaryKeyPath);
                }
            }

            // Generate new API key
            _primaryKey = GenerateApiKey("admin");

            try
            {
                File.WriteAllText(_primaryKeyPath, _primaryKey);
                _logger.LogInformation("Generated and saved new ADMIN API key to {Path}", _primaryKeyPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save ADMIN API key to {Path}", _primaryKeyPath);
            }

            return _primaryKey;
        }
    }

    public string GetOrCreateSecondaryKey()
    {
        lock (_keyLock)
        {
            if (!string.IsNullOrEmpty(_secondaryKey))
            {
                return _secondaryKey;
            }

            if (File.Exists(_secondaryKeyPath))
            {
                try
                {
                    _secondaryKey = File.ReadAllText(_secondaryKeyPath).Trim();
                    if (!string.IsNullOrEmpty(_secondaryKey))
                    {
                        return _secondaryKey;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read USER API key from {Path}", _secondaryKeyPath);
                }
            }

            // Generate new USER API key
            _secondaryKey = GenerateApiKey("user");

            try
            {
                File.WriteAllText(_secondaryKeyPath, _secondaryKey);
                _logger.LogInformation("Generated and saved new USER API key to {Path}", _secondaryKeyPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save USER API key to {Path}", _secondaryKeyPath);
            }

            return _secondaryKey;
        }
    }

    public bool ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return false;
        }

        var primaryKey = GetOrCreatePrimaryKey();
        var secondaryKey = GetOrCreateSecondaryKey();

        return string.Equals(apiKey, primaryKey, StringComparison.Ordinal) ||
               string.Equals(apiKey, secondaryKey, StringComparison.Ordinal);
    }

    public bool IsPrimaryApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return false;
        }

        var primaryKey = GetOrCreatePrimaryKey();
        return string.Equals(apiKey, primaryKey, StringComparison.Ordinal);
    }

    public bool IsSecondaryApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return false;
        }

        var secondaryKey = GetOrCreateSecondaryKey();
        return string.Equals(apiKey, secondaryKey, StringComparison.Ordinal);
    }

    public (string oldPrimaryKey, string newPrimaryKey, string oldSecondaryKey, string newSecondaryKey) ForceRegenerateApiKey()
    {
        lock (_keyLock)
        {
            var oldPrimaryKey = GetOrCreatePrimaryKey();
            var oldSecondaryKey = GetOrCreateSecondaryKey();

            // Generate new primary key
            string newPrimaryKey;
            do
            {
                newPrimaryKey = GenerateApiKey("primary");
            }
            while (newPrimaryKey == oldPrimaryKey);

            // Generate new secondary key
            string newSecondaryKey;
            do
            {
                newSecondaryKey = GenerateApiKey("secondary");
            }
            while (newSecondaryKey == oldSecondaryKey || newSecondaryKey == newPrimaryKey);

            _primaryKey = newPrimaryKey;
            _secondaryKey = newSecondaryKey;

            try
            {
                File.WriteAllText(_primaryKeyPath, _primaryKey);
                _logger.LogInformation("Generated and saved regenerated primary API key to {Path}", _primaryKeyPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save regenerated primary API key to {Path}", _primaryKeyPath);
            }

            try
            {
                File.WriteAllText(_secondaryKeyPath, _secondaryKey);
                _logger.LogInformation("Generated and saved regenerated secondary API key to {Path}", _secondaryKeyPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save regenerated secondary API key to {Path}", _secondaryKeyPath);
            }

            return (oldPrimaryKey, newPrimaryKey, oldSecondaryKey, newSecondaryKey);
        }
    }

    private string GenerateApiKey(string type)
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

        return $"lm_{type}_{key}"; // Prefix to identify as LancacheManager key with type
    }

    public void DisplayApiKey()
    {
        var primaryKey = GetOrCreatePrimaryKey();
        var secondaryKey = GetOrCreateSecondaryKey();

        Console.WriteLine("");
        Console.WriteLine("================================================================================");
        Console.WriteLine("                        LANCACHE MANAGER API KEYS");
        Console.WriteLine("================================================================================");
        Console.WriteLine("");
        Console.WriteLine("  ADMIN API KEY (Full Access - Regenerate Keys, Manage All)");
        Console.WriteLine($"  {primaryKey}");
        Console.WriteLine($"  File: {_primaryKeyPath}");
        Console.WriteLine("");
        Console.WriteLine("  USER API KEY (Limited Access - View & Manage Data Only)");
        Console.WriteLine($"  {secondaryKey}");
        Console.WriteLine($"  File: {_secondaryKeyPath}");
        Console.WriteLine("");
        Console.WriteLine("================================================================================");
        Console.WriteLine("  IMPORTANT: Save these keys securely!");
        Console.WriteLine("  • Use ADMIN key for administrative tasks");
        Console.WriteLine("  • Share USER key with team members who need read/write access");
        Console.WriteLine("  • Guest mode available for temporary read-only access (6 hours)");
        Console.WriteLine("================================================================================");
        Console.WriteLine("");
    }
}
