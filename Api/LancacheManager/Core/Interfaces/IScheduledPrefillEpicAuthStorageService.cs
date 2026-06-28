using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IScheduledPrefillEpicAuthStorageService
{
    EpicAuthData GetAuthData();
    void SaveAuthData(EpicAuthData data);
    void UpdateAuthData(Action<EpicAuthData> updater);
    void ClearAuthData();
    string GetCredentialsFilePath();
    string GetAuthDirectory();
}
