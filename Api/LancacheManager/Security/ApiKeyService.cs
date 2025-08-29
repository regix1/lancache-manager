using System.Security.Cryptography;
using System.Text;

namespace LancacheManager.Security;

public class ApiKeyService
{
    private readonly ILogger<ApiKeyService> _logger;
    private readonly string _apiKeyPath;
    private string? _apiKey;
    private readonly object _keyLock = new object();

    public ApiKeyService(ILogger<ApiKeyService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _apiKeyPath = configuration["Security:ApiKeyPath"] ?? "/data/api_key.txt";
        
        // Ensure data directory exists
        var dir = Path.GetDirectoryName(_apiKeyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
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
                        _logger.LogInformation("Loaded existing API key from {Path}", _apiKeyPath);
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

    public void DisplayApiKey()
    {
        var key = GetOrCreateApiKey();
        _logger.LogInformation("========================================");
        _logger.LogInformation("API Key: {Key}", key);
        _logger.LogInformation("Save this key! It's required for authentication.");
        _logger.LogInformation("Location: {Path}", _apiKeyPath);
        _logger.LogInformation("========================================");
    }

    public void ClearCachedKey()
    {
        lock (_keyLock)
        {
            _apiKey = null;
            _logger.LogInformation("Cleared cached API key");
        }
    }
}