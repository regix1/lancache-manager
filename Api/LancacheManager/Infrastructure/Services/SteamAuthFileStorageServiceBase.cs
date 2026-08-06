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

    protected abstract string AuthDataLabel { get; }

    protected string PathLabel => AuthDirectoryName;

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

                    // A secret written to disk in the clear has to be treated as exposed, so the
                    // whole file goes and the user signs in again, which replaces it with a fresh
                    // one. Checked before decrypting so the reason logged is the real one.
                    if (_encryption.IsUnencrypted(persisted.RefreshToken)
                        || _encryption.IsUnencrypted(persisted.SteamApiKey))
                    {
                        _logger.LogWarning(
                            "{AuthDataLabel} credentials were stored unencrypted - they have been removed, sign in again to store them encrypted.",
                            AuthDataLabel);

                        DeleteCredentialsFile();

                        _cachedData = new SteamAuthData();
                        return _cachedData;
                    }

                    var decryptedRefreshToken = _encryption.Decrypt(persisted.RefreshToken);
                    var refreshTokenDecryptFailed = decryptedRefreshToken == null
                        && !string.IsNullOrEmpty(persisted.RefreshToken);

                    var decryptedApiKey = _encryption.Decrypt(persisted.SteamApiKey);
                    var apiKeyDecryptFailed = decryptedApiKey == null && !string.IsNullOrEmpty(persisted.SteamApiKey);

                    if (refreshTokenDecryptFailed)
                    {
                        _logger.LogWarning(
                            "Failed to decrypt Steam refresh token - you may need to re-authenticate with Steam.");
                    }

                    if (apiKeyDecryptFailed)
                    {
                        _logger.LogWarning(
                            "Failed to decrypt Steam Web API key - you may need to reconfigure your API key.");
                    }

                    // Only discard the file when every stored secret is unreadable: if one still
                    // decrypts, the user keeps that half of their configuration.
                    if (refreshTokenDecryptFailed && apiKeyDecryptFailed)
                    {
                        _logger.LogWarning("Failed to decrypt all Steam auth data - clearing invalid credentials file.");

                        DeleteCredentialsFile();

                        _cachedData = new SteamAuthData();
                        return _cachedData;
                    }

                    var loaded = new SteamAuthData
                    {
                        Mode = persisted.Mode,
                        Username = persisted.Username,
                        RefreshToken = decryptedRefreshToken,
                        LastAuthenticated = persisted.LastAuthenticated,
                        SteamApiKey = decryptedApiKey
                    };

                    _cachedData = loaded;

                    // Write a secret still held under the v1 key back out with the current one, but
                    // only when both secrets decrypted: rewriting after a partial failure would drop
                    // the unreadable one for good, and it may still decrypt on a later start.
                    var storedUnderOldKey = _encryption.NeedsReEncryption(persisted.RefreshToken)
                        || _encryption.NeedsReEncryption(persisted.SteamApiKey);

                    if (storedUnderOldKey && !refreshTokenDecryptFailed && !apiKeyDecryptFailed)
                    {
                        ReEncryptCredentialsFile(loaded);
                    }

                    return loaded;
                }

                _cachedData = new SteamAuthData();
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

    /// <summary>
    /// Removes the credentials file. A file that is already gone or locked is logged and otherwise
    /// ignored: the caller has already decided not to hand the stored secret back, so a failed
    /// delete must not throw out of the getter.
    /// </summary>
    private void DeleteCredentialsFile()
    {
        try
        {
            File.Delete(_steamAuthFilePath);
            _logger.LogInformation("Deleted invalid {AuthDataLabel} auth file", AuthDataLabel);
        }
        catch (Exception deleteEx)
        {
            _logger.LogWarning(deleteEx, "Failed to delete invalid {AuthDataLabel} auth file", AuthDataLabel);
        }
    }

    /// <summary>
    /// Rewrites the credentials file so secrets kept under the v1 key end up encrypted with the
    /// current one. The write goes to a temp file and is moved into place, so a failure leaves the
    /// existing file untouched and still readable, and the caller keeps the credentials it just
    /// decrypted. The next process start tries again.
    /// </summary>
    private void ReEncryptCredentialsFile(SteamAuthData data)
    {
        try
        {
            SaveAuthData(data);
            _logger.LogInformation("Re-encrypted the {AuthDataLabel} credentials file with the current key", AuthDataLabel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to re-encrypt the {AuthDataLabel} credentials file - the existing file is unchanged and still usable",
                AuthDataLabel);
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
                    LastAuthenticated = data.LastAuthenticated,
                    SteamApiKey = _encryption.Encrypt(data.SteamApiKey)
                };

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

                        _logger.LogTrace("Steam auth file permissions set to 600 (owner read/write only)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set Unix file permissions on {PathLabel} file", PathLabel);
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
