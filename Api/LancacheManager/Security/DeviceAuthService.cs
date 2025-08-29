using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LancacheManager.Security;

public class DeviceAuthService
{
    private readonly ILogger<DeviceAuthService> _logger;
    private readonly ApiKeyService _apiKeyService;
    private readonly string _devicesDirectory;
    private readonly Dictionary<string, DeviceRegistration> _deviceCache = new();
    private readonly object _cacheLock = new object();

    public DeviceAuthService(
        ILogger<DeviceAuthService> logger,
        ApiKeyService apiKeyService,
        IConfiguration configuration)
    {
        _logger = logger;
        _apiKeyService = apiKeyService;
        _devicesDirectory = configuration["Security:DevicesPath"] ?? "/data/devices";
        
        // Ensure devices directory exists
        if (!Directory.Exists(_devicesDirectory))
        {
            Directory.CreateDirectory(_devicesDirectory);
        }

        LoadDeviceRegistrations();
    }

    public class DeviceRegistration
    {
        public string DeviceId { get; set; } = string.Empty;
        public string EncryptedApiKey { get; set; } = string.Empty;
        public DateTime RegisteredAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? DeviceName { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
    }

    public class RegisterDeviceRequest
    {
        public string DeviceId { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
    }

    public class AuthResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? DeviceId { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    public AuthResponse RegisterDevice(RegisterDeviceRequest request, string? ipAddress = null, string? userAgent = null)
    {
        try
        {
            // Validate API key
            if (!_apiKeyService.ValidateApiKey(request.ApiKey))
            {
                _logger.LogWarning("Device registration failed: Invalid API key from IP {IP}", ipAddress);
                return new AuthResponse 
                { 
                    Success = false, 
                    Message = "Invalid API key" 
                };
            }

            // Validate device ID
            if (string.IsNullOrWhiteSpace(request.DeviceId) || request.DeviceId.Length < 16)
            {
                return new AuthResponse 
                { 
                    Success = false, 
                    Message = "Invalid device ID" 
                };
            }

            // Encrypt the API key with device-specific encryption
            var encryptedKey = EncryptApiKey(request.ApiKey, request.DeviceId);
            
            var registration = new DeviceRegistration
            {
                DeviceId = request.DeviceId,
                EncryptedApiKey = encryptedKey,
                RegisteredAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddYears(100), // Effectively never expires
                DeviceName = request.DeviceName ?? "Unknown Device",
                IpAddress = ipAddress,
                UserAgent = userAgent
            };

            // Save to disk
            SaveDeviceRegistration(registration);
            
            // Update cache
            lock (_cacheLock)
            {
                _deviceCache[request.DeviceId] = registration;
            }

            _logger.LogInformation("Device registered: {DeviceId} from IP {IP}", request.DeviceId, ipAddress);

            return new AuthResponse
            {
                Success = true,
                Message = "Device registered successfully",
                DeviceId = registration.DeviceId,
                ExpiresAt = registration.ExpiresAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering device");
            return new AuthResponse 
            { 
                Success = false, 
                Message = "Registration failed" 
            };
        }
    }

    public bool ValidateDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return false;
        }

        lock (_cacheLock)
        {
            if (_deviceCache.TryGetValue(deviceId, out var registration))
            {
                if (registration.ExpiresAt > DateTime.UtcNow)
                {
                    // Verify the stored API key is still valid
                    var apiKey = DecryptApiKey(registration.EncryptedApiKey, deviceId);
                    return _apiKeyService.ValidateApiKey(apiKey);
                }
            }
        }

        // Try loading from disk if not in cache
        var registration2 = LoadDeviceRegistration(deviceId);
        if (registration2 != null && registration2.ExpiresAt > DateTime.UtcNow)
        {
            var apiKey = DecryptApiKey(registration2.EncryptedApiKey, deviceId);
            if (_apiKeyService.ValidateApiKey(apiKey))
            {
                lock (_cacheLock)
                {
                    _deviceCache[deviceId] = registration2;
                }
                return true;
            }
        }

        return false;
    }

