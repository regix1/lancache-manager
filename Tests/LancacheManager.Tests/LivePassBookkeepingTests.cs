using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class LivePassBookkeepingTests
{
    [Fact]
    public async Task RecordLogIngestPassWritesOnceSkipsUnchangedAndSignalsAsync()
    {
        using var context = new ScheduledPrefillServiceTests.TempStateServiceContext();
        var stateService = context.StateService;
        stateService.SetLogMissingSourcesWarning("alpha", "access.log is missing");

        var writes = 0;
        stateService.BeforeWrite = () => writes++;
        var positions = new Dictionary<string, long>
        {
            ["access.log"] = 12,
            ["steam"] = 4
        };
        var diagnostics = Diagnostics();
        var processed = stateService.WaitForLogsProcessedAsync(CancellationToken.None);

        stateService.RecordLogIngestPass("alpha", positions, 19, diagnostics);

        Assert.Equal(1, writes);
        await processed.WaitAsync(TimeSpan.FromSeconds(1));
        var state = stateService.GetState();
        Assert.Equal(positions, state.LogProcessing.DatasourceSourcePositions["alpha"]);
        Assert.Equal(16, state.LogProcessing.DatasourcePositions["alpha"]);
        Assert.Equal(19, state.LogProcessing.DatasourceTotalLines["alpha"]);
        Assert.True(state.HasProcessedLogs);
        var storedDiagnostics = stateService.GetLogIngestDiagnostics("alpha");
        Assert.NotNull(storedDiagnostics);
        Assert.Equal("partial", storedDiagnostics!.TerminalStatus);
        Assert.Equal("access.log is missing", storedDiagnostics.MissingSourcesMessage);

        writes = 0;
        stateService.RecordLogIngestPass("alpha", positions, 19, diagnostics: null);
        Assert.Equal(0, writes);

        writes = 0;
        stateService.RecordLogIngestPass(
            "alpha",
            new Dictionary<string, long> { ["access.log"] = 13, ["steam"] = 4 },
            totalLines: null,
            diagnostics: null);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void RecordLogIngestPassRetriesAnUnchangedWriteAfterFailure()
    {
        using var context = new ScheduledPrefillServiceTests.TempStateServiceContext();
        var stateService = context.StateService;
        stateService.RecordLogIngestPass(
            "alpha",
            new Dictionary<string, long> { ["access.log"] = 6 },
            6,
            diagnostics: null);

        var failNext = true;
        var writes = 0;
        stateService.BeforeWrite = () =>
        {
            writes++;
            if (failNext)
            {
                failNext = false;
                throw new IOException("Injected write failure.");
            }
        };
        var positions = new Dictionary<string, long> { ["access.log"] = 7 };

        Assert.Throws<ServiceUnavailableException>(() =>
            stateService.RecordLogIngestPass("alpha", positions, 7, diagnostics: null));

        writes = 0;
        stateService.RecordLogIngestPass("alpha", positions, 7, diagnostics: null);

        Assert.Equal(1, writes);
        Assert.Equal(7, stateService.GetLogPosition("alpha"));
        Assert.True(stateService.HasProcessedLogs());

        failNext = true;
        var controlPositions = new Dictionary<string, long> { ["access.log"] = 8 };
        Assert.Throws<ServiceUnavailableException>(() =>
            stateService.RecordLogIngestPass("alpha", controlPositions, 8, diagnostics: null));
        SetField(stateService, "_consecutiveFailures", 0);

        writes = 0;
        stateService.RecordLogIngestPass("alpha", controlPositions, 8, diagnostics: null);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void SeparateStateSettersWriteFourThenThreeTimes()
    {
        using var context = new ScheduledPrefillServiceTests.TempStateServiceContext();
        var stateService = context.StateService;
        var writes = 0;
        stateService.BeforeWrite = () => writes++;
        var positions = new Dictionary<string, long> { ["access.log"] = 5 };

        stateService.SetLogSourcePositions("alpha", positions);
        stateService.SetLogTotalLines("alpha", 5);
        stateService.SetLogIngestDiagnostics("alpha", Diagnostics());
        stateService.SetHasProcessedLogs(true);
        Assert.Equal(4, writes);

        writes = 0;
        stateService.SetLogSourcePositions("alpha", positions);
        stateService.SetLogTotalLines("alpha", 5);
        stateService.SetHasProcessedLogs(true);
        Assert.Equal(3, writes);
    }

    [Fact]
    public async Task BusyPassesDoNotRepeatTheImageLookupsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            seed.Downloads.AddRange(
                new Download
                {
                    Service = "steam",
                    ClientIp = "10.0.0.1",
                    StartTimeUtc = now.AddMinutes(-2),
                    EndTimeUtc = now.AddMinutes(-1),
                    GameAppId = 730,
                    GameName = "Counter-Strike 2"
                },
                new Download
                {
                    Service = "epicgames",
                    ClientIp = "10.0.0.2",
                    StartTimeUtc = now.AddMinutes(-2),
                    EndTimeUtc = now.AddMinutes(-1),
                    EpicAppId = "epic-app"
                });
            seed.GameImages.Add(new GameImage
            {
                AppId = "730",
                Service = "steam",
                ImageData = [1],
                FetchedAtUtc = now
            });
            await seed.SaveChangesAsync();
        }

        var recorder = new RecordingCommandInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options)
            .AddInterceptors(recorder)
            .Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        using var container = services.BuildServiceProvider();
        var processor = UninitializedProcessor(container);
        var images = UninitializedImageService(container);

        await FetchNamesAsync(processor);
        await FetchEpicImagesAsync(processor, false);
        await images.StartFetchForMissingArtAsync(false, CancellationToken.None);

        await using (var continued = new AppDbContext(options))
        {
            await continued.Database.ExecuteSqlRawAsync(
                "UPDATE \"Downloads\" SET \"EndTimeUtc\" = \"EndTimeUtc\" + interval '1 minute', \"CacheHitBytes\" = \"CacheHitBytes\" + 1");
        }

        var controlProcessor = UninitializedProcessor(container);
        var controlImages = UninitializedImageService(container);
        recorder.Clear();
        await FetchNamesAsync(controlProcessor);
        await FetchEpicImagesAsync(controlProcessor, false);
        await controlImages.StartFetchForMissingArtAsync(false, CancellationToken.None);
        Assert.Contains(recorder.Commands, IsLimitedDownloadScan);
        Assert.Contains(recorder.Commands, IsDistinctDownloadScan);

        recorder.Clear();
        await FetchNamesAsync(processor);
        await FetchEpicImagesAsync(processor, false);
        await images.StartFetchForMissingArtAsync(false, CancellationToken.None);

        Assert.DoesNotContain(recorder.Commands, IsLimitedDownloadScan);
        Assert.DoesNotContain(recorder.Commands, IsDistinctDownloadScan);
        Assert.Contains(recorder.Commands, IsDownloadMaxRead);

        recorder.Clear();
        await FetchEpicImagesAsync(processor, true);
        Assert.Contains(recorder.Commands, IsLimitedDownloadScan);

        await using (var inserted = new AppDbContext(options))
        {
            var now = DateTime.UtcNow;
            inserted.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "10.0.0.3",
                StartTimeUtc = now.AddMinutes(-1),
                EndTimeUtc = now,
                GameAppId = 730,
                GameName = "Counter-Strike 2"
            });
            await inserted.SaveChangesAsync();
        }

        recorder.Clear();
        await FetchNamesAsync(processor);
        await FetchEpicImagesAsync(processor, false);
        await images.StartFetchForMissingArtAsync(false, CancellationToken.None);

        Assert.Contains(recorder.Commands, IsLimitedDownloadScan);
        Assert.Contains(recorder.Commands, IsDistinctDownloadScan);
    }

    [Fact]
    public void RustProcessorSourceKeepsTheBookkeepingContracts()
    {
        var source = File.ReadAllText(Path.Combine(
            EndpointAuthorizationHost.FindRepositoryRoot(),
            "Api",
            "LancacheManager",
            "Infrastructure",
            "Services",
            "Rust",
            "RustLogProcessorService.cs"));

        Assert.DoesNotContain("BackfillMissingBannerArtAsync(", source);
        var depotCount = source.IndexOf("SteamDepotMappings.CountAsync()", StringComparison.Ordinal);
        var depotCondition = source.LastIndexOf(
            "if (!liveIngest)",
            depotCount,
            StringComparison.Ordinal);
        Assert.InRange(depotCount - depotCondition, 1, 2_000);
        Assert.Equal(1, Occurrences(source, "_stateService.RecordLogIngestPass("));
        Assert.DoesNotContain("_stateService.SetHasProcessedLogs(true)", source);
        Assert.DoesNotContain("SetLogTotalLines(datasourceName!", source);

        var partialStart = source.IndexOf(
            "if (finalProgress!.TerminalStatus == \"partial\")",
            StringComparison.Ordinal);
        var partialEnd = source.IndexOf("return false;", partialStart, StringComparison.Ordinal);
        var partialBranch = source[partialStart..partialEnd];
        Assert.Contains("SetLogSourcePositions(", partialBranch);
        Assert.Equal(2, Occurrences(source, "var mergedPositions = MergedSourcePositions("));
        Assert.DoesNotContain("_steamAppsWithoutName", source);
        Assert.Equal(1, Occurrences(source, "Interlocked.Exchange(ref _epicRowsResolvedPending, 0)"));
        Assert.True(
            source.IndexOf("Interlocked.Exchange(ref _epicRowsResolvedPending, 0)", StringComparison.Ordinal)
            > source.IndexOf("_postPassLock.WaitAsync(0)", StringComparison.Ordinal));
        Assert.DoesNotContain("epicResolvedThisPass", source);
    }

    private static LogIngestDiagnostics Diagnostics() => new()
    {
        Layout = "service-split",
        TerminalStatus = "partial",
        UnparsedLines = 2,
        FilesWithErrors = ["access.log.2.gz"],
        LastRunUtc = DateTime.UtcNow
    };

    private static RustLogProcessorService UninitializedProcessor(IServiceProvider container)
    {
        var processor = (RustLogProcessorService)RuntimeHelpers.GetUninitializedObject(
            typeof(RustLogProcessorService));
        SetField(processor, "_serviceProvider", container);
        SetField(processor, "_logger", NullLogger<RustLogProcessorService>.Instance);
        return processor;
    }

    private static GameImageFetchService UninitializedImageService(IServiceProvider container)
    {
        var service = (GameImageFetchService)RuntimeHelpers.GetUninitializedObject(
            typeof(GameImageFetchService));
        SetField(service, "_serviceProvider", container);
        SetField(service, "_artTriggerAttemptedAppIds", new HashSet<long>());
        SetField(service, "_artTriggerLock", new object());
        SetField(service, "_logger", NullLogger<GameImageFetchService>.Instance);
        return service;
    }

    private static async Task<int> FetchNamesAsync(RustLogProcessorService processor)
    {
        var method = typeof(RustLogProcessorService).GetMethod(
            "FetchMissingGameNamesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<int>)method.Invoke(processor, null)!;
    }

    private static async Task FetchEpicImagesAsync(
        RustLogProcessorService processor,
        bool epicRowsResolvedThisPass)
    {
        var method = typeof(RustLogProcessorService).GetMethod(
            "FetchMissingEpicImagesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(processor, new object[] { epicRowsResolvedThisPass })!;
    }

    private static bool IsLimitedDownloadScan(string command) =>
        command.Contains("\"Downloads\"", StringComparison.Ordinal)
        && command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase);

    private static bool IsDistinctDownloadScan(string command) =>
        command.Contains("\"Downloads\"", StringComparison.Ordinal)
        && command.Contains("DISTINCT", StringComparison.OrdinalIgnoreCase);

    private static bool IsDownloadMaxRead(string command) =>
        command.Contains("\"Downloads\"", StringComparison.Ordinal)
        && command.Contains("max", StringComparison.OrdinalIgnoreCase);

    private static int Occurrences(string source, string value)
    {
        var count = 0;
        var position = 0;
        while ((position = source.IndexOf(value, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }
        return count;
    }

    private static void SetField(object target, string name, object value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }
        }

        throw new InvalidOperationException($"Field '{name}' was not found.");
    }
}
