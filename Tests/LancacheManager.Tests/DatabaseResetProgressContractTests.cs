using System.Text.Json;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class DatabaseResetProgressContractTests
{
    [Fact]
    public void SharedStatusContractPreservesFractionalPercentAndContext()
    {
        var response = new DatabaseResetStatusResponse
        {
            IsProcessing = true,
            Status = OperationStatus.Running,
            PercentComplete = 25.5,
            StageKey = "signalr.dbReset.clearedTable",
            Context = new Dictionary<string, object?> { ["tableName"] = "Events", ["count"] = 4 }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"percentComplete\":25.5", json);
        Assert.Contains("\"stageKey\":\"signalr.dbReset.clearedTable\"", json);
        Assert.Contains("\"tableName\":\"Events\"", json);
    }

    [Fact]
    public async Task SelectedResetContextCreationFailureReachesFailedTerminalAndClearsCurrentState()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NoopSignalRProxy>();
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, RecordingTrackerProxy>();
        var trackerState = (RecordingTrackerProxy)(object)tracker;
        var service = new DatabaseService(
            context: null!,
            notifications,
            NullLogger<DatabaseService>.Instance,
            pathResolver: null!,
            new ThrowingDbContextFactory(),
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: null!,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            tracker);

        var operationId = service.StartResetAsync(["Downloads"]);
        var terminal = await trackerState.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(operationId, terminal.OperationId);
        Assert.False(terminal.Success);
        Assert.Contains("context creation failed", terminal.Error);
        Assert.False(service.IsResetOperationRunning);
        Assert.Null(DatabaseService.CurrentResetOperationId);
        Assert.Null(DatabaseService.CurrentResetProgress);

        var report = typeof(DatabaseService).GetMethod("ReportProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)report.Invoke(service,
            [operationId, true, 99d, OperationStatus.Running, "signalr.dbReset.clearedTable",
                new Dictionary<string, object?> { ["tableName"] = "late" }, "late", null, null, null])!;
        Assert.Null(DatabaseService.CurrentResetProgress);
    }

    /// <summary>
    /// Clearing UserSessions has to sign the account out of Epic and Xbox too, or the Integrations
    /// page still shows both connected after the sessions table was emptied. Neither LogoutAsync is
    /// virtual and DatabaseService holds both services by their concrete type, so nothing can stand
    /// in for them; leaving both out makes each call throw where the reset already catches it, and
    /// the warning that follows is written from that catch and from nowhere else.
    /// </summary>
    [Fact]
    public async Task ClearingUserSessionsSignsOutEpicAndXboxAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.UserSessions.Add(new UserSession
            {
                Id = Guid.NewGuid(),
                SessionTokenHash = "hash",
                SessionType = SessionType.Admin,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var logger = new CapturingLogger<DatabaseService>();
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, RecordingTrackerProxy>();
        var trackerState = (RecordingTrackerProxy)(object)tracker;
        var service = new DatabaseService(
            context: null!,
            DispatchProxy.Create<ISignalRNotificationService, NoopSignalRProxy>(),
            logger,
            pathResolver: null!,
            database.Factory,
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: null!,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            tracker);

        _ = service.StartResetAsync(["UserSessions"]);
        var terminal = await trackerState.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(terminal.Success, terminal.Error);
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("Xbox auth", StringComparison.Ordinal));
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("Epic auth", StringComparison.Ordinal));

        await using var cleared = database.Factory.CreateDbContext();
        Assert.Empty(await cleared.UserSessions.ToListAsync());
    }

    /// <summary>
    /// A reset that clears sessions and download rows logs every platform out once and before it
    /// takes the log lock, and empties the sessions table under that lock. Each logout fails into
    /// its own warning (the services are left out, as above), which is where the lock is read.
    /// </summary>
    [Fact]
    public async Task SessionLogoutsRunOnceBeforeTheLogLockAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.UserSessions.Add(new UserSession
            {
                Id = Guid.NewGuid(),
                SessionTokenHash = "hash",
                SessionType = SessionType.Admin,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, RecordingTrackerProxy>();
        var trackerState = (RecordingTrackerProxy)(object)tracker;
        var owner = new OperationStateService(
            NullLogger<OperationStateService>.Instance,
            new ConfigurationBuilder().Build(),
            stateService: null!,
            scopes: null!,
            applicationLifetime: null!,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            tracker);
        await using var services = new ServiceCollection().AddSingleton(owner).BuildServiceProvider();
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = Path.Combine(
            Path.GetTempPath(),
            "lcm-reset-log-lock-" + Guid.NewGuid().ToString("N"));
        var logoutsWithoutLock = new List<bool>();
        var deletesUnderLock = new List<bool>();
        var logger = new CapturingLogger<DatabaseService>
        {
            // The reset releases its lock only after this thread moves on, so a bounded wait tells
            // whether the reset already held the lock when it wrote the entry.
            OnLogged = entry =>
            {
                if (entry.Message.Contains("auth during session reset", StringComparison.Ordinal))
                {
                    logoutsWithoutLock.Add(owner.WaitForLogStepAsync(active: false, CancellationToken.None)
                        .Wait(TimeSpan.FromSeconds(5)));
                }
                else if (entry.Message.Contains("Clearing UserSessions table", StringComparison.Ordinal))
                {
                    deletesUnderLock.Add(owner.WaitForLogStepAsync(active: true, CancellationToken.None)
                        .Wait(TimeSpan.FromSeconds(5)));
                }
            }
        };
        var service = new DatabaseService(
            context: null!,
            DispatchProxy.Create<ISignalRNotificationService, NoopSignalRProxy>(),
            logger,
            paths,
            database.Factory,
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            services,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            tracker);

        _ = service.StartResetAsync(["UserSessions", "Downloads"]);
        var terminal = await trackerState.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(terminal.Success, terminal.Error);
        // Steam, Xbox and Epic, once each.
        Assert.Equal([true, true, true], logoutsWithoutLock);
        Assert.Equal([true], deletesUnderLock);
        await owner.WaitForLogStepAsync(active: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void SelectedResetPublishesSuccessOnlyAfterForeignKeyCleanup()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "Api",
            "LancacheManager",
            "Infrastructure",
            "Services",
            "System",
            "DatabaseService.cs"));
        var cleanup = source.IndexOf(
            "SET session_replication_role = DEFAULT;",
            StringComparison.Ordinal);
        var success = source.IndexOf(
            "_operationTracker.CompleteOperation(operationId, success: true);",
            StringComparison.Ordinal);

        Assert.True(cleanup >= 0, "foreign-key cleanup statement is missing");
        Assert.True(success > cleanup, "success terminal must follow foreign-key cleanup");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    private class NoopSignalRProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
    }

    private class RecordingTrackerProxy : DispatchProxy
    {
        private Action? _terminalCleanup;
        private OperationInfo? _operation;

        internal TaskCompletionSource<(Guid OperationId, bool Success, string Error)> Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                    _terminalCleanup = args?[4] as Action;
                    _operation = new OperationInfo
                    {
                        Id = Guid.NewGuid(),
                        Type = (OperationType)args![0]!,
                        Name = (string)args[1]!,
                        Metadata = args[3]
                    };
                    return _operation.Id;
                case nameof(IUnifiedOperationTracker.UpdateProgress):
                    if (_operation is { CompletedFlag: 0 })
                    {
                        (args![3] as Action<OperationInfo>)?.Invoke(_operation);
                    }
                    return null;
                case nameof(IUnifiedOperationTracker.CompleteOperation):
                    if (_operation is null || Interlocked.Exchange(ref _operation.CompletedFlag, 1) != 0)
                    {
                        return null;
                    }
                    var operationId = (Guid)args![0]!;
                    var success = (bool)args[1]!;
                    var error = args[2] as string ?? string.Empty;
                    (args[5] as Action<OperationInfo>)?.Invoke(_operation);
                    _terminalCleanup?.Invoke();
                    Terminal.TrySetResult((operationId, success, error));
                    return null;
                case nameof(IUnifiedOperationTracker.GetActiveOperations):
                case nameof(IUnifiedOperationTracker.GetWaitingOperations):
                    return Array.Empty<OperationInfo>();
                case nameof(IUnifiedOperationTracker.GetOperation):
                case nameof(IUnifiedOperationTracker.GetOperationByScope):
                    return null;
                case nameof(IUnifiedOperationTracker.CancelOperation):
                    return OperationCancelResult.NotFound;
                case nameof(IUnifiedOperationTracker.ForceKillOperation):
                case nameof(IUnifiedOperationTracker.TryRestoreOperation):
                    return false;
                default:
                    return null;
            }
        }
    }
}
