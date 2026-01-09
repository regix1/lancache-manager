using System.Security.Cryptography;
using System.Text;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

public class DeviceAuthService
{
    private readonly ILogger<DeviceAuthService> _logger;
    private readonly ApiKeyService _apiKeyService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, DeviceRegistration> _deviceCache = new();
    private readonly object _cacheLock = new object();

    public DeviceAuthService(
        ILogger<DeviceAuthService> logger,
        ApiKeyService apiKeyService,
        IConfiguration configuration,
        IDbContextFactory<AppDbContext> contextFactory)
    {
        _logger = logger;
        _apiKeyService = apiKeyService;
        _configuration = configuration;
        _contextFactory = contextFactory;

        // First, migrate any sessions using old insecure key derivation
        MigrateInsecureSessions();
        
        // Then load valid device registrations
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

            // Get max devices from configuration (default to 3)
            var maxDevices = _configuration.GetValue<int>("Security:MaxAdminDevices", 3);

            // Encrypt the API key with device-specific encryption
            var encryptedKey = EncryptApiKey(request.ApiKey, request.DeviceId);

            var friendlyName = string.IsNullOrWhiteSpace(request.DeviceName)
                ? "Unknown Device"
                : request.DeviceName!.Trim();

            // Parse User-Agent for OS and browser info
            var (os, browser) = UserAgentParser.Parse(userAgent);

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

            // Check how many devices are currently using the API key
            lock (_cacheLock)
            {
                var devicesUsingApiKey = _deviceCache.Values.Where(d =>
                {
                    // Skip if this is the same device (allow re-registration)
                    if (d.DeviceId == request.DeviceId)
                    {
                        return false;
                    }

                    // Check if device is using a valid key
                    try
                    {
                        var existingKey = DecryptApiKey(d.EncryptedApiKey, d.DeviceId);
                        return _apiKeyService.ValidateApiKey(existingKey);
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();

                if (devicesUsingApiKey.Count >= maxDevices)
                {
                    _logger.LogWarning("Device registration denied: Maximum devices ({MaxDevices}) already registered. New device: {NewDevice} from IP {IP}",
                        maxDevices, request.DeviceId, ipAddress);

                    return new AuthResponse
                    {
                        Success = false,
                        Message = $"Maximum number of devices ({maxDevices}) already registered. Please log out from another device first or increase MAX_ADMIN_DEVICES in configuration."
                    };
                }
            }

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
                LastSeenAt = DateTime.UtcNow
            };

            // Save to disk
            SaveDeviceRegistration(registration);

            // Update cache
            lock (_cacheLock)
            {
                _deviceCache[request.DeviceId] = registration;
            }

            _logger.LogInformation("Device registered: {DeviceId} from IP {IP}, OS: {OS}, Browser: {Browser}",
                request.DeviceId, ipAddress, os, browser);

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

        // CRITICAL: Check database first to ensure session still exists
        // This prevents zombie sessions where file cache exists but database session was deleted
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var session = context.UserSessions
                .FirstOrDefault(s => s.DeviceId == deviceId && !s.IsGuest && !s.IsRevoked);

            if (session == null)
            {
                // Log at Debug level - this is expected when sessions are cleared or devices revoked
                _logger.LogDebug("[DeviceAuth] Device {DeviceId} not found in database or revoked, denying access", deviceId);

                // Remove from cache if it exists
                lock (_cacheLock)
                {
                    _deviceCache.Remove(deviceId);
                }

                return false;
            }

            // NOTE: Do NOT update LastSeenAtUtc here - that's the heartbeat's job
            // The heartbeat endpoint (/api/sessions/current/last-seen) respects page visibility
            // and only updates LastSeenAt when the user is actively viewing the page.
            // Updating it here on every API request would make users always appear "Active"
            // even when their browser tab is minimized.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DeviceAuth] Failed to check database for device {DeviceId}, denying access", deviceId);
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

    public void UpdateLastSeen(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        // Retry with exponential backoff to handle "database is locked" errors
        // This can happen when large operations (like PICS import) hold the database lock
        const int maxRetries = 3;
        var retryDelayMs = 100;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var context = _contextFactory.CreateDbContext();
                var session = context.UserSessions
                    .FirstOrDefault(s => s.DeviceId == deviceId && !s.IsGuest && !s.IsRevoked);

                if (session != null)
                {
                    session.LastSeenAtUtc = DateTime.UtcNow;
                    context.SaveChanges();
                }
                return; // Success - exit the retry loop
            }
            catch (Exception ex) when (ex.InnerException?.Message?.Contains("database is locked") == true && attempt < maxRetries)
            {
                // Database is locked, wait and retry
                _logger.LogDebug("[DeviceAuth] Database locked, retrying UpdateLastSeen (attempt {Attempt}/{MaxRetries})", attempt, maxRetries);
                Thread.Sleep(retryDelayMs);
                retryDelayMs *= 2; // Exponential backoff
            }
            catch (Exception ex)
            {
                // Non-retryable error or max retries exceeded - log but don't throw
                // This is a non-critical operation, so we just log and continue
                _logger.LogWarning(ex, "[DeviceAuth] Failed to update LastSeen for device {DeviceId} after {Attempts} attempts", deviceId, attempt);
                return;
            }
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
            // Validate input
            if (string.IsNullOrEmpty(encryptedKey))
            {
                _logger.LogWarning("Encrypted key is null or empty");
                return string.Empty;
            }

            var buffer = Convert.FromBase64String(encryptedKey);

            using var aes = Aes.Create();
            var key = DeriveKeyFromDeviceId(deviceId);
            aes.Key = key;

            var ivLength = aes.IV.Length;

            // Validate buffer length to prevent overflow
            if (buffer.Length < ivLength)
            {
                _logger.LogWarning("Encrypted key buffer is too short (expected at least {Expected} bytes, got {Actual} bytes)", ivLength, buffer.Length);
                return string.Empty;
            }

            var iv = new byte[ivLength];
            var encryptedDataLength = buffer.Length - ivLength;

            // Additional safety check
            if (encryptedDataLength < 0)
            {
                _logger.LogWarning("Invalid encrypted data length: {Length}", encryptedDataLength);
                return string.Empty;
            }

            var encrypted = new byte[encryptedDataLength];

            Array.Copy(buffer, 0, iv, 0, ivLength);
            Array.Copy(buffer, ivLength, encrypted, 0, encrypted.Length);

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
        // Derive a 256-bit key from the device ID combined with the API key as server-side secret
        // This prevents attackers from deriving the key even if they know the device ID
        // Note: Changing the API key will invalidate all device sessions (security feature)
        var serverSecret = _apiKeyService.GetOrCreateApiKey();
        
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes($"LancacheManager_{serverSecret}_{deviceId}_v2"));
        return hash;
    }
    
    /// <summary>
    /// Migrate sessions from old key derivation (v1) to new secure derivation (v2)
    /// This clears sessions that can't be decrypted with the new key
    /// </summary>
    private void MigrateInsecureSessions()
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var sessions = context.UserSessions
                .Where(s => !s.IsGuest && !s.IsRevoked && !string.IsNullOrEmpty(s.ApiKey))
                .ToList();
            
            var invalidSessions = new List<string>();
            
            foreach (var session in sessions)
            {
                try
                {
                    // Try to decrypt with new key derivation
                    var decrypted = DecryptApiKey(session.ApiKey!, session.DeviceId);
                    if (string.IsNullOrEmpty(decrypted))
                    {
                        invalidSessions.Add(session.DeviceId);
                    }
                }
                catch
                {
                    invalidSessions.Add(session.DeviceId);
                }
            }
            
            if (invalidSessions.Count > 0)
            {
                // Remove sessions with old key derivation - users will need to re-authenticate
                var toRemove = context.UserSessions
                    .Where(s => invalidSessions.Contains(s.DeviceId))
                    .ToList();
                
                context.UserSessions.RemoveRange(toRemove);
                context.SaveChanges();
                
                _logger.LogWarning(
                    "Security upgrade: Cleared {Count} device sessions using old key derivation. Users will need to re-authenticate.",
                    invalidSessions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session migration");
        }
    }

    private void SaveDeviceRegistration(DeviceRegistration registration)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            // Check if device exists
            var existingSession = context.UserSessions.FirstOrDefault(s => s.DeviceId == registration.DeviceId);

            if (existingSession != null)
            {
                // Update existing device
                existingSession.DeviceName = registration.DeviceName ?? string.Empty;
                existingSession.IpAddress = registration.IpAddress ?? string.Empty;
                existingSession.OperatingSystem = registration.OperatingSystem ?? string.Empty;
                existingSession.Browser = registration.Browser ?? string.Empty;
                existingSession.LastSeenAtUtc = registration.LastSeenAt ?? DateTime.UtcNow;
                existingSession.ApiKey = registration.EncryptedApiKey;
            }
            else
            {
                // Create new device session
                var userSession = new UserSession
                {
                    DeviceId = registration.DeviceId,
                    DeviceName = registration.DeviceName ?? string.Empty,
                    IpAddress = registration.IpAddress ?? string.Empty,
                    OperatingSystem = registration.OperatingSystem ?? string.Empty,
                    Browser = registration.Browser ?? string.Empty,
                    IsGuest = false,
                    CreatedAtUtc = registration.RegisteredAt,
                    ExpiresAtUtc = null, // Authenticated users don't expire
                    LastSeenAtUtc = registration.LastSeenAt ?? DateTime.UtcNow,
                    IsRevoked = false,
                    ApiKey = registration.EncryptedApiKey
                };
                context.UserSessions.Add(userSession);
            }

            context.SaveChanges();
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
            using var context = _contextFactory.CreateDbContext();
            var userSession = context.UserSessions.FirstOrDefault(s => s.DeviceId == deviceId && !s.IsGuest);

            if (userSession != null)
            {
                return new DeviceRegistration
                {
                    DeviceId = userSession.DeviceId,
                    EncryptedApiKey = userSession.ApiKey ?? string.Empty,
                    RegisteredAt = userSession.CreatedAtUtc,
                    ExpiresAt = DateTime.UtcNow.AddYears(100), // Effectively never expires
                    DeviceName = userSession.DeviceName,
                    IpAddress = userSession.IpAddress,
                    OperatingSystem = userSession.OperatingSystem,
                    Browser = userSession.Browser,
                    LastSeenAt = userSession.LastSeenAtUtc
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading device registration");
        }
        return null;
    }

    /// <summary>
    /// Reload the device cache from the database
    /// Call this after making direct database changes to UserSessions
    /// </summary>
    public void ReloadDeviceCache()
    {
        LoadDeviceRegistrations();
    }

    private void LoadDeviceRegistrations()
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var sessions = context.UserSessions
                .Where(s => !s.IsGuest && !s.IsRevoked)
                .ToList();

            lock (_cacheLock)
            {
                _deviceCache.Clear();

                foreach (var userSession in sessions)
                {
                    var registration = new DeviceRegistration
                    {
                        DeviceId = userSession.DeviceId,
                        EncryptedApiKey = userSession.ApiKey ?? string.Empty,
                        RegisteredAt = userSession.CreatedAtUtc,
                        ExpiresAt = DateTime.UtcNow.AddYears(100), // Effectively never expires
                        DeviceName = userSession.DeviceName,
                        IpAddress = userSession.IpAddress,
                        OperatingSystem = userSession.OperatingSystem,
                        Browser = userSession.Browser,
                        LastSeenAt = userSession.LastSeenAtUtc
                    };

                    _deviceCache[registration.DeviceId] = registration;
                }
            }

            _logger.LogInformation("Loaded {Count} device registrations from database", _deviceCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading device registrations from database");
        }
    }

