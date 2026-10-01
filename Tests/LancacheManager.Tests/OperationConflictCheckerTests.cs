using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Tests;

public class OperationConflictCheckerTests
{
    [Fact]
    public async Task Blocks_EveryNewOperation_When_DownloadHistoryUpgrade_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        var blockerId = RegisterBulkOperation(
            tracker.Tracker,
            OperationType.DownloadHistoryUpgrade,
            "Upgrading download history");

        var operationTypes = Enum.GetValues<OperationType>();
        for (var index = 0; index < operationTypes.Length; index++)
        {
            var requestedType = operationTypes[index];
            var requestedScope = index % 2 == 0
                ? ConflictScope.Bulk()
                : ConflictScope.NamedGame("blizzard", "Diablo IV");

            var response = await tracker.Checker.CheckAsync(
                requestedType,
                requestedScope,
                CancellationToken.None);

            Assert.NotNull(response);
            Assert.Equal("OPERATION_CONFLICT", response!.Code);
            Assert.Equal("errors.conflict.downloadHistoryUpgradeActive", response.StageKey);
            Assert.False(string.IsNullOrWhiteSpace(response.StageKey));
            Assert.Null(response.Error);
            Assert.Equal(blockerId, response.ActiveOperationId);
            Assert.Equal(nameof(OperationType.DownloadHistoryUpgrade), response.ActiveOperationType);
            Assert.Equal("bulk", response.ActiveOperationScope);
        }
    }

    [Fact]
    public async Task Blocks_DownloadHistoryUpgrade_When_AnyOperation_IsActiveAsync()
    {
        foreach (var activeType in Enum.GetValues<OperationType>())
        {
            using var tracker = new TrackerHarness();
            var blockerId = RegisterBulkOperation(tracker.Tracker, activeType, activeType.ToString());

            var response = await tracker.Checker.CheckAsync(
                OperationType.DownloadHistoryUpgrade,
                ConflictScope.Bulk(),
                CancellationToken.None);

            Assert.NotNull(response);
            Assert.Equal("OPERATION_CONFLICT", response!.Code);
            Assert.Equal(
                activeType == OperationType.DownloadHistoryUpgrade
                    ? "errors.conflict.downloadHistoryUpgradeActive"
                    : "errors.conflict.globalOperationActive",
                response.StageKey);
            Assert.False(string.IsNullOrWhiteSpace(response.StageKey));
            Assert.Null(response.Error);
            Assert.Equal(blockerId, response.ActiveOperationId);
            Assert.Equal(activeType.ToString(), response.ActiveOperationType);
            Assert.Equal("bulk", response.ActiveOperationScope);
        }
    }

    [Fact]
    public async Task Waiting_DownloadHistoryUpgrade_DoesNotBlockAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(
            tracker.Tracker,
            OperationType.DownloadHistoryUpgrade,
            "Upgrading download history",
            OperationStatus.Waiting);

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Theory]
    [InlineData(OperationType.CacheClearing, OperationType.LogProcessing,
        "Cannot start LogProcessing: a CacheClearing operation is in progress.")]
    [InlineData(OperationType.DatabaseReset, OperationType.LogProcessing,
        "Cannot start LogProcessing: a DatabaseReset operation is in progress.")]
    [InlineData(OperationType.LogProcessing, OperationType.CacheClearing,
        "Cannot start CacheClearing: another operation (LogProcessing) is still running.")]
    [InlineData(OperationType.LogProcessing, OperationType.DatabaseReset,
        "Cannot start DatabaseReset: another operation (LogProcessing) is still running.")]
    public async Task ExistingGlobalConflictsKeepLegacyEnglishErrorAsync(
        OperationType activeType,
        OperationType requestedType,
        string expectedError)
    {
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, activeType, activeType.ToString());

        var response = await tracker.Checker.CheckAsync(
            requestedType,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.globalOperationActive", response!.StageKey);
        Assert.Equal(expectedError, response.Error);

        var json = JsonSerializer.Serialize(response, ConflictJsonOptions());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expectedError, document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpgradeConflictSerializationOmitsLegacyEnglishErrorAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(
            tracker.Tracker,
            OperationType.DownloadHistoryUpgrade,
            "Upgrading download history");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.NotNull(response);
        var json = JsonSerializer.Serialize(response, ConflictJsonOptions());
        using var document = JsonDocument.Parse(json);
        Assert.Equal("OPERATION_CONFLICT", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "errors.conflict.downloadHistoryUpgradeActive",
            document.RootElement.GetProperty("stageKey").GetString());
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }


    [Fact]
    public async Task ExpectedConflict_IsLoggedAtDebug_NotInformationAsync()
    {
        var logger = new CapturingLogger<OperationConflictChecker>();
        using var tracker = new TrackerHarness(logger);
        RegisterBulkOperation(tracker.Tracker, OperationType.CacheSizeScan, "Cache Size Scan");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Theory]
    [InlineData(OperationType.LogProcessing)]
    [InlineData(OperationType.DatabaseReset)]
    public async Task RepairingRecordBlocksAffectedWorkWithOriginalOperationIdAsync(
        OperationType requestedType)
    {
        using var tracker = new TrackerHarness();
        var repairId = await tracker.AddRepairBlockerAsync();

        var response = await tracker.Checker.CheckAsync(
            requestedType,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(repairId, response!.ActiveOperationId);
        Assert.Equal(nameof(OperationType.LogProcessing), response.ActiveOperationType);
        Assert.Equal("repair", response.ActiveOperationScope);
        Assert.Equal(true, response.Context!["repairPending"]);
    }

    [Fact]
    public async Task Blocks_ServiceRemoval_When_ServiceScopedEvictionRemoval_IsActive_ForSameServiceAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterEvictionRemoval(tracker.Tracker, scope: "service", key: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.ServiceRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.serviceWideActive", response!.StageKey);
        Assert.Equal("service:steam", response.ActiveOperationScope);
        Assert.Equal(nameof(OperationType.EvictionRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_CorruptionRemoval_When_ServiceRemoval_IsActive_ForSameServiceAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.serviceWideActive", response!.StageKey);
        Assert.Equal("service:steam", response.ActiveOperationScope);
        Assert.Equal(nameof(OperationType.ServiceRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_CorruptionDetection_When_ServiceRemoval_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionDetection,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.overlappingEntity", response!.StageKey);
        Assert.Equal(nameof(OperationType.ServiceRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Allows_ServiceScopedCrossTypeRemoval_When_ServiceDiffersAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionRemoval,
            ConflictScope.Service("epicgames"),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Blocks_LogRemoval_When_SameServiceLogRemoval_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterLogRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.duplicate", response!.StageKey);
        Assert.Equal("service:steam", response.ActiveOperationScope);
        Assert.Equal(nameof(OperationType.LogRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_LogRemoval_When_DifferentServiceLogRemoval_IsActiveAsync()
    {
        // Heavy data ops run one at a time: both log removals rewrite the same access.log,
        // so the second one queues instead of running concurrently.
        using var tracker = new TrackerHarness();
        RegisterLogRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("epicgames"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.LogRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_LogProcessing_When_BulkCorruptionScan_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterBulkCorruptionDetection(tracker.Tracker);

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.CorruptionDetection), response.ActiveOperationType);
    }

    [Theory]
    [InlineData(OperationType.GameRemoval)]
    [InlineData(OperationType.ServiceRemoval)]
    [InlineData(OperationType.CorruptionRemoval)]
    [InlineData(OperationType.EvictionRemoval)]
    public async Task CorruptionDetection_BlocksCacheMutatingRemovalInBothDirectionsAsync(
        OperationType removalType)
    {
        using (var activeRemoval = new TrackerHarness())
        {
            RegisterCacheMutatingRemoval(activeRemoval.Tracker, removalType);
            var scanResponse = await activeRemoval.Checker.CheckAsync(
                OperationType.CorruptionDetection,
                ConflictScope.Bulk(),
                CancellationToken.None);
            Assert.NotNull(scanResponse);
            Assert.Equal("errors.conflict.overlappingEntity", scanResponse!.StageKey);
        }

        using var activeScan = new TrackerHarness();
        RegisterBulkCorruptionDetection(activeScan.Tracker);
        var removalResponse = await activeScan.Checker.CheckAsync(
            removalType,
            ConflictScope.Service("steam"),
            CancellationToken.None);
        Assert.NotNull(removalResponse);
        Assert.Equal("errors.conflict.overlappingEntity", removalResponse!.StageKey);
    }

    [Fact]
    public async Task Blocks_GameDetection_When_LogProcessing_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.LogProcessing, "Log Processing");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.LogProcessing), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_LogRemoval_When_LogProcessing_IsActiveAsync()
    {
        // Log removal rewrites access.log while log processing reads it - never concurrent.
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.LogProcessing, "Log Processing");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
    }

    [Fact]
    public async Task Blocks_CacheSizeScan_When_EvictionScan_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.EvictionScan, "Eviction Scan");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CacheSizeScan,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
    }

    [Fact]
    public async Task Duplicate_LogProcessing_Reports_Duplicate_Not_HeavyAsync()
    {
        // The queue relies on the "duplicate" stage key to idempotently return the active op
        // instead of parking a second copy - the heavy section must preserve it.
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.LogProcessing, "Log Processing");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.duplicate", response!.StageKey);
    }


    [Fact]
    public async Task Blocks_ServiceRemoval_When_LogRemoval_IsActiveAsync()
    {
        // Service removal rewrites access.log to prune the service's log lines - the same
        // file the active log removal is rewriting - so it queues instead of racing it.
        using var tracker = new TrackerHarness();
        RegisterLogRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.ServiceRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.LogRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_LogRemoval_When_GameRemoval_IsActiveAsync()
    {
        // The game removal's Rust worker rewrites access.log too (prunes the game's lines),
        // so a new log removal must queue behind it, not silently stall on the internal lock.
        using var tracker = new TrackerHarness();
        RegisterNamedGameRemoval(tracker.Tracker, service: "blizzard", gameName: "Diablo IV");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("teso"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.GameRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_GameRemoval_When_LogProcessing_IsActiveAsync()
    {
        // Log processing reads access.log from a saved position; a removal shrinking the file
        // underneath it corrupts the position, so the removal queues.
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.LogProcessing, "Log Processing");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
    }

    [Fact]
    public async Task Blocks_NamedGameRemoval_When_ServiceRemoval_IsActive_ForSameServiceAsync()
    {
        // A service-wide removal for "blizzard" must cover (block) a named Blizzard game removal.
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "blizzard");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.serviceWideActive", response!.StageKey);
        Assert.Equal("service:blizzard", response.ActiveOperationScope);
        Assert.Equal(nameof(OperationType.ServiceRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Allows_NamedGameRemoval_When_ServiceRemoval_IsActive_ForDifferentServiceAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "riot");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Blocks_NamedGameRemoval_When_SameNamedGameRemoval_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterNamedGameRemoval(tracker.Tracker, service: "blizzard", gameName: "Diablo IV");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.duplicate", response!.StageKey);
    }

    [Fact]
    public async Task Allows_NamedGameRemoval_When_DifferentNamedGameRemoval_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterNamedGameRemoval(tracker.Tracker, service: "blizzard", gameName: "Diablo IV");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Overwatch"),
            CancellationToken.None);

        Assert.Null(response);
    }




    private static Guid RegisterBulkOperation(
        UnifiedOperationTracker tracker,
        OperationType type,
        string name,
        OperationStatus initialStatus = OperationStatus.Running)
    {
        // Bulk-scope heavy ops (LogProcessing / GameDetection / EvictionScan / CacheSizeScan)
        // register without metadata, so DeriveScope falls back to Bulk().
        return tracker.RegisterOperation(
            type,
            name,
            new CancellationTokenSource(),
            initialStatus: initialStatus);
    }

    private static JsonSerializerOptions ConflictJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static void RegisterBulkCorruptionDetection(UnifiedOperationTracker tracker)
    {
        tracker.RegisterOperation(
            OperationType.CorruptionDetection,
            "Corruption Detection",
            new CancellationTokenSource(),
            new CorruptionDetectionMetrics());
    }

    private static void RegisterNamedGameRemoval(UnifiedOperationTracker tracker, string service, string gameName)
    {
        // Mirrors GamesController.RemoveNamedGameFromCacheAsync: entityKind "named",
        // entityKey "{service}:{gameName}".
        tracker.RegisterOperation(
            OperationType.GameRemoval,
            $"Game Removal: {gameName}",
            new CancellationTokenSource(),
            new RemovalMetrics
            {
                EntityKind = "named",
                EntityKey = $"{service}:{gameName}",
                EntityName = gameName
            });
    }

    private static void RegisterLogRemoval(UnifiedOperationTracker tracker, string serviceName)
    {
        tracker.RegisterOperation(
            OperationType.LogRemoval,
            "Log Removal",
            new CancellationTokenSource(),
            new RemovalMetrics
            {
                EntityKind = "service",
                EntityKey = serviceName.ToLowerInvariant(),
                EntityName = serviceName
            });
    }

    private static void RegisterServiceRemoval(UnifiedOperationTracker tracker, string serviceName)
    {
        tracker.RegisterOperation(
            OperationType.ServiceRemoval,
            $"Service removal: {serviceName}",
            new CancellationTokenSource(),
            new RemovalMetrics
            {
                EntityKey = serviceName.ToLowerInvariant(),
                EntityName = serviceName
            });
    }

    private static void RegisterEvictionRemoval(
        UnifiedOperationTracker tracker,
        string scope,
        string key)
    {
        tracker.RegisterOperation(
            OperationType.EvictionRemoval,
            $"Eviction removal: {scope}:{key}",
            new CancellationTokenSource(),
            new EvictionRemovalMetadata
            {
                Scope = scope,
                Key = key
            });
    }

    private sealed class TrackerHarness : IDisposable
    {
        private readonly string _root;
        private readonly OperationRepairTests.RepairHarness _repairHarness;
        private readonly TaskCompletionSource<bool> _repairEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseRepair =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _repairFinish;

        public UnifiedOperationTracker Tracker { get; }

        public OperationConflictChecker Checker { get; }

        public TrackerHarness(ILogger<OperationConflictChecker>? logger = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "lm-conflict-repair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _repairHarness = OperationRepairTests.RepairHarness.CreateAsync(
                    _root,
                    apply: async (_, cancellationToken) =>
                    {
                        _repairEntered.TrySetResult(true);
                        await _releaseRepair.Task.WaitAsync(cancellationToken);
                    })
                .GetAwaiter().GetResult();
            var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
            Tracker = tracker;
            Checker = new OperationConflictChecker(
                tracker,
                _repairHarness.Owner,
                logger ?? NullLogger<OperationConflictChecker>.Instance);
        }

        public void Dispose()
        {
            foreach (var operation in Tracker.GetActiveOperations())
            {
                Tracker.CompleteOperation(operation.Id, success: false, error: "Disposed test harness");
            }
            _releaseRepair.TrySetResult(true);
            _repairFinish?.GetAwaiter().GetResult();
            _repairHarness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        public async Task<Guid> AddRepairBlockerAsync()
        {
            var repair = new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.LogProcessing,
                Name = "Log processing",
                StartedAt = DateTime.UtcNow,
                Sources =
                [
                    new OperationRepairSource
                    {
                        Datasource = "alpha",
                        LogRoot = "logs/alpha",
                        CacheRoot = "cache/alpha",
                        KeyScheme = "steam"
                    }
                ],
                LogProcessing = new LogProcessingRepair()
            };
            await _repairHarness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
            await _repairHarness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
            _repairFinish = _repairHarness.Owner.FinishRepairAsync(
                repair.Id,
                success: false,
                cancelled: false,
                error: "interrupted");
            await _repairEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return repair.Id;
        }
    }

    private static void RegisterCacheMutatingRemoval(
        UnifiedOperationTracker tracker,
        OperationType type)
    {
        tracker.RegisterOperation(
            type,
            $"{type}: steam",
            new CancellationTokenSource(),
            type == OperationType.EvictionRemoval
                ? new EvictionRemovalMetadata { Scope = "service", Key = "steam" }
                : new RemovalMetrics
                {
                    EntityKind = "service",
                    EntityKey = "steam",
                    EntityName = "steam"
                });
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }
}

internal static class OperationConflictTestServices
{
    private static readonly Lazy<OperationRepairTests.RepairHarness> _repair = new(() =>
        OperationRepairTests.RepairHarness.CreateAsync(
                Path.Combine(Path.GetTempPath(), "lm-operation-conflict-owner-" + Guid.NewGuid().ToString("N")))
            .GetAwaiter()
            .GetResult());

    public static OperationStateService Owner => _repair.Value.Owner;

    public static OperationConflictChecker Create(
        IUnifiedOperationTracker tracker,
        ILogger<OperationConflictChecker> logger)
    {
        return new OperationConflictChecker(tracker, _repair.Value.Owner, logger);
    }
}
