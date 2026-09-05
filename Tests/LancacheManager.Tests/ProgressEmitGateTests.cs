using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

public class ProgressEmitGateTests
{
    [Fact]
    public void StageChangesEmitImmediatelyAndDuplicatesDoNot()
    {
        var gate = new ProgressEmitGate(250);

        Assert.True(gate.ShouldEmit("stage.one", 1, 1_000));
        Assert.False(gate.ShouldEmit("stage.one", 1, 2_000));
        Assert.True(gate.ShouldEmit("stage.two", 2, 1_001));
    }

    [Fact]
    public void PendingLatestRevisionEmitsAtTimeBoundary()
    {
        var gate = new ProgressEmitGate(250);

        Assert.True(gate.ShouldEmit("stage", 1, 10_000));
        Assert.False(gate.ShouldEmit("stage", 2, 10_249));
        Assert.True(gate.ShouldEmit("stage", 2, 10_250));
        Assert.False(gate.ShouldEmit("stage", 2, 10_500));
    }

    [Fact]
    public void TickWrapUsesMonotonicUnsignedElapsedTime()
    {
        var gate = new ProgressEmitGate(250);
        var beforeWrap = long.MaxValue - 100;
        var afterWrap = long.MinValue + 200;

        Assert.True(gate.ShouldEmit("stage", 1, beforeWrap));
        Assert.True(gate.ShouldEmit("stage", 2, afterWrap));
    }

    [Fact]
    public void ConcurrentCallersEmitOneCopyOfARevision()
    {
        var gate = new ProgressEmitGate(0);
        var emitted = 0;

        Parallel.For(0, 64, _ =>
        {
            if (gate.ShouldEmit("stage", 7, 1_000))
            {
                Interlocked.Increment(ref emitted);
            }
        });

        Assert.Equal(1, emitted);
    }

    [Fact]
    public async Task ScheduledProgressCallbackDoesNotWaitForBlockedBrowserDelivery()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, BlockedSend>();
        var blocked = (BlockedSend)(object)notifications;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"progress_emit_{Guid.NewGuid():N}")
            .Options;
        var dbContexts = new TestDbContexts(options);
        var daemon = new TestDaemon(
            NullLogger<SteamDaemonService>.Instance,
            notifications,
            new ConfigurationBuilder().Build(),
            DispatchProxy.Create<IPathResolver, NullReturningProxy>(),
            DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillSessionService(dbContexts, NullLogger<PrefillSessionService>.Instance),
            new PrefillCacheService(dbContexts, NullLogger<PrefillCacheService>.Instance),
            new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions()));
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            IsPrefilling = true,
            PrefillState = PrefillState.Downloading,
            CurrentAppId = "app-1",
            CurrentAppName = "Game",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        daemon.InjectSession(session);
        daemon.AddSubscriber(session.Id, "blocked-client");
        var callbackSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        daemon.PrefillProgressUpdated += (_, _, _) =>
        {
            callbackSeen.TrySetResult();
            return Task.CompletedTask;
        };

        var publish = daemon.PublishProgressAsync(session, new PrefillProgress
        {
            State = "downloading",
            CurrentAppId = session.CurrentAppId,
            CurrentAppName = session.CurrentAppName,
            BytesDownloaded = 1,
            TotalBytes = 10,
            TotalApps = 1
        });

        await blocked.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await callbackSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(publish.IsCompleted);

        blocked.ReleaseSend.TrySetResult();
        await publish;
    }

    private sealed class TestDaemon : SteamDaemonService
    {
        public TestDaemon(
            Microsoft.Extensions.Logging.ILogger<SteamDaemonService> logger,
            ISignalRNotificationService notifications,
            IConfiguration configuration,
            IPathResolver pathResolver,
            IStateService stateService,
            PrefillSessionService sessionService,
            PrefillCacheService cacheService,
            IOptionsMonitor<PrefillNetworkOptions> networkOptions)
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService,
                cacheService, networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

        public Task PublishProgressAsync(DaemonSession session, PrefillProgress progress)
            => NotifyPrefillProgressAsync(session, progress);
    }

    private class BlockedSend : NullReturningProxy
    {
        public TaskCompletionSource SendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.SendToPrefillClientRawAsync))
            {
                SendStarted.TrySetResult();
                return ReleaseSend.Task;
            }

            return base.Invoke(targetMethod, args);
        }
    }

    private sealed class TestDbContexts : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContexts(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }
}
