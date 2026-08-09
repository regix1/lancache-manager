using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

internal static class StateTestMethods
{
    internal static StateService CreateStateService(string root)
    {
        var configuration = new ConfigurationBuilder().Build();

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = root;

        var dataProtection = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(root, "dp-keys")));
        var apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            configuration,
            pathResolver);
        var encryption = new SecureStateEncryptionService(
            dataProtection,
            apiKeyService,
            NullLogger<SecureStateEncryptionService>.Instance);
        var steamAuthStorage = new SteamAuthStorageService(
            NullLogger<SteamAuthStorageService>.Instance,
            pathResolver,
            encryption);

        return new StateService(
            NullLogger<StateService>.Instance, pathResolver, encryption, steamAuthStorage);
    }
}
