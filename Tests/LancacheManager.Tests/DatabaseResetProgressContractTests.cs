using System.Text.Json;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
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

        internal TaskCompletionSource<(Guid OperationId, bool Success, string Error)> Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                    _terminalCleanup = args?[4] as Action;
                    return Guid.NewGuid();
                case nameof(IUnifiedOperationTracker.CompleteOperation):
                    var operationId = (Guid)args![0]!;
                    var success = (bool)args[1]!;
                    var error = args[2] as string ?? string.Empty;
                    _terminalCleanup?.Invoke();
                    Terminal.TrySetResult((operationId, success, error));
                    return null;
                case nameof(IUnifiedOperationTracker.GetActiveOperations):
                case nameof(IUnifiedOperationTracker.GetWaitingOperations):
                    return Array.Empty<OperationInfo>();
                case nameof(IUnifiedOperationTracker.GetOperation):
                case nameof(IUnifiedOperationTracker.GetOperationByEntityKey):
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
