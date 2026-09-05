using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Xbox / Microsoft account authentication credentials for Integrations / game mapping. Both the
/// MSA refresh token and the device-identity key are secrets, so a failure to decrypt either one
/// makes the credentials file unusable.
/// </summary>
public class XboxAuthStorageService : AuthFileStorageServiceBase<XboxAuthData, PersistedXboxAuthData>
{
    public XboxAuthStorageService(
        ILogger<XboxAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "xbox_auth";

    protected override string AuthDataLabel => "Xbox";

    protected override XboxAuthData? DecryptPersisted(PersistedXboxAuthData persisted)
    {
        var decryptedRefreshToken = Encryption.Decrypt(persisted.RefreshToken);
        var decryptedDeviceKey = Encryption.Decrypt(persisted.DeviceKeyPkcs8);

        var refreshTokenDecryptFailed = decryptedRefreshToken == null
            && !string.IsNullOrEmpty(persisted.RefreshToken);
        var deviceKeyDecryptFailed = decryptedDeviceKey == null
            && !string.IsNullOrEmpty(persisted.DeviceKeyPkcs8);

        if (refreshTokenDecryptFailed || deviceKeyDecryptFailed)
        {
            Logger.LogWarning(
                "Failed to decrypt {AuthDataLabel} credentials - clearing invalid credentials file.",
                AuthDataLabel);

            return null;
        }

        return new XboxAuthData
        {
            OwnerAccountId = persisted.OwnerAccountId,
            RefreshToken = decryptedRefreshToken,
            DeviceKeyPkcs8 = decryptedDeviceKey,
            DisplayName = persisted.DisplayName,
            Xuid = persisted.Xuid,
            LastAuthenticated = persisted.LastAuthenticated,
            GamesDiscovered = persisted.GamesDiscovered
        };
    }

    protected override PersistedXboxAuthData EncryptForStorage(XboxAuthData data)
    {
        return new PersistedXboxAuthData
        {
            OwnerAccountId = data.OwnerAccountId,
            RefreshToken = Encryption.Encrypt(data.RefreshToken),
            DeviceKeyPkcs8 = Encryption.Encrypt(data.DeviceKeyPkcs8),
            DisplayName = data.DisplayName,
            Xuid = data.Xuid,
            LastAuthenticated = data.LastAuthenticated,
            GamesDiscovered = data.GamesDiscovered
        };
    }

    protected override bool HasCredentials(XboxAuthData data) => !string.IsNullOrEmpty(data.RefreshToken);

    protected override Guid? GetOwnerAccountId(XboxAuthData data) => data.OwnerAccountId;

    protected override Guid? GetOwnerAccountId(PersistedXboxAuthData persisted) => persisted.OwnerAccountId;

    protected override void SetOwnerAccountId(XboxAuthData data, Guid accountId) => data.OwnerAccountId = accountId;

    protected override bool IsStoredUnencrypted(PersistedXboxAuthData persisted)
        => Encryption.IsUnencrypted(persisted.RefreshToken)
            || Encryption.IsUnencrypted(persisted.DeviceKeyPkcs8);

    // Xbox discards the file when either secret fails to decrypt, so reaching here means both did.
    protected override bool NeedsReEncryption(PersistedXboxAuthData persisted, XboxAuthData decrypted)
        => Encryption.NeedsReEncryption(persisted.RefreshToken)
            || Encryption.NeedsReEncryption(persisted.DeviceKeyPkcs8);
}
