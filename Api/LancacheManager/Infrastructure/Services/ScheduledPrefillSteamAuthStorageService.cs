using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Stores Steam credentials used only by scheduled prefill headless runs,
/// isolated from the main SteamKit2 / depot-mapping credential store.
/// </summary>
public class ScheduledPrefillSteamAuthStorageService
    : SteamAuthFileStorageServiceBase,
        IScheduledPrefillSteamAuthStorageService
{
    public ScheduledPrefillSteamAuthStorageService(
        ILogger<ScheduledPrefillSteamAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "scheduled_prefill_steam_auth";
}