    public List<DeviceRegistration> GetAllDevices()
    {
        lock (_cacheLock)
        {
            return _deviceCache.Values
                .Where(d => d.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(d => d.RegisteredAt)
                .ToList();
        }
    }

    public bool RevokeDevice(string deviceId)
    {
        try
        {
            lock (_cacheLock)
            {
                _deviceCache.Remove(deviceId);
            }

            var filePath = GetDeviceFilePath(deviceId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Revoked device: {DeviceId}", deviceId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking device {DeviceId}", deviceId);
            return false;
        }
    }

    private string EncryptApiKey(string apiKey, string deviceId)
    {
        // Simple encryption using device ID as part of the key
        // In production, you might want to use a more sophisticated approach
        using var aes = Aes.Create();
        var key = DeriveKeyFromDeviceId(deviceId);
        aes.Key = key;
        aes.GenerateIV();

        var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(
            Encoding.UTF8.GetBytes(apiKey), 0, apiKey.Length);

        var result = new byte[aes.IV.Length + encrypted.Length];
        Array.Copy(aes.IV, 0, result, 0, aes.IV.Length);
        Array.Copy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

        return Convert.ToBase64String(result);
    }

    private string DecryptApiKey(string encryptedKey, string deviceId)
    {
        try
        {
            var buffer = Convert.FromBase64String(encryptedKey);
            
            using var aes = Aes.Create();
            var key = DeriveKeyFromDeviceId(deviceId);
            aes.Key = key;
            
            var iv = new byte[aes.IV.Length];
            var encrypted = new byte[buffer.Length - iv.Length];
            
            Array.Copy(buffer, 0, iv, 0, iv.Length);
            Array.Copy(buffer, iv.Length, encrypted, 0, encrypted.Length);
            
            aes.IV = iv;
            
            var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error decrypting API key");
            return string.Empty;
        }
    }

    private byte[] DeriveKeyFromDeviceId(string deviceId)
    {
        // Derive a 256-bit key from the device ID
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes($"LancacheManager_{deviceId}_v1"));
        return hash;
    }

    private void SaveDeviceRegistration(DeviceRegistration registration)
    {
        try
        {
            var filePath = GetDeviceFilePath(registration.DeviceId);
            var json = JsonSerializer.Serialize(registration, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving device registration");
        }
    }

    private DeviceRegistration? LoadDeviceRegistration(string deviceId)
    {
        try
        {
            var filePath = GetDeviceFilePath(deviceId);
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<DeviceRegistration>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading device registration");
        }
        return null;
    }

    private void LoadDeviceRegistrations()
    {
        try
        {
            if (!Directory.Exists(_devicesDirectory))
            {
                return;
            }

            var files = Directory.GetFiles(_devicesDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var registration = JsonSerializer.Deserialize<DeviceRegistration>(json);
                    if (registration != null && registration.ExpiresAt > DateTime.UtcNow)
                    {
                        lock (_cacheLock)
                        {
                            _deviceCache[registration.DeviceId] = registration;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error loading device file: {File}", file);
                }
            }

            _logger.LogInformation("Loaded {Count} device registrations", _deviceCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading device registrations");
        }
    }

    private string GetDeviceFilePath(string deviceId)
    {
        // Sanitize device ID for filesystem
        var safeId = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(deviceId))
            .Replace("/", "_")
            .Replace("+", "-")
            .Replace("=", "");
        
        return Path.Combine(_devicesDirectory, $"{safeId}.json");
    }

    public int RevokeAllDevices()
    {
        try
        {
            int revokedCount = 0;
            
            // Clear the cache
            lock (_cacheLock)
            {
                revokedCount = _deviceCache.Count;
                _deviceCache.Clear();
            }
            
            // Delete all device files
            if (Directory.Exists(_devicesDirectory))
            {
                var files = Directory.GetFiles(_devicesDirectory, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete device file: {file}");
                    }
                }
                
                _logger.LogWarning($"Revoked all {revokedCount} device registrations");
            }
            
            return revokedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking all devices");
            return 0;
        }
    }
}