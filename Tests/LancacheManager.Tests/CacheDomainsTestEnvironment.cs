using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.StatusCheck;

namespace LancacheManager.Tests;

/// <summary>Supplies NOFETCH as the test sets it and no other variable, so the service uses its
/// default GitHub repository and branch. A read of <see cref="SlowKey"/> waits until the reader gives
/// up on it and then fails; a read of <see cref="LateKey"/> waits as long and then answers. Shared by
/// the CacheDomainsService test classes.</summary>
internal sealed class CacheDomainsTestEnvironment : ILancacheEnvironmentSource
{
    public string? NoFetch { get; set; }

    public string? SlowKey { get; set; }

    public string? LateKey { get; set; }

    public async Task<EnvValueResult> GetValueAsync(string key, CancellationToken cancellationToken)
    {
        if (key == SlowKey)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (key == LateKey)
        {
            var readerGaveUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => readerGaveUp.TrySetResult()))
            {
                await readerGaveUp.Task;
            }
        }

        return new EnvValueResult
        {
            Value = key == "NOFETCH" ? NoFetch : null,
            Source = EnvValueSource.EnvFile
        };
    }

    /// <summary>A service that keeps its data under <paramref name="rootDirectory"/>, reads this
    /// environment and sends every request to <paramref name="handler"/>.</summary>
    public CacheDomainsService NewService(
        string rootDirectory, HttpMessageHandler handler, CapturingLogger<CacheDomainsService> logger)
    {
        var paths = CacheScanGateHarness.CreateProxy<IPathResolver>((method, _) =>
            method.Name == nameof(IPathResolver.GetDataDirectory) ? rootDirectory : null);

        return new CacheDomainsService(
            logger,
            paths,
            DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
            this,
            new SingleHandlerHttpClients(handler),
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>());
    }
}
