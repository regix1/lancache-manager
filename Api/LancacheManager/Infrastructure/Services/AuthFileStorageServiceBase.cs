using System.Text.Json;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Shared file-backed auth storage: owns the credentials file under the security directory,
/// the atomic write, the in-memory cache and the lock guarding both. The integration-specific
/// subclass supplies the encrypted-field mapping in each direction, so the secrets themselves
/// are encrypted and decrypted where their shape is known.
/// </summary>
/// <typeparam name="TAuthData">Decrypted in-memory shape handed to callers.</typeparam>
/// <typeparam name="TPersistedAuthData">On-disk JSON shape carrying the ciphertext.</typeparam>
public abstract class AuthFileStorageServiceBase<TAuthData, TPersistedAuthData>
    where TAuthData : class, new()
    where TPersistedAuthData : class, new()
{
    private readonly ILogger _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _authDirectory;
    private readonly string _authFilePath;
    private readonly object _lock = new object();
    private TAuthData? _cachedData;

    protected AuthFileStorageServiceBase(
        ILogger logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;

        var securityDir = _pathResolver.GetSecurityDirectory();
        _authDirectory = Path.Combine(securityDir, AuthDirectoryName);
        _authFilePath = Path.Combine(_authDirectory, "credentials.json");

        EnsureDirectoryExists();
    }

    /// <summary>
    /// Directory name under the security directory, e.g. <c>epic_auth</c>.
    /// </summary>
    protected abstract string AuthDirectoryName { get; }

    /// <summary>
    /// Human-readable integration name used in log messages, e.g. <c>Epic</c>.
    /// </summary>
    protected abstract string AuthDataLabel { get; }

    protected ILogger Logger => _logger;

    protected SecureStateEncryptionService Encryption => _encryption;

    /// <summary>
    /// Decrypts the on-disk shape into the in-memory shape. Returns null when a stored secret
    /// fails to decrypt, which tells the caller to discard the credentials file. Implementations
    /// log the reason before returning null so the message names the field that failed.
    /// </summary>
    protected abstract TAuthData? DecryptPersisted(TPersistedAuthData persisted);

    /// <summary>
    /// Encrypts the in-memory shape into the on-disk shape about to be serialized.
    /// </summary>
    protected abstract TPersistedAuthData EncryptForStorage(TAuthData data);

    /// <summary>
    /// True when the loaded data carries a usable credential.
    /// </summary>
    protected abstract bool HasCredentials(TAuthData data);

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_authDirectory))
            {
                Directory.CreateDirectory(_authDirectory);
                _logger.LogInformation("Created {DirectoryName} directory: {Directory}", AuthDirectoryName, _authDirectory);

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(
                            _authDirectory,
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

    public TAuthData GetAuthData()
    {
        lock (_lock)
        {
            if (_cachedData != null)
            {
                return _cachedData;
            }

            try
            {
                if (File.Exists(_authFilePath))
                {
                    var json = File.ReadAllText(_authFilePath);
                    var persisted = JsonSerializer.Deserialize<TPersistedAuthData>(json) ?? new TPersistedAuthData();

                    var decrypted = DecryptPersisted(persisted);

                    if (decrypted == null)
                    {
                        try
                        {
                            File.Delete(_authFilePath);
                            _logger.LogInformation("Deleted invalid {AuthDataLabel} auth file", AuthDataLabel);
                        }
                        catch (Exception deleteEx)
                        {
                            _logger.LogWarning(deleteEx, "Failed to delete invalid {AuthDataLabel} auth file", AuthDataLabel);
                        }

                        _cachedData = new TAuthData();
                        return _cachedData;
                    }

                    _cachedData = decrypted;
                }
                else
                {
                    _cachedData = new TAuthData();
                }

                return _cachedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load {AuthDataLabel} auth data, using default", AuthDataLabel);
                _cachedData = new TAuthData();
                return _cachedData;
            }
        }
    }

    public void SaveAuthData(TAuthData data)
    {
        lock (_lock)
        {
            try
            {
                EnsureDirectoryExists();

                var persisted = EncryptForStorage(data);

                var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });

                var tempFile = _authFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                using (var fs = File.OpenWrite(tempFile))
                {
                    fs.Flush(true);
                }

                File.Move(tempFile, _authFilePath, true);

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(_authFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    public void UpdateAuthData(Action<TAuthData> updater)
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
                if (File.Exists(_authFilePath))
                {
                    File.Delete(_authFilePath);
                    _logger.LogInformation("Deleted {AuthDataLabel} credentials file: {Path}", AuthDataLabel, _authFilePath);
                }

                _cachedData = new TAuthData();
                _logger.LogInformation("Cleared {AuthDataLabel} authentication data", AuthDataLabel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear {AuthDataLabel} auth data", AuthDataLabel);
                _cachedData = new TAuthData();
                throw;
            }
        }
    }

    public bool HasSavedCredentials()
    {
        try
        {
            if (!File.Exists(_authFilePath))
                return false;

            var data = GetAuthData();
            return HasCredentials(data);
        }
        catch
        {
            return false;
        }
    }

    public string GetCredentialsFilePath() => _authFilePath;

    public string GetAuthDirectory() => _authDirectory;
}
