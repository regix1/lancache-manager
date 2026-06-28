using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Shared file-backed Steam auth storage with encrypted credentials under the security directory.
/// </summary>
public abstract class SteamAuthFileStorageServiceBase
{
    private readonly ILogger _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _steamAuthDirectory;
    private readonly string _steamAuthFilePath;
    private readonly object _lock = new object();
    private SteamAuthData? _cachedData;

    protected SteamAuthFileStorageServiceBase(
        ILogger logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;

        var securityDir = _pathResolver.GetSecurityDirectory();
        _steamAuthDirectory = Path.Combine(securityDir, AuthDirectoryName);
        _steamAuthFilePath = Path.Combine(_steamAuthDirectory, "credentials.json");

        EnsureDirectoryExists();
    }

    protected abstract string AuthDirectoryName { get; }

    protected virtual bool IncludeSteamApiKey => AuthDirectoryName == "steam_auth";

    protected virtual bool LogFilePermissionTrace => AuthDirectoryName == "steam_auth";

    protected virtual string AuthDataLabel => AuthDirectoryName switch
    {
        "steam_auth" => "Steam",
        "scheduled_prefill_steam_auth" => "scheduled prefill Steam",
        _ => AuthDirectoryName.Replace('_', ' ')
    };

    protected string PathLabel => AuthDirectoryName;

    protected virtual string FilePermissionLogLabel =>
        IncludeSteamApiKey ? PathLabel : $"{AuthDataLabel} auth";

    protected object SyncRoot => _lock;

    protected string AuthFilePath => _steamAuthFilePath;

    protected ILogger Logger => _logger;

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_steamAuthDirectory))
            {
                Directory.CreateDirectory(_steamAuthDirectory);
                _logger.LogInformation("Created {PathLabel} directory: {Directory}", PathLabel, _steamAuthDirectory);

                if (OperatingSystem.IsWindows())
                {
                    // On Windows, the directory inherits ACLs from parent, which is fine for most cases
                }
                else if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(
                            _steamAuthDirectory,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on {PathLabel} directory", PathLabel);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create {PathLabel} directory", PathLabel);
            throw;
        }
    }

    public SteamAuthData GetAuthData()
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

                    var decryptedRefreshToken = _encryption.Decrypt(persisted.RefreshToken);
                    var refreshTokenDecryptFailed = decryptedRefreshToken == null
                        && !string.IsNullOrEmpty(persisted.RefreshToken);

                    string? decryptedApiKey = null;
                    var apiKeyDecryptFailed = false;

                    if (IncludeSteamApiKey)
                    {
                        decryptedApiKey = _encryption.Decrypt(persisted.SteamApiKey);
                        apiKeyDecryptFailed = decryptedApiKey == null && !string.IsNullOrEmpty(persisted.SteamApiKey);
                    }

                    if (refreshTokenDecryptFailed)
                    {
                        if (IncludeSteamApiKey)
                        {
                            _logger.LogWarning(
                                "Failed to decrypt Steam refresh token - you may need to re-authenticate with Steam.");
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Failed to decrypt {AuthDataLabel} refresh token - clearing invalid credentials file.",
                                AuthDataLabel);
                        }
                    }

                    if (apiKeyDecryptFailed)
                    {
                        _logger.LogWarning(
                            "Failed to decrypt Steam Web API key - you may need to reconfigure your API key.");
                    }

                    var shouldClearFile = IncludeSteamApiKey
                        ? refreshTokenDecryptFailed && apiKeyDecryptFailed
                        : refreshTokenDecryptFailed;

                    if (shouldClearFile)
                    {
                        if (IncludeSteamApiKey)
                        {
                            _logger.LogWarning("Failed to decrypt all Steam auth data - clearing invalid credentials file.");
                        }

                        try
                        {
                            File.Delete(_steamAuthFilePath);
                            _logger.LogInformation("Deleted invalid {AuthDataLabel} auth file", AuthDataLabel);
                        }
                        catch (Exception deleteEx)
                        {
                            _logger.LogWarning(deleteEx, "Failed to delete invalid {AuthDataLabel} auth file", AuthDataLabel);
                        }

                        _cachedData = new SteamAuthData();
                        return _cachedData;
                    }

                    _cachedData = new SteamAuthData
                    {
                        Mode = persisted.Mode,
                        Username = persisted.Username,
                        RefreshToken = decryptedRefreshToken,
                        LastAuthenticated = persisted.LastAuthenticated,
                        SteamApiKey = decryptedApiKey
                    };
                }
                else
                {
                    _cachedData = new SteamAuthData();
                }

                return _cachedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load {AuthDataLabel} auth data, using default", AuthDataLabel);
                _cachedData = new SteamAuthData();
                return _cachedData;
            }
        }
    }

    public void SaveAuthData(SteamAuthData data)
    {
        lock (_lock)
        {
            try
            {
                EnsureDirectoryExists();

                var persisted = new PersistedSteamAuthData
                {
                    Mode = data.Mode,
                    Username = data.Username,
                    RefreshToken = _encryption.Encrypt(data.RefreshToken),
                    LastAuthenticated = data.LastAuthenticated
                };

                if (IncludeSteamApiKey)
                {
                    persisted.SteamApiKey = _encryption.Encrypt(data.SteamApiKey);
                }

                var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });

                var tempFile = _steamAuthFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                using (var fs = File.OpenWrite(tempFile))
                {
                    fs.Flush(true);
                }

                File.Move(tempFile, _steamAuthFilePath, true);

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(_steamAuthFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                        if (LogFilePermissionTrace)
                        {
                            _logger.LogTrace("Steam auth file permissions set to 600 (owner read/write only)");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on {FilePermissionLogLabel} file", FilePermissionLogLabel);
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

    public void UpdateAuthData(Action<SteamAuthData> updater)
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
                var defaultData = new SteamAuthData();
                SaveAuthData(defaultData);
                _cachedData = defaultData;
                _logger.LogInformation("Cleared {AuthDataLabel} authentication data", AuthDataLabel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear {AuthDataLabel} auth data", AuthDataLabel);
                throw;
            }
        }
    }

    public string GetCredentialsFilePath() => _steamAuthFilePath;

    public string GetAuthDirectory() => _steamAuthDirectory;
}
