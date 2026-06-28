using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Epic credentials used only by scheduled prefill headless runs,
/// isolated from Integrations / game-mapping credential storage.
/// </summary>
public class ScheduledPrefillEpicAuthStorageService
    : EpicAuthFileStorageServiceBase,
        IScheduledPrefillEpicAuthStorageService
{
    public ScheduledPrefillEpicAuthStorageService(
        ILogger<ScheduledPrefillEpicAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "scheduled_prefill_epic_auth";
}
