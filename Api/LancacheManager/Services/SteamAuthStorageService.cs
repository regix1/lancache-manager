using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace LancacheManager.Services;

/// <summary>
/// Manages Steam authentication credentials in a separate encrypted file
/// Uses Microsoft ASP.NET Core Data Protection API with API key as part of encryption
/// </summary>
public class SteamAuthStorageService
{
    private readonly ILogger<SteamAuthStorageService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _steamAuthDirectory;
    private readonly string _steamAuthFilePath;
    private readonly object _lock = new object();
    private SteamAuthData? _cachedData;

    public SteamAuthStorageService(
        ILogger<SteamAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;

        // Create steam_auth directory in data folder
        var dataDir = _pathResolver.GetDataDirectory();
        _steamAuthDirectory = Path.Combine(dataDir, "steam_auth");
        _steamAuthFilePath = Path.Combine(_steamAuthDirectory, "credentials.json");

        // Ensure steam_auth directory exists
        EnsureDirectoryExists();
    }

    /// <summary>
    /// Steam authentication data (decrypted in memory)
    /// NOTE: GuardData is NOT stored - modern Steam auth uses refresh tokens only
    /// </summary>
    public class SteamAuthData
    {
        public string Mode { get; set; } = "anonymous"; // "anonymous" or "authenticated"
        public string? Username { get; set; }
        public string? RefreshToken { get; set; } // Decrypted in memory, encrypted in storage
        public DateTime? LastAuthenticated { get; set; }
    }

    /// <summary>
    /// Internal class for JSON serialization with encrypted fields
    /// NOTE: GuardData is NOT stored - modern Steam auth uses refresh tokens only
    /// </summary>
    private class PersistedSteamAuthData
    {
        public string Mode { get; set; } = "anonymous";
        public string? Username { get; set; }
        public string? RefreshToken { get; set; } // Encrypted with ENC2: prefix
        public DateTime? LastAuthenticated { get; set; }
    }

