using System.Collections.Concurrent;
using System.Net;
using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace LancacheManager.Tests;

public sealed class DepotImportTests
{
    [Theory]
    [InlineData(RunTrigger.Manual)]
    [InlineData(RunTrigger.Scheduled)]
    public async Task SnapshotReplacesPopulatedCatalogAndCommitsBeforeSchedulePublication(RunTrigger trigger)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.Downloads.Add(new Download
            {
                DepotId = 10,
                GameAppId = 1,
                GameName = "Old owner",
                GameImageUrl = "https://example.com/old.jpg"
            });
            await initial.SaveChangesAsync();
        }

        Assert.True(await fixture.Service.ImportFromGitHubAsync(trigger: trigger));

        await using var db = fixture.Database.Factory.CreateDbContext();
        var rows = await db.SteamDepotMappings.OrderBy(row => row.DepotId).ThenBy(row => row.AppId).ToListAsync();
        Assert.Equal(new long[] { 2, 3, 9 }, rows.Select(row => row.AppId));
        Assert.DoesNotContain(rows, row => row.DepotId == 20);
        Assert.Equal("New owner", rows.Single(row => row.IsOwner && row.DepotId == 10).AppName);
        Assert.Equal("orphan-resolved", rows.Single(row => row.DepotId == 90).Source);
        var download = await db.Downloads.SingleAsync();
        Assert.Equal(2, download.GameAppId);
        Assert.Equal("New owner", download.GameName);
        Assert.Equal(2u, Get<ConcurrentDictionary<uint, uint>>(fixture.Service, "_depotOwners")[10]);
        Assert.False(Get<ConcurrentDictionary<uint, HashSet<uint>>>(fixture.Service, "_depotToAppMappings").ContainsKey(20));
        Assert.False(Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_appNames").ContainsKey(1));
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.True(fixture.State.GetState().HasDataLoaded);
        Assert.True(fixture.State.GetLastPicsCrawl() > fixture.PreviousCrawl);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastFullPicsCrawl());
        Assert.Null(fixture.State.GetState().LastViabilityCheck);
        Assert.Null(fixture.Service.PendingFullScan);
        Assert.Contains(fixture.Events.Snapshots, item => item.Event == "SchedulesUpdated"
            && item.Content.ToString().Contains("\"PendingFullScan\":null", StringComparison.Ordinal));
        var terminal = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        Assert.True(terminal.Content.GetProperty("Success").GetBoolean());
        Assert.False(terminal.Content.GetProperty("Cancelled").GetBoolean());
        Assert.Equal(trigger == RunTrigger.Manual, terminal.Content.GetProperty("ShowNotification").GetBoolean());
        using var published = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.MappingFile));
        Assert.Equal("retained", published.RootElement.GetProperty("futureField").GetString());
        Assert.Equal("2.8", published.RootElement.GetProperty("metadata").GetProperty("version").GetString());
    }

    [Fact]
    public async Task EqualCursorCanBeImportedAgainAndOlderCursorCannotReplaceIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        var completed = fixture.State.GetLastPicsCrawl();
        fixture.Source = Snapshot(199);
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(completed, fixture.State.GetLastPicsCrawl());
        await using var db = fixture.Database.Factory.CreateDbContext();
        Assert.Equal(3, await db.SteamDepotMappings.CountAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":0},\"depotMappings\":{\"10\":{\"appIds\":[2]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":4294967296},\"depotMappings\":{\"10\":{\"appIds\":[2]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"bad\":{\"appIds\":[2]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"10\":{\"appIds\":[0]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"10\":{\"appIds\":[2,2]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"10\":{\"appIds\":[2],\"ownerId\":3}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"10\":{\"appIds\":[2],\"appNames\":[]}}}")]
    public async Task InvalidSnapshotDoesNotMutateCatalogOrCheckpoint(string source)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = source;
        var before = await File.ReadAllTextAsync(fixture.MappingFile);

        Assert.False(await fixture.Service.ImportFromGitHubAsync());

        Assert.Equal(before, await File.ReadAllTextAsync(fixture.MappingFile));
        await fixture.AssertPreviousAsync();
    }

    [Fact]
    public async Task DatabaseCancellationRollsBackDeletionAndInsertedBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var snapshot = JsonSerializer.Deserialize<PicsJsonData>(Snapshot(200), JsonOptions)!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Pics.ImportToDatabaseAsync(
            snapshot, true, cancellation.Token, (_, progress) =>
            {
                if (progress == 95) cancellation.Cancel();
                return Task.CompletedTask;
            }));

        await fixture.AssertPreviousAsync();
    }

    [Fact]
    public async Task DatabaseErrorRollsBackDeletionOnPopulatedTable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var db = fixture.Database.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SteamDepotMappings\" ADD CONSTRAINT reject_app CHECK (\"AppId\" <> 2)");
        }
        fixture.Source = Snapshot(200);

        Assert.False(await fixture.Service.ImportFromGitHubAsync());

        await fixture.AssertPreviousAsync();
    }

    [Fact]
    public async Task StateSaveFailureKeepsCheckpointAndRestartRefusesPublishedCursorUntilRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        var temporaryState = fixture.StateFile + ".tmp";
        Directory.CreateDirectory(temporaryState);

        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.NotNull(fixture.Service.PendingFullScan);
        Assert.Equal(200u, (await fixture.Pics.LoadFromJsonAsync())!.Metadata!.LastChangeNumber);
        var persisted = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(fixture.StateFile))!;
        Assert.Equal(100u, persisted.LastPicsChangeNumber);
        var baseline = await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync");
        Assert.Equal(100u, baseline.LastChangeNumber);
        Assert.False(baseline.HasUsableBaseline);

        Directory.Delete(temporaryState);
        var restartedState = StateTestMethods.CreateStateService(fixture.Root);
        Assert.Equal(100u, restartedState.GetState().LastPicsChangeNumber);
        using (var restarted = fixture.Restart())
        {
            var restartedBaseline = await InvokeAsync<SteamKit2Service.DepotBaseline>(restarted, "GetDepotBaselineAsync");
            Assert.Equal(100u, restartedBaseline.LastChangeNumber);
            Assert.False(restartedBaseline.HasUsableBaseline);
            await (Task)Invoke(restarted, "LoadPicsMetadataAsync")!;
            Assert.Equal(fixture.PreviousCrawl, Get<DateTime>(restarted, "_lastCrawlTime"));
        }
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
    }

    [Fact]
    public async Task DuplicateAdmissionDoesNotClaimSuccessAndCancellationDoesNotStampFreshness()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Download = async token =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Snapshot(200);
        };
        using var cancellation = new CancellationTokenSource();
        var import = fixture.Service.ImportFromGitHubAsync(cancellation.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.ImportFromGitHubAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        await fixture.AssertPreviousAsync();
        Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        fixture.Download = null;
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
    }

    [Fact]
    public async Task LegacyCursorIsPinnedBeforeFailedImportAndZeroNeverFallsBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastPicsChangeNumber = null);
        fixture.Source = Snapshot(200);
        fixture.Events.BeforeSend = (_, content) =>
        {
            if (content.ToString().Contains("Applying mappings to downloads", StringComparison.Ordinal))
                throw new IOException("Application unavailable");
        };
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        fixture.State.UpdateState(state => state.LastPicsChangeNumber = 0);
        fixture.Pics.WritePicsJsonFile(Snapshot(200));
        var baseline = await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync");
        Assert.Equal(0u, baseline.LastChangeNumber);
        Assert.False(baseline.HasUsableBaseline);
    }

    [Fact]
    public async Task CommittedBaselineRequiresBothCatalogsAndMatchingCursor()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        Assert.True((await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync")).HasUsableBaseline);
        await fixture.Pics.ClearDepotMappingsAsync();
        Assert.False((await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync")).HasUsableBaseline);
        await fixture.SeedAsync();
        File.Delete(fixture.MappingFile);
        Assert.False((await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync")).HasUsableBaseline);
    }

    [Fact]
    public async Task CompletionRejectsAnOldViabilityWriteAndOnlyFullCrawlStampsFullTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var version = Get<long>(fixture.Service, "_baselineVersion");
        fixture.Service.UpdateLastCrawlTime(200);
        Assert.False((bool)Invoke(fixture.Service, "CacheViabilityResult", true, 100u, 99u, version)!);
        Assert.Null(fixture.State.GetState().LastViabilityCheck);
        Assert.Null(fixture.Service.PendingFullScan);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastFullPicsCrawl());
        fixture.Service.UpdateLastCrawlTime(201, fullScan: true);
        Assert.True(fixture.State.GetLastFullPicsCrawl() > fixture.PreviousCrawl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededViabilityResponseCannotRestoreTheOldRequirement(bool scheduled)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkedCursors = new ConcurrentQueue<uint>();
        fixture.CheckChanges = async (cursor, token) =>
        {
            checkedCursors.Enqueue(cursor);
            if (cursor == 100)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return (300u, true);
            }
            return (300u, false);
        };
        Set(fixture.Service, "_initialized", true);
        Set(fixture.Service, "_isRunning", true);
        Set(fixture.Service, "_crawlIncrementalMode", true);
        Task<IncrementalViabilityCheck>? check = scheduled ? null : fixture.Service.CheckViabilityAsync(CancellationToken.None);
        var pending = scheduled
            ? (Task)Invoke(fixture.Service, "ExecuteWorkAsync", CancellationToken.None)!
            : check!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        if (check != null)
        {
            Assert.True((await check).IsViable);
            Assert.Equal(200u, (await check).LastChangeNumber);
        }
        Assert.Equal(new uint[] { 100, 200 }, checkedCursors);
        Assert.False(fixture.State.GetState().RequiresFullScan);
        Assert.Equal(200u, fixture.State.GetState().LastViabilityCheckChangeNumber);
        Assert.Null(fixture.Service.PendingFullScan);
        Assert.False(fixture.Service.IsRebuildRunning);
    }

    [Fact]
    public async Task RestartUsesCommittedCursorAndLocalCompletionTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        using var restarted = fixture.Restart();
        await (Task)Invoke(restarted, "LoadPicsMetadataAsync")!;
        Assert.Equal(fixture.State.GetLastPicsCrawl(), Get<DateTime>(restarted, "_lastCrawlTime"));
        var result = await restarted.CheckViabilityAsync(CancellationToken.None);
        Assert.Equal(200u, result.LastChangeNumber);
        Assert.True(result.IsViable);
    }

    [Fact]
    public async Task OlderViabilityCompletionPreservesANewerFullScanRequirement()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var logger = new CapturingLogger<SteamKit2Service>();
        logger.OnLogged = entry =>
        {
            if (!entry.Message.StartsWith("Cached viability check result in state.json", StringComparison.Ordinal)) return;
            logger.OnLogged = null;
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Set(fixture.Service, "_logger", logger);
        var olderCheck = Task.Run(() => fixture.Service.CheckViabilityAsync(CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(await fixture.Service.ImportFromGitHubAsync());
            fixture.CheckChanges = (cursor, _) => Task.FromResult((cursor + 500000, true));
            Set(fixture.Service, "_initialized", true);
            Set(fixture.Service, "_isRunning", true);
            Set(fixture.Service, "_crawlIncrementalMode", true);
            await (Task)Invoke(fixture.Service, "ExecuteWorkAsync", CancellationToken.None)!;
            Assert.NotNull(fixture.Service.PendingFullScan);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await olderCheck).IsViable);
        Assert.Equal(200u, fixture.State.GetState().LastViabilityCheckChangeNumber);
        Assert.True(fixture.State.GetState().RequiresFullScan);
        Assert.NotNull(fixture.Service.PendingFullScan);
    }

    [Fact]
    public async Task RestartReusesFreshViabilityForTheSameCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state =>
        {
            state.RequiresFullScan = false;
            state.LastViabilityCheck = DateTime.UtcNow;
            state.LastViabilityCheckChangeNumber = 100;
            state.ViabilityChangeGap = 2;
        });
        var queries = 0;
        fixture.CheckChanges = (cursor, _) =>
        {
            queries++;
            return Task.FromResult((cursor + 2, false));
        };

        using var restarted = fixture.Restart();
        await (Task)Invoke(restarted, "InitializeAsync", CancellationToken.None)!;
        var result = await restarted.CheckViabilityAsync(CancellationToken.None);

        Assert.Equal(0, queries);
        Assert.True(result.IsViable);
        Assert.False(result.WillTriggerFullScan);
        Assert.Equal(2u, result.ChangeGap);
        Assert.Null(restarted.PendingFullScan);
    }

    [Fact]
    public async Task CancellationAfterDatabaseCommitDoesNotCommitFreshness()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        using var cancellation = new CancellationTokenSource();
        fixture.Events.BeforeSend = (_, content) =>
        {
            if (content.ToString().Contains("Finalizing import...", StringComparison.Ordinal)) cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ImportFromGitHubAsync(cancellation.Token));
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.NotNull(fixture.Service.PendingFullScan);
        await using var db = fixture.Database.Factory.CreateDbContext();
        Assert.Contains(await db.SteamDepotMappings.ToListAsync(), row => row.AppId == 2);
    }

    [Fact]
    public async Task ReloadFailurePreservesLiveMappingsAndRetryNeedsNoReset()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        fixture.Events.BeforeSend = (_, content) =>
        {
            if (content.ToString().Contains("Applying mappings to downloads...", StringComparison.Ordinal)) fixture.FailReload = true;
        };
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(1u, Get<ConcurrentDictionary<uint, uint>>(fixture.Service, "_depotOwners")[10]);
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.NotNull(fixture.Service.PendingFullScan);
        fixture.Events.BeforeSend = null;
        fixture.FailReload = false;
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(2u, Get<ConcurrentDictionary<uint, uint>>(fixture.Service, "_depotOwners")[10]);
    }

    [Fact]
    public async Task FilePublicationFailureDoesNotAdvanceCheckpoint()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        fixture.FailPublication = true;
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.NotNull(fixture.Service.PendingFullScan);
        fixture.FailPublication = false;
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
    }

    [Fact]
    public async Task LateCancellationCannotChangeCommittedImportTerminal()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Events.BeforeSend = (name, _) =>
        {
            if (name != "DepotMappingComplete") return;
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        using var cancellation = new CancellationTokenSource();
        var import = Task.Run(() => fixture.Service.ImportFromGitHubAsync(cancellation.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Null(fixture.Service.PendingFullScan);
        Assert.False(await fixture.Service.CancelRebuildAsync());
        cancellation.Cancel();
        release.Set();
        Assert.True(await import);
        var terminal = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        Assert.True(terminal.Content.GetProperty("Success").GetBoolean());
        Assert.False(terminal.Content.GetProperty("Cancelled").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizationCommitsTheCandidateAndOnlyFullScanSetsFullTime(bool incremental)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        Assert.False(fixture.State.GetState().HasDataLoaded);
        Set(fixture.Service, "_lastChangeNumberSeen", 200u);
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source, null)!;
        await reporter.StartAsync();
        await (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", incremental, reporter.Token)!;
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.True(fixture.State.GetState().HasDataLoaded);
        Assert.True(fixture.State.GetLastPicsCrawl() > fixture.PreviousCrawl);
        Assert.Equal(!incremental, fixture.State.GetLastFullPicsCrawl() > fixture.PreviousCrawl);
        Assert.Null(fixture.Service.PendingFullScan);
    }

    [Fact]
    public async Task LocalImportAndApplyDoNotClaimFreshness()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await fixture.Pics.ImportToDatabaseAsync();
        Set(fixture.Service, "_baselineCommitted", true);
        await fixture.Service.ManuallyApplyDepotMappingsAsync();
        Assert.False(Get<bool>(fixture.Service, "_baselineCommitted"));
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.False(fixture.State.GetState().HasDataLoaded);
        Assert.NotNull(fixture.Service.PendingFullScan);
    }

    [Fact]
    public async Task HttpFailurePreservesThePreviousCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.StatusCode = HttpStatusCode.BadGateway;
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        await fixture.AssertPreviousAsync();
    }

    [Fact]
    public async Task FailedLegacyPinPreventsCatalogMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastPicsChangeNumber = null);
        Directory.CreateDirectory(fixture.StateFile + ".tmp");
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Null(fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        await using var db = fixture.Database.Factory.CreateDbContext();
        Assert.Equal(new long[] { 1, 4, 9 }, await db.SteamDepotMappings.OrderBy(row => row.AppId).Select(row => row.AppId).ToArrayAsync());
    }

    [Theory]
    [InlineData(true, 100u)]
    [InlineData(false, 100u)]
    [InlineData(true, 99u)]
    [InlineData(false, 99u)]
    public async Task CachedAnswersMustMatchTheCommittedCursor(bool requiresFullScan, uint cachedCursor)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state =>
        {
            state.RequiresFullScan = requiresFullScan;
            state.LastViabilityCheckChangeNumber = cachedCursor;
        });
        var queries = 0;
        fixture.CheckChanges = (cursor, _) =>
        {
            queries++;
            return Task.FromResult((cursor + 10, false));
        };
        var result = await fixture.Service.CheckViabilityAsync(CancellationToken.None);
        Assert.Equal(cachedCursor == 100 ? 0 : 1, queries);
        Assert.Equal(cachedCursor != 100 || !requiresFullScan, result.IsViable);
        if (result.IsViable) Assert.Null(fixture.Service.PendingFullScan);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task FailedOrCancelledFinalizationDoesNotCommitTheCandidate(bool cancel, bool incremental, bool loaded)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.HasDataLoaded = loaded);
        Set(fixture.Service, "_lastChangeNumberSeen", 200u);
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source, null)!;
        await reporter.StartAsync();
        fixture.Events.BeforeSend = (_, content) =>
        {
            if (!content.ToString().Contains("applyingToDownloads", StringComparison.Ordinal)) return;
            if (cancel) source.Cancel();
            else Directory.CreateDirectory(fixture.StateFile + ".tmp");
        };
        var finalizing = (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", incremental, reporter.Token)!;
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalizing);
        else await Assert.ThrowsAsync<ServiceUnavailableException>(() => finalizing);
        fixture.Events.BeforeSend = null;
        await reporter.CompleteAsync(false, cancelled: cancel);
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastFullPicsCrawl());
        Assert.Equal(loaded, fixture.State.GetState().HasDataLoaded);
        Assert.Equal(loaded, JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(fixture.StateFile))!.HasDataLoaded);
        Assert.NotNull(fixture.Service.PendingFullScan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingJsonDoesNotChangeLoadedState(bool loaded)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Pics.WritePicsJsonFile(Snapshot(100).Replace("\"version\"", "\"totalMappings\":2,\"version\"", StringComparison.Ordinal));
        fixture.State.UpdateState(state => state.HasDataLoaded = loaded);
        var before = await File.ReadAllTextAsync(fixture.StateFile);
        Assert.NotNull(await fixture.Pics.LoadFromJsonAsync());
        Assert.Equal(loaded, fixture.State.GetState().HasDataLoaded);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.StateFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CachedAnswerRequiresUsableBaseline(bool requiresFullScan)
    {
        Assert.False(SteamKit2Service.ShouldReuseCachedViability(requiresFullScan, false));
        Assert.True(SteamKit2Service.ShouldReuseCachedViability(requiresFullScan, true));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string Snapshot(uint cursor) => $$$$"""
        {"metadata":{"lastChangeNumber":{{{{cursor}}}},"version":"2.8","lastUpdated":"2026-01-01T00:00:00Z"},
         "futureField":"retained","depotMappings":{"10":{"ownerId":2,"appIds":[2,3],"appNames":["New owner","Shared app"],"discoveredAt":"2026-01-01T00:00:00Z"}}}
        """;

    private static object? Invoke(object instance, string method, params object?[] args)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var member = type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (member != null) return member.Invoke(instance, args);
        }
        throw new MissingMethodException(instance.GetType().FullName, method);
    }

    private static async Task<T> InvokeAsync<T>(object instance, string method) =>
        await (Task<T>)Invoke(instance, method)!;

    private static void Set(object instance, string field, object value)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var member = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (member == null) continue;
            member.SetValue(instance, value);
            return;
        }
        throw new MissingFieldException(instance.GetType().FullName, field);
    }

    private static T Get<T>(object instance, string field)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var member = type.GetField(field, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (member != null) return (T)member.GetValue(instance)!;
            var property = type.GetProperty(field, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (property != null) return (T)property.GetValue(instance)!;
        }
        throw new MissingFieldException(instance.GetType().FullName, field);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SteamService _steam;
        private readonly ProcessManager _processes;
        private readonly GameImageFetchService _images = null!;
        private readonly ServiceScheduleRegistry _schedules;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "depot-import-" + Guid.NewGuid().ToString("N"));
        public DateTime PreviousCrawl { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public TestDatabase Database { get; }
        public StateService State { get; }
        public PicsDataService Pics { get; }
        public SteamKit2Service Service { get; }
        public RecordingNotifications Events { get; }
        public string Source { get; set; } = Snapshot(200);
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public Func<CancellationToken, Task<string>>? Download { get; set; }
        public Func<uint, CancellationToken, Task<(uint CurrentChangeNumber, bool RequiresFullScan)>> CheckChanges { get; set; }
            = (cursor, _) => Task.FromResult((cursor + 10, false));
        public bool FailPublication { get; set; }
        public bool FailReload { get; set; }
        public string MappingFile => Pics.GetPicsJsonFilePath();
        public string StateFile { get; }

        private Fixture(TestDatabase database)
        {
            Database = database;
            Directory.CreateDirectory(Root);
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = Root;
            Directory.CreateDirectory(paths.GetPicsDirectory());
            State = StateTestMethods.CreateStateService(Root);
            StateFile = Path.Combine(paths.GetStateDirectory(), "state.json");
            var registrations = new ServiceCollection();
            var options = new DbContextOptionsBuilder<AppDbContext>(Database.Options).AddInterceptors(new ReadFailure(this)).Options;
            registrations.AddScoped(_ => new AppDbContext(options));
            registrations.AddSingleton<GameImageFetchService>(_ => _images);
            _services = registrations.BuildServiceProvider();
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            Pics = new PicsDataService(
                NullLogger<PicsDataService>.Instance,
                scopes,
                paths,
                State,
                TimeProvider.System,
                (source, destination, overwrite) =>
                {
                    if (FailPublication) throw new IOException("Publication unavailable");
                    File.Move(source, destination, overwrite);
                });
            var clients = new Clients(this);
            _steam = new SteamService(new HttpClient(new StoreHandler()), NullLogger<SteamService>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
            Events = (RecordingNotifications)(object)notifications;
            _processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var tracker = new UnifiedOperationTracker(_processes, NullLogger<UnifiedOperationTracker>.Instance);
            _images = new GameImageFetchService(_services, NullLogger<GameImageFetchService>.Instance,
                new ConfigurationBuilder().Build(), State, notifications,
                DispatchProxy.Create<IImageCacheService, NullReturningProxy>(), tracker);
            Get<SemaphoreSlim>(_images, "_executionLock").Wait();
            var storage = Get<SteamAuthStorageService>(State, "_steamAuthStorage");
            Service = new Probe(this, scopes, _steam, Pics, State, clients,
                notifications, storage, tracker);
            _schedules = new ServiceScheduleRegistry([Service], State, notifications, tracker);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var database = await TestDatabase.CreateAsync();
            try
            {
                return new Fixture(database);
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async Task SeedAsync()
        {
            await using var db = Database.Factory.CreateDbContext();
            await db.SteamDepotMappings.ExecuteDeleteAsync();
            db.SteamDepotMappings.AddRange(
                new SteamDepotMapping { DepotId = 10, AppId = 1, AppName = "Old owner", IsOwner = true, DiscoveredAt = PreviousCrawl },
                new SteamDepotMapping { DepotId = 20, AppId = 4, AppName = "Removed app", IsOwner = true, DiscoveredAt = PreviousCrawl },
                new SteamDepotMapping { DepotId = 90, AppId = 9, AppName = "Local orphan", IsOwner = true, Source = "orphan-resolved", DiscoveredAt = PreviousCrawl });
            await db.SaveChangesAsync();
            Pics.WritePicsJsonFile(Snapshot(100));
            State.UpdateState(state =>
            {
                state.SetupCompleted = true;
                state.LastPicsChangeNumber = 100;
                state.LastPicsCrawl = PreviousCrawl;
                state.LastFullPicsCrawl = PreviousCrawl;
                state.RequiresFullScan = true;
                state.LastViabilityCheck = DateTime.UtcNow;
                state.LastViabilityCheckChangeNumber = 100;
                state.ViabilityChangeGap = 99;
            });
            Set(Service, "_automaticScanSkipped", true);
            await (Task)Invoke(Service, "LoadDepotMappingsAsync", false, CancellationToken.None)!;
        }

        public async Task AssertPreviousAsync()
        {
            Assert.False(State.GetState().HasDataLoaded);
            Assert.Equal(100u, State.GetState().LastPicsChangeNumber);
            Assert.Equal(PreviousCrawl, State.GetLastPicsCrawl());
            Assert.NotNull(Service.PendingFullScan);
            await using var db = Database.Factory.CreateDbContext();
            Assert.Equal(new long[] { 1, 4, 9 }, await db.SteamDepotMappings.OrderBy(row => row.AppId).Select(row => row.AppId).ToArrayAsync());
        }

        public SteamKit2Service Restart()
        {
            var state = StateTestMethods.CreateStateService(Root);
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = Root;
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            var pics = new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state);
            return new Probe(this, scopes, _steam, pics, state, new Clients(this),
                (ISignalRNotificationService)(object)Events, Get<SteamAuthStorageService>(state, "_steamAuthStorage"),
                new UnifiedOperationTracker(_processes, NullLogger<UnifiedOperationTracker>.Instance));
        }

        public async ValueTask DisposeAsync()
        {
            var handler = typeof(ServiceScheduleRegistry)
                .GetMethod("OnServiceExecutionStateChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<string>>(_schedules);
            ConfigurableScheduledService.ServiceExecutionStateChanged -= handler;
            ScheduledBackgroundService.ServiceExecutionStateChanged -= handler;
            Service.Dispose();
            _steam.Dispose();
            _processes.Dispose();
            Get<SemaphoreSlim>(_images, "_executionLock").Release();
            await _services.DisposeAsync();
            await Database.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }

        private sealed class Clients(Fixture fixture) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(new DownloadHandler(fixture));
        }

        private sealed class ReadFailure(Fixture fixture) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData commandEvent, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                if (fixture.FailReload && command.CommandText.Contains("SteamDepotMappings", StringComparison.Ordinal))
                    throw new IOException("Depot mappings cannot be loaded");
                return base.ReaderExecutingAsync(command, commandEvent, result, cancellationToken);
            }
        }

        private sealed class Probe(
            Fixture fixture, IServiceScopeFactory scopes, SteamService steam, PicsDataService pics, StateService state,
            IHttpClientFactory clients, ISignalRNotificationService notifications, SteamAuthStorageService storage, IUnifiedOperationTracker tracker)
            : SteamKit2Service(NullLogger<SteamKit2Service>.Instance, scopes, steam, pics, state, clients, notifications,
                new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, storage), storage, tracker)
        {
            internal override Task<(uint CurrentChangeNumber, bool RequiresFullScan)> CheckChangesAsync(uint changeNumberToCheck, CancellationToken ct)
                => fixture.CheckChanges(changeNumberToCheck, ct);
        }

        private sealed class StoreHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"2":{"success":true,"data":{"name":"New owner","header_image":"https://example.com/new.jpg"}}}""")
                });
        }

        private sealed class DownloadHandler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => new(fixture.StatusCode)
                {
                    Content = new StringContent(fixture.Download == null ? fixture.Source : await fixture.Download(cancellationToken))
                };
        }
    }

    public class RecordingNotifications : DispatchProxy
    {
        public ConcurrentQueue<(string Event, JsonElement Content)> Snapshots { get; } = new();
        public Action<string, JsonElement>? BeforeSend { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is { Length: >= 2 } && args[0] is string name && args[1] is { } content)
            {
                var snapshot = JsonSerializer.SerializeToElement(content);
                BeforeSend?.Invoke(name, snapshot);
                Snapshots.Enqueue((name, snapshot));
            }
            return Task.CompletedTask;
        }
    }
}
