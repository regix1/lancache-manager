using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

// The image fetch passes here take the image fetch's process-wide execution lock.
[Collection(nameof(GameImageExecutionLockCollection))]
public class ScheduledServiceStartupTests
{
    [Fact]
    public async Task GameImageFetchService_StartupWaitsForSetup_AndRunsFetchWorkAsync()
    {
        var calls = new List<string>();
        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.WaitForSetupCompletedAsync) => TrackCallAsync("wait", calls),
            nameof(IStateService.GetServiceInterval) => null,
            nameof(IStateService.GetServiceRunOnStartup) => null,
            _ => DefaultReturn(method.ReturnType)
        });

        var service = new TestGameImageFetchService(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<GameImageFetchService>.Instance,
            new ConfigurationBuilder().Build(),
            stateService,
            CreateDefaultProxy<ISignalRNotificationService>(),
            CreateDefaultProxy<IImageCacheService>(),
            CreateDefaultProxy<IUnifiedOperationTracker>(),
            calls);

        await service.InvokeStartupAsync(CancellationToken.None);

        Assert.Collection(
            calls,
            call => Assert.Equal("wait", call),
            call => Assert.Equal("fetch", call));
    }

    [Fact]
    public async Task OperationHistoryCleanupService_StartupRunsCleanupImmediatelyAsync()
    {
        var expiredId = Guid.NewGuid();
        var recentId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var operations = new List<CacheClearOperation>
        {
            new()
            {
                Id = expiredId,
                Status = OperationStatus.Completed,
                EndTime = DateTime.UtcNow.AddHours(-25)
            },
            new()
            {
                Id = recentId,
                Status = OperationStatus.Completed,
                EndTime = DateTime.UtcNow.AddHours(-1)
            },
            new()
            {
                Id = activeId,
                Status = OperationStatus.Running
            }
        };
        var removedIds = new List<Guid>();
        var stateService = CreateProxy<IStateService>((method, args) => method.Name switch
        {
            nameof(IStateService.GetCacheClearOperations) => operations,
            nameof(IStateService.RemoveCacheClearOperation) => RemoveOperation(
                (Guid)args![0]!,
                operations,
                removedIds),
            nameof(IStateService.GetServiceInterval) => null,
            nameof(IStateService.GetServiceRunOnStartup) => null,
            _ => DefaultReturn(method.ReturnType)
        });

        // Cleanup now routes through ScheduledRunReporter, which awaits the tracker's
        // onTerminalEmit gate. A no-op DispatchProxy never fires that gate and hangs.
        var tracker = CreateRealTracker();
        var service = new TestOperationHistoryCleanupService(
            NullLogger<OperationHistoryCleanupService>.Instance,
            new ConfigurationBuilder().Build(),
            stateService,
            CreateDefaultProxy<ISignalRNotificationService>(),
            tracker);

        await service.InvokeStartupAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(expiredId, removedIds);
        Assert.DoesNotContain(recentId, removedIds);
        Assert.DoesNotContain(activeId, removedIds);
    }

    [Theory]
    [InlineData(RunTrigger.Manual)]
    [InlineData(RunTrigger.Scheduled)]
    public async Task GameImageFetchService_ABackgroundPassUnderHiddenSendsAHiddenRowAsync(RunTrigger trigger)
    {
        var tracker = CreateRealTracker();
        await using var services = NoDownloads();
        var service = CreateImageFetch(services, tracker);
        service.SetNotificationMode(NotificationMode.Hidden);

        var operationId = await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, trigger);

        var run = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == operationId);
        Assert.Equal(RunVisibility.Hidden, run.Visibility);
        var operation = tracker.GetOperation(run.OperationId)!;
        Assert.Equal(NotificationMode.Hidden, operation.Notice!.Mode);
        Assert.Equal(trigger, operation.Notice.Trigger);
        // The pass finds no downloads and ends skipped. Waiting until it releases the process-wide
        // execution lock keeps it from outliving its services or refusing the next pass.
        var executionLock = (SemaphoreSlim)typeof(GameImageFetchService)
            .GetField("_executionLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.True(SpinWait.SpinUntil(
            () => operation.Status.IsTerminal() && executionLock.CurrentCount == 1, TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(RunTrigger.Manual, 1)]
    [InlineData(RunTrigger.Scheduled, 0)]
    [InlineData(RunTrigger.RunAll, 0)]
    public async Task GameImageFetchService_WithNoDownloadsOnlyARunNowReportsASkippedRunAsync(RunTrigger trigger, int runs)
    {
        var tracker = CreateRealTracker();
        await using var services = NoDownloads();
        var service = CreateImageFetch(services, tracker);
        var notice = new RunNotice(NotificationMode.Manual, trigger);
        service.SelectRunNotice(notice);

        await InvokeScopedWorkAsync(service);

        AssertOnlySkippedRuns(tracker, notice, runs);
    }

    [Theory]
    [InlineData(RunTrigger.Manual, 1)]
    [InlineData(RunTrigger.Scheduled, 0)]
    [InlineData(RunTrigger.RunAll, 0)]
    public async Task CacheSnapshotService_WithNoCacheSizeOnlyARunNowReportsASkippedRunAsync(RunTrigger trigger, int runs)
    {
        // Only Windows reads no cache size: on Linux the cache path resolves to a mount through
        // /proc/mounts, and a mounted drive always reports a size.
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), $"cache-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var tracker = CreateRealTracker();
            var configuration = new ConfigurationBuilder().Build();
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            var datasources = new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance);
            var cache = new CacheManagementService(
                configuration,
                NullLogger<CacheManagementService>.Instance,
                paths,
                rustProcessHelper: null!,
                nginxLogRotationService: null!,
                datasources,
                CreateDefaultProxy<IStateService>(),
                dbContextFactory: null!,
                gameCacheDetectionService: null!,
                tracker,
                CreateDefaultProxy<ISignalRNotificationService>(),
                CreateDefaultProxy<ILancacheEnvFileReader>(),
                CreateDefaultProxy<IOperationConflictChecker>(),
                new DatasourceCapabilityService(datasources),
                CacheScanGateHarness.Idle());
            await using var services = new ServiceCollection().BuildServiceProvider();
            var service = new CacheSnapshotService(
                services,
                services.GetRequiredService<IServiceScopeFactory>(),
                cache,
                StateTestMethods.CreateStateService(root),
                NullLogger<CacheSnapshotService>.Instance,
                configuration,
                CreateDefaultProxy<ISignalRNotificationService>(),
                tracker);
            var notice = new RunNotice(NotificationMode.Manual, trigger);
            service.SelectRunNotice(notice);

            await InvokeScopedWorkAsync(service);

            AssertOnlySkippedRuns(tracker, notice, runs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(RunTrigger.Manual, 1)]
    [InlineData(RunTrigger.Scheduled, 0)]
    [InlineData(RunTrigger.RunAll, 0)]
    public async Task OperationHistoryCleanupService_ARunNowWithNothingToCleanReportsOneSkippedRunAsync(
        RunTrigger trigger, int runs)
    {
        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetCacheClearOperations) => new List<CacheClearOperation>(),
            nameof(IStateService.GetServiceInterval) => null,
            nameof(IStateService.GetServiceRunOnStartup) => null,
            _ => DefaultReturn(method.ReturnType)
        });
        var tracker = CreateRealTracker();
        var service = new TestOperationHistoryCleanupService(
            NullLogger<OperationHistoryCleanupService>.Instance,
            new ConfigurationBuilder().Build(),
            stateService,
            CreateDefaultProxy<ISignalRNotificationService>(),
            tracker);
        var notice = new RunNotice(NotificationMode.Manual, trigger);
        service.SelectRunNotice(notice);

        await service.InvokeWorkAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        AssertOnlySkippedRuns(tracker, notice, runs);
    }

    [Fact]
    public void OperationHistoryCleanupService_LoadsPersistedScheduleOverrides()
    {
        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetServiceInterval) => 0.5,
            nameof(IStateService.GetServiceRunOnStartup) => false,
            _ => DefaultReturn(method.ReturnType)
        });

        var service = new TestOperationHistoryCleanupService(
            NullLogger<OperationHistoryCleanupService>.Instance,
            new ConfigurationBuilder().Build(),
            stateService,
            CreateDefaultProxy<ISignalRNotificationService>(),
            CreateDefaultProxy<IUnifiedOperationTracker>());

        Assert.Equal(TimeSpan.FromMinutes(30), service.EffectiveInterval);
        Assert.False(service.RunOnStartup);
    }

    [Fact]
    public void OperationHistoryCleanupService_IsHiddenByDefaultAndAfterAReset()
    {
        var service = new TestOperationHistoryCleanupService(
            NullLogger<OperationHistoryCleanupService>.Instance,
            new ConfigurationBuilder().Build(),
            CreateDefaultProxy<IStateService>(),
            CreateDefaultProxy<ISignalRNotificationService>(),
            CreateDefaultProxy<IUnifiedOperationTracker>());
        // Reset to Defaults also rewrites the scheduled prefill config, so the state has to hold one.
        var state = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetScheduledPrefillConfig) => ScheduledPrefillConfigFactory.CreateDefault(),
            _ => DefaultReturn(method.ReturnType)
        });
        var schedules = new ServiceScheduleRegistry(
            [service], state, CreateDefaultProxy<ISignalRNotificationService>());

        // The Schedules page shows the mode listed here for each service.
        Assert.Equal(NotificationMode.Hidden, ListedMode());
        schedules.SetNotificationMode(service.ServiceKey, NotificationMode.All);
        Assert.Equal(NotificationMode.All, ListedMode());
        schedules.ResetToDefaults();
        Assert.Equal(NotificationMode.Hidden, ListedMode());

        NotificationMode ListedMode() =>
            schedules.GetAll().Single(schedule => schedule.Key == service.ServiceKey).NotificationMode;
    }

    private static UnifiedOperationTracker CreateRealTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private static ServiceProvider NoDownloads()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"scheduled-service-{Guid.NewGuid():N}")
            .Options;
        return new ServiceCollection()
            .AddScoped(_ => new AppDbContext(options))
            .AddSingleton(CreateDefaultProxy<IHttpClientFactory>())
            .BuildServiceProvider();
    }

    private static GameImageFetchService CreateImageFetch(IServiceProvider services, IUnifiedOperationTracker tracker) => new(
        services,
        NullLogger<GameImageFetchService>.Instance,
        new ConfigurationBuilder().Build(),
        CreateDefaultProxy<IStateService>(),
        CreateDefaultProxy<ISignalRNotificationService>(),
        CreateDefaultProxy<IImageCacheService>(),
        tracker);

    // The scoped services' protected tick: opens a scope and runs the service's work in it.
    private static Task InvokeScopedWorkAsync(ScopedScheduledBackgroundService service) =>
        ((Task)typeof(ScopedScheduledBackgroundService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancellationToken)])!
            .Invoke(service, [CancellationToken.None])!)
            .WaitAsync(TimeSpan.FromSeconds(10));

    private static void AssertOnlySkippedRuns(UnifiedOperationTracker tracker, RunNotice notice, int runs)
    {
        var listed = tracker.GetRuns().Runs;
        Assert.Equal(runs, listed.Count);
        Assert.All(listed, run =>
        {
            Assert.Equal("skipped", run.Status);
            Assert.True(run.Retained);
            Assert.Same(notice, tracker.GetOperation(run.OperationId)!.Notice);
        });
    }

    private static Task TrackCallAsync(string call, List<string> calls)
    {
        calls.Add(call);
        return Task.CompletedTask;
    }

    private static object? RemoveOperation(
        Guid id,
        List<CacheClearOperation> operations,
        List<Guid> removedIds)
    {
        removedIds.Add(id);
        operations.RemoveAll(op => op.Id == id);
        return null;
    }

    private static T CreateDefaultProxy<T>() where T : class
        => CreateProxy<T>((method, _) => DefaultReturn(method.ReturnType));

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultReturn(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [DefaultValue(resultType)]);
        }

        return DefaultValue(returnType);
    }

    private static object? DefaultValue(Type type)
    {
        if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null)
        {
            return null;
        }

        return Activator.CreateInstance(type);
    }

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Target method was null.");
            }

            return Handler!(targetMethod, args);
        }
    }

    private sealed class TestOperationHistoryCleanupService : OperationHistoryCleanupService
    {
        public TestOperationHistoryCleanupService(
            ILogger<OperationHistoryCleanupService> logger,
            IConfiguration configuration,
            IStateService stateService,
            ISignalRNotificationService notifications,
            IUnifiedOperationTracker operationTracker)
            : base(logger, configuration, stateService, notifications, operationTracker)
        {
        }

        public Task InvokeStartupAsync(CancellationToken cancellationToken)
            => base.OnStartupAsync(cancellationToken);

        public Task InvokeWorkAsync(CancellationToken cancellationToken)
            => base.ExecuteWorkAsync(cancellationToken);
    }

    private sealed class TestGameImageFetchService : GameImageFetchService
    {
        private readonly ICollection<string> _calls;

        public TestGameImageFetchService(
            IServiceProvider serviceProvider,
            ILogger<GameImageFetchService> logger,
            IConfiguration configuration,
            IStateService stateService,
            ISignalRNotificationService notifications,
            IImageCacheService imageCacheService,
            IUnifiedOperationTracker operationTracker,
            ICollection<string> calls)
            : base(serviceProvider, logger, configuration, stateService, notifications, imageCacheService, operationTracker)
        {
            _calls = calls;
        }

        public Task InvokeStartupAsync(CancellationToken cancellationToken)
            => base.OnStartupAsync(cancellationToken);

        protected override Task ExecuteWorkAsync(
            IServiceProvider scopedServices,
            CancellationToken stoppingToken)
        {
            _calls.Add("fetch");
            return Task.CompletedTask;
        }
    }
}
