using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IScheduledPrefillXboxAuthStorageService
{
    XboxAuthData GetAuthData();
    void SaveAuthData(XboxAuthData data);
    void UpdateAuthData(Action<XboxAuthData> updater);
    void ClearAuthData();
    string GetCredentialsFilePath();
    string GetAuthDirectory();
}
