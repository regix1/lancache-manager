using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class MetricsScrapeGateTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 10, false)]
    [InlineData(11, 10, true)]
    [InlineData(10, 10, false)]
    public void RefreshRequiresTheFirstRunOrANewerScrape(
        long lastScrapeTicks,
        long lastRefreshTicks,
        bool expected)
    {
        Assert.Equal(expected, LancacheMetricsService.ShouldRefresh(lastScrapeTicks, lastRefreshTicks));
    }

    [Fact]
    public void CollectingTheObservableMeterRecordsAScrape()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var service = new LancacheMetricsService(
            services,
            NullLogger<LancacheMetricsService>.Instance,
            new ConfigurationBuilder().Build(),
            Stopwatch.GetTimestamp,
            EmptyActivity);
        var meter = Assert.IsType<Meter>(typeof(LancacheMetricsService)
            .GetField("_meter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service));
        var scrapeTicks = typeof(LancacheMetricsService)
            .GetField("_lastScrapeTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(0L, scrapeTicks.GetValue(service));

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "lancache_info")
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, _, _, _) => { });
        listener.Start();

        var before = Stopwatch.GetTimestamp();
        listener.RecordObservableInstruments();
        var after = Stopwatch.GetTimestamp();

        Assert.InRange(Assert.IsType<long>(scrapeTicks.GetValue(service)), before, after);
    }

    [Fact]
    public async Task MonotonicScrapeGateSurvivesAClockRollback()
    {
        var monotonicTicks = 100L;
        var utcTicks = 1_000L;
        using var root = new ServiceCollection().BuildServiceProvider();
        using var service = new LancacheMetricsService(
            root,
            NullLogger<LancacheMetricsService>.Instance,
            new ConfigurationBuilder().Build(),
            () => monotonicTicks,
            EmptyActivity);
        var requests = new CountingServices();

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWorkAsync(service, requests));
        Assert.Equal(1, requests.Count);
        Assert.Equal(100L, ReadTick(service, "_lastRefreshTicks"));
        Assert.Equal(0L, ReadTick(service, "_lastScrapeTicks"));

        monotonicTicks = 110;
        await InvokeWorkAsync(service, requests);
        Assert.Equal(1, requests.Count);
        Assert.Equal(100L, ReadTick(service, "_lastRefreshTicks"));

        utcTicks = 900;
        monotonicTicks = 120;
        service.RecordScrape();
        monotonicTicks = 130;
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWorkAsync(service, requests));
        Assert.Equal(2, requests.Count);
        Assert.Equal(120L, ReadTick(service, "_lastScrapeTicks"));
        Assert.Equal(130L, ReadTick(service, "_lastRefreshTicks"));

        monotonicTicks = 140;
        service.RecordScrape();
        monotonicTicks = 150;
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWorkAsync(service, requests));
        Assert.Equal(3, requests.Count);
        Assert.Equal(140L, ReadTick(service, "_lastScrapeTicks"));
        Assert.Equal(150L, ReadTick(service, "_lastRefreshTicks"));

        using var control = new LancacheMetricsService(
            root,
            NullLogger<LancacheMetricsService>.Instance,
            new ConfigurationBuilder().Build(),
            () => utcTicks,
            EmptyActivity);
        var controlRequests = new CountingServices();
        utcTicks = 1_000;
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWorkAsync(control, controlRequests));
        Assert.Equal(1_000L, ReadTick(control, "_lastRefreshTicks"));

        utcTicks = 900;
        control.RecordScrape();
        utcTicks = 800;
        await InvokeWorkAsync(control, controlRequests);
        Assert.Equal(1, controlRequests.Count);
        Assert.Equal(900L, ReadTick(control, "_lastScrapeTicks"));
        Assert.Equal(1_000L, ReadTick(control, "_lastRefreshTicks"));
    }

    [Fact]
    public void SourceKeepsTheScrapeSignalAtTheGaugeAndJitAfterDatabaseCreation()
    {
        var root = EndpointAuthorizationHost.FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "Api",
            "LancacheManager",
            "Core",
            "Services",
            "Dashboard",
            "LancacheMetricsService.cs"));
        var middleware = File.ReadAllText(Path.Combine(
            root,
            "Api",
            "LancacheManager",
            "Middleware",
            "MetricsAuthenticationMiddleware.cs"));
        var postgres = File.ReadAllText(Path.Combine(root, "postgresql.conf"));
        var setup = File.ReadAllText(Path.Combine(root, "scripts", "postgres-setup.sh"));

        var execute = service.IndexOf("protected override async Task ExecuteWorkAsync", StringComparison.Ordinal);
        var gate = service.IndexOf("ShouldRefresh(", execute, StringComparison.Ordinal);
        var update = service.IndexOf("UpdateMetricsAsync(", execute, StringComparison.Ordinal);
        Assert.True(execute >= 0);
        Assert.True(gate > execute);
        Assert.True(update > gate);
        Assert.DoesNotContain("RecordScrape", middleware, StringComparison.Ordinal);
        Assert.Contains("jit = off", postgres, StringComparison.Ordinal);
        Assert.Contains("ALTER SYSTEM SET jit = off;", setup, StringComparison.Ordinal);
        Assert.True(
            setup.IndexOf("ALTER SYSTEM SET jit = off;", StringComparison.Ordinal)
            > setup.IndexOf("CREATE DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void CurrentGaugesUseOneTypedSnapshotAndClearTogether()
    {
        var current = new DownloadSpeedSnapshot
        {
            GameSpeeds = [new GameSpeedInfo(), new GameSpeedInfo()],
            ClientSpeeds = [new ClientSpeedInfo()],
            TotalBytesPerSecond = 123.5
        };
        var reads = 0;
        using var root = new ServiceCollection().BuildServiceProvider();
        using var service = new LancacheMetricsService(
            root,
            NullLogger<LancacheMetricsService>.Instance,
            new ConfigurationBuilder().Build(),
            Stopwatch.GetTimestamp,
            () =>
            {
                reads++;
                return current;
            });
        var meter = ReadField<Meter>(service, "_meter");
        var activeDownloads = -1;
        var activeClients = -1;
        var throughput = -1d;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (!ReferenceEquals(instrument.Meter, meter))
            {
                return;
            }

            if (instrument.Name is
                "lancache_active_downloads" or
                "lancache_active_clients" or
                "lancache_throughput_bytes_per_second")
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "lancache_active_downloads")
            {
                activeDownloads = measurement;
            }
            else if (instrument.Name == "lancache_active_clients")
            {
                activeClients = measurement;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "lancache_throughput_bytes_per_second")
            {
                throughput = measurement;
            }
        });
        listener.Start();

        void Collect(int expectedDownloads, int expectedClients, double expectedThroughput)
        {
            activeDownloads = -1;
            activeClients = -1;
            throughput = -1;
            listener.RecordObservableInstruments();
            Assert.Equal(expectedDownloads, activeDownloads);
            Assert.Equal(expectedClients, activeClients);
            Assert.Equal(expectedThroughput, throughput);
        }

        Collect(2, 1, 123.5);

        current = new DownloadSpeedSnapshot
        {
            GameSpeeds = [new GameSpeedInfo(), new GameSpeedInfo()],
            ClientSpeeds = [new ClientSpeedInfo()],
            TotalBytesPerSecond = 0
        };
        Collect(2, 1, 0);

        current = new DownloadSpeedSnapshot();
        Collect(0, 0, 0);

        Assert.Equal(9, reads);
    }

    [Fact]
    public void CurrentGaugesAgeAtStoredMeasurementAndActivityBoundaries()
    {
        var startedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var measuredUntilUtc = startedAt.AddSeconds(2).UtcDateTime;
        var activeUntilUtc = startedAt.AddSeconds(5).UtcDateTime;
        var clock = new MutableClock(startedAt.AddSeconds(1));
        var snapshot = new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "metrics-expiry",
            Revision = 1,
            TimestampUtc = startedAt.UtcDateTime,
            IsAvailable = true,
            WindowSeconds = 2,
            TotalBytesPerSecond = 123.5,
            EntriesInWindow = 4,
            GameSpeeds =
            [
                new GameSpeedInfo
                {
                    Key = "steam|10.0.0.5|metrics",
                    Service = "steam",
                    ClientIp = "10.0.0.5",
                    BytesPerSecond = 123.5,
                    TotalBytes = 247,
                    RequestCount = 4,
                    FirstSeenUtc = startedAt.UtcDateTime,
                    LastSeenUtc = startedAt.UtcDateTime,
                    ActiveUntilUtc = activeUntilUtc,
                    Sources =
                    [
                        new DownloadSource
                        {
                            Datasources = ["test"],
                            FirstSeenUtc = startedAt.UtcDateTime,
                            LastSeenUtc = startedAt.UtcDateTime,
                            MeasuredUntilUtc = measuredUntilUtc,
                            ActiveUntilUtc = activeUntilUtc,
                            BytesPerSecond = 123.5,
                            TotalBytes = 247,
                            RequestCount = 4,
                        },
                    ],
                },
            ],
            ClientSpeeds =
            [
                new ClientSpeedInfo
                {
                    ClientIp = "10.0.0.5",
                    BytesPerSecond = 123.5,
                    TotalBytes = 247,
                    ActiveGames = 1,
                    ActiveUntilUtc = activeUntilUtc,
                },
            ],
        };
        var tracker = CacheScanGateHarness.TrackerWith(snapshot, [], clock);
        using var root = new ServiceCollection().BuildServiceProvider();
        using var service = new LancacheMetricsService(
            root,
            NullLogger<LancacheMetricsService>.Instance,
            new ConfigurationBuilder().Build(),
            Stopwatch.GetTimestamp,
            tracker.GetCurrentSnapshot);
        var meter = ReadField<Meter>(service, "_meter");
        var activeDownloads = -1;
        var activeClients = -1;
        var throughput = -1d;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (!ReferenceEquals(instrument.Meter, meter))
            {
                return;
            }

            if (instrument.Name is
                "lancache_active_downloads" or
                "lancache_active_clients" or
                "lancache_throughput_bytes_per_second")
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "lancache_active_downloads")
            {
                activeDownloads = measurement;
            }
            else if (instrument.Name == "lancache_active_clients")
            {
                activeClients = measurement;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "lancache_throughput_bytes_per_second")
            {
                throughput = measurement;
            }
        });
        listener.Start();

        void Collect(int expectedDownloads, int expectedClients, double expectedThroughput)
        {
            activeDownloads = -1;
            activeClients = -1;
            throughput = -1;
            listener.RecordObservableInstruments();
            Assert.Equal(expectedDownloads, activeDownloads);
            Assert.Equal(expectedClients, activeClients);
            Assert.Equal(expectedThroughput, throughput);
        }

        Collect(1, 1, 123.5);

        clock.UtcNow = startedAt.AddSeconds(2);
        Collect(1, 1, 0);

        clock.UtcNow = startedAt.AddSeconds(5);
        Collect(0, 0, 0);
    }

    private static Task InvokeWorkAsync(
        LancacheMetricsService service,
        IServiceProvider scopedServices)
    {
        var method = typeof(LancacheMetricsService).GetMethod(
            "ExecuteWorkAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IServiceProvider), typeof(CancellationToken)],
            modifiers: null)!;
        return Assert.IsAssignableFrom<Task>(method.Invoke(
            service,
            [scopedServices, CancellationToken.None]));
    }

    private static T ReadField<T>(LancacheMetricsService service, string field) =>
        Assert.IsType<T>(typeof(LancacheMetricsService)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service));

    private static long ReadTick(LancacheMetricsService service, string field) =>
        ReadField<long>(service, field);

    private static DownloadSpeedSnapshot EmptyActivity() => new();

    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CountingServices : IServiceProvider
    {
        public int Count { get; private set; }

        public object? GetService(Type serviceType)
        {
            Assert.Equal(typeof(AppDbContext), serviceType);
            Count++;
            return null;
        }
    }
}
