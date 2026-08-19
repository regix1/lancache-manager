using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for managing Steam authentication credentials in a separate encrypted file
/// Uses Microsoft ASP.NET Core Data Protection API with API key as part of encryption
/// </summary>
public class SteamAuthStorageService : AuthFileStorageServiceBase<SteamAuthData, PersistedSteamAuthData>
{
    public SteamAuthStorageService(
        ILogger<SteamAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "steam_auth";

    protected override string AuthDataLabel => "Steam";

    protected override bool IsStoredUnencrypted(PersistedSteamAuthData persisted)
        => Encryption.IsUnencrypted(persisted.RefreshToken)
            || Encryption.IsUnencrypted(persisted.SteamApiKey);

    /// <summary>
    /// Steam is the one integration that keeps going when only part of its configuration is
    /// unreadable: the refresh token and the Web API key are independent, so losing one is no
    /// reason to make the user redo the other. The file is only discarded when both are unreadable.
    /// </summary>
    protected override SteamAuthData? DecryptPersisted(PersistedSteamAuthData persisted)
    {
        var decryptedRefreshToken = Encryption.Decrypt(persisted.RefreshToken);
        var refreshTokenDecryptFailed = decryptedRefreshToken == null
            && !string.IsNullOrEmpty(persisted.RefreshToken);

        var decryptedApiKey = Encryption.Decrypt(persisted.SteamApiKey);
        var apiKeyDecryptFailed = decryptedApiKey == null && !string.IsNullOrEmpty(persisted.SteamApiKey);

        if (refreshTokenDecryptFailed)
        {
            Logger.LogWarning("Failed to decrypt Steam refresh token - you may need to re-authenticate with Steam.");
        }

        if (apiKeyDecryptFailed)
        {
            Logger.LogWarning("Failed to decrypt Steam Web API key - you may need to reconfigure your API key.");
        }

        if (refreshTokenDecryptFailed && apiKeyDecryptFailed)
        {
            Logger.LogWarning("Failed to decrypt all Steam auth data - clearing invalid credentials file.");
            return null;
        }

        return new SteamAuthData
        {
            Mode = persisted.Mode,
            Username = persisted.Username,
            RefreshToken = decryptedRefreshToken,
            LastAuthenticated = persisted.LastAuthenticated,
            SteamApiKey = decryptedApiKey
        };
    }

    protected override PersistedSteamAuthData EncryptForStorage(SteamAuthData data)
    {
        return new PersistedSteamAuthData
        {
            Mode = data.Mode,
            Username = data.Username,
            RefreshToken = Encryption.Encrypt(data.RefreshToken),
            LastAuthenticated = data.LastAuthenticated,
            SteamApiKey = Encryption.Encrypt(data.SteamApiKey)
        };
    }

    protected override bool HasCredentials(SteamAuthData data) => !string.IsNullOrEmpty(data.RefreshToken);

    /// <summary>
    /// Only rewrite when everything decrypted. Because Steam survives a partial failure, a save in
    /// that state would drop the unreadable secret for good, and it may still decrypt on a later
    /// start, for example once a missing API key is restored.
    /// </summary>
    protected override bool NeedsReEncryption(PersistedSteamAuthData persisted, SteamAuthData decrypted)
    {
        var storedUnderOldKey = Encryption.NeedsReEncryption(persisted.RefreshToken)
            || Encryption.NeedsReEncryption(persisted.SteamApiKey);
        if (!storedUnderOldKey)
        {
            return false;
        }

        var refreshTokenMissing = !string.IsNullOrEmpty(persisted.RefreshToken)
            && string.IsNullOrEmpty(decrypted.RefreshToken);
        var apiKeyMissing = !string.IsNullOrEmpty(persisted.SteamApiKey)
            && string.IsNullOrEmpty(decrypted.SteamApiKey);

        return !refreshTokenMissing && !apiKeyMissing;
    }

    /// <summary>
    /// Migrates Steam auth data from old state.json to new separate file
    /// NOTE: GuardData is NOT migrated - modern Steam auth uses refresh tokens only
    /// </summary>
    public void MigrateFromStateJson(SteamAuthState? oldAuthState)
    {
        if (oldAuthState == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            try
            {
                if (File.Exists(AuthFilePath))
                {
                    return;
                }

                if (string.IsNullOrEmpty(oldAuthState.RefreshToken) && oldAuthState.Mode == SteamAuthMode.Anonymous)
                {
                    return;
                }

                Logger.LogInformation("Migrating Steam auth data from state.json to separate encrypted file");

                var newData = new SteamAuthData
                {
                    Mode = oldAuthState.Mode.ToWireString(),
                    Username = oldAuthState.Username,
                    RefreshToken = oldAuthState.RefreshToken,
                    LastAuthenticated = oldAuthState.LastAuthenticated
                };

                SaveAuthData(newData);
                Logger.LogInformation("Successfully migrated Steam auth data to separate file (refresh token only)");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to migrate Steam auth data from state.json");
            }
        }
    }
}
