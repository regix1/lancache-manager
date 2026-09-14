using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

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
    private readonly Dictionary<Guid, TPersistedAuthData> _savedLogins = [];
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private IntegrationLogin? _pendingLogin;
    private long _generation;
    private long _version;
    private bool _releasing;
    private bool _dispatching;
    private readonly HashSet<Guid> _usedAttempts = [];
    private long _releaseVersion;

    public long IntegrationReleaseVersion { get { lock (_lock) return _releaseVersion; } }

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

    protected virtual TPersistedAuthData EncryptSavedLogin(TAuthData data) => EncryptForStorage(data);

    protected virtual TAuthData? DecryptSavedLogin(TPersistedAuthData persisted) => DecryptPersisted(persisted);

    protected virtual bool IsSavedUnencrypted(TPersistedAuthData persisted) => IsStoredUnencrypted(persisted);

    protected virtual bool NeedsSavedReEncryption(TPersistedAuthData persisted, TAuthData decrypted)
        => NeedsReEncryption(persisted, decrypted);

    protected virtual void MergeCredentials(TAuthData auth, TAuthData current) { }

    private static TAuthData Snapshot(TAuthData data)
        => JsonSerializer.Deserialize<TAuthData>(JsonSerializer.Serialize(data))!;

    public IntegrationAccess GetIntegrationAccess(IntegrationCaller caller)
    {
        if (!caller.AuthenticationEnabled) caller = new(null, null, false);
        lock (_lock)
        {
            var auth = ReadAuth(repair: false);
            var owner = GetOwnerAccountId(auth);
            if (caller.AuthenticationEnabled && (caller.AccountId is null || caller.SessionId is null))
                return new(false, false, false, false, false, "account-required");
            if (caller.AuthenticationEnabled && owner is not null && owner != caller.AccountId)
                return new(false, false, false, false, false, "owned-by-another-account");
            if (_releasing)
                return new(false, false, false, false, false, "release-in-progress");
            if (_pendingLogin is { } pending && pending.ExpiresAtUtc > DateTime.UtcNow)
            {
                var mine = pending.AccountId == caller.AccountId && pending.SessionId == caller.SessionId
                    && pending.Shared != caller.AuthenticationEnabled;
                return new(mine, false, mine && owner is not null, mine, false, "login-in-progress",
                    mine ? pending.AttemptId : null, mine ? pending.ExpiresAtUtc : null);
            }
            if (owner is null && HasCredentials(auth) && caller.AuthenticationEnabled)
                return new(false, false, false, false, caller.OwnsInstallation, "reauthentication-required");
            return new(true, true, owner is not null || HasCredentials(auth), false, false, null);
        }
    }

    public async Task<IntegrationLogin> BeginIntegrationLoginAsync(
        IntegrationCaller caller, Guid? attemptId = null, bool recover = false,
        CancellationToken cancellationToken = default)
    {
        if (caller is { AuthenticationEnabled: false }) caller = new(null, null, false);
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lock)
            {
                var access = GetIntegrationAccess(caller);
                if (!access.CanSignIn && !(recover && access.CanRecover))
                    IntegrationLease.Refuse(access.OwnershipReason);
                if (attemptId == Guid.Empty) IntegrationLease.Refuse("attempt-required");
                var id = attemptId ?? Guid.NewGuid();
                if (!_usedAttempts.Add(id)) IntegrationLease.Refuse("attempt-expired");
                _version++;
                return _pendingLogin = new(id, ++_generation,
                    caller.AuthenticationEnabled ? caller.AccountId : null, caller.SessionId,
                    DateTime.UtcNow.AddMinutes(15), !caller.AuthenticationEnabled, recover);
            }
        }
        finally { _admissionGate.Release(); }
    }

    public IntegrationLogin ContinueIntegrationLogin(IntegrationCaller caller, Guid? attemptId)
    {
        lock (_lock)
        {
            if (attemptId is null || attemptId == Guid.Empty) IntegrationLease.Refuse("attempt-required");
            var login = _pendingLogin;
            if (login is null || login.AttemptId != attemptId || login.ExpiresAtUtc <= DateTime.UtcNow || _releasing)
                IntegrationLease.Refuse("attempt-expired");
            IntegrationLease.ValidateCaller(login!, caller);
            return login!;
        }
    }

    public bool IsIntegrationLoginCurrent(IntegrationLogin login)
    {
        lock (_lock)
            return !_releasing && _pendingLogin == login && login.Generation == _generation
                && login.ExpiresAtUtc > DateTime.UtcNow;
    }

    public IntegrationLogin SetIntegrationLoginExpiry(IntegrationLogin login, DateTime expiresAtUtc)
    {
        lock (_lock)
        {
            if (!IsIntegrationLoginCurrent(login)) IntegrationLease.Refuse("attempt-expired");
            return _pendingLogin = login with { ExpiresAtUtc = expiresAtUtc < login.ExpiresAtUtc ? expiresAtUtc : login.ExpiresAtUtc };
        }
    }

    public async Task RunIntegrationActionAsync(IntegrationCaller caller, Action action, CancellationToken cancellationToken = default)
    {
        if (caller is { AuthenticationEnabled: false }) caller = new(null, null, false);
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lock)
            {
                var access = GetIntegrationAccess(caller);
                if (!access.CanManage || access.OwnershipReason is not null)
                    IntegrationLease.Refuse(access.OwnershipReason);
                action();
            }
        }
        finally { _admissionGate.Release(); }
    }

    public bool RunIntegrationLogin(IntegrationLogin login, Action action)
    {
        lock (_lock)
        {
            if (!IsIntegrationLoginCurrent(login)) return false;
            action();
            return true;
        }
    }

    public bool CompleteIntegrationLogin(IntegrationLogin login, TAuthData auth, Action? committed = null)
    {
        lock (_lock)
        {
            if (!IsIntegrationLoginCurrent(login)) return false;
            if (GetOwnerAccountId(auth) != login.AccountId)
                throw new InvalidOperationException("Integration credential owner differs from its admitted account.");
            SaveAuthData(auth);
            _pendingLogin = null;
            committed?.Invoke();
            return true;
        }
    }

    public bool FinishIntegrationLogin(IntegrationLogin login, Action? finished = null)
    {
        lock (_lock)
        {
            if (_pendingLogin != login || login.Generation != _generation) return false;
            _pendingLogin = null;
            _generation++;
            _version++;
            finished?.Invoke();
            return true;
        }
    }

    public void CancelIntegrationLogin(IntegrationCaller caller, Guid? attemptId, Action? cancelled = null)
    {
        lock (_lock)
        {
            var login = ContinueIntegrationLogin(caller, attemptId);
            FinishIntegrationLogin(login, cancelled);
        }
    }

    public (long Version, TAuthData Auth) GetIntegrationSnapshot()
    {
        lock (_lock)
        {
            var auth = GetAuthData();
            return (_version, auth);
        }
    }

    public bool IsIntegrationCurrent(long version)
    {
        lock (_lock) return !_releasing && (_pendingLogin is null || _pendingLogin.ExpiresAtUtc <= DateTime.UtcNow) && _version == version;
    }

    public long? TryUpdateAuth(long expectedVersion, Action<TAuthData> updater, Action? committed = null, IntegrationLease? lease = null)
    {
        lock (_lock)
        {
            if (!IsIntegrationCurrent(expectedVersion) || (_dispatching && lease is null)) return null;
            if (lease is not null) ValidateIntegrationLease(lease);
            UpdateAuthData(updater);
            committed?.Invoke();
            return _version;
        }
    }

    public bool RunIfCurrent(long expectedVersion, Action action, IntegrationLease? lease = null)
    {
        lock (_lock)
        {
            if (!IsIntegrationCurrent(expectedVersion) || (_dispatching && lease is null)) return false;
            if (lease is not null) ValidateIntegrationLease(lease);
            action();
            return true;
        }
    }

    public bool TryInvalidateAuth(long expectedVersion, Action? invalidated = null, IntegrationLease? lease = null)
    {
        lock (_lock)
        {
            if (!IsIntegrationCurrent(expectedVersion) || (_dispatching && lease is null)) return false;
            if (lease is not null) ValidateIntegrationLease(lease);
            if (lease?.Caller?.AuthenticationEnabled == false) ClearAuthData();
            else InvalidateAuthData();
            invalidated?.Invoke();
            return true;
        }
    }

    public string? GetIntegrationLoginReason(IntegrationCaller caller)
    {
        lock (_lock)
        {
            if (!caller.AuthenticationEnabled)
            {
                var shared = GetIntegrationAccess(caller);
                if (!shared.CanManage || shared.OwnershipReason is not null) return shared.OwnershipReason;
                return HasCredentials(ReadAuth(repair: false)) ? null : "integration-sign-in-required";
            }
            if (caller.AccountId is null) return "account-required";
            var access = GetIntegrationAccess(caller);
            if (!access.CanManage || access.OwnershipReason is not null) return access.OwnershipReason;
            return GetOwnerAccountId(GetAuthData()) == caller.AccountId ? null : "integration-sign-in-required";
        }
    }

    public async Task<IntegrationLease> AcquireIntegrationLoginAsync(
        IntegrationCaller caller, CancellationToken cancellationToken = default)
    {
        if (caller is { AuthenticationEnabled: false }) caller = new(null, null, false);
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lock)
            {
                var reason = GetIntegrationLoginReason(caller);
                if (reason is not null) IntegrationLease.Refuse(reason);
                if (_pendingLogin is { } expired && expired.ExpiresAtUtc <= DateTime.UtcNow)
                    FinishIntegrationLogin(expired);
                var generation = _generation;
                _dispatching = true;
                return new(this, generation, caller, () =>
                {
                    lock (_lock)
                    {
                        if (_generation != generation || _releasing) IntegrationLease.Refuse("attempt-expired");
                        var currentReason = GetIntegrationLoginReason(caller);
                        if (currentReason is not null) IntegrationLease.Refuse(currentReason);
                    }
                }, () =>
                {
                    lock (_lock) _dispatching = false;
                    _admissionGate.Release();
                });
            }
        }
        catch { _admissionGate.Release(); throw; }
    }

    public TAuthData GetIntegrationLogin(IntegrationLease lease)
    {
        lock (_lock)
        {
            ValidateIntegrationLease(lease);
            var active = GetAuthData();
            return HasCredentials(active) || lease.Caller!.AuthenticationEnabled == false
                ? active : GetSavedLogin(lease.Caller.AccountId!.Value);
        }
    }

    public TAuthData GetIntegrationLogin(IntegrationCaller caller)
    {
        lock (_lock)
        {
            var reason = GetIntegrationLoginReason(caller);
            if (reason is not null) IntegrationLease.Refuse(reason);
            var active = GetAuthData();
            return HasCredentials(active) || !caller.AuthenticationEnabled ? active : GetSavedLogin(caller.AccountId!.Value);
        }
    }

    public void ValidateIntegrationLease(IntegrationLease lease)
    {
        if (!ReferenceEquals(lease.Store, this)) IntegrationLease.Refuse("attempt-expired");
        lease.Validate();
    }

    /// <summary>A null caller is reserved for trusted installation invalidation and shutdown paths.</summary>
    public async Task<IntegrationLease> BeginIntegrationReleaseAsync(
        IntegrationCaller? caller = null, CancellationToken cancellationToken = default)
    {
        if (caller is { AuthenticationEnabled: false }) caller = new(null, null, false);
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lock)
            {
                if (caller is not null)
                {
                    var access = GetIntegrationAccess(caller);
                    if (!access.CanLogout) IntegrationLease.Refuse(access.OwnershipReason);
                }
                _releasing = true;
                _releaseVersion++;
                _pendingLogin = null;
                var generation = ++_generation;
                _version++;
                return new(this, generation, caller, () =>
                {
                    lock (_lock)
                        if (!_releasing || _generation != generation) IntegrationLease.Refuse("attempt-expired");
                }, () =>
                {
                    lock (_lock) _releasing = false;
                    _admissionGate.Release();
                });
            }
        }
        catch { _admissionGate.Release(); throw; }
    }

    public void CompleteIntegrationRelease(IntegrationLease lease, Action<TAuthData>? clear = null)
    {
        lock (_lock)
        {
            ValidateIntegrationLease(lease);
            if (clear is null) ClearAuthData();
            else UpdateAuthData(clear, saveSavedLogin: false);
        }
    }

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

    protected abstract Guid? GetOwnerAccountId(TAuthData data);

    protected abstract Guid? GetOwnerAccountId(TPersistedAuthData persisted);

    protected abstract void SetOwnerAccountId(TAuthData data, Guid accountId);

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_authDirectory))
        {
            Directory.CreateDirectory(_authDirectory);
            _logger.LogInformation("Created {DirectoryName} directory: {Directory}", AuthDirectoryName, _authDirectory);
        }

        SetDirectoryPermissions(_authDirectory);
    }

    private void SetDirectoryPermissions(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set Unix file permissions on {DirectoryName} directory", AuthDirectoryName);
        }
    }

    public TAuthData GetAuthData() => ReadAuth(repair: true);

    private TAuthData ReadAuth(bool repair)
    {
        lock (_lock)
        {
            if (_cachedData != null)
            {
                return Snapshot(_cachedData);
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

                        _cachedData = new TAuthData();
                        if (GetOwnerAccountId(persisted) is { } owner)
                            SetOwnerAccountId(_cachedData, owner);
                        else if (repair) DeleteCredentialsFile();
                        return Snapshot(_cachedData);
                    }

                    var decrypted = DecryptPersisted(persisted);

                    if (decrypted == null)
                    {
                        _cachedData = new TAuthData();
                        if (GetOwnerAccountId(persisted) is { } owner)
                            SetOwnerAccountId(_cachedData, owner);
                        else if (repair) DeleteCredentialsFile();
                        return Snapshot(_cachedData);
                    }

                    _cachedData = decrypted;

                    // A secret still under the v1 key is written back with the current one right
                    // now instead of waiting for some later save that may never come.
                    if (repair && NeedsReEncryption(persisted, decrypted))
                    {
                        ReEncryptCredentialsFile(decrypted);
                    }

                    return Snapshot(decrypted);
                }

                _cachedData = new TAuthData();
                return Snapshot(_cachedData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load {AuthDataLabel} auth data", AuthDataLabel);
                throw;
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

    public void SaveAuthData(TAuthData data, bool saveSavedLogin = true)
    {
        lock (_lock)
        {
            EnsureDirectoryExists();

            data = Snapshot(data);
            if (saveSavedLogin && _cachedData is null && File.Exists(_authFilePath)) GetAuthData();
            if (saveSavedLogin && _cachedData is not null) MergeCredentials(data, _cachedData);
            SaveFile(_authFilePath, data);
            _cachedData = data;
            if (saveSavedLogin) _version++;
            if (saveSavedLogin && GetOwnerAccountId(data) is { } accountId && HasCredentials(data))
            {
                try { _savedLogins[accountId] = SaveFile(GetSavedLoginPath(accountId), data, savedLogin: true); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Active {AuthDataLabel} credentials were saved, but the account's saved copy could not be updated", AuthDataLabel);
                    throw;
                }
            }
        }
    }

    public TAuthData GetSavedLogin(Guid accountId)
    {
        lock (_lock)
        {
            var path = GetSavedLoginPath(accountId);
            try
            {
                if (_savedLogins.TryGetValue(accountId, out var cached))
                {
                    var cachedLogin = DecryptSavedLogin(cached);
                    if (cachedLogin is not null && GetOwnerAccountId(cachedLogin) == accountId)
                    {
                        return cachedLogin;
                    }

                    DeleteSavedLoginFile(path);
                    _savedLogins.Remove(accountId);
                    return new TAuthData();
                }

                if (!File.Exists(path))
                {
                    return new TAuthData();
                }

                var json = File.ReadAllText(path);
                var persisted = JsonSerializer.Deserialize<TPersistedAuthData>(json) ?? new TPersistedAuthData();
                if (GetOwnerAccountId(persisted) != accountId)
                {
                    _logger.LogWarning(
                        "Refused {AuthDataLabel} saved login whose owner did not match its account file",
                        AuthDataLabel);
                    return new TAuthData();
                }

                if (IsSavedUnencrypted(persisted))
                {
                    DeleteSavedLoginFile(path);
                    return new TAuthData();
                }

                var decrypted = DecryptSavedLogin(persisted);
                if (decrypted is null || GetOwnerAccountId(decrypted) != accountId)
                {
                    DeleteSavedLoginFile(path);
                    return new TAuthData();
                }

                if (NeedsSavedReEncryption(persisted, decrypted))
                {
                    persisted = SaveFile(path, decrypted, savedLogin: true);
                }

                _savedLogins[accountId] = persisted;
                return decrypted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load saved {AuthDataLabel} login", AuthDataLabel);
                return new TAuthData();
            }
        }
    }

    public void SaveSavedLogin(Guid accountId, TAuthData data)
    {
        lock (_lock)
        {
            data = Snapshot(data);
            SetOwnerAccountId(data, accountId);
            _savedLogins[accountId] = SaveFile(GetSavedLoginPath(accountId), data, savedLogin: true);
        }
    }

    public bool HasSavedLogin(Guid accountId) => HasCredentials(GetSavedLogin(accountId));

    public void ClearSavedLogin(Guid accountId)
    {
        lock (_lock)
        {
            DeleteFile(GetSavedLoginPath(accountId));
            _savedLogins.Remove(accountId);
        }
    }

    public void InvalidateAuthData()
    {
        lock (_lock)
        {
            var auth = GetAuthData();
            var ownerAccountId = GetOwnerAccountId(auth);
            var savedMatches = ownerAccountId is { } owner
                && JsonSerializer.Serialize(auth) == JsonSerializer.Serialize(GetSavedLogin(owner));
            ClearAuthData();
            if (savedMatches && ownerAccountId is { } accountId)
            {
                ClearSavedLogin(accountId);
            }
        }
    }

    private string GetSavedLoginPath(Guid accountId)
        => Path.Combine(_authDirectory, "saved", $"{accountId:N}.json");

    public async Task ClearAccountLoginsAsync(IReadOnlySet<Guid> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0) return;
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lock)
            {
                foreach (var accountId in accountIds.Order())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = GetSavedLoginPath(accountId);
                    foreach (var file in new[] { path, path + ".tmp" })
                    {
                        try { DeleteFile(file); }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                    }
                    _savedLogins.Remove(accountId);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var persisted = ReadFile(_authFilePath);
                var scratch = ReadFile(_authFilePath + ".tmp");
                if (scratch is not null && GetOwnerAccountId(scratch) is { } scratchOwner && accountIds.Contains(scratchOwner))
                    DeleteFile(_authFilePath + ".tmp");

                if (persisted is not null && GetOwnerAccountId(persisted) is { } owner && accountIds.Contains(owner))
                {
                    var retained = ClearStoredLogin(persisted);
                    if (retained is null) DeleteFile(_authFilePath);
                    else
                    {
                        if (scratch is not null
                            && (GetOwnerAccountId(scratch) is not { } pendingOwner || !accountIds.Contains(pendingOwner))
                            && JsonSerializer.Serialize(scratch) != JsonSerializer.Serialize(retained))
                            throw new IOException("Authentication scratch file belongs to a preserved login; resolve it before retrying the account reset.");
                        WriteFile(_authFilePath, retained);
                    }
                    _cachedData = null;
                    _version++;
                    _releaseVersion++;
                }
                else if (_cachedData is not null && GetOwnerAccountId(_cachedData) is { } cachedOwner && accountIds.Contains(cachedOwner))
                {
                    _cachedData = null;
                    _version++;
                }

                if (_pendingLogin?.AccountId is { } pendingAccount && accountIds.Contains(pendingAccount))
                {
                    _pendingLogin = null;
                    _generation++;
                }
            }
        }
        finally { _admissionGate.Release(); }
    }

    protected virtual TPersistedAuthData? ClearStoredLogin(TPersistedAuthData persisted) => null;

    protected virtual TPersistedAuthData? ReadFile(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var persisted = JsonSerializer.Deserialize<TPersistedAuthData>(json, new JsonSerializerOptions
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        })
            ?? throw new JsonException("Authentication file contains a null record.");
        if (GetOwnerAccountId(persisted) == Guid.Empty)
            throw new JsonException("Authentication file contains an empty owner identifier.");
        return persisted;
    }

    protected virtual TPersistedAuthData SaveFile(string path, TAuthData data, bool savedLogin = false)
    {
        var persisted = savedLogin ? EncryptSavedLogin(data) : EncryptForStorage(data);
        WriteFile(path, persisted);
        return persisted;
    }

    protected virtual void WriteFile(string path, TPersistedAuthData persisted)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Authentication file has no parent directory");
        Directory.CreateDirectory(directory);
        SetDirectoryPermissions(directory);

        var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });
        var tempFile = path + ".tmp";
        File.WriteAllText(tempFile, json);
        using (var fs = File.OpenWrite(tempFile))
        {
            fs.Flush(true);
        }

        File.Move(tempFile, path, true);
        if (OperatingSystem.IsLinux())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set Unix file permissions on {AuthDataLabel} auth file", AuthDataLabel);
            }
        }

    }

    private void DeleteSavedLoginFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete invalid {AuthDataLabel} saved login", AuthDataLabel);
        }
    }

    public void UpdateAuthData(Action<TAuthData> updater, bool saveSavedLogin = true, long? expectedReleaseVersion = null)
    {
        lock (_lock)
        {
            if (expectedReleaseVersion is { } expected && (expected != _releaseVersion || _releasing))
                IntegrationLease.Refuse("release-in-progress");
            var data = GetAuthData();
            updater(data);
            SaveAuthData(data, saveSavedLogin);
        }
    }

    public void ClearAuthData()
    {
        lock (_lock)
        {
            _generation++;
            _pendingLogin = null;
            _releaseVersion++;
            try
            {
                if (File.Exists(_authFilePath))
                {
                    DeleteFile(_authFilePath);
                    _logger.LogInformation("Deleted {AuthDataLabel} credentials file: {Path}", AuthDataLabel, _authFilePath);
                }

                _cachedData = new TAuthData();
                _version++;
                _logger.LogInformation("Cleared {AuthDataLabel} authentication data", AuthDataLabel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear {AuthDataLabel} auth data", AuthDataLabel);
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

    protected virtual void DeleteFile(string path) => File.Delete(path);
}
