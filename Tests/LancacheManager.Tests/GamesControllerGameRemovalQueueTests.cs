using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Configuration;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class GamesControllerGameRemovalQueueTests : IDisposable
{
    private readonly string _root;
    private readonly RecordingOperationQueue _queue;
    private readonly GamesController _controller;

    public GamesControllerGameRemovalQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-game-removal-queue-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(_root, "cache");
        var logPath = Path.Combine(_root, "logs");
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(logPath);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = cachePath,
                ["LanCache:DataSources:0:LogPath"] = logPath,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        var datasourceService = new DatasourceService(
            configuration,
            pathResolver,
            NullLogger<DatasourceService>.Instance);
        var capabilityService = new DatasourceCapabilityService(datasourceService);

        _queue = new RecordingOperationQueue(new QueuedOperationResponse
        {
            OperationId = Guid.NewGuid(),
            Queued = true,
            Status = "waiting"
        });

        var conflict = CreateProxy<IOperationConflictChecker>((method, _) =>
        {
            if (method.Name == nameof(IOperationConflictChecker.CheckAsync))
            {
                return Task.FromResult<OperationConflictResponse?>(new OperationConflictResponse
                {
                    StageKey = "errors.conflict.overlappingEntity",
                    Error = "blocked"
                });
            }

            return DefaultReturn(method.ReturnType);
        });

        _controller = new GamesController(
            gameCacheDetectionService: CreateCachedDetectionService(),
            cacheManagementService: null!,
            notifications: CreateProxy<ISignalRNotificationService>((method, _) => DefaultReturn(method.ReturnType)),
            logger: NullLogger<GamesController>.Instance,
            pathResolver: pathResolver,
            operationTracker: CreateProxy<IUnifiedOperationTracker>((method, _) => DefaultReturn(method.ReturnType)),
            conflictChecker: conflict,
            operationQueue: _queue,
            capabilityService: capabilityService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveGameFromCache_WhenConflict_EnqueuesSteamGameRemovalAsync()
    {
        var result = await _controller.RemoveGameFromCacheAsync(570, CancellationToken.None);

        AssertQueuedGameRemoval(result, ConflictScope.SteamGame(570));
    }

    [Fact]
    public async Task RemoveEpicGameFromCache_WhenConflict_EnqueuesEpicGameRemovalAsync()
    {
        var result = await _controller.RemoveEpicGameFromCacheAsync("Fortnite", CancellationToken.None);

        AssertQueuedGameRemoval(result, ConflictScope.EpicGame("cat-fortnite", "Fortnite"));
    }

    [Fact]
    public async Task RemoveNamedGameFromCache_WhenConflict_EnqueuesNamedGameRemovalAsync()
    {
        var result = await _controller.RemoveNamedGameFromCacheAsync("blizzard", "Diablo IV", CancellationToken.None);

        AssertQueuedGameRemoval(result, ConflictScope.NamedGame("blizzard", "Diablo IV"));
    }

    [Fact]
    public void StartRemoval_StartedPayload_UsesPlatformIdentityFields()
    {
        var source = ReadSource("Controllers", "Cache", "GamesController.cs");

        Assert.Contains("GameAppId: isNameKeyed ? null : appId", source, StringComparison.Ordinal);
        Assert.Contains("EpicAppId: isEpic ? epicAppId : null", source, StringComparison.Ordinal);
        Assert.Contains("GameName: displayName", source, StringComparison.Ordinal);
        Assert.Contains("StartedEventName: SignalREvents.GameRemovalStarted", source, StringComparison.Ordinal);
    }

    private void AssertQueuedGameRemoval(IActionResult result, ConflictScope expectedScope)
    {
        var accepted = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(202, accepted.StatusCode);
        var body = Assert.IsType<QueuedOperationResponse>(accepted.Value);
        Assert.True(body.Queued);
        Assert.Equal(_queue.Response.OperationId, body.OperationId);
        Assert.Equal(OperationType.GameRemoval, _queue.Type);
        Assert.Equal(expectedScope, _queue.Scope);
        Assert.NotNull(_queue.Start);
    }

    private static GameCacheDetectionService CreateCachedDetectionService()
    {
        var service = (GameCacheDetectionService)RuntimeHelpers.GetUninitializedObject(
            typeof(GameCacheDetectionService));
        typeof(GameCacheDetectionService)
            .GetField("_detectionCacheLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new SemaphoreSlim(1, 1));
        typeof(GameCacheDetectionService)
            .GetField("_cachedDetectionResponse", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(
                service,
                new GameCacheDetectionService.DetectionOperationResponse
                {
                    Games =
                    [
                        new GameCacheInfo
                        {
                            GameAppId = 570,
                            GameName = "Dota 2",
                            Service = "steam"
                        },
                        new GameCacheInfo
                        {
                            GameAppId = 0,
                            GameName = "Fortnite",
                            Service = "epicgames",
                            EpicAppId = "cat-fortnite"
                        },
                        new GameCacheInfo
                        {
                            GameAppId = 0,
                            GameName = "Diablo IV",
                            Service = "blizzard"
                        }
                    ]
                });
        return service;
    }

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, "Api", "LancacheManager", .. pathSegments]));
    }

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
        => !type.IsValueType || Nullable.GetUnderlyingType(type) != null
            ? null
            : Activator.CreateInstance(type);

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler!(targetMethod!, args);
    }

    private sealed class RecordingOperationQueue(QueuedOperationResponse response) : IOperationQueue
    {
        public QueuedOperationResponse Response { get; } = response;
        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public string? DisplayName { get; private set; }
        public Func<Task<Guid?>>? Start { get; private set; }

        public Task<QueuedOperationResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct)
        {
            Type = type;
            Scope = scope;
            DisplayName = displayName;
            Start = start;
            return Task.FromResult(Response);
        }

        public string? GetWaitingBlockerName(Guid waitingOperationId) => null;
    }
}