    public int RevokeAllDevices()
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var devices = context.UserSessions.Where(s => !s.IsGuest).ToList();
            int revokedCount = devices.Count;

            foreach (var device in devices)
            {
                context.UserSessions.Remove(device);
            }

            context.SaveChanges();

            // Clear the cache
            lock (_cacheLock)
            {
                _deviceCache.Clear();
            }

            _logger.LogWarning("Revoked all {Count} device registrations", revokedCount);

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

            // Check database
            using var context = _contextFactory.CreateDbContext();
            return context.UserSessions.Any(s => !s.IsGuest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for device registrations");
            return false;
        }
    }

    /// <summary>
    /// Get all registered devices (for viewing)
    /// </summary>
    public List<DeviceInfo> GetAllDevices()
    {
        var devices = new List<DeviceInfo>();

        try
        {
            // Read from database instead of cache
            using var context = _contextFactory.CreateDbContext();
            var sessions = context.UserSessions
                .Where(s => !s.IsGuest)
                .ToList();

            foreach (var session in sessions)
            {
                // Ensure DateTime values from EF Core are marked as UTC for proper JSON serialization
                devices.Add(new DeviceInfo
                {
                    DeviceId = session.DeviceId,
                    DeviceName = session.DeviceName ?? "Unknown Device",
                    IpAddress = session.IpAddress ?? string.Empty,
                    LocalIp = null,
                    Hostname = null,
                    OperatingSystem = session.OperatingSystem ?? string.Empty,
                    Browser = session.Browser ?? string.Empty,
                    RegisteredAt = session.CreatedAtUtc.AsUtc(),
                    LastSeenAt = session.LastSeenAtUtc.AsUtc(),
                    ExpiresAt = (session.ExpiresAtUtc ?? DateTime.MaxValue).AsUtc(),
                    IsExpired = session.ExpiresAtUtc.HasValue && session.ExpiresAtUtc <= DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading devices from database");
        }

        return devices.OrderByDescending(d => d.LastSeenAt ?? d.RegisteredAt).ToList();
    }

    /// <summary>
    /// Revoke a specific device registration
    /// Returns: (success, message) - message provides details about the operation
    /// </summary>
    public (bool success, string message) RevokeDevice(string deviceId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var userSession = context.UserSessions.FirstOrDefault(s => s.DeviceId == deviceId && !s.IsGuest);

            if (userSession != null)
            {
                context.UserSessions.Remove(userSession);
                context.SaveChanges();

                // Remove from cache
                lock (_cacheLock)
                {
                    _deviceCache.Remove(deviceId);
                }

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
    /// Clear the in-memory device cache. Called when UserSessions table is cleared.
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            var count = _deviceCache.Count;
            _deviceCache.Clear();
            _logger.LogInformation("Cleared {Count} device registrations from in-memory cache", count);
        }
    }

    /// <summary>
    /// Device information for display (no sensitive data)
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
    }
}
