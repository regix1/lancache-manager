using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for managing Xbox / Microsoft account authentication credentials in a separate encrypted file.
/// Mirrors <see cref="EpicAuthStorageService"/> but encrypts TWO secret fields: the MSA
/// <c>RefreshToken</c> and the stable device-identity <c>DeviceKeyPkcs8</c> (the ECDSA private key the
/// request signer reuses across restarts).
/// </summary>
public class XboxAuthStorageService
{
    private readonly ILogger<XboxAuthStorageService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _xboxAuthDirectory;
    private readonly string _xboxAuthFilePath;
    private readonly object _lock = new object();
    private XboxAuthData? _cachedData;

    public XboxAuthStorageService(
        ILogger<XboxAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;

        // Create xbox_auth directory in security folder
        var securityDir = _pathResolver.GetSecurityDirectory();
        _xboxAuthDirectory = Path.Combine(securityDir, "xbox_auth");
        _xboxAuthFilePath = Path.Combine(_xboxAuthDirectory, "credentials.json");

        // Ensure xbox_auth directory exists
        EnsureDirectoryExists();
    }

    /// <summary>
    /// Ensures the xbox_auth directory exists
    /// </summary>
    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_xboxAuthDirectory))
            {
                Directory.CreateDirectory(_xboxAuthDirectory);
                _logger.LogInformation("Created xbox_auth directory: {Directory}", _xboxAuthDirectory);

                // Set directory permissions (Windows: inherits ACLs from parent, Linux: 700)
                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(_xboxAuthDirectory,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on xbox_auth directory");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create xbox_auth directory");
            throw;
        }
    }

    /// <summary>
    /// Gets the current Xbox auth data (with decrypted fields)
    /// </summary>
    public XboxAuthData GetAuthData()
    {
        lock (_lock)
        {
            if (_cachedData != null)
            {
                return _cachedData;
            }

            try
            {
                if (File.Exists(_xboxAuthFilePath))
                {
                    var json = File.ReadAllText(_xboxAuthFilePath);
                    var persisted = JsonSerializer.Deserialize<PersistedXboxAuthData>(json) ?? new PersistedXboxAuthData();

                    // Convert persisted data to in-memory data, decrypting sensitive fields
                    var decryptedRefreshToken = _encryption.Decrypt(persisted.RefreshToken);
                    var decryptedDeviceKey = _encryption.Decrypt(persisted.DeviceKeyPkcs8);

                    // Check if decryption failed for any encrypted field
                    var refreshTokenDecryptFailed = decryptedRefreshToken == null && !string.IsNullOrEmpty(persisted.RefreshToken);
                    var deviceKeyDecryptFailed = decryptedDeviceKey == null && !string.IsNullOrEmpty(persisted.DeviceKeyPkcs8);

                    if (refreshTokenDecryptFailed || deviceKeyDecryptFailed)
                    {
                        _logger.LogWarning("Failed to decrypt Xbox credentials - clearing invalid credentials file.");

                        // Clear the invalid encrypted file
                        try
                        {
                            File.Delete(_xboxAuthFilePath);
                            _logger.LogInformation("Deleted invalid Xbox auth file");
                        }
                        catch (Exception deleteEx)
                        {
                            _logger.LogWarning(deleteEx, "Failed to delete invalid Xbox auth file");
                        }

                        _cachedData = new XboxAuthData();
                        return _cachedData;
                    }

                    _cachedData = new XboxAuthData
                    {
                        RefreshToken = decryptedRefreshToken,
                        DeviceKeyPkcs8 = decryptedDeviceKey,
                        DisplayName = persisted.DisplayName,
                        Xuid = persisted.Xuid,
                        LastAuthenticated = persisted.LastAuthenticated,
                        GamesDiscovered = persisted.GamesDiscovered
                    };
                }
                else
                {
                    _cachedData = new XboxAuthData();
                }

                return _cachedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Xbox auth data, using default");
                _cachedData = new XboxAuthData();
                return _cachedData;
            }
        }
    }

    /// <summary>
    /// Saves Xbox auth data (encrypts sensitive fields using Microsoft Data Protection API)
    /// </summary>
    public void SaveAuthData(XboxAuthData data)
    {
        lock (_lock)
        {
            try
            {
                // Ensure directory exists
                EnsureDirectoryExists();

                // Convert to persisted data with encrypted sensitive fields
                var persisted = new PersistedXboxAuthData
                {
                    RefreshToken = _encryption.Encrypt(data.RefreshToken),
                    DeviceKeyPkcs8 = _encryption.Encrypt(data.DeviceKeyPkcs8),
                    DisplayName = data.DisplayName,
                    Xuid = data.Xuid,
                    LastAuthenticated = data.LastAuthenticated,
                    GamesDiscovered = data.GamesDiscovered
                };

                var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });

                // Write to temp file first then move (atomic operation)
                var tempFile = _xboxAuthFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // Force flush to disk
                using (var fs = File.OpenWrite(tempFile))
                {
                    fs.Flush(true);
                }

                // Atomically replace the old file
                File.Move(tempFile, _xboxAuthFilePath, true);

                // Set file permissions (Linux: 600)
                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(_xboxAuthFilePath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        _logger.LogTrace("Xbox auth file permissions set to 600 (owner read/write only)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on xbox_auth file");
                    }
                }

                _cachedData = data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save Xbox auth data");
                throw;
            }
        }
    }

    /// <summary>
    /// Updates a specific part of Xbox auth data
    /// </summary>
    public void UpdateAuthData(Action<XboxAuthData> updater)
    {
        lock (_lock)
        {
            var data = GetAuthData();
            updater(data);
            SaveAuthData(data);
        }
    }

    /// <summary>
    /// Clears all Xbox authentication data (logs out)
    /// </summary>
    public void ClearAuthData()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_xboxAuthFilePath))
                {
                    File.Delete(_xboxAuthFilePath);
                    _logger.LogInformation("Deleted Xbox credentials file: {Path}", _xboxAuthFilePath);
                }

                _cachedData = new XboxAuthData();
                _logger.LogInformation("Cleared Xbox authentication data");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear Xbox auth data");
                // Even if file deletion fails, clear the in-memory cache
                _cachedData = new XboxAuthData();
                throw;
            }
        }
    }

    /// <summary>
    /// Returns true if the credentials file exists and has a non-null RefreshToken
    /// </summary>
    public bool HasSavedCredentials()
    {
        try
        {
            if (!File.Exists(_xboxAuthFilePath))
                return false;

            var data = GetAuthData();
            return !string.IsNullOrEmpty(data.RefreshToken);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the path to the Xbox auth credentials file (for logging/debugging)
    /// </summary>
    public string GetCredentialsFilePath() => _xboxAuthFilePath;

    /// <summary>
    /// Gets the path to the Xbox auth directory (for logging/debugging)
    /// </summary>
    public string GetAuthDirectory() => _xboxAuthDirectory;
}
