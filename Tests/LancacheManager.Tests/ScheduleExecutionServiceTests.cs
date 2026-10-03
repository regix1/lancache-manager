using System.Net;
using System.Text.Json;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class ScheduleExecutionServiceTests
{
    [Fact]
    public void CaptureKeepsEveryTerminalStatusAcrossNotificationModes()
    {
        var service = ScheduleExecutionTestService.Create();
        foreach (var mode in Enum.GetValues<NotificationMode>())
        {
            foreach (var status in new[]
            {
                OperationStatus.Completed,
                OperationStatus.Failed,
                OperationStatus.Cancelled,
                OperationStatus.Skipped
            })
            {
                var actor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "saved-name");
                var operation = Complete(status, new RunNotice(mode, RunTrigger.Manual, actor));

                var execution = service.Capture(operation, "logRotation");

                Assert.Equal(status, execution.Status);
                Assert.Equal(ScheduleActorKind.Account, execution.ActorKind);
                Assert.Equal(actor.AccountId, execution.AccountId);
                Assert.Equal("saved-name", execution.Username);
                Assert.Equal(RunTrigger.Manual, execution.Trigger);
                Assert.Equal(status == OperationStatus.Completed ? null : "terminal detail", execution.Detail);
            }
        }
    }

    [Fact]
    public void CaptureClassifiesAutomaticAndRestoredRunsWithoutGuessingAUser()
    {
        var service = ScheduleExecutionTestService.Create();
        var scheduled = service.Capture(
            Complete(OperationStatus.Completed, new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled)),
            "cacheSizeScan");
        var restored = service.Capture(
            Complete(OperationStatus.Failed, new RunNotice(
                NotificationMode.All,
                RunTrigger.Manual,
                new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "stale-name"),
                restoredOrigin: true)),
            "scheduledPrefill");

        Assert.Equal(ScheduleActorKind.Server, scheduled.ActorKind);
        Assert.Equal(RunTrigger.Scheduled, scheduled.Trigger);
        Assert.Null(scheduled.AccountId);
        Assert.Equal(ScheduleActorKind.Unknown, restored.ActorKind);
        Assert.Null(restored.Trigger);
        Assert.Null(restored.AccountId);
        Assert.Null(restored.Username);
    }

    [Fact]
    public async Task ACompletedRunWithAWarningKeepsItOnItsHistoryRowAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new ScheduleExecutionService(
            database.Factory,
            NullLogger<ScheduleExecutionService>.Instance);
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var id = tracker.RegisterOperation(
            OperationType.LogRotation,
            "Log Rotation",
            new CancellationTokenSource(),
            notice: new RunNotice(NotificationMode.All, RunTrigger.Scheduled));
        tracker.SetWarning(id, new RunWarning(
            "signalr.scheduledPrefill.failedApps",
            new Dictionary<string, object?> { ["failed"] = 1, ["total"] = 3 }));
        tracker.CompleteOperation(id, success: true, error: null, cancelled: false, skipped: false);

        Assert.True(await service.InsertAsync(service.Capture(tracker.GetOperation(id)!, "scheduledPrefill")));

        var item = Assert.Single((await service.GetPageAsync(1, 10)).Items);
        Assert.Equal("signalr.scheduledPrefill.failedApps", item.Warning!.StageKey);
        Assert.Equal(1, ((JsonElement)item.Warning.Context["failed"]!).GetInt32());
        Assert.Equal(3, ((JsonElement)item.Warning.Context["total"]!).GetInt32());
    }

    [Fact]
    public void AnEvictionScanWhoseDetectionFailedKeepsItsWarningOnItsHistoryRowAsync()
    {
        var service = ScheduleExecutionTestService.Create();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var id = tracker.RegisterOperation(
            OperationType.EvictionScan,
            "Eviction Scan",
            new CancellationTokenSource(),
            metadata: new Dictionary<string, object?>
            {
                ["context"] = new Dictionary<string, object?> { ["detectionError"] = "Detection failed" }
            },
            notice: new RunNotice(NotificationMode.All, RunTrigger.Scheduled));
        tracker.CompleteOperation(id, success: true, error: null, cancelled: false, skipped: false);

        var execution = service.Capture(tracker.GetOperation(id)!, "evictionScan");

        Assert.Equal("signalr.gameDetect.error.fatal", execution.Warning!.StageKey);
        Assert.Equal("Detection failed", execution.Warning.Context["errorDetail"]);
    }

    [Fact]
    public async Task AWarningLongerThanItsColumnIsCutToFitAndTheRowIsKeptAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new ScheduleExecutionService(
            database.Factory,
            NullLogger<ScheduleExecutionService>.Instance);
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var id = tracker.RegisterOperation(
            OperationType.LogRotation,
            "Log Rotation",
            new CancellationTokenSource(),
            notice: new RunNotice(NotificationMode.All, RunTrigger.Scheduled));
        tracker.SetWarning(id, new RunWarning(
            "signalr.logRotation.logReopenPartlyFailed",
            new Dictionary<string, object?> { ["errors"] = new string('x', 5000) }));
        tracker.CompleteOperation(id, success: true, error: null, cancelled: false, skipped: false);

        var execution = service.Capture(tracker.GetOperation(id)!, "logRotation");

        Assert.True(JsonSerializer.Serialize(execution.Warning).Length <= 4096);
        Assert.True(await service.InsertAsync(execution));
        var item = Assert.Single((await service.GetPageAsync(1, 10)).Items);
        Assert.Equal("signalr.logRotation.logReopenPartlyFailed", item.Warning!.StageKey);
    }

    [Fact]
    public async Task InsertRejectsOneOperationTwiceAndPagesTiedTimesByNewestId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new ScheduleExecutionService(
            database.Factory,
            NullLogger<ScheduleExecutionService>.Instance);
        var startedAt = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var first = Execution(Guid.NewGuid(), startedAt);
        var second = Execution(Guid.NewGuid(), startedAt);

        Assert.True(await service.InsertAsync(first));
        Assert.False(await service.InsertAsync(Execution(first.OperationId, startedAt)));
        Assert.True(await service.InsertAsync(second));

        var page = await service.GetPageAsync(1, 1);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(second.OperationId, Assert.Single(page.Items).OperationId);

        await using var freshContext = new AppDbContext(database.Options);
        Assert.Equal(2, await freshContext.ScheduleExecutions.CountAsync());
        Assert.Equal(
            new[] { second.OperationId, first.OperationId },
            await freshContext.ScheduleExecutions
                .OrderByDescending(item => item.StartedAt)
                .ThenByDescending(item => item.Id)
                .Select(item => item.OperationId)
                .ToArrayAsync());
    }

    [Fact]
    public async Task GetPageFiltersSearchesAndClampsPostgreSqlResults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new ScheduleExecutionService(
            database.Factory,
            NullLogger<ScheduleExecutionService>.Instance);
        var startedAt = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var statuses = new[]
        {
            OperationStatus.Completed,
            OperationStatus.Failed,
            OperationStatus.Cancelled,
            OperationStatus.Skipped
        };
        var rows = Enumerable.Range(0, 123)
            .Select(index => new ScheduleExecution
            {
                OperationId = Guid.NewGuid(),
                ServiceKey = (index % 3) switch
                {
                    0 => "logRotation",
                    1 => "cacheSizeScan",
                    _ => "scheduledPrefill"
                },
                Status = statuses[index % statuses.Length],
                Trigger = RunTrigger.Scheduled,
                ActorKind = index % 2 == 0 ? ScheduleActorKind.Server : ScheduleActorKind.Unknown,
                Username = index % 5 == 0 ? null : $"owner-{index}",
                StartedAt = startedAt,
                CompletedAt = startedAt.AddSeconds(5),
                Detail = statuses[index % statuses.Length] == OperationStatus.Completed
                    ? null
                    : $"terminal detail {index}",
                ScheduleName = index % 7 == 0 ? null : $"Schedule {index}",
                Platform = index % 3 == 2 ? PrefillPlatform.Steam : null,
                WorkerStarted = true
            })
            .ToList();
        rows[5].Username = "literal%_owner";
        rows[6].ScheduleName = "Quarterly MiXeD Cache";
        rows[7].ScheduleName = "not-a-guid result";
        rows[8].ServiceKey = "scheduledPrefill";
        rows[8].Status = OperationStatus.Failed;
        rows[8].ScheduleName = "Combined Needle";

        await using (var context = new AppDbContext(database.Options))
        {
            context.ScheduleExecutions.AddRange(rows);
            await context.SaveChangesAsync();
        }

        var page20 = await service.GetPageAsync(1, 20);
        var page50 = await service.GetPageAsync(1, 50);
        var page100 = await service.GetPageAsync(1, 100);
        Assert.Equal((123, 7, 20), (page20.TotalCount, page20.TotalPages, page20.Items.Count));
        Assert.Equal((123, 3, 50), (page50.TotalCount, page50.TotalPages, page50.Items.Count));
        Assert.Equal((123, 2, 100), (page100.TotalCount, page100.TotalPages, page100.Items.Count));

        var secondPage = await service.GetPageAsync(2, 50);
        var thirdPage = await service.GetPageAsync(3, 50);
        var orderedIds = rows
            .OrderByDescending(row => row.StartedAt)
            .ThenByDescending(row => row.Id)
            .Select(row => row.OperationId)
            .ToArray();
        Assert.Equal(orderedIds[..50], page50.Items.Select(row => row.OperationId));
        Assert.Equal(orderedIds[50..100], secondPage.Items.Select(row => row.OperationId));
        Assert.Equal(orderedIds[100..], thirdPage.Items.Select(row => row.OperationId));
        Assert.Empty(page50.Items.Select(row => row.OperationId)
            .Intersect(secondPage.Items.Select(row => row.OperationId)));

        var selectedService = await service.GetPageAsync(1, 100, serviceKey: " scheduledPrefill ");
        Assert.Equal(rows.Count(row => row.ServiceKey == "scheduledPrefill"), selectedService.TotalCount);
        Assert.All(selectedService.Items, row => Assert.Equal("scheduledPrefill", row.ServiceKey));
        Assert.Equal(0, (await service.GetPageAsync(
            1,
            100,
            serviceKey: "ScheduledPrefill")).TotalCount);

        foreach (var status in statuses)
        {
            var selectedStatus = await service.GetPageAsync(1, 100, status: status);
            Assert.Equal(rows.Count(row => row.Status == status), selectedStatus.TotalCount);
            Assert.All(selectedStatus.Items, row => Assert.Equal(status, row.Status));
        }

        var mixedCase = await service.GetPageAsync(1, 100, search: "mIxEd CaChE");
        Assert.Equal(rows[6].OperationId, Assert.Single(mixedCase.Items).OperationId);
        var literal = await service.GetPageAsync(1, 100, search: "%_");
        Assert.Equal(rows[5].OperationId, Assert.Single(literal.Items).OperationId);
        var serviceText = await service.GetPageAsync(1, 100, search: "LOGROTATION");
        Assert.Equal(rows.Count(row => row.ServiceKey == "logRotation"), serviceText.TotalCount);
        var operationId = await service.GetPageAsync(
            1,
            100,
            search: rows[9].OperationId.ToString().ToUpperInvariant());
        Assert.Equal(rows[9].OperationId, Assert.Single(operationId.Items).OperationId);
        var ordinaryText = await service.GetPageAsync(1, 100, search: "NOT-A-GUID");
        Assert.Equal(rows[7].OperationId, Assert.Single(ordinaryText.Items).OperationId);

        var combined = await service.GetPageAsync(
            int.MaxValue,
            20,
            serviceKey: " scheduledPrefill ",
            status: OperationStatus.Failed,
            search: " combined needle ");
        Assert.Equal(1, combined.Page);
        Assert.Equal(1, combined.TotalPages);
        Assert.Equal(rows[8].OperationId, Assert.Single(combined.Items).OperationId);

        var empty = await service.GetPageAsync(1, 20, serviceKey: "", search: "");
        var whitespace = await service.GetPageAsync(1, 20, serviceKey: " 	 ", search: " 	 ");
        Assert.Equal(123, empty.TotalCount);
        Assert.Equal(123, whitespace.TotalCount);

        var beyondLast = await service.GetPageAsync(int.MaxValue, 20);
        Assert.Equal(7, beyondLast.Page);
        Assert.Equal(3, beyondLast.Items.Count);
        var noMatches = await service.GetPageAsync(
            int.MaxValue,
            20,
            serviceKey: "missing-service");
        Assert.Equal(1, noMatches.Page);
        Assert.Equal(0, noMatches.TotalPages);
        Assert.Equal(0, noMatches.TotalCount);
        Assert.Empty(noMatches.Items);
    }

    [Fact]
    public async Task HistoryEndpointUsesMvcJsonAndCanonicalErrors()
    {
        using var host = new EndpointAuthorizationHost();
        using var anonymous = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var denied = await anonymous.GetAsync("/api/system/schedules/history");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var client = await host.CreateAdminClientAsync();
        var contexts = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var context = await contexts.CreateDbContextAsync())
        {
            await context.ScheduleExecutions.ExecuteDeleteAsync();
            context.ScheduleExecutions.AddRange(
                new ScheduleExecution
                {
                    OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ServiceKey = "logRotation",
                    Status = OperationStatus.Completed,
                    Trigger = RunTrigger.Scheduled,
                    ActorKind = ScheduleActorKind.Server,
                    StartedAt = new DateTime(2026, 9, 27, 13, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 9, 27, 13, 0, 5, DateTimeKind.Utc),
                    WorkerStarted = true
                },
                new ScheduleExecution
                {
                    OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    ServiceKey = "cacheSizeScan",
                    Status = OperationStatus.Skipped,
                    Trigger = RunTrigger.Startup,
                    ActorKind = ScheduleActorKind.Server,
                    StartedAt = new DateTime(2026, 9, 27, 13, 1, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 9, 27, 13, 1, 1, DateTimeKind.Utc),
                    WorkerStarted = false
                },
                new ScheduleExecution
                {
                    OperationId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    ServiceKey = "databaseBackup",
                    Status = OperationStatus.Failed,
                    ActorKind = ScheduleActorKind.Unknown,
                    StartedAt = new DateTime(2026, 9, 27, 13, 2, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 9, 27, 13, 2, 8, DateTimeKind.Utc),
                    Detail = "The database did not answer before the backup deadline.",
                    WorkerStarted = true
                },
                new ScheduleExecution
                {
                    OperationId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    ServiceKey = "scheduledPrefill",
                    Status = OperationStatus.Cancelled,
                    Trigger = RunTrigger.Manual,
                    ActorKind = ScheduleActorKind.Account,
                    AccountId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    Username = "history-owner",
                    StartedAt = new DateTime(2026, 9, 27, 13, 3, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 9, 27, 13, 3, 9, DateTimeKind.Utc),
                    Detail = "Cancelled after the active download stopped.",
                    ScheduleId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    ScheduleName = "Nightly",
                    Platform = PrefillPlatform.Steam,
                    WorkerStarted = true
                });
            await context.SaveChangesAsync();
        }

        try
        {
            using var response = await client.GetAsync(
                "/api/system/schedules/history?page=1&pageSize=100");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("page").GetInt32());
            Assert.Equal(100, root.GetProperty("pageSize").GetInt32());
            Assert.Equal(4, root.GetProperty("totalCount").GetInt32());
            Assert.Equal(1, root.GetProperty("totalPages").GetInt32());
            var items = root.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(4, items.Length);

            var prefill = items.Single(item =>
                item.GetProperty("serviceKey").GetString() == "scheduledPrefill");
            Assert.Equal("cancelled", prefill.GetProperty("status").GetString());
            Assert.Equal("manual", prefill.GetProperty("trigger").GetString());
            Assert.Equal("account", prefill.GetProperty("actorKind").GetString());
            Assert.Equal("history-owner", prefill.GetProperty("username").GetString());
            Assert.Equal("Nightly", prefill.GetProperty("scheduleName").GetString());
            Assert.Equal("Steam", prefill.GetProperty("platform").GetString());
            Assert.Equal(
                "Cancelled after the active download stopped.",
                prefill.GetProperty("detail").GetString());

            var ordinary = items.Single(item =>
                item.GetProperty("serviceKey").GetString() == "logRotation");
            Assert.Equal("completed", ordinary.GetProperty("status").GetString());
            Assert.Equal("scheduled", ordinary.GetProperty("trigger").GetString());
            Assert.Equal("server", ordinary.GetProperty("actorKind").GetString());
            Assert.False(ordinary.TryGetProperty("username", out _));
            Assert.False(ordinary.TryGetProperty("detail", out _));
            Assert.False(ordinary.TryGetProperty("scheduleName", out _));
            Assert.False(ordinary.TryGetProperty("platform", out _));

            var unknown = items.Single(item =>
                item.GetProperty("serviceKey").GetString() == "databaseBackup");
            Assert.Equal("failed", unknown.GetProperty("status").GetString());
            Assert.Equal("unknown", unknown.GetProperty("actorKind").GetString());
            Assert.False(unknown.TryGetProperty("trigger", out _));
            Assert.False(unknown.TryGetProperty("username", out _));
            Assert.Equal(
                "The database did not answer before the backup deadline.",
                unknown.GetProperty("detail").GetString());

            using var filtered = await client.GetAsync(
                "/api/system/schedules/history?serviceKey=scheduledPrefill&status=cancelled&search=HISTORY-OWNER");
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            using var filteredDocument = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
            Assert.Equal(
                "44444444-4444-4444-4444-444444444444",
                filteredDocument.RootElement
                    .GetProperty("items")[0]
                    .GetProperty("operationId")
                    .GetString());

            var invalidRequests = new Dictionary<string, string>
            {
                ["/api/system/schedules/history?page=0"] = "Page must be at least 1.",
                ["/api/system/schedules/history?pageSize=101"] =
                    "Page size must be between 1 and 100.",
                ["/api/system/schedules/history?status=running"] =
                    "Status must be completed, failed, cancelled, or skipped."
            };
            foreach (var (path, message) in invalidRequests)
            {
                using var invalid = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                using var invalidDocument = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
                Assert.Equal(message, invalidDocument.RootElement.GetProperty("error").GetString());
                Assert.Equal(400, invalidDocument.RootElement.GetProperty("statusCode").GetInt32());
            }

            using var invalidStatus = await client.GetAsync(
                "/api/system/schedules/history?status=not-a-status");
            Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
            using var bindingDocument = JsonDocument.Parse(
                await invalidStatus.Content.ReadAsStringAsync());
            Assert.True(bindingDocument.RootElement
                .GetProperty("errors")
                .TryGetProperty("status", out _));
        }
        finally
        {
            await using var context = await contexts.CreateDbContextAsync();
            await context.ScheduleExecutions.ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task InsertContainsWriteFailureWhileActorLookupPropagatesIt()
    {
        var service = new ScheduleExecutionService(
            new FailedContextSource(),
            NullLogger<ScheduleExecutionService>.Instance);

        Assert.False(await service.InsertAsync(Execution(Guid.NewGuid(), DateTime.UtcNow)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResolveActorAsync(Guid.NewGuid()));
    }

    [Fact]
    public void ScheduleOutcomePredicateIncludesOnlyOwnedTerminalShapes()
    {
        var predicate = typeof(ServiceScheduleRegistry)
            .GetMethod("IsScheduleOutcome", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parentId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var ordinary = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogRotation,
            Name = "Log Rotation"
        };
        var childPhase = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogRotation,
            Name = "Log Rotation child phase",
            ParentOperationId = parentId
        };
        var prefillContainer = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.ScheduledPrefill,
            Name = "Scheduled Prefill",
            Metadata = new ScheduledPrefillOperationMetadata()
        };
        var prefillSchedule = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.ScheduledPrefill,
            Name = "Scheduled Prefill - Steam - Nightly",
            ParentOperationId = parentId,
            Metadata = new ScheduledPrefillServiceRunState(
                PrefillPlatform.Steam,
                scheduleId,
                "Nightly",
                new RunNotice(NotificationMode.All, RunTrigger.Scheduled))
        };
        var integrationLogin = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.XboxMapping,
            Name = "Xbox Mapping Sign-in",
            Metadata = new Dictionary<string, object?>
            {
                ["integrationLogin"] = new IntegrationLogin(
                    Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
                    DateTime.UtcNow.AddMinutes(5), Shared: false, Recover: false)
            }
        };

        Assert.True((bool)predicate.Invoke(null, [ordinary, null])!);
        Assert.False((bool)predicate.Invoke(null, [childPhase, null])!);
        Assert.False((bool)predicate.Invoke(null, [prefillContainer, null])!);
        Assert.True((bool)predicate.Invoke(null, [prefillSchedule, scheduleId])!);
        Assert.False((bool)predicate.Invoke(null, [integrationLogin, null])!);
    }

    [Fact]
    public async Task TerminalWriteBroadcastsAgainOnlyAfterTheInsertSucceeds()
    {
        var executions = new DelayedScheduleExecutionService();
        var notifications = (ScheduleNotifications)(object)DispatchProxy
            .Create<ISignalRNotificationService, ScheduleNotifications>();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var state = DispatchProxy.Create<IStateService, ScheduleStateProxy>();
        _ = new ServiceScheduleRegistry(
            [], state, (ISignalRNotificationService)(object)notifications, executions, tracker);
        var actor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "saved-name");
        var operationId = tracker.RegisterOperation(
            OperationType.LogRotation,
            "Log Rotation",
            new CancellationTokenSource(),
            notice: new RunNotice(NotificationMode.All, RunTrigger.Manual, actor));

        tracker.CompleteOperation(operationId, success: true);

        var execution = await executions.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await notifications.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, notifications.Count);
        Assert.Same(actor, tracker.GetOperation(operationId)!.Notice?.Actor);
        Assert.Equal(actor.AccountId, execution.AccountId);
        executions.Release.TrySetResult(true);
        await notifications.Second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, notifications.Count);
    }

    private static OperationInfo Complete(OperationStatus status, RunNotice notice)
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var id = tracker.RegisterOperation(
            OperationType.LogRotation,
            "Log Rotation",
            new CancellationTokenSource(),
            notice: notice);
        tracker.CompleteOperation(
            id,
            success: status is OperationStatus.Completed or OperationStatus.Skipped,
            error: status == OperationStatus.Completed ? null : "terminal detail",
            cancelled: status == OperationStatus.Cancelled,
            skipped: status == OperationStatus.Skipped);
        return tracker.GetOperation(id)!;
    }

    private static ScheduleExecution Execution(Guid operationId, DateTime startedAt) => new()
    {
        OperationId = operationId,
        ServiceKey = "logRotation",
        Status = OperationStatus.Completed,
        Trigger = RunTrigger.Scheduled,
        ActorKind = ScheduleActorKind.Server,
        StartedAt = startedAt,
        CompletedAt = startedAt.AddSeconds(5),
        WorkerStarted = true
    };

    private sealed class FailedContextSource : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            throw new InvalidOperationException("Database unavailable");
    }

    private sealed class DelayedScheduleExecutionService : ScheduleExecutionService
    {
        public DelayedScheduleExecutionService()
            : base(null!, NullLogger<ScheduleExecutionService>.Instance)
        {
        }

        public TaskCompletionSource<ScheduleExecution> Captured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<bool> InsertAsync(
            ScheduleExecution execution,
            CancellationToken cancellationToken = default)
        {
            Captured.TrySetResult(execution);
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private class ScheduleNotifications : DispatchProxy
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public TaskCompletionSource First { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is { Length: > 0 }
                && args[0] is string eventName
                && eventName == SignalREvents.SchedulesUpdated)
            {
                var count = Interlocked.Increment(ref _count);
                if (count == 1) First.TrySetResult();
                if (count == 2) Second.TrySetResult();
            }

            return targetMethod?.ReturnType == typeof(Task)
                ? Task.CompletedTask
                : targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
        }
    }

    private class ScheduleStateProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}

internal static class ScheduleExecutionTestService
{
    public static ScheduleExecutionService Create(
        ScheduleActor? actor = null,
        ScheduleExecutionResponse? response = null) => new QuietScheduleExecutionService(actor, response);

    private sealed class QuietScheduleExecutionService : ScheduleExecutionService
    {
        private readonly ScheduleActor _actor;
        private readonly ScheduleExecutionResponse? _response;

        public QuietScheduleExecutionService(
            ScheduleActor? actor,
            ScheduleExecutionResponse? response)
            : base(null!, NullLogger<ScheduleExecutionService>.Instance)
        {
            _actor = actor ?? new ScheduleActor(ScheduleActorKind.Unknown, null, null);
            _response = response;
        }

        public override Task<ScheduleActor> ResolveActorAsync(
            Guid? accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_actor);

        public override Task<bool> InsertAsync(
            ScheduleExecution execution,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public override Task<ScheduleExecutionResponse> GetPageAsync(
            int page,
            int pageSize,
            string? serviceKey = null,
            OperationStatus? status = null,
            string? search = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_response ?? new ScheduleExecutionResponse
            {
                Page = page,
                PageSize = pageSize
            });
    }
}
