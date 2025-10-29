using static LancacheManager.Infrastructure.Repositories.SteamAuthRepository;

namespace LancacheManager.Infrastructure.Repositories.Interfaces;

public interface ISteamAuthRepository
{
    SteamAuthData GetSteamAuthData();
    void SaveSteamAuthData(SteamAuthData data);
    void UpdateSteamAuthData(Action<SteamAuthData> updater);
    void ClearSteamAuthData();
    void MigrateFromStateJson(StateRepository.SteamAuthState? oldAuthState);
    string GetCredentialsFilePath();
    string GetAuthDirectory();
}
