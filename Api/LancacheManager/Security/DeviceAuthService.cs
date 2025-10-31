using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancacheManager.Infrastructure.Services.Interfaces;

namespace LancacheManager.Security;

public class DeviceAuthService
{
    private readonly ILogger<DeviceAuthService> _logger;
    private readonly ApiKeyService _apiKeyService;
    private readonly IPathResolver _pathResolver;
    private readonly string _devicesDirectory;
    private readonly Dictionary<string, DeviceRegistration> _deviceCache = new();
    private readonly object _cacheLock = new object();

    public DeviceAuthService(
        ILogger<DeviceAuthService> logger,
        ApiKeyService apiKeyService,
        IConfiguration configuration,
        IPathResolver pathResolver)
    {
        _logger = logger;
        _apiKeyService = apiKeyService;
        _pathResolver = pathResolver;
        _devicesDirectory = configuration["Security:DevicesPath"] ?? Path.Combine(_pathResolver.GetDataDirectory(), "devices");

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
        public string? LocalIp { get; set; }
        public string? UserAgent { get; set; }
        public string? Hostname { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public bool IsPrimaryAdmin { get; set; }
    }

    public class RegisterDeviceRequest
    {
        public string DeviceId { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? LocalIp { get; set; }
    }

    public class AuthResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? DeviceId { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? DeviceName { get; set; }
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

            var friendlyName = string.IsNullOrWhiteSpace(request.DeviceName)
                ? "Unknown Device"
                : request.DeviceName!.Trim();

            // Parse User-Agent for OS and browser info
            var (os, browser) = ParseUserAgent(userAgent);

            // Try to get hostname from IP (this may not work for internet clients or NAT)
            string? hostname = null;
            if (!string.IsNullOrEmpty(ipAddress))
            {
                try
                {
                    var ipAddr = System.Net.IPAddress.Parse(ipAddress);
                    var hostEntry = System.Net.Dns.GetHostEntry(ipAddr);
                    hostname = hostEntry.HostName;
                }
                catch
                {
                    // DNS lookup failed, that's okay
                }
            }

            // Check if another device is already using this API key
            lock (_cacheLock)
            {
                var existingDevice = _deviceCache.Values.FirstOrDefault(d =>
                {
                    try
                    {
                        // Skip if this is the same device (allow re-registration)
                        if (d.DeviceId == request.DeviceId)
                        {
                            return false;
                        }

                        var existingKey = DecryptApiKey(d.EncryptedApiKey, d.DeviceId);
                        return existingKey == request.ApiKey;
                    }
                    catch
                    {
                        return false;
                    }
                });

                if (existingDevice != null)
                {
                    // Another device is already using this API key
                    var keyType = _apiKeyService.IsPrimaryApiKey(request.ApiKey) ? "ADMIN" : "USER";
                    _logger.LogWarning("Device registration denied: Another device ({ExistingDevice}) is already using the {KeyType} API key. New device: {NewDevice} from IP {IP}",
                        existingDevice.DeviceId, keyType, request.DeviceId, ipAddress);

                    return new AuthResponse
                    {
                        Success = false,
                        Message = $"Only one device can use the {keyType} API key at a time. Another device ({existingDevice.DeviceName ?? "Unknown"}) is currently authenticated. Please log out from the other device first or regenerate the API keys."
                    };
                }
            }

            // Determine if this is admin or user key (for logging purposes)
            bool isPrimary = _apiKeyService.IsPrimaryApiKey(request.ApiKey);

            var registration = new DeviceRegistration
            {
                DeviceId = request.DeviceId,
                EncryptedApiKey = encryptedKey,
                RegisteredAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddYears(100), // Effectively never expires
                DeviceName = friendlyName,
                IpAddress = ipAddress,
                LocalIp = request.LocalIp, // Client-detected local IP via WebRTC
                UserAgent = userAgent,
                Hostname = hostname,
                OperatingSystem = os,
                Browser = browser,
                LastSeenAt = DateTime.UtcNow,
                IsPrimaryAdmin = isPrimary
            };

            // Save to disk
            SaveDeviceRegistration(registration);

            // Update cache
            lock (_cacheLock)
            {
                _deviceCache[request.DeviceId] = registration;
            }

            _logger.LogInformation("Device registered: {DeviceId} from IP {IP}, OS: {OS}, Browser: {Browser}, Primary Admin: {IsPrimary}",
                request.DeviceId, ipAddress, os, browser, isPrimary);

            return new AuthResponse
            {
                Success = true,
                Message = "Device registered successfully",
                DeviceId = registration.DeviceId,
                ExpiresAt = registration.ExpiresAt,
                DeviceName = registration.DeviceName
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

    /// <summary>
    /// Check if any device has ever been registered (on any device)
    /// This indicates the system has been set up at least once
    /// </summary>
    public bool HasAnyDeviceEverBeenRegistered()
    {
        try
        {
            // First check the cache
            lock (_cacheLock)
            {
                if (_deviceCache.Count > 0)
                {
                    return true;
                }
            }

            // Check if any device files exist on disk
            if (Directory.Exists(_devicesDirectory))
            {
                var files = Directory.GetFiles(_devicesDirectory, "*.json");
                return files.Length > 0;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for device registrations");
            return false;
        }
    }

    /// <summary>
    /// Get all registered devices (for admin viewing)
    /// </summary>
    public List<DeviceInfo> GetAllDevices()
    {
        var devices = new List<DeviceInfo>();

        lock (_cacheLock)
        {
            foreach (var registration in _deviceCache.Values)
            {
                // Decrypt the API key to check if it's the admin key
                bool isAdminKey = false;
                try
                {
                    var decryptedKey = DecryptApiKey(registration.EncryptedApiKey, registration.DeviceId);
                    isAdminKey = _apiKeyService.IsPrimaryApiKey(decryptedKey);
                }
                catch
                {
                    // If decryption fails, assume it's not the admin key
                    isAdminKey = false;
                }

                devices.Add(new DeviceInfo
                {
                    DeviceId = registration.DeviceId,
                    DeviceName = registration.DeviceName,
                    IpAddress = registration.IpAddress,
                    LocalIp = registration.LocalIp,
                    Hostname = registration.Hostname,
                    OperatingSystem = registration.OperatingSystem,
                    Browser = registration.Browser,
                    RegisteredAt = registration.RegisteredAt,
                    LastSeenAt = registration.LastSeenAt,
                    ExpiresAt = registration.ExpiresAt,
                    IsExpired = registration.ExpiresAt <= DateTime.UtcNow,
                    IsPrimaryAdmin = registration.IsPrimaryAdmin,
                    IsAdminKey = isAdminKey
                });
            }
        }

        return devices.OrderByDescending(d => d.LastSeenAt ?? d.RegisteredAt).ToList();
    }

    /// <summary>
    /// Check if a device is the primary admin
    /// </summary>
    public bool IsPrimaryAdmin(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        lock (_cacheLock)
        {
            if (_deviceCache.TryGetValue(deviceId, out var device))
            {
                return device.IsPrimaryAdmin;
            }
        }

        return false;
    }

    /// <summary>
    /// Check if a specific device is using the ADMIN API key
    /// More efficient than GetAllDevices() when you only need to check one device
    /// </summary>
    public bool IsDeviceUsingAdminKey(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        lock (_cacheLock)
        {
            if (_deviceCache.TryGetValue(deviceId, out var device))
            {
                try
                {
                    var decryptedKey = DecryptApiKey(device.EncryptedApiKey, deviceId);
                    return _apiKeyService.IsPrimaryApiKey(decryptedKey);
                }
                catch
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Revoke a specific device registration
    /// Returns: (success, message) - message provides details about the operation
    /// </summary>
    public (bool success, string message) RevokeDevice(string deviceId)
    {
        try
        {
            // Remove from cache
            lock (_cacheLock)
            {
                _deviceCache.Remove(deviceId);
            }

            // Delete file
            var filePath = GetDeviceFilePath(deviceId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogWarning("Revoked device: {DeviceId}", deviceId);
                return (true, "Device revoked successfully");
            }

            return (false, "Device not found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking device: {DeviceId}", deviceId);
            return (false, "Error revoking device");
        }
    }

    /// <summary>
    /// Parse User-Agent string to extract OS and browser information
    /// </summary>
    private (string? os, string? browser) ParseUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return (null, null);
        }

        string? os = null;
        string? browser = null;

        // Detect OS
        if (userAgent.Contains("Windows NT 10.0"))
            os = "Windows 10/11";
        else if (userAgent.Contains("Windows NT 6.3"))
            os = "Windows 8.1";
        else if (userAgent.Contains("Windows NT 6.2"))
            os = "Windows 8";
        else if (userAgent.Contains("Windows NT 6.1"))
            os = "Windows 7";
        else if (userAgent.Contains("Windows"))
            os = "Windows";
        else if (userAgent.Contains("Mac OS X"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Mac OS X (\d+[._]\d+)");
            os = match.Success ? $"macOS {match.Groups[1].Value.Replace('_', '.')}" : "macOS";
        }
        else if (userAgent.Contains("Linux"))
            os = "Linux";
        else if (userAgent.Contains("Android"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Android (\d+(\.\d+)?)");
            os = match.Success ? $"Android {match.Groups[1].Value}" : "Android";
        }
        else if (userAgent.Contains("iPhone") || userAgent.Contains("iPad"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"OS (\d+_\d+)");
            os = match.Success ? $"iOS {match.Groups[1].Value.Replace('_', '.')}" : "iOS";
        }

        // Detect Browser (order matters - check specific browsers before generic ones)
        if (userAgent.Contains("Edg/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Edg/([\d.]+)");
            browser = match.Success ? $"Edge {match.Groups[1].Value}" : "Edge";
        }
        else if (userAgent.Contains("OPR/") || userAgent.Contains("Opera/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"(?:OPR|Opera)/([\d.]+)");
            browser = match.Success ? $"Opera {match.Groups[1].Value}" : "Opera";
        }
        else if (userAgent.Contains("Chrome/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Chrome/([\d.]+)");
            browser = match.Success ? $"Chrome {match.Groups[1].Value}" : "Chrome";
        }
        else if (userAgent.Contains("Safari/") && !userAgent.Contains("Chrome"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Version/([\d.]+)");
            browser = match.Success ? $"Safari {match.Groups[1].Value}" : "Safari";
        }
        else if (userAgent.Contains("Firefox/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Firefox/([\d.]+)");
            browser = match.Success ? $"Firefox {match.Groups[1].Value}" : "Firefox";
        }

        return (os, browser);
    }

    /// <summary>
    /// Device information for admin display (no sensitive data)
    /// </summary>
    public class DeviceInfo
    {
        public string DeviceId { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? IpAddress { get; set; }
        public string? LocalIp { get; set; }
        public string? Hostname { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsExpired { get; set; }
        public bool IsPrimaryAdmin { get; set; }
        public bool IsAdminKey { get; set; }  // True if using ADMIN API key, false if using USER key
    }
}
