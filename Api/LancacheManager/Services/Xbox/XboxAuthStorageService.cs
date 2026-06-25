using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// Persists the manager's own Xbox / Microsoft Store refresh token (used by the mapping path to
/// query owned-title metadata) in a separate encrypted file under the security directory.
///
/// Only the refresh token is sensitive, so it is the ONLY field encrypted - via
/// <see cref="SecureStateEncryptionService"/> (ASP.NET Core Data Protection, API-key-bound), NOT
/// DPAPI (DPAPI is Windows-only and this app ships as a Linux container). The directory is created
/// 700 and the file 600 on Linux, mirroring <c>SteamAuthStorageService</c>.
/// </summary>
public class XboxAuthStorageService
{
    private readonly ILogger<XboxAuthStorageService> _logger;
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _xboxAuthDirectory;
    private readonly string _xboxAuthFilePath;
    private readonly object _lock = new();
    private XboxAuthData? _cachedData;

    public XboxAuthStorageService(
        ILogger<XboxAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _encryption = encryption;

        var securityDir = pathResolver.GetSecurityDirectory();
        _xboxAuthDirectory = Path.Combine(securityDir, "xbox_auth");
        _xboxAuthFilePath = Path.Combine(_xboxAuthDirectory, "credentials.json");

        EnsureDirectoryExists();
    }

    /// <summary>Path to the credentials file (for logging/diagnostics only - never the token itself).</summary>
    public string FilePath => _xboxAuthFilePath;

    /// <summary>Ensures the xbox_auth directory exists with 700 permissions on Linux.</summary>
    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_xboxAuthDirectory))
            {
                Directory.CreateDirectory(_xboxAuthDirectory);
                _logger.LogInformation("Created xbox_auth directory: {Directory}", _xboxAuthDirectory);

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        // 700 - owner read/write/execute only.
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

    /// <summary>Returns the stored Xbox auth data with the refresh token decrypted, or an empty record.</summary>
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

                    var decryptedRefreshToken = _encryption.Decrypt(persisted.RefreshToken);
                    var refreshTokenDecryptFailed = decryptedRefreshToken == null && !string.IsNullOrEmpty(persisted.RefreshToken);

                    if (refreshTokenDecryptFailed)
                    {
                        // Decryption failed (e.g. API key rotated) - clear the invalid file so the
                        // manager re-authenticates rather than looping on an undecryptable token.
                        _logger.LogWarning("Failed to decrypt Xbox refresh token - clearing invalid credentials file.");
                        try
                        {
                            File.Delete(_xboxAuthFilePath);
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
                        DisplayName = persisted.DisplayName,
                        RefreshToken = decryptedRefreshToken,
                        LastAuthenticatedUtc = persisted.LastAuthenticatedUtc
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

    /// <summary>Encrypts the refresh token and atomically persists the Xbox auth data (file 600 on Linux).</summary>
    public void SaveAuthData(XboxAuthData data)
    {
        lock (_lock)
        {
            try
            {
                EnsureDirectoryExists();

                var persisted = new PersistedXboxAuthData
                {
                    DisplayName = data.DisplayName,
                    // Encrypt ONLY the refresh token (API-key-bound Data Protection, not DPAPI).
                    RefreshToken = _encryption.Encrypt(data.RefreshToken),
                    LastAuthenticatedUtc = data.LastAuthenticatedUtc
                };

                var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });

                // Write to a temp file then atomically move into place.
                var tempFile = _xboxAuthFilePath + ".tmp";
                File.WriteAllText(tempFile, json);
                using (var fs = File.OpenWrite(tempFile))
                {
                    fs.Flush(true);
                }
                File.Move(tempFile, _xboxAuthFilePath, true);

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        // 600 - owner read/write only.
                        File.SetUnixFileMode(_xboxAuthFilePath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    /// <summary>Clears the stored Xbox refresh token (logout) and removes the credentials file.</summary>
    public void ClearAuthData()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_xboxAuthFilePath))
                {
                    File.Delete(_xboxAuthFilePath);
                }
                _cachedData = new XboxAuthData();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear Xbox auth data");
            }
        }
    }

    /// <summary>In-memory Xbox auth data (refresh token decrypted).</summary>
    public sealed class XboxAuthData
    {
        public string? DisplayName { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime? LastAuthenticatedUtc { get; set; }
    }

    /// <summary>On-disk shape (refresh token stored encrypted with the ENC2: prefix).</summary>
    private sealed class PersistedXboxAuthData
    {
        public string? DisplayName { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime? LastAuthenticatedUtc { get; set; }
    }
}
