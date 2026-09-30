using System.Data.Common;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.BattleNet;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Core.Services.Xbox;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class ResolverUnmatchedScanTests
{
    private const string ProductId = "9NBLGGH537DL";
    private const string Fragment = "/filestreamingservice/files/12345678-90ab-cdef-1234-567890abcdef";

    [Fact]
    public async Task XboxWithoutAUsableCatalogDoesNotReadDownloadsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(Enumerable.Range(1, 50).Select(index => NewDownload(
                "wsus",
                $"https://windows.example.test/update/{index}",
                active: false)));
            await seed.SaveChangesAsync();
        }

        recorder.Clear();
        var resolved = await NewXboxService(options).ResolveDownloadsAsync();

        Assert.Equal(0, resolved);
        Assert.DoesNotContain(recorder.Commands, MentionsDownloads);
    }

    [Fact]
    public async Task XboxKeepsProductIdRecoveryWithoutPatternsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.XboxGameMappings.Add(new XboxGameMapping
            {
                ProductId = ProductId,
                Title = "Halo Infinite",
                ImageUrl = "https://images.example.test/halo.jpg",
                LastSeenAtUtc = DateTime.UtcNow
            });
            var download = NewDownload("xbox", "https://assets.example.test/no-pattern", active: false);
            download.XboxProductId = ProductId;
            seed.Downloads.Add(download);
            await seed.SaveChangesAsync();
        }

        var resolved = await NewXboxService(database.Options).ResolveDownloadsAsync();

        Assert.Equal(1, resolved);
        await using var db = database.Factory.CreateDbContext();
        var renamed = await db.Downloads.SingleAsync();
        Assert.Equal("Halo Infinite", renamed.GameName);
        Assert.Equal("xbox", renamed.Service);
    }

    [Fact]
    public async Task XboxUnchangedUnmatchedRowsRunOnlyTheAggregateAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        await using (var seed = new AppDbContext(options))
        {
            seed.XboxCdnPatterns.Add(NewXboxPattern(Fragment));
            seed.Downloads.Add(NewDownload("wsus", "https://windows.example.test/unmatched", active: false));
            await seed.SaveChangesAsync();
        }

        var service = NewXboxService(options);
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        recorder.Clear();

        Assert.Equal(0, await service.ResolveDownloadsAsync());
        Assert.Single(recorder.Commands, MentionsDownloads);
    }

    [Fact]
    public async Task XboxRenameRechecksThatTheRowIsInactiveAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        long id;
        await using (var seed = new AppDbContext(options))
        {
            seed.XboxCdnPatterns.Add(NewXboxPattern(Fragment));
            var download = NewDownload("wsus", $"https://assets.example.test{Fragment}", active: false);
            seed.Downloads.Add(download);
            await seed.SaveChangesAsync();
            id = download.Id;
        }

        var changed = 0;
        recorder.OnExecuting = command =>
        {
            if (!IsDownloadUpdate(command) || Interlocked.Exchange(ref changed, 1) != 0)
            {
                return;
            }

            using var concurrent = database.Factory.CreateDbContext();
            concurrent.Downloads
                .Where(download => download.Id == id)
                .ExecuteUpdate(setters => setters.SetProperty(download => download.IsActive, true));
        };

        Assert.Equal(0, await NewXboxService(options).ResolveDownloadsAsync());
        await using var db = database.Factory.CreateDbContext();
        var untouched = await db.Downloads.SingleAsync();
        Assert.True(untouched.IsActive);
        Assert.Equal("wsus", untouched.Service);
        Assert.Null(untouched.GameName);
    }

    [Fact]
    public async Task EpicMemoSkipsTheLoadUntilTheCatalogChangesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        var logger = new WarningLogger<EpicMappingService>();
        await using (var seed = new AppDbContext(options))
        {
            seed.EpicCdnPatterns.Add(NewEpicPattern("/Builds/other/", "Other"));
            seed.Downloads.Add(NewDownload("epicgames", EpicUrl("wanted"), active: false));
            await seed.SaveChangesAsync();
        }

        var service = NewEpicService(options, logger);
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        recorder.Clear();
        logger.Clear();

        Assert.Equal(0, await service.ResolveDownloadsAsync());
        Assert.Single(recorder.Commands, MentionsDownloads);
        Assert.Equal(0, logger.WarningCount);

        await using (var update = database.Factory.CreateDbContext())
        {
            update.EpicCdnPatterns.Add(NewEpicPattern("/Builds/wanted/", "Wanted Game"));
            await update.SaveChangesAsync();
        }

        Assert.Equal(1, await service.ResolveDownloadsAsync());
        await using var db = database.Factory.CreateDbContext();
        Assert.Equal("Wanted Game", (await db.Downloads.SingleAsync()).GameName);
    }

    [Fact]
    public async Task EpicCandidateSwapChangesTheMemoKeyAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.EpicCdnPatterns.Add(NewEpicPattern("/Builds/match/", "Matched Game"));
            seed.Downloads.Add(NewDownload("epicgames", EpicUrl("first-unmatched"), active: false));
            await seed.SaveChangesAsync();
        }

        var service = NewEpicService(database.Options, NullLogger<EpicMappingService>.Instance);
        Assert.Equal(0, await service.ResolveDownloadsAsync());

        await using (var swap = database.Factory.CreateDbContext())
        {
            await swap.Downloads.ExecuteDeleteAsync();
            swap.Downloads.Add(NewDownload("epicgames", EpicUrl("match"), active: false));
            await swap.SaveChangesAsync();
        }

        Assert.Equal(1, await service.ResolveDownloadsAsync());
        await using var db = database.Factory.CreateDbContext();
        Assert.Equal("Matched Game", (await db.Downloads.SingleAsync()).GameName);
    }

    [Fact]
    public async Task EpicInactiveTransitionInvalidatesTheMemoAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        var laterEnd = new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc);
        var earlierEnd = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc);
        long activeId;
        await using (var seed = new AppDbContext(options))
        {
            seed.EpicCdnPatterns.Add(NewEpicPattern("/Builds/match/", "Matched Game"));
            var inactive = NewDownload("epicgames", EpicUrl("unmatched-a"), active: false);
            inactive.EndTimeUtc = laterEnd;
            var active = NewDownload("epicgames", EpicUrl("unmatched-b"), active: true);
            active.EndTimeUtc = earlierEnd;
            active.Datasource = "secondary";
            seed.Downloads.AddRange(inactive, active);
            await seed.SaveChangesAsync();
            activeId = active.Id;
        }

        var service = NewEpicService(options, NullLogger<EpicMappingService>.Instance);
        Assert.Equal(0, await service.ResolveDownloadsAsync());

        await using var before = database.Factory.CreateDbContext();
        var beforeKey = await before.Downloads
            .Where(download => EF.Functions.Like(download.Service, "%epic%")
                               && (string.IsNullOrEmpty(download.EpicAppId) || download.GameName == null)
                               && download.LastUrl != null)
            .GroupBy(download => 1)
            .Select(group => new
            {
                Count = group.Count(),
                MaxId = group.Max(download => download.Id),
                IdSum = group.Sum(download => download.Id),
                MaxInactiveEnd = group.Max(download => download.IsActive ? (DateTime?)null : download.EndTimeUtc)
            })
            .SingleAsync();
        Assert.Equal(1, await before.Downloads.CountAsync(download => !download.IsActive));

        await before.Downloads
            .Where(download => download.Id == activeId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(download => download.IsActive, false)
                .SetProperty(download => download.LastUrl, EpicUrl("match")));

        await using var after = database.Factory.CreateDbContext();
        var afterKey = await after.Downloads
            .Where(download => EF.Functions.Like(download.Service, "%epic%")
                               && (string.IsNullOrEmpty(download.EpicAppId) || download.GameName == null)
                               && download.LastUrl != null)
            .GroupBy(download => 1)
            .Select(group => new
            {
                Count = group.Count(),
                MaxId = group.Max(download => download.Id),
                IdSum = group.Sum(download => download.Id),
                MaxInactiveEnd = group.Max(download => download.IsActive ? (DateTime?)null : download.EndTimeUtc)
            })
            .SingleAsync();
        Assert.Equal(beforeKey, afterKey);
        Assert.Equal(2, await after.Downloads.CountAsync(download => !download.IsActive));

        recorder.Clear();
        Assert.Equal(1, await service.ResolveDownloadsAsync());
        Assert.True(recorder.Commands.Count(MentionsDownloads) > 1);

        var named = await after.Downloads.SingleAsync(download => download.Id == activeId);
        Assert.Equal("Matched Game", named.GameName);
        Assert.Equal("MatchedGame", named.EpicAppId);
    }

    [Fact]
    public async Task BlizzardUnchangedUnknownRowsRunOnlyTheAggregateAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NewDownload(
                "blizzard",
                "https://blizzard.example.test/tpr/not-a-product/data/hash",
                active: false));
            await seed.SaveChangesAsync();
        }

        var service = NewBattleNetService(options, NewTracker());
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled);
        Assert.Equal(0, await service.ResolveDownloadsAsync(notice));
        recorder.Clear();

        Assert.Equal(0, await service.ResolveDownloadsAsync(notice));
        Assert.Single(recorder.Commands, MentionsDownloads);
    }

    [Fact]
    public async Task BlizzardInactiveTransitionInvalidatesTheMemoAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        var laterEnd = new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc);
        var earlierEnd = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc);
        long activeId;
        await using (var seed = new AppDbContext(options))
        {
            var inactive = NewDownload(
                "blizzard",
                "https://blizzard.example.test/tpr/not-a-product-a/data/hash",
                active: false);
            inactive.EndTimeUtc = laterEnd;
            var active = NewDownload(
                "blizzard",
                "https://blizzard.example.test/tpr/not-a-product-b/data/hash",
                active: true);
            active.EndTimeUtc = earlierEnd;
            active.Datasource = "secondary";
            seed.Downloads.AddRange(inactive, active);
            await seed.SaveChangesAsync();
            activeId = active.Id;
        }

        var service = NewBattleNetService(options, NewTracker());
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled);
        Assert.Equal(0, await service.ResolveDownloadsAsync(notice));

        await using var before = database.Factory.CreateDbContext();
        var beforeKey = await before.Downloads
            .Where(download => EF.Functions.Like(download.Service, "%blizzard%")
                               && download.GameName == null
                               && download.LastUrl != null)
            .GroupBy(download => 1)
            .Select(group => new
            {
                Count = group.Count(),
                MaxId = group.Max(download => download.Id),
                IdSum = group.Sum(download => download.Id),
                MaxInactiveEnd = group.Max(download => download.IsActive ? (DateTime?)null : download.EndTimeUtc)
            })
            .SingleAsync();
        Assert.Equal(1, await before.Downloads.CountAsync(download => !download.IsActive));

        await before.Downloads
            .Where(download => download.Id == activeId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(download => download.IsActive, false)
                .SetProperty(
                    download => download.LastUrl,
                    "https://blizzard.example.test/tpr/wow/data/hash"));

        await using var after = database.Factory.CreateDbContext();
        var afterKey = await after.Downloads
            .Where(download => EF.Functions.Like(download.Service, "%blizzard%")
                               && download.GameName == null
                               && download.LastUrl != null)
            .GroupBy(download => 1)
            .Select(group => new
            {
                Count = group.Count(),
                MaxId = group.Max(download => download.Id),
                IdSum = group.Sum(download => download.Id),
                MaxInactiveEnd = group.Max(download => download.IsActive ? (DateTime?)null : download.EndTimeUtc)
            })
            .SingleAsync();
        Assert.Equal(beforeKey, afterKey);
        Assert.Equal(2, await after.Downloads.CountAsync(download => !download.IsActive));

        recorder.Clear();
        Assert.Equal(1, await service.ResolveDownloadsAsync(notice));
        Assert.True(recorder.Commands.Count(MentionsDownloads) > 1);

        var named = await after.Downloads.SingleAsync(download => download.Id == activeId);
        Assert.Equal("World of Warcraft", named.GameName);
    }

    [Fact]
    public async Task ActiveEpicContinuationDoesNotInvalidateTheMemoAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        long id;
        await using (var seed = new AppDbContext(options))
        {
            seed.EpicCdnPatterns.Add(NewEpicPattern("/Builds/other/", "Other"));
            var download = NewDownload("epicgames", EpicUrl("running-a"), active: true);
            seed.Downloads.Add(download);
            await seed.SaveChangesAsync();
            id = download.Id;
        }

        var service = NewEpicService(options, NullLogger<EpicMappingService>.Instance);
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        await using (var continuation = database.Factory.CreateDbContext())
        {
            await continuation.Downloads
                .Where(download => download.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(download => download.EndTimeUtc, download => download.EndTimeUtc.AddMinutes(1))
                    .SetProperty(download => download.LastUrl, EpicUrl("running-b")));
        }

        recorder.Clear();
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        Assert.Single(recorder.Commands, MentionsDownloads);

        await using (var ended = database.Factory.CreateDbContext())
        {
            await ended.Downloads
                .Where(download => download.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(download => download.IsActive, false));
        }

        recorder.Clear();
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        Assert.True(recorder.Commands.Count(MentionsDownloads) > 1);

        recorder.Clear();
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        Assert.Single(recorder.Commands, MentionsDownloads);
    }

    [Fact]
    public async Task XboxInactiveTransitionStillResolvesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        var laterEnd = new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc);
        var earlierEnd = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc);
        long activeId;
        await using (var seed = new AppDbContext(options))
        {
            seed.XboxCdnPatterns.Add(NewXboxPattern(Fragment));
            var inactive = NewDownload("wsus", "https://windows.example.test/unmatched-a", active: false);
            inactive.EndTimeUtc = laterEnd;
            var active = NewDownload("wsus", $"https://assets.example.test{Fragment}", active: true);
            active.EndTimeUtc = earlierEnd;
            active.Datasource = "secondary";
            seed.Downloads.AddRange(inactive, active);
            await seed.SaveChangesAsync();
            activeId = active.Id;
        }

        var service = NewXboxService(options);
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        await using (var before = database.Factory.CreateDbContext())
        {
            var untouched = await before.Downloads.SingleAsync(download => download.Id == activeId);
            Assert.True(untouched.IsActive);
            Assert.Equal("wsus", untouched.Service);
            Assert.Null(untouched.GameName);
        }

        await using (var ended = database.Factory.CreateDbContext())
        {
            await ended.Downloads
                .Where(download => download.Id == activeId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(download => download.IsActive, false));
        }

        recorder.Clear();
        Assert.Equal(1, await service.ResolveDownloadsAsync());
        Assert.True(recorder.Commands.Count(MentionsDownloads) > 1);

        await using var after = database.Factory.CreateDbContext();
        var named = await after.Downloads.SingleAsync(download => download.Id == activeId);
        Assert.Equal("Halo Infinite", named.GameName);
        Assert.Equal("xbox", named.Service);
        Assert.Equal(ProductId, named.XboxProductId);
    }

    [Fact]
    public async Task XboxRenameRechecksTheMatchedUrlAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        long id;
        await using (var seed = new AppDbContext(options))
        {
            seed.XboxCdnPatterns.Add(NewXboxPattern(Fragment));
            var download = NewDownload("wsus", $"https://assets.example.test{Fragment}", active: false);
            seed.Downloads.Add(download);
            await seed.SaveChangesAsync();
            id = download.Id;
        }

        var changed = 0;
        recorder.OnExecuting = command =>
        {
            if (!IsDownloadUpdate(command) || Interlocked.Exchange(ref changed, 1) != 0)
            {
                return;
            }

            using var concurrent = database.Factory.CreateDbContext();
            concurrent.Downloads
                .Where(download => download.Id == id)
                .ExecuteUpdate(setters => setters.SetProperty(
                    download => download.LastUrl,
                    "https://windows.example.test/moved"));
        };

        var service = NewXboxService(options);
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        recorder.OnExecuting = null;
        recorder.Clear();
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        Assert.True(recorder.Commands.Count(MentionsDownloads) > 1);

        await using var db = database.Factory.CreateDbContext();
        var untouched = await db.Downloads.SingleAsync();
        Assert.Equal("wsus", untouched.Service);
        Assert.Null(untouched.GameName);
    }

    [Fact]
    public async Task EpicDeletionBeforeSaveReturnsZeroAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        long id;
        await using (var seed = new AppDbContext(options))
        {
            seed.EpicCdnPatterns.Add(NewEpicPattern("/Builds/match/", "Matched Game"));
            var download = NewDownload("epicgames", EpicUrl("match"), active: false);
            seed.Downloads.Add(download);
            await seed.SaveChangesAsync();
            id = download.Id;
        }

        DeleteBeforeDownloadUpdate(database, recorder, id);

        Assert.Equal(0, await NewEpicService(options, NullLogger<EpicMappingService>.Instance).ResolveDownloadsAsync());
        await using var db = database.Factory.CreateDbContext();
        Assert.Empty(await db.Downloads.ToListAsync());
    }

    [Fact]
    public async Task BlizzardDeletionBeforeSaveCompletesWithoutFailureAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recorder = new RecordingCommandInterceptor();
        var options = WithRecorder(database, recorder);
        long id;
        await using (var seed = new AppDbContext(options))
        {
            var download = NewDownload(
                "blizzard",
                "https://blizzard.example.test/tpr/wow/data/hash",
                active: false);
            seed.Downloads.Add(download);
            await seed.SaveChangesAsync();
            id = download.Id;
        }

        DeleteBeforeDownloadUpdate(database, recorder, id);
        var tracker = NewTracker();
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled);

        Assert.Equal(0, await NewBattleNetService(options, tracker).ResolveDownloadsAsync(notice));
        Assert.DoesNotContain(tracker.GetRuns().Runs, run => run.Status == "failed");
        await using var db = database.Factory.CreateDbContext();
        Assert.Empty(await db.Downloads.ToListAsync());
    }

    private static DbContextOptions<AppDbContext> WithRecorder(
        TestDatabase database,
        RecordingCommandInterceptor recorder) =>
        new DbContextOptionsBuilder<AppDbContext>(database.Options)
            .AddInterceptors(recorder)
            .Options;

    private static XboxMappingService NewXboxService(DbContextOptions<AppDbContext> options)
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        return new XboxMappingService(
            new TestDbContextFactory(options),
            notifications,
            new XboxApiDirectClient(new HttpClient(), NullLogger<XboxApiDirectClient>.Instance),
            NullLogger<XboxMappingService>.Instance);
    }

    private static EpicMappingService NewEpicService(
        DbContextOptions<AppDbContext> options,
        ILogger<EpicMappingService> logger)
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>();
        var scopes = DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>();
        var state = DispatchProxy.Create<IStateService, NullReturningProxy>();
        return new EpicMappingService(
            logger,
            new EpicApiDirectClient(new HttpClient(), NullLogger<EpicApiDirectClient>.Instance),
            null!,
            notifications,
            new TestDbContextFactory(options),
            tracker,
            scopes,
            state);
    }

    private static BattleNetMappingService NewBattleNetService(
        DbContextOptions<AppDbContext> options,
        IUnifiedOperationTracker tracker) =>
        new(
            new TestDbContextFactory(options),
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            tracker,
            NullLogger<BattleNetMappingService>.Instance);

    private static UnifiedOperationTracker NewTracker() =>
        new(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);

    private static Download NewDownload(string service, string url, bool active) =>
        new()
        {
            Service = service,
            ClientIp = "127.0.0.1",
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
            EndTimeUtc = DateTime.UtcNow.AddMinutes(-5),
            LastUrl = url,
            IsActive = active,
            CacheHitBytes = 1024
        };

    private static XboxCdnPattern NewXboxPattern(string fragment) =>
        new()
        {
            ProductId = ProductId,
            Title = "Halo Infinite",
            UrlFragment = fragment,
            CdnHost = "assets.example.test",
            LastSeenAtUtc = DateTime.UtcNow
        };

    private static EpicCdnPattern NewEpicPattern(string path, string name) =>
        new()
        {
            AppId = name.Replace(" ", "", StringComparison.Ordinal),
            Name = name,
            ChunkBaseUrl = path,
            LastSeenAtUtc = DateTime.UtcNow
        };

    private static string EpicUrl(string path) =>
        $"https://epic.example.test/Builds/{path}/CloudDir/chunk";

    private static bool MentionsDownloads(string command) =>
        command.Contains("\"Downloads\"", StringComparison.Ordinal);

    private static bool IsDownloadUpdate(DbCommand command) =>
        command.CommandText.Contains("UPDATE \"Downloads\"", StringComparison.Ordinal);

    private static void DeleteBeforeDownloadUpdate(
        TestDatabase database,
        RecordingCommandInterceptor recorder,
        long id)
    {
        var deleted = 0;
        recorder.OnExecuting = command =>
        {
            if (!IsDownloadUpdate(command) || Interlocked.Exchange(ref deleted, 1) != 0)
            {
                return;
            }

            using var concurrent = database.Factory.CreateDbContext();
            concurrent.Downloads.Where(download => download.Id == id).ExecuteDelete();
        };
    }

    private sealed class WarningLogger<T> : ILogger<T>
    {
        private readonly object _sync = new();
        private readonly List<string> _warnings = [];

        public int WarningCount
        {
            get
            {
                lock (_sync)
                {
                    return _warnings.Count;
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
            {
                return;
            }

            lock (_sync)
            {
                _warnings.Add(formatter(state, exception));
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _warnings.Clear();
            }
        }
    }
}
