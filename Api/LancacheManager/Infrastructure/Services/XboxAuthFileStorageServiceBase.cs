using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Shared file-backed Xbox auth storage with encrypted refresh token and device key under the security directory.
/// </summary>
public abstract class XboxAuthFileStorageServiceBase
{
    private readonly ILogger _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _xboxAuthDirectory;
    private readonly string _xboxAuthFilePath;
    private readonly object _lock = new object();
    private XboxAuthData? _cachedData;

    protected XboxAuthFileStorageServiceBase(
        ILogger logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;

        var securityDir = _pathResolver.GetSecurityDirectory();
        _xboxAuthDirectory = Path.Combine(securityDir, AuthDirectoryName);
        _xboxAuthFilePath = Path.Combine(_xboxAuthDirectory, "credentials.json");

        EnsureDirectoryExists();
    }

    protected abstract string AuthDirectoryName { get; }

    protected string AuthDataLabel => AuthDirectoryName switch
    {
        "xbox_auth" => "Xbox",
        "scheduled_prefill_xbox_auth" => "scheduled prefill Xbox",
        _ => AuthDirectoryName.Replace('_', ' ')
    };

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_xboxAuthDirectory))
            {
                Directory.CreateDirectory(_xboxAuthDirectory);
                _logger.LogInformation("Created {DirectoryName} directory: {Directory}", AuthDirectoryName, _xboxAuthDirectory);

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(
                            _xboxAuthDirectory,
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
                    var decryptedDeviceKey = _encryption.Decrypt(persisted.DeviceKeyPkcs8);

                    var refreshTokenDecryptFailed = decryptedRefreshToken == null
                        && !string.IsNullOrEmpty(persisted.RefreshToken);
                    var deviceKeyDecryptFailed = decryptedDeviceKey == null
                        && !string.IsNullOrEmpty(persisted.DeviceKeyPkcs8);

                    if (refreshTokenDecryptFailed || deviceKeyDecryptFailed)
                    {
                        _logger.LogWarning(
                            "Failed to decrypt {AuthDataLabel} credentials - clearing invalid credentials file.",
                            AuthDataLabel);

                        try
                        {
                            File.Delete(_xboxAuthFilePath);
                            _logger.LogInformation("Deleted invalid {AuthDataLabel} auth file", AuthDataLabel);
                        }
                        catch (Exception deleteEx)
                        {
                            _logger.LogWarning(deleteEx, "Failed to delete invalid {AuthDataLabel} auth file", AuthDataLabel);
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
                _logger.LogError(ex, "Failed to load {AuthDataLabel} auth data, using default", AuthDataLabel);
                _cachedData = new XboxAuthData();
                return _cachedData;
            }
        }
    }

    public void SaveAuthData(XboxAuthData data)
    {
        lock (_lock)
        {
            try
            {
                EnsureDirectoryExists();

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
                        File.SetUnixFileMode(_xboxAuthFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    public void UpdateAuthData(Action<XboxAuthData> updater)
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
                if (File.Exists(_xboxAuthFilePath))
                {
                    File.Delete(_xboxAuthFilePath);
                    _logger.LogInformation("Deleted {AuthDataLabel} credentials file: {Path}", AuthDataLabel, _xboxAuthFilePath);
                }

                _cachedData = new XboxAuthData();
                _logger.LogInformation("Cleared {AuthDataLabel} authentication data", AuthDataLabel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear {AuthDataLabel} auth data", AuthDataLabel);
                _cachedData = new XboxAuthData();
                throw;
            }
        }
    }

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

    public string GetCredentialsFilePath() => _xboxAuthFilePath;

    public string GetAuthDirectory() => _xboxAuthDirectory;
}
