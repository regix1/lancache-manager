using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Xbox credentials used only by scheduled prefill headless runs,
/// isolated from Integrations / game-mapping credential storage.
/// </summary>
public class ScheduledPrefillXboxAuthStorageService
    : XboxAuthFileStorageServiceBase,
        IScheduledPrefillXboxAuthStorageService
{
    public ScheduledPrefillXboxAuthStorageService(
        ILogger<ScheduledPrefillXboxAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "scheduled_prefill_xbox_auth";
}
