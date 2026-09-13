using System.Collections.Concurrent;
using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class PrefillRunTests
{
    [Fact]
    public async Task RunIsolationAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        var third = await fixture.StartAsync("30");
        Assert.Equal(3, fixture.Daemon.GetRuns(fixture.Session.Id).Count);
        Assert.Equal("20", Assert.Single(second.Options.AppIds!));

        fixture.Client.Set(first, "downloading", 10, "downloading");
        fixture.Client.Set(second, "downloading", 20, "downloading");
        fixture.Client.Set(third, "downloading", 30, "downloading");
        await fixture.RefreshAsync();
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, second.PrefillRunId);

        Assert.Equal([second.PrefillRunId], fixture.Client.Cancelled);
        Assert.False(first.Completion.Task.IsCompleted);
        Assert.False(third.Completion.Task.IsCompleted);
        Assert.Equal("cancelled", (await second.Completion.Task).Snapshot.State);
        Assert.Equal(10, first.Snapshot.BytesTransferred);
        Assert.Equal(30, third.Snapshot.BytesTransferred);
        Assert.True(fixture.Session.IsPrefilling);
    }

    [Fact]
    public async Task RunAdmissionAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var schedule = Guid.NewGuid();
        await fixture.StartAsync("10", schedule);
        await Assert.ThrowsAsync<PrefillAlreadyRunningException>(() => fixture.StartAsync("20", schedule));
        await fixture.StartAsync("20");
        await fixture.StartAsync("30");
        await Assert.ThrowsAsync<PrefillAlreadyRunningException>(() => fixture.StartAsync("40"));
        Assert.Equal(3, fixture.Client.StartCount);
    }

    [Fact]
    public async Task AmbiguousCancelAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.StartAsync("10");
        await fixture.StartAsync("20");
        var error = await Assert.ThrowsAsync<DaemonCommandException>(() => fixture.Daemon.CancelPrefillAsync(fixture.Session.Id));
        Assert.Equal("ambiguous-operation", error.ErrorCode);
        Assert.Empty(fixture.Client.Cancelled);
    }

    [Fact]
    public async Task TerminalReplayAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        fixture.Client.Set(first, "completed", 100, "success");
        await Task.WhenAll(fixture.RefreshAsync(), fixture.RefreshAsync());
        await fixture.RefreshAsync();
        Assert.Equal("completed", (await first.Completion.Task).Snapshot.State);
        fixture.Client.Set(first, "failed", 1000, "failed");
        await fixture.RefreshAsync();
        Assert.Equal(100, first.Snapshot.BytesTransferred);
        Assert.False(second.Completion.Task.IsCompleted);
        await using var context = new AppDbContext(fixture.Options);
        var row = await context.PrefillHistoryEntries.SingleAsync(entry => entry.RunId == first.PrefillRunId);
        Assert.Equal(PrefillHistoryEntryStatus.Completed, row.Status);
        Assert.Equal(100, row.BytesDownloaded);
    }

    [Fact]
    public async Task StalledRunDrainAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        first.LastProgressTicksUtc = DateTime.UtcNow.AddMinutes(-10).Ticks;
        fixture.Client.CompleteCancellation = false;
        Assert.True(await fixture.Daemon.FailStalledSessionAsync(fixture.Session, first.PrefillRunId,
            DateTime.UtcNow, TimeSpan.FromMinutes(3), "Stalled"));
        Assert.False(first.Completion.Task.IsCompleted);
        Assert.False(second.CancelRequested);
        fixture.Client.Set(first, "cancelled", 5, "cancelled");
        await fixture.RefreshAsync();
        Assert.Equal("failed", (await first.Completion.Task).Snapshot.State);
        Assert.Equal("stalled", first.Snapshot.Reason);
        Assert.Equal("signalr.scheduledPrefill.failedStalled", first.ErrorStageKey);
    }

    [Fact]
    public async Task EditRunCancellationAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var first = await fixture.StartAsync("10");
        var sibling = await fixture.StartAsync("20");
        var edit = fixture.Daemon.PersistentEditSessionGate.BeginEditAction("edit-a", "download-a",
            PersistentPrefillEditActionKind.Prefill, fixture.Session.Id);
        edit.ConfirmEffect(PersistentPrefillEditResourceKind.Prefill, first.PrefillRunId);
        edit.Complete(PersistentPrefillEditActionOutcome.Succeeded);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        var controller = new PersistentPrefillController(services, null!, null!,
            NullLogger<PersistentPrefillController>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await controller.CleanupEditSessionAsync(new PersistentPrefillEditSessionCleanupRequest
        {
            EditSessionId = "edit-a",
            CleanupId = "cleanup-a",
            Services = [new PersistentPrefillEditSessionCleanupServiceRequest
                { Service = PrefillPlatform.Steam, BaselineSessionId = fixture.Session.Id, BaselineSelectedAppIds = [] }]
        }, timeout.Token);
        Assert.IsType<OkResult>(result);
        Assert.Equal([first.PrefillRunId], fixture.Client.Cancelled);
        Assert.False(sibling.CancelRequested);
        Assert.Same(fixture.Session, fixture.Daemon.GetActivePersistentSession());
        Assert.Equal(DaemonAuthState.Authenticated, fixture.Session.AuthState);
    }

    [Fact]
    public async Task WatchdogOwnershipAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        await run.PrefillWork.WaitAsync();
        run.LastProgressTicksUtc = DateTime.UtcNow.AddMinutes(-10).Ticks;
        var stalled = fixture.Daemon.FailStalledSessionAsync(fixture.Session, run.PrefillRunId,
            DateTime.UtcNow, TimeSpan.FromMinutes(3), "Stalled");
        Assert.False(stalled.IsCompleted);
        run.LastProgressTicksUtc = DateTime.UtcNow.Ticks;
        run.PrefillWork.Release();
        Assert.False(await stalled);
        Assert.Empty(fixture.Client.Cancelled);
        Assert.False(run.CancelRequested);
    }

    [Fact]
    public async Task AuthenticationFanoutAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        var third = await fixture.StartAsync("30");
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "OnStatusChangeAsync", fixture.Session,
            new DaemonStatus { Status = "awaiting-login" });
        Assert.Equal(3, fixture.Client.Cancelled.Count);
        foreach (var run in new[] { first, second, third })
        {
            Assert.Equal("failed", (await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Snapshot.State);
            Assert.Equal("auth-lost", run.Snapshot.Reason);
            Assert.True(run.LastProgress!.RequiresLogin);
        }
        Assert.False(fixture.Session.IsPrefilling);
    }

    [Theory]
    [InlineData("operationProgress")]
    [InlineData("inlineSelection")]
    [InlineData("targetedCancel")]
    [InlineData("concurrentPrefill")]
    [InlineData("activeOperations")]
    public void MissingRequiredFeatureKeepsExclusiveMode(string feature)
    {
        var status = RunClient.Capabilities(Guid.NewGuid().ToString());
        Assert.True(status.SupportsConcurrentPrefill);
        status.Features!.Remove(feature);
        Assert.False(status.SupportsConcurrentPrefill);
    }

    [Theory]
    [InlineData("legacy", false, 1)]
    [InlineData("partial", false, 1)]
    [InlineData("malformed-instance", false, 1)]
    [InlineData("missing-instance", false, 1)]
    [InlineData("missing-inventory", false, 1)]
    [InlineData("invalid-run-limit", false, 1)]
    [InlineData("invalid-request-limit", false, 1)]
    [InlineData("valid-limit-one", true, 1)]
    [InlineData("valid", true, 3)]
    public async Task SessionCapabilitiesAsync(string scenario, bool concurrent, int expectedLimit)
    {
        foreach (var persistent in new[] { false, true })
        {
            await using var fixture = await RunFixture.CreateAsync(persistent: persistent);
            var status = RunClient.Capabilities(fixture.Client.InstanceId);
            switch (scenario)
            {
                case "legacy": status = new DaemonStatus(); break;
                case "partial": status.Features!.Remove("targetedCancel"); break;
                case "malformed-instance": status.DaemonInstanceId = "not-a-guid"; break;
                case "missing-instance": status.DaemonInstanceId = null; break;
                case "missing-inventory": status.ActiveOperations = null; break;
                case "invalid-run-limit": status.MaxConcurrentRuns = 17; break;
                case "invalid-request-limit": status.MaxConcurrentRequests = 0; break;
                case "valid-limit-one": status.MaxConcurrentRuns = 1; break;
            }
            fixture.Session.Capabilities = status;
            fixture.Client.Status = status;
            Assert.Equal(concurrent, status.SupportsConcurrentPrefill);

            IReadOnlyList<string> features;
            int limit;
            if (persistent)
            {
                using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
                var controller = new PersistentPrefillController(services, null!, null!,
                    NullLogger<PersistentPrefillController>.Instance);
                var result = await controller.ListAsync(CancellationToken.None);
                var response = Assert.Single(Assert.IsType<List<PersistentPrefillSessionDto>>(
                    Assert.IsType<OkObjectResult>(result.Result).Value));
                features = response.Features;
                limit = response.MaxConcurrentRuns;
            }
            else
            {
                var response = DaemonSessionDto.FromSession(fixture.Session);
                features = response.Features;
                limit = response.MaxConcurrentRuns;
            }
            Assert.Equal(expectedLimit, limit);
            if (concurrent)
            {
                Assert.Equal(status.Features, features);
                Assert.NotSame(status.Features, features);
            }
            else
                Assert.Empty(features);
        }
    }
}

