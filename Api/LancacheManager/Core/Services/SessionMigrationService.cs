using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public class SessionMigrationService
{
    private readonly ILogger<SessionMigrationService> _logger;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IPathResolver _pathResolver;

    public SessionMigrationService(
        ILogger<SessionMigrationService> logger,
        IDbContextFactory<AppDbContext> contextFactory,
        IPathResolver pathResolver)
    {
        _logger = logger;
        _contextFactory = contextFactory;
        _pathResolver = pathResolver;
    }

    /// <summary>
    /// Migrate old JSON-based sessions to database
    /// This is a one-time migration helper
    /// </summary>
    public async Task<(int devicesImported, int guestSessionsImported, int filesDeleted)> MigrateOldSessionsToDatabase()
    {
        int devicesImported = 0;
        int guestSessionsImported = 0;
        int filesDeleted = 0;

        try
        {
            using var context = _contextFactory.CreateDbContext();
            var devicesDirectory = _pathResolver.GetDevicesDirectory();

            // 1. Migrate authenticated device sessions
            if (Directory.Exists(devicesDirectory))
            {
                var deviceFiles = Directory.GetFiles(devicesDirectory, "*.json");
                foreach (var filePath in deviceFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(filePath);
                        var oldDevice = JsonSerializer.Deserialize<OldDeviceRegistration>(json);

                        if (oldDevice != null)
                        {
                            // Check if already migrated
                            var exists = await context.UserSessions.AnyAsync(s => s.DeviceId == oldDevice.DeviceId);
                            if (!exists)
                            {
                                var userSession = new UserSession
                                {
                                    DeviceId = oldDevice.DeviceId,
                                    DeviceName = oldDevice.DeviceName ?? "Unknown Device",
                                    IpAddress = oldDevice.IpAddress ?? string.Empty,
                                    OperatingSystem = oldDevice.OperatingSystem ?? string.Empty,
                                    Browser = oldDevice.Browser ?? string.Empty,
                                    IsGuest = false,
                                    CreatedAtUtc = oldDevice.RegisteredAt,
                                    ExpiresAtUtc = null, // Authenticated users don't expire
                                    LastSeenAtUtc = oldDevice.LastSeenAt ?? oldDevice.RegisteredAt,
                                    IsRevoked = false,
                                    ApiKey = oldDevice.EncryptedApiKey
                                };

                                context.UserSessions.Add(userSession);
                                devicesImported++;
                            }

                            // Delete old file after migration
                            File.Delete(filePath);
                            filesDeleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to migrate device file: {FilePath}", filePath);
                    }
                }
            }

            // 2. Migrate guest sessions
            var guestSessionsDirectory = Path.Combine(devicesDirectory, "guest_sessions");
            if (Directory.Exists(guestSessionsDirectory))
            {
                var guestFiles = Directory.GetFiles(guestSessionsDirectory, "*.json");
                foreach (var filePath in guestFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(filePath);
                        var oldGuestSession = JsonSerializer.Deserialize<OldGuestSession>(json);

                        if (oldGuestSession != null)
                        {
                            // Extract device ID from old format (guest_{deviceId}_{timestamp})
                            var sessionId = oldGuestSession.DeviceId ?? oldGuestSession.SessionId;
                            if (string.IsNullOrEmpty(sessionId))
                            {
                                var parts = oldGuestSession.SessionId.Split('_');
                                if (parts.Length >= 3 && parts[0] == "guest")
                                {
                                    sessionId = parts[1]; // Extract deviceId from guest_{deviceId}_{timestamp}
                                }
                                else
                                {
                                    sessionId = oldGuestSession.SessionId; // Use as-is if not old format
                                }
                            }

                            // Check if already migrated
                            var exists = await context.UserSessions.AnyAsync(s => s.DeviceId == sessionId);
                            if (!exists)
                            {
                                var userSession = new UserSession
                                {
                                    DeviceId = sessionId, // Use device ID directly (new format)
                                    DeviceName = oldGuestSession.DeviceName ?? "Guest Device",
                                    IpAddress = oldGuestSession.IpAddress ?? string.Empty,
                                    OperatingSystem = oldGuestSession.OperatingSystem ?? string.Empty,
                                    Browser = oldGuestSession.Browser ?? string.Empty,
                                    IsGuest = true,
                                    CreatedAtUtc = oldGuestSession.CreatedAt,
                                    ExpiresAtUtc = oldGuestSession.ExpiresAt,
                                    LastSeenAtUtc = oldGuestSession.LastSeenAt ?? oldGuestSession.CreatedAt,
                                    IsRevoked = oldGuestSession.IsRevoked,
                                    RevokedAtUtc = oldGuestSession.RevokedAt,
                                    RevokedBy = oldGuestSession.RevokedBy
                                };

                                context.UserSessions.Add(userSession);
                                guestSessionsImported++;
                            }

                            // Delete old file after migration
                            File.Delete(filePath);
                            filesDeleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to migrate guest session file: {FilePath}", filePath);
                    }
                }

                // Try to delete the guest_sessions directory if empty
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(guestSessionsDirectory).Any())
                    {
                        Directory.Delete(guestSessionsDirectory);
                        _logger.LogInformation("Deleted empty guest_sessions directory");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete guest_sessions directory");
                }
            }

            // Save all migrations to database
            if (devicesImported > 0 || guestSessionsImported > 0)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation(
                    "Migration complete: {DevicesImported} devices, {GuestSessionsImported} guest sessions imported, {FilesDeleted} files deleted",
                    devicesImported, guestSessionsImported, filesDeleted);
            }
            else
            {
                _logger.LogInformation("No old sessions found to migrate");
            }

            return (devicesImported, guestSessionsImported, filesDeleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session migration");
            throw;
        }
    }

    // Old data structures for deserialization
    private class OldDeviceRegistration
    {
        public string DeviceId { get; set; } = string.Empty;
        public string EncryptedApiKey { get; set; } = string.Empty;
        public DateTime RegisteredAt { get; set; }
        public string? DeviceName { get; set; }
        public string? IpAddress { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
        public DateTime? LastSeenAt { get; set; }
    }

    private class OldGuestSession
    {
        public string SessionId { get; set; } = string.Empty;
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? IpAddress { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public bool IsRevoked { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? RevokedBy { get; set; }
    }
}
