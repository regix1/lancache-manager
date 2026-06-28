using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Xbox / Microsoft account authentication credentials for Integrations / game mapping.
/// </summary>
public class XboxAuthStorageService : XboxAuthFileStorageServiceBase
{
    public XboxAuthStorageService(
        ILogger<XboxAuthStorageService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
        : base(logger, pathResolver, encryption)
    {
    }

    protected override string AuthDirectoryName => "xbox_auth";
}