internal sealed class RunFixture : IAsyncDisposable
{
    private readonly TestDatabase? _database;
    public DbContextOptions<AppDbContext> Options { get; }
    public SteamDaemonService Daemon { get; }
    public DaemonSession Session { get; }
    public RunClient Client { get; }
    public PrefillSessionService History { get; }

    private RunFixture(TestDatabase? database = null, bool persistent = false)
    {
        _database = database;
        Options = database?.Options ?? new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_runs_{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var created = PrefillCacheChangeTests.NewDaemon(Options);
        Daemon = created.Daemon;
        Session = persistent ? new DaemonSession
        {
            Id = created.Session.Id,
            UserId = created.Session.UserId,
            IsPersistent = true,
            Status = DaemonSessionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        } : created.Session;
        created.Daemon.InjectSession(Session);
        Session.IsPrefilling = false;
        Session.PrefillRunId = null;
        Session.AuthState = DaemonAuthState.Authenticated;
        var client = DispatchProxy.Create<IDaemonClient, RunClient>();
        Client = (RunClient)(object)client;
        Session.Client = client;
        Session.Capabilities = RunClient.Capabilities(Client.InstanceId);
        History = new PrefillSessionService(new TestDbContextFactory(Options), NullLogger<PrefillSessionService>.Instance);
    }

    public static async Task<RunFixture> CreateAsync(bool relational = false, bool persistent = false)
    {
        var fixture = new RunFixture(relational ? await TestDatabase.CreateAsync() : null, persistent);
        await using var context = new AppDbContext(fixture.Options);
        await context.Database.EnsureCreatedAsync();
        context.PrefillSessions.Add(new PrefillSession
        {
            SessionId = fixture.Session.Id,
            CreatedBySessionId = fixture.Session.UserId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
        return fixture;
    }

    public async Task<DaemonRun> StartAsync(string appId, Guid? schedule = null)
    {
        var response = await Daemon.PrefillAsync(Session.Id, appIds: [appId], scheduleId: schedule,
            scheduleName: schedule.HasValue ? "Named schedule" : null, notificationMode: "silent");
        await RefreshAsync();
        return Assert.IsType<DaemonRun>(Daemon.GetRun(Session.Id, response.RunId!.Value));
    }

    public async Task RefreshAsync()
    {
        await Session.RecoveryWork.WaitAsync();
        Session.RecoveryWork.Release();
        await Daemon.RefreshRunsAsync(Session.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await Session.CancellationTokenSource.CancelAsync();
        await Session.RecoveryWork.WaitAsync();
        Session.RecoveryWork.Release();
        if (_database is not null) await _database.DisposeAsync();
    }
}

internal class RunClient : NullReturningProxy
{
    public DaemonStatus? Status { get; set; }
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public ConcurrentDictionary<Guid, DaemonOperationPage> Pages { get; } = new();
    public List<Guid> Cancelled { get; } = [];
    public int StartCount { get; private set; }
    public bool LoseAcknowledgement { get; set; }
    public bool OmitInventory { get; set; }
    public bool CompleteCancellation { get; set; } = true;
    public bool Offline { get; set; }
    public bool MissingPages { get; set; }
    public Func<Guid, int, DaemonOperationPage>? ReadPage { get; set; }

    public static DaemonStatus Capabilities(string instance) => new()
    {
        Status = "logged-in",
        ProtocolVersion = 2,
        DaemonInstanceId = instance,
        MaxConcurrentRuns = 3,
        MaxConcurrentRequests = 8,
        Features = ["operationProgress", "inlineSelection", "targetedCancel", "concurrentPrefill", "activeOperations"],
        ActiveOperations = [],
        RecentOperations = []
    };

    public void Set(DaemonRun run, string state, long bytes, string itemResult)
    {
        var current = Pages[run.PrefillRunId];
        var sequence = current.Operation.Sequence + 1;
        var item = new DaemonRunItem
        {
            AppId = run.Options.AppIds![0],
            State = state,
            Result = itemResult,
            BytesTransferred = bytes,
            TotalBytes = 100,
            Sequence = sequence
        };
        Pages[run.PrefillRunId] = current with
        {
            Operation = current.Operation with
            {
                State = state,
                Sequence = sequence,
                UpdatedAt = DateTimeOffset.UtcNow,
                CurrentItem = item,
                BytesTransferred = bytes,
                CompletedApps = itemResult == "success" ? 1 : 0,
                FailedApps = itemResult == "failed" ? 1 : 0,
                CancelledApps = itemResult == "cancelled" ? 1 : 0
            },
            Items = [item]
        };
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDaemonClient.GetStatusAsync))
        {
            if (Status is not null) return Task.FromResult<DaemonStatus?>(Status);
            if (Offline) return Task.FromResult<DaemonStatus?>(null);
            var status = Capabilities(InstanceId);
            if (!OmitInventory)
            {
                status.ActiveOperations = Pages.Values.Where(page => page.Operation.State is not ("completed" or "failed" or "cancelled"))
                    .Select(page => page.Operation).ToList();
                status.RecentOperations = Pages.Values.Where(page => page.Operation.State is "completed" or "failed" or "cancelled")
                    .Select(page => page.Operation).ToList();
            }
            return Task.FromResult<DaemonStatus?>(status);
        }
        if (targetMethod?.Name == nameof(IDaemonClient.PrefillAsync) && args?[0] is Guid id)
        {
            StartCount++;
            var options = (DaemonRunOptions)args[2]!;
            var snapshot = new DaemonRunSnapshot
            {
                OperationId = id.ToString(),
                DaemonInstanceId = InstanceId,
                Sequence = 1,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = "downloading",
                SelectionResolved = true,
                TotalApps = options.AppIds!.Count
            };
            Pages[id] = new DaemonOperationPage(snapshot, options, true, options.AppIds.Count,
                options.AppIds.Select(app => new DaemonRunItem { AppId = app, Sequence = 1 }).ToArray(), null);
            return LoseAcknowledgement ? Task.FromException<PrefillResult>(new IOException("Acknowledgement lost"))
                : Task.FromResult(new PrefillResult { Success = true, RunId = id, DaemonInstanceId = InstanceId, State = "started" });
        }
        if (targetMethod?.Name == nameof(IDaemonClient.GetOperationAsync))
        {
            if (Offline) return Task.FromException<DaemonOperationPage>(new IOException("Disconnected"));
            if (MissingPages) return Task.FromException<DaemonOperationPage>(new DaemonCommandException("operation-not-found"));
            if (ReadPage is not null) return Task.FromResult(ReadPage((Guid)args![0]!, (int)args[2]!));
            return Pages.TryGetValue((Guid)args![0]!, out var page) ? Task.FromResult(page)
                : Task.FromException<DaemonOperationPage>(new DaemonCommandException("operation-not-found"));
        }
        if (targetMethod?.Name == nameof(IDaemonClient.CancelPrefillAsync) && args?[0] is Guid cancelled)
        {
            Cancelled.Add(cancelled);
            var page = Pages[cancelled];
            if (CompleteCancellation)
            {
                page = page with
                {
                    Operation = page.Operation with
                    {
                        State = "cancelled",
                        Sequence = page.Operation.Sequence + 1,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CancelledApps = page.TotalItems
                    },
                    Items = page.Items.Select(item => item with { State = "cancelled", Result = "cancelled", Sequence = item.Sequence + 1 }).ToArray()
                };
                Pages[cancelled] = page;
            }
            return Task.FromResult(page.Operation);
        }
        return base.Invoke(targetMethod, args);
    }
}
