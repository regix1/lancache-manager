using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The Steam sign-in outlives the request that started it, so nothing on the request path ends its
/// tracked operation any more. These tests pin the property that replaces that: however the sign-in
/// ends, the operation it registered is finished before the call returns. An operation left running
/// would leave a notification card that never clears.
/// </summary>
public sealed class SteamLoginOperationLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lcm-steam-login", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SignInThatFails_LeavesNoRunningOperationAsync()
    {
        var tracker = CreateTracker();
        var service = CreateService(tracker);

        // No Steam client is wired up, so the credentials poll throws before it can reach Steam.
        // That is the shape of every unsuccessful sign-in: the operation is registered first and
        // something after it fails.
        var result = await service.AuthenticateAsync("account", "password");

        Assert.False(result.Success);
        Assert.NotNull(result.OperationId);
        Assert.Empty(tracker.GetActiveOperations(OperationType.DepotMapping));

        var operation = tracker.GetOperation(result.OperationId!.Value);
        Assert.NotNull(operation);
        Assert.Equal(OperationStatus.Failed, operation!.Status);
    }

    [Fact]
    public async Task SignInThatFails_LetsTheNextSignInStartAsync()
    {
        var tracker = CreateTracker();
        var service = CreateService(tracker);

        var first = await service.AuthenticateAsync("account", "password");
        var second = await service.AuthenticateAsync("account", "password");

        // A guard left set would refuse every later sign-in with the in-progress message instead of
        // registering a second operation of its own.
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.NotNull(second.OperationId);
        Assert.Empty(tracker.GetActiveOperations(OperationType.DepotMapping));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static UnifiedOperationTracker CreateTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private SteamKit2Service CreateService(IUnifiedOperationTracker tracker)
    {
        Directory.CreateDirectory(_root);

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        var configuration = new ConfigurationBuilder().Build();
        var apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance, configuration, pathResolver);
        var dataProtection = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(_root, "dp-keys")));
        var encryption = new SecureStateEncryptionService(
            dataProtection, apiKeyService, NullLogger<SecureStateEncryptionService>.Instance);
        var steamAuthStorage = new SteamAuthStorageService(
            NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption);
        var stateService = new StateService(
            NullLogger<StateService>.Instance, pathResolver, encryption, steamAuthStorage);

        // The real service takes a dozen collaborators the sign-in never touches, so build it
        // uninitialized and fill in only what the sign-in path reads. This is how
        // SteamMappingConcurrencyTests reaches the same class.
        var service = (SteamKit2Service)RuntimeHelpers.GetUninitializedObject(typeof(SteamKit2Service));
        SetPrivateField(service, "_logger", NullLogger.Instance);
        SetPrivateField(service, "_notifications",
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>());
        SetPrivateField(service, "_operationTracker", tracker);
        SetPrivateField(service, "_stateService", stateService);
        SetPrivateField(service, "_cancellationTokenSource", new CancellationTokenSource());
        SetPrivateField(service, "_sessionGate", new SemaphoreSlim(1, 1));
        SetPrivateField(service, "_depotToAppMappings", new ConcurrentDictionary<uint, HashSet<uint>>());
        return service;
    }

    private static void SetPrivateField<T>(SteamKit2Service service, string name, T value)
    {
        for (var type = typeof(SteamKit2Service); type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                field.SetValue(service, value);
                return;
            }
        }

        throw new InvalidOperationException($"Field '{name}' was not found.");
    }
}
