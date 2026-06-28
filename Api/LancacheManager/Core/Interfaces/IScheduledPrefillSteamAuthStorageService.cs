using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IScheduledPrefillSteamAuthStorageService
{
    SteamAuthData GetAuthData();
    void SaveAuthData(SteamAuthData data);
    void UpdateAuthData(Action<SteamAuthData> updater);
    void ClearAuthData();
    string GetCredentialsFilePath();
    string GetAuthDirectory();
}
