using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

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

        var service = new TestOperationHistoryCleanupService(
            NullLogger<OperationHistoryCleanupService>.Instance,
            new ConfigurationBuilder().Build(),
            stateService);

        await service.InvokeStartupAsync(CancellationToken.None);

        Assert.Contains(expiredId, removedIds);
        Assert.DoesNotContain(recentId, removedIds);
        Assert.DoesNotContain(activeId, removedIds);
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
            stateService);

        Assert.Equal(TimeSpan.FromMinutes(30), service.EffectiveInterval);
        Assert.False(service.RunOnStartup);
    }

    private static Task TrackCallAsync(string call, ICollection<string> calls)
    {
        calls.Add(call);
        return Task.CompletedTask;
    }

    private static object? RemoveOperation(
        Guid id,
        List<CacheClearOperation> operations,
        ICollection<Guid> removedIds)
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
            IStateService stateService)
            : base(logger, configuration, stateService)
        {
        }

        public Task InvokeStartupAsync(CancellationToken cancellationToken)
            => base.OnStartupAsync(cancellationToken);
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
            ICollection<string> calls)
            : base(serviceProvider, logger, configuration, stateService, notifications, imageCacheService)
        {
            _calls = calls;
        }

        public Task InvokeStartupAsync(CancellationToken cancellationToken)
            => base.OnStartupAsync(cancellationToken);

        protected override Task ExecuteScopedWorkAsync(
            IServiceProvider scopedServices,
            CancellationToken stoppingToken)
        {
            _calls.Add("fetch");
            return Task.CompletedTask;
        }
    }
}
