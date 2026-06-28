using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Epic Games authentication credentials for Integrations / game mapping.
/// </summary>
public class EpicAuthStorageService : EpicAuthFileStorageServiceBase
{
    public EpicAuthStorageService(
        ILogger<EpicAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "epic_auth";
}