    /// <summary>
    /// Ensures the steam_auth directory exists
    /// </summary>
    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_steamAuthDirectory))
            {
                Directory.CreateDirectory(_steamAuthDirectory);
                _logger.LogInformation("Created steam_auth directory: {Directory}", _steamAuthDirectory);

                // Set directory permissions (Windows: restrict to current user, Linux: 700)
                if (OperatingSystem.IsWindows())
                {
                    // On Windows, the directory inherits ACLs from parent, which is fine for most cases
                    _logger.LogDebug("Steam auth directory created (Windows - using inherited ACLs)");
                }
                else
                {
                    // On Linux/Unix, set permissions to 700 (owner only)
                    try
                    {
                        var dirInfo = new DirectoryInfo(_steamAuthDirectory);
                        // Use chmod via UnixFileMode (available in .NET 6+)
                        File.SetUnixFileMode(_steamAuthDirectory,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        _logger.LogDebug("Steam auth directory permissions set to 700 (owner only)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on steam_auth directory");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create steam_auth directory");
            throw;
        }
    }

    /// <summary>
    /// Gets the current Steam auth data (with decrypted fields)
    /// </summary>
    public SteamAuthData GetSteamAuthData()
    {
        lock (_lock)
        {
            if (_cachedData != null)
            {
                return _cachedData;
            }

            try
            {
                if (File.Exists(_steamAuthFilePath))
                {
                    var json = File.ReadAllText(_steamAuthFilePath);
                    var persisted = JsonSerializer.Deserialize<PersistedSteamAuthData>(json) ?? new PersistedSteamAuthData();

                    // Convert persisted data to in-memory data, decrypting sensitive fields
                    _cachedData = new SteamAuthData
                    {
                        Mode = persisted.Mode,
                        Username = persisted.Username,
                        // Decrypt using Microsoft Data Protection API with API key
                        RefreshToken = _encryption.Decrypt(persisted.RefreshToken),
                        LastAuthenticated = persisted.LastAuthenticated
                    };

                    _logger.LogDebug("Loaded Steam auth data from encrypted file");
                }
                else
                {
                    _cachedData = new SteamAuthData();
                    _logger.LogDebug("No Steam auth file found, using default anonymous mode");
                }

                return _cachedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Steam auth data, using default");
                _cachedData = new SteamAuthData();
                return _cachedData;
            }
        }
    }

    /// <summary>
    /// Saves Steam auth data (encrypts sensitive fields using Microsoft Data Protection API)
    /// </summary>
    public void SaveSteamAuthData(SteamAuthData data)
    {
        lock (_lock)
        {
            try
            {
                // Ensure directory exists
                EnsureDirectoryExists();

                // Convert to persisted data with encrypted sensitive fields
                var persisted = new PersistedSteamAuthData
                {
                    Mode = data.Mode,
                    Username = data.Username,
                    // Encrypt using Microsoft Data Protection API with API key as part of encryption
                    RefreshToken = _encryption.Encrypt(data.RefreshToken),
                    LastAuthenticated = data.LastAuthenticated
                };

                var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });

                // Write to temp file first then move (atomic operation)
                var tempFile = _steamAuthFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // Force flush to disk
                using (var fs = File.OpenWrite(tempFile))
                {
                    fs.Flush(true);
                }

                // Atomically replace the old file
                File.Move(tempFile, _steamAuthFilePath, true);

                // Set file permissions (Windows: current user only, Linux: 600)
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(_steamAuthFilePath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        _logger.LogTrace("Steam auth file permissions set to 600 (owner read/write only)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on steam_auth file");
                    }
                }

                _cachedData = data;
                _logger.LogDebug("Saved Steam auth data to encrypted file with Microsoft Data Protection API");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save Steam auth data");
                throw;
            }
        }
    }

    /// <summary>
    /// Updates a specific part of Steam auth data
    /// </summary>
    public void UpdateSteamAuthData(Action<SteamAuthData> updater)
    {
        lock (_lock)
        {
            var data = GetSteamAuthData();
            updater(data);
            SaveSteamAuthData(data);
        }
    }

    /// <summary>
    /// Clears all Steam authentication data (logs out)
    /// </summary>
    public void ClearSteamAuthData()
    {
        lock (_lock)
        {
            try
            {
                var defaultData = new SteamAuthData();
                SaveSteamAuthData(defaultData);
                _cachedData = defaultData;
                _logger.LogInformation("Cleared Steam authentication data");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear Steam auth data");
                throw;
            }
        }
    }

    /// <summary>
    /// Migrates Steam auth data from old state.json to new separate file
    /// NOTE: GuardData is NOT migrated - modern Steam auth uses refresh tokens only
    /// </summary>
    public void MigrateFromStateJson(StateService.SteamAuthState? oldAuthState)
    {
        if (oldAuthState == null)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                // Only migrate if we don't already have data
                if (File.Exists(_steamAuthFilePath))
                {
                    _logger.LogDebug("Steam auth file already exists, skipping migration");
                    return;
                }

                // Check if there's actually data to migrate (only refresh token matters)
                if (string.IsNullOrEmpty(oldAuthState.RefreshToken) && oldAuthState.Mode == "anonymous")
                {
                    _logger.LogDebug("No Steam auth data to migrate from state.json");
                    return;
                }

                _logger.LogInformation("Migrating Steam auth data from state.json to separate encrypted file");

                var newData = new SteamAuthData
                {
                    Mode = oldAuthState.Mode,
                    Username = oldAuthState.Username,
                    RefreshToken = oldAuthState.RefreshToken,
                    // GuardData is NOT migrated - modern auth uses refresh tokens only
                    LastAuthenticated = oldAuthState.LastAuthenticated
                };

                SaveSteamAuthData(newData);
                _logger.LogInformation("Successfully migrated Steam auth data to separate file (refresh token only)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate Steam auth data from state.json");
            }
        }
    }

    /// <summary>
    /// Gets the path to the Steam auth credentials file (for logging/debugging)
    /// </summary>
    public string GetCredentialsFilePath() => _steamAuthFilePath;

    /// <summary>
    /// Gets the path to the Steam auth directory (for logging/debugging)
    /// </summary>
    public string GetAuthDirectory() => _steamAuthDirectory;
}
