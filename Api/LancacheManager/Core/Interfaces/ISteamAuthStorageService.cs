using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface ISteamAuthStorageService
{
    SteamAuthData GetAuthData();
    void SaveAuthData(SteamAuthData data);
    void UpdateAuthData(Action<SteamAuthData> updater);
    void ClearAuthData();
    void MigrateFromStateJson(SteamAuthState? oldAuthState);
    string GetCredentialsFilePath();
    string GetAuthDirectory();
}
