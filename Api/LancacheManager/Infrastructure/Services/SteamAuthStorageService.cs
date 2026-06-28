using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for managing Steam authentication credentials in a separate encrypted file
/// Uses Microsoft ASP.NET Core Data Protection API with API key as part of encryption
/// </summary>
public class SteamAuthStorageService : SteamAuthFileStorageServiceBase, ISteamAuthStorageService
{
    public SteamAuthStorageService(
        ILogger<SteamAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "steam_auth";

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
