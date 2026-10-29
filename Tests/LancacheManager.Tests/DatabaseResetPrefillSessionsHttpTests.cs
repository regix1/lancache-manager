using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A reset answers its request at once and keeps working after the request's services are gone, so
/// these tests start it the way the page does, through the endpoint, and wait for the operation.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class DatabaseResetPrefillSessionsHttpTests
{
    [Fact]
    public async Task PrefillSessionsResetThroughTheEndpointForgetsOnlyTheDeletedRunsAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var admin = await host.CreateAdminClientAsync();
        var contexts = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var now = DateTime.UtcNow;
        var endedSessionId = "reset-ended-" + Guid.NewGuid().ToString("N")[..8];
        var liveSessionId = "reset-live-" + Guid.NewGuid().ToString("N")[..8];
        var endedRunId = Guid.NewGuid();
        var finishedRunId = Guid.NewGuid();
        var runningRunId = Guid.NewGuid();
        await using (var seed = await contexts.CreateDbContextAsync())
        {
            seed.PrefillSessions.AddRange(
                DatabaseResetPrefillSessionsTests.NewSession(endedSessionId, PrefillSessionStatus.Terminated, now),
                DatabaseResetPrefillSessionsTests.NewSession(liveSessionId, PrefillSessionStatus.Active, now));
            seed.PrefillRuns.AddRange(
                DatabaseResetPrefillSessionsTests.NewRun(endedRunId, endedSessionId, now, completedAtUtc: now.AddMinutes(-20)),
                DatabaseResetPrefillSessionsTests.NewRun(finishedRunId, liveSessionId, now, completedAtUtc: now.AddMinutes(-5)),
                DatabaseResetPrefillSessionsTests.NewRun(runningRunId, liveSessionId, now, completedAtUtc: null));
            seed.PrefillHistoryEntries.AddRange(
                DatabaseResetPrefillSessionsTests.NewEntry("10", endedSessionId, endedRunId, PrefillHistoryEntryStatus.Completed, now),
                DatabaseResetPrefillSessionsTests.NewEntry("10", liveSessionId, finishedRunId, PrefillHistoryEntryStatus.Completed, now),
                DatabaseResetPrefillSessionsTests.NewEntry("10", liveSessionId, runningRunId, PrefillHistoryEntryStatus.InProgress, now));
            await seed.SaveChangesAsync();
        }

        var daemon = host.Application.Services.GetRequiredService<SteamDaemonService>();
        var live = new DaemonSession
        {
            Id = liveSessionId,
            UserId = Guid.NewGuid(),
            IsPersistent = true,
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
            Client = DispatchProxy.Create<IDaemonClient, RunClient>()
        };
        live.Runs[finishedRunId] = NewDaemonRun(finishedRunId, liveSessionId, terminalCompletedFlag: 2, now);
        live.Runs[runningRunId] = NewDaemonRun(runningRunId, liveSessionId, terminalCompletedFlag: 0, now);
        var sessions = (ConcurrentDictionary<string, DaemonSession>)typeof(PrefillDaemonServiceBase)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(daemon)!;
        sessions[liveSessionId] = live;

        try
        {
            var operation = await ResetAsync(host, admin, "PrefillSessions");

            Assert.Equal(OperationStatus.Completed, operation.Status);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.Equal(
                [runningRunId],
                await verify.PrefillRuns.AsNoTracking()
                    .Where(run => run.Id == endedRunId || run.Id == finishedRunId || run.Id == runningRunId)
                    .Select(run => run.Id)
                    .ToListAsync());
            Assert.Equal(
                [runningRunId],
                await verify.PrefillHistoryEntries.AsNoTracking()
                    .Where(entry => entry.SessionId == endedSessionId || entry.SessionId == liveSessionId)
                    .Select(entry => entry.RunId)
                    .ToListAsync());
            Assert.Equal([runningRunId], daemon.GetRuns(liveSessionId).Select(run => run.RunId));
        }
        finally
        {
            sessions.TryRemove(liveSessionId, out _);
        }
    }

    // Clearing log rows takes the log lock from a service the reset resolves after its request ended.
    [Fact]
    public async Task DownloadsAndLogEntriesResetThroughTheEndpointCompletesAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var admin = await host.CreateAdminClientAsync();

        var operation = await ResetAsync(host, admin, "Downloads", "LogEntries");

        Assert.Equal(OperationStatus.Completed, operation.Status);
    }

    // A full reset parked on the wait queue starts after the request that asked for it has ended.
    [Fact]
    public async Task FullResetStartedAfterItsRequestEndedEmptiesTheLinkedTablesAsync()
    {
        using var host = new EndpointAuthorizationHost();
        var contexts = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await SeedLinkedRowsAsync(contexts);
        DatabaseService service;
        using (var request = host.Application.Services.CreateScope())
        {
            service = request.ServiceProvider.GetRequiredService<DatabaseService>();
        }

        var operation = await WaitForOperationAsync(host, service.StartFullResetAsync());

        Assert.Equal(OperationStatus.Completed, operation.Status);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.False(await verify.Downloads.AnyAsync());
        Assert.False(await verify.LogEntries.AnyAsync());
        Assert.False(await verify.EventDownloads.AnyAsync());
        Assert.False(await verify.ClientGroupMembers.AnyAsync());
        Assert.False(await verify.PrefillHistoryEntries.AnyAsync());
        Assert.False(await verify.UserPreferences.AnyAsync());
        Assert.False(await verify.CachedCorruptionDetections.AnyAsync());
    }

    // Each table the Data section offers is reset alone beside rows that point at it, with the
    // database enforcing every foreign key.
    [Fact]
    public async Task EveryOfferedTableResetsAloneBesideLinkedRowsAsync()
    {
        using var host = new EndpointAuthorizationHost();
        var contexts = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var offered = DatabaseResetOfferedTablesTests.OfferedTableNames();
        Assert.NotEmpty(offered);

        foreach (var table in offered)
        {
            await SeedLinkedRowsAsync(contexts);
            // A sessions reset signs the admin out, so every reset gets a client of its own.
            using var admin = await host.CreateAdminClientAsync();

            var operation = await ResetAsync(host, admin, table);

            Assert.True(operation.Status == OperationStatus.Completed, $"{table} reset ended {operation.Status}");
        }
    }

    private static async Task<OperationInfo> ResetAsync(EndpointAuthorizationHost host, HttpClient admin, params string[] tables)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/database/tables")
        {
            Content = JsonContent.Create(new { tables })
        };
        using var response = await admin.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await WaitForOperationAsync(host, JsonDocument.Parse(body).RootElement.GetProperty("operationId").GetGuid());
    }

    // One row in every table a foreign key links, each pointing at its parent. The suite database
    // keeps rows between tests, so the unique client address comes from a fresh Guid.
    private static async Task SeedLinkedRowsAsync(IDbContextFactory<AppDbContext> contexts)
    {
        var now = DateTime.UtcNow;
        var address = Guid.NewGuid().ToString("N");
        var prefillSessionId = "reset-linked-" + Guid.NewGuid().ToString("N")[..8];
        var runId = Guid.NewGuid();
        var userSessionId = Guid.NewGuid();
        var corruptionScanId = Guid.NewGuid();
        await using var seed = await contexts.CreateDbContextAsync();
        var download = new Download
        {
            Service = "steam",
            ClientIp = "192.0.2.10",
            StartTimeUtc = now.AddMinutes(-2),
            EndTimeUtc = now,
            CacheHitBytes = 4096
        };
        var linkedEvent = new Event
        {
            Name = "LAN Party",
            StartTimeUtc = now.AddHours(-1),
            EndTimeUtc = now.AddHours(1),
            CreatedAtUtc = now,
            ColorIndex = 1
        };
        seed.Downloads.Add(download);
        seed.Events.Add(linkedEvent);
        seed.UserSessions.Add(new UserSession
        {
            Id = userSessionId,
            SessionTokenHash = Guid.NewGuid().ToString("N"),
            SessionType = SessionType.Admin,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
            LastSeenAtUtc = now
        });
        seed.CachedCorruptionScans.Add(new CachedCorruptionScan
        {
            ScanId = corruptionScanId,
            DetectionMode = CorruptionDetectionMode.RepeatedMiss,
            IsCurrent = false,
            Threshold = 3,
            LookbackDays = 30,
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Status = "completed",
            StartedAtUtc = now.AddSeconds(-1),
            CompletedAtUtc = now
        });
        seed.PrefillSessions.Add(
            DatabaseResetPrefillSessionsTests.NewSession(prefillSessionId, PrefillSessionStatus.Terminated, now));
        await seed.SaveChangesAsync();

        seed.LogEntries.Add(new LogEntryRecord
        {
            Timestamp = now,
            ClientIp = download.ClientIp,
            Service = download.Service,
            Method = "GET",
            Url = "/depot/10/chunk/" + Guid.NewGuid().ToString("N"),
            StatusCode = 200,
            BytesServed = 4096,
            CacheStatus = "HIT",
            Datasource = "default",
            DownloadId = download.Id,
            CreatedAt = now
        });
        seed.EventDownloads.Add(new EventDownload { EventId = linkedEvent.Id, DownloadId = download.Id, TaggedAtUtc = now });
        seed.UserPreferences.Add(new UserPreferences { SessionId = userSessionId, SelectedTheme = "dark", UpdatedAtUtc = now });
        seed.ClientGroups.Add(new ClientGroup
        {
            Nickname = "Group " + Guid.NewGuid().ToString("N")[..8],
            Members = { new ClientGroupMember { ClientIp = $"fd00::{address[..4]}:{address[4..8]}:{address[8..12]}" } }
        });
        seed.CachedCorruptionDetections.Add(new CachedCorruptionDetection
        {
            ScanId = corruptionScanId,
            ServiceName = "steam",
            CorruptedChunkCount = 1,
            CandidatesJson = "[]",
            LastDetectedUtc = now
        });
        seed.PrefillRuns.Add(
            DatabaseResetPrefillSessionsTests.NewRun(runId, prefillSessionId, now, completedAtUtc: now.AddMinutes(-5)));
        seed.PrefillHistoryEntries.Add(
            DatabaseResetPrefillSessionsTests.NewEntry("10", prefillSessionId, runId, PrefillHistoryEntryStatus.Completed, now));
        await seed.SaveChangesAsync();
    }

    private static async Task<OperationInfo> WaitForOperationAsync(EndpointAuthorizationHost host, Guid operationId)
    {
        var tracker = host.Application.Services.GetRequiredService<IUnifiedOperationTracker>();
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (tracker.GetOperation(operationId) is { Status: OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled } operation)
            {
                return operation;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Reset {operationId} did not end.");
    }

    private static DaemonRun NewDaemonRun(Guid id, string sessionId, int terminalCompletedFlag, DateTime now)
    {
        var instance = Guid.NewGuid().ToString();
        return new DaemonRun
        {
            PrefillRunId = id,
            SessionId = sessionId,
            DaemonInstanceId = instance,
            Options = new DaemonRunOptions { AppIds = ["10"] },
            Snapshot = new DaemonRunSnapshot
            {
                OperationId = id.ToString(),
                DaemonInstanceId = instance,
                StartedAt = now.AddMinutes(-30),
                UpdatedAt = now,
                State = terminalCompletedFlag == 2 ? "completed" : "downloading"
            },
            TerminalCompletedFlag = terminalCompletedFlag,
            CompletedAtUtc = terminalCompletedFlag == 2 ? now.AddMinutes(-5) : null
        };
    }
}
