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

    /// <summary>The lock guarding this service's file and cache, for subclasses that add their own
    /// file operations.</summary>
    protected object SyncRoot => _lock;

    /// <summary>Full path of the credentials file, for subclasses that add their own file
    /// operations.</summary>
    protected string AuthFilePath => _authFilePath;

    /// <summary>
    /// True when any stored secret carries no encryption prefix, meaning it is sitting on disk in
    /// the clear. Such a secret has to be assumed exposed, so the whole file is discarded and the
    /// user signs in again with a fresh one. Implementations pass each secret field to
    /// <see cref="SecureStateEncryptionService.IsUnencrypted"/>.
    ///
    /// Asked BEFORE anything is decrypted, so the reason logged is the real one rather than a
    /// decrypt failure, and so a service that tolerates one secret failing cannot accidentally keep
    /// a plaintext one alongside a readable encrypted one.
    /// </summary>
    protected abstract bool IsStoredUnencrypted(TPersistedAuthData persisted);

    /// <summary>
    /// Decrypts the on-disk shape into the in-memory shape. Returns null when the file should be
    /// discarded, which for most services means any stored secret failed to decrypt. A service
    /// holding more than one secret may instead return partial data when only some failed, keeping
    /// the half the user can still use. Implementations log the reason before returning null so the
    /// message names the field that failed.
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

    /// <summary>
    /// True when at least one secret on disk is still a v1 (ENC:) value, so the file should be
    /// rewritten with the current key. Implementations pass each secret field to
    /// <see cref="SecureStateEncryptionService.NeedsReEncryption"/>. Unencrypted secrets are not
    /// this method's business: <see cref="IsStoredUnencrypted"/> has already discarded that file.
    /// </summary>
    /// <param name="decrypted">
    /// What the decrypt actually produced. A service that keeps going after one secret fails must
    /// refuse the rewrite in that state, because saving would drop the unreadable secret for good
    /// and it may still decrypt on a later start.
    /// </param>
    protected abstract bool NeedsReEncryption(TPersistedAuthData persisted, TAuthData decrypted);

    private void EnsureDirectoryExists()
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

                    // A secret written to disk in the clear has to be treated as exposed, so the
                    // whole file goes and the user signs in again, which replaces it with a fresh
                    // one. Checked before decrypting so the reason logged is the real one.
                    if (IsStoredUnencrypted(persisted))
                    {
                        _logger.LogWarning(
                            "{AuthDataLabel} credentials were stored unencrypted - they have been removed, sign in again to store them encrypted.",
                            AuthDataLabel);

                        DeleteCredentialsFile();

                        _cachedData = new TAuthData();
                        return _cachedData;
                    }

                    var decrypted = DecryptPersisted(persisted);

                    if (decrypted == null)
                    {
                        DeleteCredentialsFile();

                        _cachedData = new TAuthData();
                        return _cachedData;
                    }

                    _cachedData = decrypted;

                    // A secret still under the v1 key is written back with the current one right
                    // now instead of waiting for some later save that may never come.
                    if (NeedsReEncryption(persisted, decrypted))
                    {
                        ReEncryptCredentialsFile(decrypted);
                    }

                    return decrypted;
                }

                _cachedData = new TAuthData();
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

    /// <summary>
    /// Removes the credentials file. A file that is already gone or locked is logged and otherwise
    /// ignored: the caller has already decided not to hand the stored secret back, so a failed
    /// delete must not throw out of the getter.
    /// </summary>
    private void DeleteCredentialsFile()
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
    }

    /// <summary>
    /// Rewrites the credentials file so secrets kept under the v1 key end up encrypted with the
    /// current one. The write goes to a temp file and is moved into place, so a failure leaves the
    /// existing file untouched and still readable, and the caller keeps the credentials it just
    /// decrypted. The next process start tries again.
    /// </summary>
    private void ReEncryptCredentialsFile(TAuthData data)
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

    public void SaveAuthData(TAuthData data)
    {
        lock (_lock)
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
