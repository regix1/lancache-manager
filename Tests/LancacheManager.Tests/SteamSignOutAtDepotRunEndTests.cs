using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A user signs out of Steam while a depot crawl ends. Whatever the order of the sign-out's steps and the end of the
/// crawl's code, the sign-out must answer and leave no saved Steam sign-in behind.
/// </summary>
public sealed class SteamSignOutAtDepotRunEndTests
{
    private const string SignOutBeforeTheRunEnds = "sign-out while the crawl still runs";
    private const string RunEndsInsideTheSignOut = "crawl ends after the sign-out found it and before the sign-out stops it";
    private const string SignOutAfterTheRunEnded = "sign-out after the crawl ended";
    private const string SignOutCancelsASourceDisposedAfterItsRead = "sign-out cancels the source it read after the crawl freed its lock and disposed that source";

    [Theory]
    [InlineData(SignOutBeforeTheRunEnds)]
    [InlineData(RunEndsInsideTheSignOut)]
    [InlineData(SignOutAfterTheRunEnded)]
    [InlineData(SignOutCancelsASourceDisposedAfterItsRead)]
    public async Task ASteamSignOutAsTheDepotCrawlEnds_AnswersAndClearsTheSignIn(string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var tracker = (UnifiedOperationTracker)CacheScanGateHarness.GetField(host.Service, "_operationTracker")!;
        var held = (HeldOperationUpdate)(object)System.Reflection.DispatchProxy.Create<ISignalRNotificationService, HeldOperationUpdate>();
        var storage = (SteamAuthStorageService)CacheScanGateHarness.GetField(host.Service, "_steamAuthRepository")!;

        Task? crawl = null;
        if (order == SignOutCancelsASourceDisposedAfterItsRead)
        {
            // The sign-out reads `_currentRebuildCts`, and only then cancels what it read. A crawl that ends in between
            // runs `ReleaseDepotLock()`, which unpublishes the source, then `runCts.Dispose()`, so the sign-out's cancel
            // lands on a disposed source. This row sets what the sign-out finds at that cancel without a crawl: the flag
            // taken and a source that is already disposed, published for its read.
            var disposed = new CancellationTokenSource();
            disposed.Dispose();
            CacheScanGateHarness.SetField(host.Service, "_rebuildActive", 1);
            CacheScanGateHarness.SetField(host.Service, "_currentRebuildCts", disposed);
        }
        else
        {
            crawl = await host.StartFullCrawlAsync();
            Assert.NotNull(crawl);
            await host.SteamApi.Download(1).Entered.Task.WaitAsync(ForceStoppedCrawlHost.Timeout);
        }

        SignIn(storage);

        Task signOut;
        switch (order)
        {
            case SignOutBeforeTheRunEnds:
                signOut = host.Service.LogoutAsync();
                await Task.WhenAny(signOut, Task.Delay(ForceStoppedCrawlHost.Timeout));
                host.SteamApi.Download(1).Fail();
                break;
            case RunEndsInsideTheSignOut:
                // The sign-out sends the crawl's canceling row before it cancels the crawl's source; that send is held.
                CacheScanGateHarness.SetField(tracker, "_notifications", held);
                signOut = Task.Run(() => host.Service.LogoutAsync());
                await held.Entered.Task.WaitAsync(ForceStoppedCrawlHost.Timeout);
                host.SteamApi.Download(1).Fail();
                await crawl!.WaitAsync(ForceStoppedCrawlHost.Timeout);
                held.Release();
                break;
            case SignOutAfterTheRunEnded:
                host.SteamApi.Download(1).Fail();
                await crawl!.WaitAsync(ForceStoppedCrawlHost.Timeout);
                signOut = host.Service.LogoutAsync();
                break;
            default:
                signOut = host.Service.LogoutAsync();
                break;
        }

        var failure = await Record.ExceptionAsync(() => signOut.WaitAsync(ForceStoppedCrawlHost.Timeout));
        Assert.True(failure is null,
            $"The Steam sign-out ({order}) failed with {failure?.GetType().Name}: {failure?.Message}; it must answer");
        Assert.True(storage.GetAuthData().RefreshToken is null,
            $"After the Steam sign-out ({order}) the saved Steam sign-in is still there");
    }

    private static void SignIn(SteamAuthStorageService storage) => storage.SaveAuthData(new SteamAuthData
    {
        OwnerAccountId = Guid.NewGuid(),
        Mode = "authenticated",
        Username = "user",
        RefreshToken = "token"
    });

    /// <summary>
    /// Sends every notification at once, except the first operation row, which blocks the thread that sends it
    /// until <see cref="Release"/>. Not sealed, for DispatchProxy.
    /// </summary>
    internal class HeldOperationUpdate : NullReturningProxy
    {
        private readonly object _gate = new();
        private bool _released;
        private int _held;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                Monitor.PulseAll(_gate);
            }
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAdminAsync)
                && args?[0] is string eventName
                && eventName == SignalREvents.OperationUpdated
                && Interlocked.Exchange(ref _held, 1) == 0)
            {
                Entered.TrySetResult();
                lock (_gate)
                {
                    if (!_released) Monitor.Wait(_gate, ForceStoppedCrawlHost.Timeout);
                }
            }

            return base.Invoke(targetMethod, args);
        }
    }
}
