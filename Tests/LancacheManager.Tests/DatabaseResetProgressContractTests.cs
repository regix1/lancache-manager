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
    public void RustProgressFileSchemaRoundTripsStageAndCompleteContext()
    {
        const string json = """
            {
              "isProcessing": true,
              "percentComplete": 25.5,
              "status": "deleting",
              "message": "Clearing Downloads",
              "stageKey": "signalr.dbReset.deleting",
              "context": {
                "tableName": "Downloads",
                "deletedRows": 25,
                "totalRows": 100,
                "tablesCleared": 1,
                "totalTables": 4,
                "filesDeleted": 0
              },
              "tablesCleared": 1,
              "totalTables": 4,
              "filesDeleted": 0,
              "timestamp": "2026-07-12T12:00:00Z"
            }
            """;

        var progress = JsonSerializer.Deserialize<RustDatabaseResetService.ProgressData>(json)!;
        Assert.Equal("signalr.dbReset.deleting", progress.StageKey);
        Assert.Equal("Downloads", ((JsonElement)progress.Context["tableName"]!).GetString());
        Assert.Equal(25, ((JsonElement)progress.Context["deletedRows"]!).GetInt32());
        Assert.Equal(100, ((JsonElement)progress.Context["totalRows"]!).GetInt32());
    }

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

    private sealed class ThrowingDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            throw new InvalidOperationException("context creation failed");

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<AppDbContext>(new InvalidOperationException("context creation failed"));
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
                case nameof(IUnifiedOperationTracker.ForceKillOperation):
                case nameof(IUnifiedOperationTracker.TryRestoreOperation):
                    return false;
                default:
                    return null;
            }
        }
    }
}
