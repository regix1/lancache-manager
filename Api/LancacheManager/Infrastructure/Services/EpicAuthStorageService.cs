using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for managing Epic Games authentication credentials in a separate encrypted file.
/// Mirrors SteamAuthStorageService but only encrypts the RefreshToken field.
/// </summary>
public class EpicAuthStorageService
{
    private readonly ILogger<EpicAuthStorageService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _epicAuthDirectory;
    private readonly string _epicAuthFilePath;
    private readonly object _lock = new object();
    private EpicAuthData? _cachedData;

    public EpicAuthStorageService(
        ILogger<EpicAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;

        // Create epic_auth directory in security folder
        var securityDir = _pathResolver.GetSecurityDirectory();
        _epicAuthDirectory = Path.Combine(securityDir, "epic_auth");
        _epicAuthFilePath = Path.Combine(_epicAuthDirectory, "credentials.json");

        // Ensure epic_auth directory exists
        EnsureDirectoryExists();
    }

    /// <summary>
    /// Ensures the epic_auth directory exists
    /// </summary>
    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_epicAuthDirectory))
            {
                Directory.CreateDirectory(_epicAuthDirectory);
                _logger.LogInformation("Created epic_auth directory: {Directory}", _epicAuthDirectory);

                // Set directory permissions (Windows: restrict to current user, Linux: 700)
                if (OperatingSystem.IsWindows())
                {
                    // On Windows, the directory inherits ACLs from parent, which is fine for most cases
                }
                else if (OperatingSystem.IsLinux())
                {
                    // On Linux/Unix, set permissions to 700 (owner only)
                    try
                    {
                        File.SetUnixFileMode(_epicAuthDirectory,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on epic_auth directory");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create epic_auth directory");
            throw;
        }
    }

    /// <summary>
    /// Gets the current Epic auth data (with decrypted fields)
    /// </summary>
    public EpicAuthData GetEpicAuthData()
    {
        lock (_lock)
        {
            if (_cachedData != null)
            {
                return _cachedData;
            }

            try
            {
                if (File.Exists(_epicAuthFilePath))
                {
                    var json = File.ReadAllText(_epicAuthFilePath);
                    var persisted = JsonSerializer.Deserialize<PersistedEpicAuthData>(json) ?? new PersistedEpicAuthData();

                    // Convert persisted data to in-memory data, decrypting sensitive fields
                    var decryptedRefreshToken = _encryption.Decrypt(persisted.RefreshToken);

                    // Check if decryption failed for encrypted fields
                    var refreshTokenDecryptFailed = decryptedRefreshToken == null && !string.IsNullOrEmpty(persisted.RefreshToken);

                    if (refreshTokenDecryptFailed)
                    {
                        _logger.LogWarning("Failed to decrypt Epic refresh token - clearing invalid credentials file.");

                        // Clear the invalid encrypted file
                        try
                        {
                            File.Delete(_epicAuthFilePath);
                            _logger.LogInformation("Deleted invalid Epic auth file");
                        }
                        catch (Exception deleteEx)
                        {
                            _logger.LogWarning(deleteEx, "Failed to delete invalid Epic auth file");
                        }

                        _cachedData = new EpicAuthData();
                        return _cachedData;
                    }

                    _cachedData = new EpicAuthData
                    {
                        RefreshToken = decryptedRefreshToken,
                        DisplayName = persisted.DisplayName,
                        AccountId = persisted.AccountId,
                        LastAuthenticated = persisted.LastAuthenticated,
                        GamesDiscovered = persisted.GamesDiscovered
                    };
                }
                else
                {
                    _cachedData = new EpicAuthData();
                }

                return _cachedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Epic auth data, using default");
                _cachedData = new EpicAuthData();
                return _cachedData;
            }
        }
    }

    /// <summary>
    /// Saves Epic auth data (encrypts sensitive fields using Microsoft Data Protection API)
    /// </summary>
    public void SaveEpicAuthData(EpicAuthData data)
    {
        lock (_lock)
        {
            try
            {
                // Ensure directory exists
                EnsureDirectoryExists();

                // Convert to persisted data with encrypted sensitive fields
                var persisted = new PersistedEpicAuthData
                {
                    RefreshToken = _encryption.Encrypt(data.RefreshToken),
                    DisplayName = data.DisplayName,
                    AccountId = data.AccountId,
                    LastAuthenticated = data.LastAuthenticated,
                    GamesDiscovered = data.GamesDiscovered
                };

                var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });

                // Write to temp file first then move (atomic operation)
                var tempFile = _epicAuthFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // Force flush to disk
                using (var fs = File.OpenWrite(tempFile))
                {
                    fs.Flush(true);
                }

                // Atomically replace the old file
                File.Move(tempFile, _epicAuthFilePath, true);

                // Set file permissions (Windows: current user only, Linux: 600)
                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(_epicAuthFilePath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        _logger.LogTrace("Epic auth file permissions set to 600 (owner read/write only)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on epic_auth file");
                    }
                }

                _cachedData = data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save Epic auth data");
                throw;
            }
        }
    }

    /// <summary>
    /// Updates a specific part of Epic auth data
    /// </summary>
    public void UpdateEpicAuthData(Action<EpicAuthData> updater)
    {
        lock (_lock)
        {
            var data = GetEpicAuthData();
            updater(data);
            SaveEpicAuthData(data);
        }
    }

    /// <summary>
    /// Clears all Epic authentication data (logs out)
    /// </summary>
    public void ClearEpicAuthData()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_epicAuthFilePath))
                {
                    File.Delete(_epicAuthFilePath);
                    _logger.LogInformation("Deleted Epic credentials file: {Path}", _epicAuthFilePath);
                }

                _cachedData = new EpicAuthData();
                _logger.LogInformation("Cleared Epic authentication data");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear Epic auth data");
                // Even if file deletion fails, clear the in-memory cache
                _cachedData = new EpicAuthData();
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
            if (!File.Exists(_epicAuthFilePath))
                return false;

            var data = GetEpicAuthData();
            return !string.IsNullOrEmpty(data.RefreshToken);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the path to the Epic auth credentials file (for logging/debugging)
    /// </summary>
    public string GetCredentialsFilePath() => _epicAuthFilePath;

    /// <summary>
    /// Gets the path to the Epic auth directory (for logging/debugging)
    /// </summary>
    public string GetAuthDirectory() => _epicAuthDirectory;
}
