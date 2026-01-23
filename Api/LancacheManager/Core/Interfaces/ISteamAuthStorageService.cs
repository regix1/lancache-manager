using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface ISteamAuthStorageService
{
    SteamAuthData GetSteamAuthData();
    void SaveSteamAuthData(SteamAuthData data);
    void UpdateSteamAuthData(Action<SteamAuthData> updater);
    void ClearSteamAuthData();
    void MigrateFromStateJson(SteamAuthState? oldAuthState);
    string GetCredentialsFilePath();
    string GetAuthDirectory();
}
