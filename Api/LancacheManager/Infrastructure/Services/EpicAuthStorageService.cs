using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Epic Games authentication credentials for Integrations / game mapping. The refresh token is
/// the only secret, so it alone decides whether a credentials file is still usable.
/// </summary>
public class EpicAuthStorageService : AuthFileStorageServiceBase<EpicAuthData, PersistedEpicAuthData>
{
    public EpicAuthStorageService(
        ILogger<EpicAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "epic_auth";

    protected override string AuthDataLabel => "Epic";

    protected override EpicAuthData? DecryptPersisted(PersistedEpicAuthData persisted)
    {
        var decryptedRefreshToken = Encryption.Decrypt(persisted.RefreshToken);
        var refreshTokenDecryptFailed = decryptedRefreshToken == null
            && !string.IsNullOrEmpty(persisted.RefreshToken);

        if (refreshTokenDecryptFailed)
        {
            Logger.LogWarning(
                "Failed to decrypt {AuthDataLabel} refresh token - clearing invalid credentials file.",
                AuthDataLabel);

            return null;
        }

        return new EpicAuthData
        {
            RefreshToken = decryptedRefreshToken,
            DisplayName = persisted.DisplayName,
            AccountId = persisted.AccountId,
            LastAuthenticated = persisted.LastAuthenticated,
            GamesDiscovered = persisted.GamesDiscovered
        };
    }

    protected override PersistedEpicAuthData EncryptForStorage(EpicAuthData data)
    {
        return new PersistedEpicAuthData
        {
            RefreshToken = Encryption.Encrypt(data.RefreshToken),
            DisplayName = data.DisplayName,
            AccountId = data.AccountId,
            LastAuthenticated = data.LastAuthenticated,
            GamesDiscovered = data.GamesDiscovered
        };
    }

    protected override bool HasCredentials(EpicAuthData data) => !string.IsNullOrEmpty(data.RefreshToken);
}
