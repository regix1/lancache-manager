using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Shared file-backed Epic auth storage with encrypted refresh tokens under the security directory.
/// </summary>
public abstract class EpicAuthFileStorageServiceBase
{
    private readonly ILogger _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _epicAuthDirectory;
    private readonly string _epicAuthFilePath;
    private readonly object _lock = new object();
    private EpicAuthData? _cachedData;

    protected EpicAuthFileStorageServiceBase(
        ILogger logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;

        var securityDir = _pathResolver.GetSecurityDirectory();
        _epicAuthDirectory = Path.Combine(securityDir, AuthDirectoryName);
        _epicAuthFilePath = Path.Combine(_epicAuthDirectory, "credentials.json");

        EnsureDirectoryExists();
    }

    protected abstract string AuthDirectoryName { get; }

    protected string AuthDataLabel => AuthDirectoryName switch
    {
        "epic_auth" => "Epic",
        "scheduled_prefill_epic_auth" => "scheduled prefill Epic",
        _ => AuthDirectoryName.Replace('_', ' ')
    };

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_epicAuthDirectory))
            {
                Directory.CreateDirectory(_epicAuthDirectory);
                _logger.LogInformation("Created {DirectoryName} directory: {Directory}", AuthDirectoryName, _epicAuthDirectory);

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(
                            _epicAuthDirectory,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on {DirectoryName} directory", AuthDirectoryName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create {DirectoryName} directory", AuthDirectoryName);
            throw;
        }
    }

    public EpicAuthData GetAuthData()
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

                    var decryptedRefreshToken = _encryption.Decrypt(persisted.RefreshToken);
                    var refreshTokenDecryptFailed = decryptedRefreshToken == null
                        && !string.IsNullOrEmpty(persisted.RefreshToken);

                    if (refreshTokenDecryptFailed)
                    {
                        _logger.LogWarning(
                            "Failed to decrypt {AuthDataLabel} refresh token - clearing invalid credentials file.",
                            AuthDataLabel);

                        try
                        {
                            File.Delete(_epicAuthFilePath);
                            _logger.LogInformation("Deleted invalid {AuthDataLabel} auth file", AuthDataLabel);
                        }
                        catch (Exception deleteEx)
                        {
                            _logger.LogWarning(deleteEx, "Failed to delete invalid {AuthDataLabel} auth file", AuthDataLabel);
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
                _logger.LogError(ex, "Failed to load {AuthDataLabel} auth data, using default", AuthDataLabel);
                _cachedData = new EpicAuthData();
                return _cachedData;
            }
        }
    }

    public void SaveAuthData(EpicAuthData data)
    {
        lock (_lock)
        {
            try
            {
                EnsureDirectoryExists();

                var persisted = new PersistedEpicAuthData
                {
                    RefreshToken = _encryption.Encrypt(data.RefreshToken),
                    DisplayName = data.DisplayName,
                    AccountId = data.AccountId,
                    LastAuthenticated = data.LastAuthenticated,
                    GamesDiscovered = data.GamesDiscovered
                };

                var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });

                var tempFile = _epicAuthFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                using (var fs = File.OpenWrite(tempFile))
                {
                    fs.Flush(true);
                }

                File.Move(tempFile, _epicAuthFilePath, true);

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(_epicAuthFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on {AuthDataLabel} auth file", AuthDataLabel);
                    }
                }

                _cachedData = data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save {AuthDataLabel} auth data", AuthDataLabel);
                throw;
            }
        }
    }

    public void UpdateAuthData(Action<EpicAuthData> updater)
    {
        lock (_lock)
        {
            var data = GetAuthData();
            updater(data);
            SaveAuthData(data);
        }
    }

    public void ClearAuthData()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_epicAuthFilePath))
                {
                    File.Delete(_epicAuthFilePath);
                    _logger.LogInformation("Deleted {AuthDataLabel} credentials file: {Path}", AuthDataLabel, _epicAuthFilePath);
                }

                _cachedData = new EpicAuthData();
                _logger.LogInformation("Cleared {AuthDataLabel} authentication data", AuthDataLabel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear {AuthDataLabel} auth data", AuthDataLabel);
                _cachedData = new EpicAuthData();
                throw;
            }
        }
    }

    public bool HasSavedCredentials()
    {
        try
        {
            if (!File.Exists(_epicAuthFilePath))
                return false;

            var data = GetAuthData();
            return !string.IsNullOrEmpty(data.RefreshToken);
        }
        catch
        {
            return false;
        }
    }

    public string GetCredentialsFilePath() => _epicAuthFilePath;

    public string GetAuthDirectory() => _epicAuthDirectory;
}
