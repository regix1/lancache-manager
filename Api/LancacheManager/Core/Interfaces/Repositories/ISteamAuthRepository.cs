using static LancacheManager.Infrastructure.Repositories.SteamAuthRepository;
using LancacheManager.Infrastructure.Repositories;


namespace LancacheManager.Core.Interfaces.Repositories;

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
