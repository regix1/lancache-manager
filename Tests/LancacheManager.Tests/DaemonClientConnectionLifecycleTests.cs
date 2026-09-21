using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class DaemonClientConnectionLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamCacheStatusV2SendsScopeVersionAndOneAbsoluteExpiry(bool useTcp)
    {
        var now = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);
        var expiresAtUtc = now.AddSeconds(120);
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = (DaemonClientBase)endpoint.CreateClient();
        client.CacheStatusClock = new CacheStatusClock(now);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = new[] { "cacheStatusAppIds", "cacheStatusV2" }
                }));
            var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult
                {
                    Version = 2,
                    Apps = [AppCacheStatus.Current("20")]
                }));
            await release.Task.WaitAsync(timeout.Token);
            return request;
        }, timeout.Token);

        CacheStatusResult result;
        try
        {
            result = await client.CheckCacheStatusAsync(
                [20, 20],
                [new CachedDepotInput { AppId = 10, DepotId = 100, ManifestId = 1000 }],
                [new CacheAppScope { AppId = 20, Authority = CacheAuthority.Empty }],
                expiresAtUtc,
                timeout.Token);
        }
        finally
        {
            release.TrySetResult();
        }
        var command = await server;
        var parameters = command.GetProperty("parameters");
        Assert.Equal("2", parameters.GetProperty("cacheStatusVersion").GetString());
        Assert.Equal(expiresAtUtc.ToString("O"), parameters.GetProperty("expiresAtUtc").GetString());
        using var scope = JsonDocument.Parse(parameters.GetProperty("scope").GetString()!);
        var app = Assert.Single(scope.RootElement.EnumerateArray());
        Assert.Equal(20U, app.GetProperty("appId").GetUInt32());
        Assert.Equal("Empty", app.GetProperty("authority").GetString());
        Assert.Equal(CacheOutcome.Current, Assert.Single(result.Apps).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StringCacheStatusV2UsesTypedTransportAndExactExpiry(bool useTcp)
    {
        var now = new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.Zero);
        var expiresAtUtc = now.AddSeconds(120);
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = (DaemonClientBase)endpoint.CreateClient();
        client.CacheStatusClock = new CacheStatusClock(now);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = new[] { "cacheStatusV2" }
                }));
            var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult
                {
                    Version = 2,
                    Apps =
                    [
                        AppCacheStatus.Current("Case/opaque-id"),
                        AppCacheStatus.Current("123"),
                        new AppCacheStatus
                        {
                            AppId = "missing/id",
                            Outcome = CacheOutcome.Unknown,
                            Reason = CacheReason.NoCacheEvidence,
                            IsUpToDate = false
                        }
                    ]
                }));
            await release.Task.WaitAsync(timeout.Token);
            return request;
        }, timeout.Token);

        CacheStatusResult result;
        try
        {
            result = await client.CheckCacheStatusAsync(
                [
                    new CachedAppInput { AppId = "Case/opaque-id", Revision = "revision-1" },
                    new CachedAppInput { AppId = "123", Revision = "revision-2" },
                    new CachedAppInput { AppId = "missing/id" }
                ],
                expiresAtUtc,
                timeout.Token);
        }
        finally
        {
            release.TrySetResult();
        }

        var request = await server;
        var parameters = request.GetProperty("parameters");
        Assert.Equal("2", parameters.GetProperty("cacheStatusVersion").GetString());
        Assert.Equal(expiresAtUtc.ToString("O"), parameters.GetProperty("expiresAtUtc").GetString());
        using var cachedApps = JsonDocument.Parse(parameters.GetProperty("cachedApps").GetString()!);
        Assert.Equal("Case/opaque-id", cachedApps.RootElement[0].GetProperty("appId").GetString());
        Assert.Equal(JsonValueKind.String, cachedApps.RootElement[1].GetProperty("appId").ValueKind);
        Assert.Equal("123", cachedApps.RootElement[1].GetProperty("appId").GetString());
        Assert.Equal(CacheOutcome.Current, result.Apps[0].Outcome);
        Assert.Equal(CacheOutcome.Current, result.Apps[1].Outcome);
        Assert.Equal(CacheOutcome.Unknown, result.Apps[2].Outcome);
        Assert.Null(result.Apps[2].IsUpToDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StringCacheStatusLegacyAcceptsOnlyTrueRows(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = (DaemonClientBase)endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = Array.Empty<string>()
                }));
            var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult
                {
                    Apps =
                    [
                        new AppCacheStatus { AppId = "current/id", IsUpToDate = true },
                        new AppCacheStatus { AppId = "outdated/id", IsUpToDate = false }
                    ]
                }));
            await release.Task.WaitAsync(timeout.Token);
            return request;
        }, timeout.Token);

        CacheStatusResult result;
        try
        {
            result = await client.CheckCacheStatusAsync(
                [
                    new CachedAppInput { AppId = "current/id", Revision = "one" },
                    new CachedAppInput { AppId = "outdated/id", Revision = "two" },
                    new CachedAppInput { AppId = "missing/id", Revision = "three" }
                ],
                DateTimeOffset.UtcNow.AddMinutes(2),
                timeout.Token);
        }
        finally
        {
            release.TrySetResult();
        }

        var request = await server;
        var parameters = request.GetProperty("parameters");
        Assert.Equal(["cachedApps"], parameters.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(CacheOutcome.Current, result.Apps[0].Outcome);
        Assert.All(result.Apps.Skip(1), app =>
        {
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.Equal(CacheReason.UnsupportedDaemon, app.Reason);
        });
    }

    [Fact]
    public async Task CacheStatusExpiryCancelsSendLockWait()
    {
        var now = new DateTimeOffset(2026, 9, 21, 3, 0, 0, TimeSpan.Zero);
        var clock = new CacheStatusClock(now);
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = (DaemonClientBase)endpoint.CreateClient();
        client.CacheStatusClock = clock;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var statusId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            statusId.TrySetResult(statusRequest.Id);
            await respond.Task.WaitAsync(timeout.Token);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = new[] { "cacheStatusV2" }
                }));
            using var noCommand = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadRequestAsync(stream, noCommand.Token));
        }, timeout.Token);

        var resultTask = client.CheckCacheStatusAsync(
            [new CachedAppInput { AppId = "app/id", Revision = "one" }],
            now.AddSeconds(120),
            timeout.Token);
        var firstId = await statusId.Task.WaitAsync(timeout.Token);
        var sendLock = (SemaphoreSlim)typeof(DaemonClientBase)
            .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
        await sendLock.WaitAsync(timeout.Token);
        try
        {
            respond.TrySetResult();
            while (!PendingCommandIds(client).Any(id => id != firstId))
                await Task.Delay(1, timeout.Token);
            clock.Advance(TimeSpan.FromSeconds(121));
        }
        finally
        {
            sendLock.Release();
        }

        var result = await resultTask;
        await server;
        Assert.Equal(CacheReason.DeadlineReached, Assert.Single(result.Apps).Reason);
    }

    [Fact]
    public async Task StringCacheStatusStopsAfterDeadlineBatchAndKeepsCompletedRows()
    {
        var now = new DateTimeOffset(2026, 9, 21, 4, 0, 0, TimeSpan.Zero);
        var clock = new CacheStatusClock(now);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"cache_status_{Guid.NewGuid():N}")
            .Options;
        var (daemon, session, _) = PrefillCacheChangeTests.NewDaemon(options, clock);
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = (DaemonClientBase)endpoint.CreateClient();
        session.Client = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var expiresAtUtc = now.AddSeconds(120);
        var ids = Enumerable.Range(1, 33).Select(value => $"app-{value:D2}").ToList();
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            for (var batchIndex = 0; batchIndex < 2; batchIndex++)
            {
                var statusRequest = await ReadRequestAsync(stream, timeout.Token);
                Assert.Equal("status", statusRequest.Type);
                await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                    result: JsonSerializer.SerializeToElement(new
                    {
                        isLoggedIn = true,
                        isInitialized = true,
                        features = new[] { "cacheStatusV2" }
                    }));
                var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
                var parameters = request.GetProperty("parameters");
                Assert.Equal(expiresAtUtc.ToString("O"), parameters.GetProperty("expiresAtUtc").GetString());
                var cachedApps = JsonSerializer.Deserialize<List<CachedAppInput>>(
                    parameters.GetProperty("cachedApps").GetString()!,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var rows = batchIndex == 0
                    ? cachedApps.Select(app => AppCacheStatus.Current(app.AppId)).ToList()
                    : cachedApps.Select((app, index) => index < 4
                        ? AppCacheStatus.Current(app.AppId)
                        : AppCacheStatus.Unknown(app.AppId, CacheReason.DeadlineReached)).ToList();
                await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                    timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult
                    {
                        Version = 2,
                        Apps = rows,
                        Message = batchIndex == 1 ? "deadline-message" : null
                    }));
            }

            using var noCommand = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadRequestAsync(stream, noCommand.Token));
        }, timeout.Token);

        var result = await daemon.GetStringStatusAsync(session.Id, ids, expiresAtUtc, timeout.Token);
        await server;
        Assert.All(result.Apps.Take(20), app => Assert.Equal(CacheOutcome.Current, app.Outcome));
        Assert.All(result.Apps.Skip(20), app =>
        {
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.Equal(CacheReason.DeadlineReached, app.Reason);
        });
        Assert.Equal("deadline-message", result.Message);
    }

    [Fact]
    public async Task StringCacheStatusLegacyUnknownRowsDoNotStopLaterBatches()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"cache_status_{Guid.NewGuid():N}")
            .Options;
        var (daemon, session, _) = PrefillCacheChangeTests.NewDaemon(options);
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = (DaemonClientBase)endpoint.CreateClient();
        session.Client = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ids = Enumerable.Range(1, 33).Select(value => $"app-{value:D2}").ToList();
        var commands = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            for (var batchIndex = 0; batchIndex < 3; batchIndex++)
            {
                var statusRequest = await ReadRequestAsync(stream, timeout.Token);
                Assert.Equal("status", statusRequest.Type);
                await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                    result: JsonSerializer.SerializeToElement(new
                    {
                        isLoggedIn = true,
                        isInitialized = true,
                        features = Array.Empty<string>()
                    }));
                var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
                commands++;
                var cachedApps = JsonSerializer.Deserialize<List<CachedAppInput>>(
                    request.GetProperty("parameters").GetProperty("cachedApps").GetString()!,
                    JsonSerializerOptions.Web)!;
                var rows = batchIndex == 0
                    ? [new AppCacheStatus { AppId = cachedApps[0].AppId, IsUpToDate = false }]
                    : cachedApps.Select(app => new AppCacheStatus { AppId = app.AppId, IsUpToDate = true }).ToList();
                await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                    timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult { Apps = rows }));
            }
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        CacheStatusResult result;
        try
        {
            result = await daemon.GetStringStatusAsync(
                session.Id,
                ids,
                DateTimeOffset.UtcNow.AddMinutes(2),
                timeout.Token);
        }
        finally
        {
            release.TrySetResult();
        }
        await server;

        Assert.Equal(3, commands);
        Assert.All(result.Apps.Take(16), app =>
        {
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.Equal(CacheReason.UnsupportedDaemon, app.Reason);
        });
        Assert.All(result.Apps.Skip(16), app => Assert.Equal(CacheOutcome.Current, app.Outcome));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamCacheStatusLegacyFalseIsUnknownAndAbsentSendsNoCommand(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = (DaemonClientBase)endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = new[] { "cacheStatusAppIds" }
                }));
            var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult
                {
                    Apps = [new AppCacheStatus { AppId = "20", IsUpToDate = false }]
                }));
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        CacheStatusResult result;
        try
        {
            result = await client.CheckCacheStatusAsync(
                [20],
                [],
                [new CacheAppScope { AppId = 20, Authority = CacheAuthority.Empty }],
                DateTimeOffset.UtcNow.AddMinutes(2),
                timeout.Token);
        }
        finally
        {
            release.TrySetResult();
        }
        await server;
        var app = Assert.Single(result.Apps);
        Assert.Equal(CacheOutcome.Unknown, app.Outcome);
        Assert.Equal(CacheReason.UnsupportedDaemon, app.Reason);

        using var secondEndpoint = LoopbackEndpoint.Create(useTcp);
        using var secondClient = (DaemonClientBase)secondEndpoint.CreateClient();
        var noCommandServer = Task.Run(async () =>
        {
            using var connection = await secondEndpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = new[] { "cacheStatusAppIds" }
                }));
            using var noCommand = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadRequestAsync(stream, noCommand.Token));
        }, timeout.Token);
        var absent = await secondClient.CheckCacheStatusAsync(
            [20],
            [],
            [new CacheAppScope { AppId = 20, Authority = CacheAuthority.Absent }],
            DateTimeOffset.UtcNow.AddMinutes(2),
            timeout.Token);
        await noCommandServer;
        Assert.Equal(CacheReason.UnsupportedDaemon, Assert.Single(absent.Apps).Reason);
    }

    [Fact]
    public async Task SteamCacheStatusLegacyAbsentRowsDoNotStopLaterBatches()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var context = new AppDbContext(database.Options))
        {
            context.PrefillCachedApps.AddRange(Enumerable.Range(1, 16).Select(appId => new PrefillCachedApp
            {
                Platform = PrefillPlatform.Steam,
                AppId = appId.ToString(),
                CacheRevision = "legacy",
                CachedAtUtc = DateTime.UtcNow
            }));
            await context.SaveChangesAsync();
        }

        var (daemon, session, _) = PrefillCacheChangeTests.NewDaemon(database.Options);
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        session.Client = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ids = Enumerable.Range(1, 33).Select(value => value.ToString()).ToList();
        var commands = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            for (var batchIndex = 0; batchIndex < 3; batchIndex++)
            {
                var statusRequest = await ReadRequestAsync(stream, timeout.Token);
                Assert.Equal("status", statusRequest.Type);
                await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                    result: JsonSerializer.SerializeToElement(new
                    {
                        isLoggedIn = true,
                        isInitialized = true,
                        features = new[] { "cacheStatusAppIds" }
                    }));
                if (batchIndex == 0)
                    continue;

                var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
                commands++;
                using var appIds = JsonDocument.Parse(
                    request.GetProperty("parameters").GetProperty("appIds").GetString()!);
                var rows = appIds.RootElement.EnumerateArray()
                    .Select(appId => new AppCacheStatus
                    {
                        AppId = appId.GetUInt32().ToString(),
                        IsUpToDate = true
                    }).ToList();
                await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                    timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult { Apps = rows }));
            }
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        CacheStatusResult result;
        try
        {
            result = await daemon.GetCacheStatusAsync(
                session.Id,
                ids,
                DateTimeOffset.UtcNow.AddMinutes(2),
                timeout.Token);
        }
        finally
        {
            release.TrySetResult();
        }
        await server;

        Assert.Equal(2, commands);
        Assert.All(result.Apps.Take(16), app =>
        {
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.Equal(CacheReason.UnsupportedDaemon, app.Reason);
        });
        Assert.All(result.Apps.Skip(16), app => Assert.Equal(CacheOutcome.Current, app.Outcome));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamCacheStatusDeadlineAfterStatusSendsNoCacheCommand(bool useTcp)
    {
        var now = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);
        var clock = new CacheStatusClock(now);
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = (DaemonClientBase)endpoint.CreateClient();
        client.CacheStatusClock = clock;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            clock.Advance(TimeSpan.FromSeconds(121));
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = new[] { "cacheStatusAppIds", "cacheStatusV2" }
                }));
            using var noCommand = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadRequestAsync(stream, noCommand.Token));
        }, timeout.Token);

        var result = await client.CheckCacheStatusAsync(
            [20],
            [],
            [new CacheAppScope { AppId = 20, Authority = CacheAuthority.Empty }],
            now.AddSeconds(120),
            timeout.Token);
        await server;
        Assert.Equal(CacheReason.DeadlineReached, Assert.Single(result.Apps).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamCacheStatusSendsDistinctIdsAndCompletePhysicalSnapshot(bool useTcp)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var context = new AppDbContext(database.Options))
        {
            context.PrefillCachedDepots.AddRange(
                new PrefillCachedDepot
                {
                    AppId = 10, DepotId = 100, ManifestId = 1000, TotalBytes = 1,
                    CachedAtUtc = DateTime.UtcNow
                },
                new PrefillCachedDepot
                {
                    AppId = 30, DepotId = 200, ManifestId = 2000, TotalBytes = 1,
                    CachedAtUtc = DateTime.UtcNow
                });
            await context.SaveChangesAsync();
        }

        var (daemon, session, _) = PrefillCacheChangeTests.NewDaemon(database.Options);
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        session.Client = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("status", statusRequest.Type);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = new[] { "cacheStatusAppIds" }
                }));
            var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult
                {
                    Apps = [new AppCacheStatus { AppId = "20", IsUpToDate = true }]
                }));
            await release.Task.WaitAsync(timeout.Token);
            return request;
        }, timeout.Token);

        CacheStatusResult result;
        try
        {
            result = await daemon.GetCacheStatusAsync(
                session.Id,
                ["20", "020", "invalid"],
                DateTimeOffset.UtcNow.AddMinutes(2),
                ["windows"],
                timeout.Token);
        }
        finally
        {
            release.TrySetResult();
        }
        var command = await server;
        Assert.Equal("check-cache-status", command.GetProperty("type").GetString());
        var parameters = command.GetProperty("parameters");
        Assert.Equal("windows", parameters.GetProperty("os").GetString());
        using var appIds = JsonDocument.Parse(parameters.GetProperty("appIds").GetString()!);
        Assert.Equal(20U, Assert.Single(appIds.RootElement.EnumerateArray()).GetUInt32());
        using var cachedDepots = JsonDocument.Parse(parameters.GetProperty("cachedDepots").GetString()!);
        Assert.Equal(2, cachedDepots.RootElement.GetArrayLength());
        Assert.Contains(cachedDepots.RootElement.EnumerateArray(), depot =>
            depot.GetProperty("appId").GetInt64() == 10
            && depot.GetProperty("depotId").GetInt64() == 100);
        Assert.Contains(cachedDepots.RootElement.EnumerateArray(), depot =>
            depot.GetProperty("appId").GetInt64() == 30
            && depot.GetProperty("depotId").GetInt64() == 200);
        Assert.True(Assert.Single(result.Apps, app => app.AppId == "20").IsUpToDate);
        Assert.True(Assert.Single(result.Apps, app => app.AppId == "020").IsUpToDate);
        Assert.Equal(CacheReason.InvalidAppId,
            Assert.Single(result.Apps, app => app.AppId == "invalid").Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamCacheStatusWithoutFeatureReturnsUnknownWithoutSendingCommand(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("status", statusRequest.Type);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = Array.Empty<string>()
                }));
            using var noCommand = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                await ReadRequestAsync(stream, noCommand.Token);
                return false;
            }
            catch (OperationCanceledException) when (noCommand.IsCancellationRequested)
            {
                return true;
            }
        }, timeout.Token);

        var result = await client.CheckCacheStatusAsync(
            [20],
            [new CachedDepotInput { AppId = 10, DepotId = 100, ManifestId = 1000 }],
            timeout.Token);
        var app = Assert.Single(result.Apps);
        Assert.Equal(CacheOutcome.Unknown, app.Outcome);
        Assert.Equal(CacheReason.UnsupportedDaemon, app.Reason);
        Assert.True(await server);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamCacheStatusPreservesLargeManifestIds(bool useTcp)
    {
        const ulong manifestId = ulong.MaxValue - 1;
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = new[] { "cacheStatusAppIds" }
                }));
            var request = await ReadRequestWithBodyAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.GetProperty("id").GetString()!, true, null, null,
                timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult { Apps = [] }));
            return request;
        }, timeout.Token);

        await client.CheckCacheStatusAsync(
            [20],
            [new CachedDepotInput { AppId = 10, DepotId = 100, ManifestId = manifestId }],
            timeout.Token);
        var command = await server;
        using var cachedDepots = JsonDocument.Parse(
            command.GetProperty("parameters").GetProperty("cachedDepots").GetString()!);
        var serializedManifest = Assert.Single(cachedDepots.RootElement.EnumerateArray())
            .GetProperty("manifestId");
        Assert.Equal(manifestId, serializedManifest.ValueKind == JsonValueKind.String
            ? ulong.Parse(serializedManifest.GetString()!)
            : serializedManifest.GetUInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheStatusQueryRejectsAStaleConnectionGeneration(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            using var noCommand = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                await ReadRequestAsync(stream, noCommand.Token);
                return false;
            }
            catch (OperationCanceledException) when (noCommand.IsCancellationRequested)
            {
                return true;
            }
        }, timeout.Token);
        await client.ConnectAsync(timeout.Token);
        var lifecycle = typeof(DaemonClientBase)
            .GetField("_connectionLifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
        var lifecycleType = lifecycle.GetType();
        var generation = (long)lifecycleType.GetProperty("CurrentGeneration",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(lifecycle)!;
        lifecycleType.GetMethod("MarkDisconnected",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(lifecycle, null);
        lifecycleType.GetMethod("CreateGeneration",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(lifecycle, null);
        var readResult = typeof(DaemonClientBase)
            .GetMethod("ReadResultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(CacheStatusResult));
        var query = (Task<CacheStatusResult>)readResult.Invoke(client,
            [
                "check-cache-status",
                new Dictionary<string, string>(),
                TimeSpan.FromMinutes(10),
                timeout.Token,
                generation
            ])!;

        await Assert.ThrowsAsync<DaemonCommandException>(() => query);
        Assert.True(await server);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedAppsUseWireNamesAsync(bool useTcp)
    {
        await using var database = await TestDatabase.CreateAsync();
        var cache = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        await cache.RecordCachedAppAsync(PrefillPlatform.Steam, "Case/opaque-id", "Game", 1, null, "revision-1");
        await cache.RecordCachedAppAsync(PrefillPlatform.Steam, "Legacy/id", "Legacy", 1, null);
        await cache.RecordCachedAppAsync(PrefillPlatform.Steam, "unrequested", "Other", 1, null, "revision-2");
        var (daemon, session, _) = PrefillCacheChangeTests.NewDaemon(database.Options);
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        session.Client = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("status", statusRequest.Type);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    isLoggedIn = true,
                    isInitialized = true,
                    features = Array.Empty<string>()
                }));
            using var request = JsonDocument.Parse((await ReadRequestWithBodyAsync(stream, timeout.Token)).GetRawText());
            await WriteResponseAsync(stream, request.RootElement.GetProperty("id").GetString()!, true,
                null, null, timeout.Token, result: JsonSerializer.SerializeToElement(new CacheStatusResult
                {
                    Apps = [new AppCacheStatus { AppId = "Case/opaque-id", IsUpToDate = true }]
                }));
            await release.Task.WaitAsync(timeout.Token);
            return request.RootElement.Clone();
        }, timeout.Token);
        var requested = new List<string> { "case/OPAQUE-id", "CASE/opaque-ID", "legacy/ID", "not-cached" };
        CacheStatusResult status;
        try
        {
            var method = typeof(PrefillDaemonServiceBase).GetMethod("GetStringAppCacheStatusAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            status = await (Task<CacheStatusResult>)method.Invoke(daemon,
                [session.Id, requested, DateTimeOffset.UtcNow.AddMinutes(2), timeout.Token])!;
        }
        finally
        {
            release.TrySetResult();
        }
        var command = await server;
        Assert.Equal("check-cache-status", command.GetProperty("type").GetString());
        var json = command.GetProperty("parameters").GetProperty("cachedApps").GetString()!;
        using var entries = JsonDocument.Parse(json);
        Assert.Equal(3, entries.RootElement.GetArrayLength());
        foreach (var entry in entries.RootElement.EnumerateArray())
        {
            var names = entry.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(entry.TryGetProperty("revision", out _)
                ? ["appId", "revision"]
                : ["appId"], names);
        }
        var apps = JsonSerializer.Deserialize<List<CachedAppInput>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        })!;
        Assert.Equal("revision-1", Assert.Single(apps, app => app.AppId == "case/OPAQUE-id").Revision);
        Assert.Null(Assert.Single(apps, app => app.AppId == "legacy/ID").Revision);
        Assert.Null(Assert.Single(apps, app => app.AppId == "not-cached").Revision);
        var (verified, outdated, unknown) = status.ResolveAppIds(requested);
        Assert.Equal(["case/OPAQUE-id"], verified);
        Assert.Empty(outdated);
        Assert.Equal(["legacy/ID", "not-cached"], unknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedAppsReachPrefillAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runId = Guid.NewGuid();
        var instanceId = Guid.NewGuid().ToString();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var status = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, status.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(RunClient.Capabilities(instanceId), JsonSerializerOptions.Web));
            var length = new byte[4];
            await ReadExactlyAsync(stream, length, timeout.Token);
            var body = new byte[BitConverter.ToInt32(length)];
            await ReadExactlyAsync(stream, body, timeout.Token);
            using var request = JsonDocument.Parse(body);
            await WriteResponseAsync(stream, request.RootElement.GetProperty("id").GetString()!, true,
                null, null, timeout.Token, result: JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    runId,
                    daemonInstanceId = instanceId,
                    state = "started"
                }));
            await release.Task.WaitAsync(timeout.Token);
            return request.RootElement.Clone();
        }, timeout.Token);
        try
        {
            Assert.True((await client.GetStatusAsync(timeout.Token))?.SupportsConcurrentPrefill);
            Assert.True((await client.PrefillAsync(runId, instanceId, new DaemonRunOptions
            {
                AppIds = ["Case/opaque-id", "Legacy/id"],
                MaxConcurrency = 1,
                CachedApps = [new CachedAppInput { AppId = "Case/opaque-id", Revision = "revision-1" },
                    new CachedAppInput { AppId = "Legacy/id" }]
            }, cancellationToken: timeout.Token)).Success);
        }
        finally
        {
            release.TrySetResult();
        }
        var command = await server;
        Assert.Equal("prefill", command.GetProperty("type").GetString());
        var json = command.GetProperty("parameters").GetProperty("cachedApps").GetString()!;
        using var entries = JsonDocument.Parse(json);
        Assert.Equal(2, entries.RootElement.GetArrayLength());
        foreach (var entry in entries.RootElement.EnumerateArray())
        {
            Assert.True(entry.TryGetProperty("appId", out _));
            Assert.False(entry.TryGetProperty("AppId", out _));
            Assert.False(entry.TryGetProperty("Revision", out _));
        }
        var apps = JsonSerializer.Deserialize<List<CachedAppInput>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        })!;
        Assert.Equal("revision-1", Assert.Single(apps, app => app.AppId == "Case/opaque-id").Revision);
        Assert.Null(Assert.Single(apps, app => app.AppId == "Legacy/id").Revision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmptyCacheSnapshotIsSentOverTheTransportAsync(bool useTcp, bool concurrent)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runId = Guid.NewGuid();
        var instanceId = Guid.NewGuid().ToString();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            if (concurrent)
            {
                var status = await ReadRequestAsync(stream, timeout.Token);
                Assert.Equal("status", status.Type);
                await WriteResponseAsync(stream, status.Id, true, null, null, timeout.Token,
                    result: JsonSerializer.SerializeToElement(RunClient.Capabilities(instanceId),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            var length = new byte[4];
            await ReadExactlyAsync(stream, length, timeout.Token);
            var body = new byte[BitConverter.ToInt32(length)];
            await ReadExactlyAsync(stream, body, timeout.Token);
            using var request = JsonDocument.Parse(body);
            Assert.Equal("[]", request.RootElement.GetProperty("parameters").GetProperty("cachedDepots").GetString());
            await WriteResponseAsync(stream, request.RootElement.GetProperty("id").GetString()!, true,
                null, null, timeout.Token, result: JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    runId,
                    daemonInstanceId = instanceId,
                    state = "started"
                }));
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);
        try
        {
            if (concurrent)
            {
                Assert.True((await client.GetStatusAsync(timeout.Token))?.SupportsConcurrentPrefill);
                Assert.True((await client.PrefillAsync(runId, instanceId, new DaemonRunOptions
                {
                    AppIds = ["10"],
                    Selection = "selected",
                    MaxConcurrency = 1
                }, [], timeout.Token)).Success);
            }
            else
            {
                Assert.True((await client.PrefillAsync(cachedDepots: [], cancellationToken: timeout.Token)).Success);
            }
        }
        finally { release.TrySetResult(); }
        await server;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SavedLogin_UsesPlatformChallengeAndCredentialCommandsAsync(
        bool useTcp,
        bool epic)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        byte[] publicKey = [4, .. parameters.Q.X!, .. parameters.Q.Y!];
        var challenge = JsonSerializer.SerializeToElement(new
        {
            challengeId = "saved-login-challenge",
            credentialType = "refreshToken",
            serverPublicKey = Convert.ToBase64String(publicKey)
        });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = 0;
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var start = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal(epic ? "get-auto-login-challenge" : "provide-auto-login", start.Type);
            await WriteResponseAsync(stream, start.Id, true, null, null, timeout.Token, result: challenge);
            var credential = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("provide-auto-login", credential.Type);
            await WriteResponseAsync(stream, credential.Id, true, null, null, timeout.Token);
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);
        try
        {
            var accepted = epic
                ? await client.ProvideEpicAutoLoginWithDispatchAsync("session", "test-refresh-token", () => dispatched++, timeout.Token)
                : await client.ProvideXboxAutoLoginWithDispatchAsync("session", "test-refresh-token", () => dispatched++, timeout.Token);
            Assert.True(accepted);
            Assert.Equal(1, dispatched);
        }
        finally
        {
            release.TrySetResult();
        }
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteClose_FailsPendingCallAndReconnectsCleanlyAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var closeServer = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            await ReadRequestAsync(stream, timeout.Token);
        }, timeout.Token);

        await Assert.ThrowsAsync<IOException>(() => client.SendCommandAsync(
            "blocked",
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: timeout.Token));
        await closeServer;
        await WaitUntilAsync(() => !IsConnected(client), timeout.Token);

        var responseWritten = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closeReconnectedServer = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var responseServer = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.Id, success: true, request.Type, null, timeout.Token);
            responseWritten.TrySetResult();
            await closeReconnectedServer.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        try
        {
            var response = await client.SendCommandAsync(
                "after-reconnect",
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: timeout.Token);
            await responseWritten.Task.WaitAsync(timeout.Token);

            Assert.True(response.Success);
            Assert.Equal("after-reconnect", response.Message);
            Assert.True(IsConnected(client));
        }
        finally
        {
            closeReconnectedServer.TrySetResult();
        }

        await responseServer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCalls_CorrelateResponsesThatArriveOutOfOrderAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var first = await ReadRequestAsync(stream, timeout.Token);
            var second = await ReadRequestAsync(stream, timeout.Token);

            await WriteResponseAsync(stream, second.Id, success: true, second.Type, null, timeout.Token);
            await WriteResponseAsync(stream, first.Id, success: true, first.Type, null, timeout.Token);
        }, timeout.Token);

        var firstCall = client.SendCommandAsync("first", timeout: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        var secondCall = client.SendCommandAsync("second", timeout: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        var responses = await Task.WhenAll(firstCall, secondCall);

        Assert.Equal("first", responses[0].Message);
        Assert.Equal("second", responses[1].Message);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelPrefillAsync_RejectedResponseThrowsAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("cancel-prefill", request.Type);
            await WriteResponseAsync(stream, request.Id, success: false, null, "still running", timeout.Token);
        }, timeout.Token);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CancelPrefillAsync(timeout.Token));

        Assert.Contains("still running", exception.Message, StringComparison.Ordinal);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefillAsync_LoginRejectionPreservesTypedResult(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("prefill", request.Type);
            await WriteResponseAsync(stream, request.Id, false, null, "Login expired", timeout.Token, requiresLogin: true);
        }, timeout.Token);
        var result = await client.PrefillAsync(cancellationToken: timeout.Token);
        Assert.False(result.Success);
        Assert.True(result.RequiresLogin);
        Assert.Equal("Steam is no longer signed in. Sign in again, then retry the prefill.", result.ErrorMessage);
        Assert.Equal("errors.steam.signInLost", result.StageKey);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefillAsync_UsesRunIdForCommandAndAcceptsLegacyAck(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runId = Guid.NewGuid();
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal(runId.ToString(), request.Id);
            await WriteResponseAsync(stream, request.Id, true, "Prefill started", null, timeout.Token);
        }, timeout.Token);

        var result = await client.PrefillAsync(cancellationToken: timeout.Token, runId: runId);
        Assert.True(result.Success);
        await server;
    }

    [Theory]
    [InlineData("get-owned-games", "auth-lost", "errors.steam.signInLost", true)]
    [InlineData("get-selected-apps-status", "auth-lost", "errors.steam.signInLost", true)]
    [InlineData("check-cache-status", "auth-lost", "errors.steam.signInLost", true)]
    [InlineData("get-owned-games", "game-details-unavailable", "errors.steam.gameDetailsUnavailable", false)]
    [InlineData("get-selected-apps-status", "game-details-unavailable", "errors.steam.gameDetailsUnavailable", false)]
    [InlineData("check-cache-status", "game-details-unavailable", "errors.steam.gameDetailsUnavailable", false)]
    [InlineData("get-owned-games", null, "errors.prefill.requestFailed", false)]
    [InlineData("get-selected-apps-status", null, "errors.prefill.requestFailed", false)]
    [InlineData("check-cache-status", null, "errors.prefill.requestFailed", false)]
    public async Task Queries_ClassifyFailureAndHideRemoteException(
        string command, string? errorCode, string stageKey, bool requiresLogin)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            if (command == "check-cache-status")
            {
                Assert.Equal("status", request.Type);
                await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                    result: JsonSerializer.SerializeToElement(new
                    {
                        isLoggedIn = true,
                        isInitialized = true,
                        features = new[] { "cacheStatusAppIds" }
                    }));
                request = await ReadRequestAsync(stream, timeout.Token);
            }
            Assert.Equal(command, request.Type);
            await WriteResponseAsync(stream, request.Id, false, null, "Remote.JobFailure: stack details",
                timeout.Token, requiresLogin, errorCode);
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        try
        {
            if (command == "check-cache-status" && !requiresLogin)
            {
                var result = await QueryCacheStatusAsync(client, timeout.Token);
                var app = Assert.Single(result.Apps);
                Assert.Equal(CacheOutcome.Unknown, app.Outcome);
                Assert.Equal(CacheReason.StatusUnavailable, app.Reason);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<DaemonCommandException>(
                    () => QueryAsync(client, command, timeout.Token));
                Assert.Equal(stageKey, failure.StageKey);
                Assert.Equal(requiresLogin, failure.RequiresLogin);
                Assert.DoesNotContain("Remote.JobFailure", failure.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            release.TrySetResult();
        }
        await server;
    }

    [Theory]
    [InlineData("get-owned-games", null)]
    [InlineData("get-selected-apps-status", null)]
    [InlineData("check-cache-status", null)]
    [InlineData("get-owned-games", "{}")]
    [InlineData("get-selected-apps-status", "{}")]
    [InlineData("check-cache-status", "{\"apps\":null}")]
    public async Task Queries_RejectMissingRequiredBody(string command, string? body)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            if (command == "check-cache-status")
            {
                Assert.Equal("status", request.Type);
                await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                    result: JsonSerializer.SerializeToElement(new
                    {
                        isLoggedIn = true,
                        isInitialized = true,
                        features = new[] { "cacheStatusAppIds" }
                    }));
                request = await ReadRequestAsync(stream, timeout.Token);
            }
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                result: body is null ? null : JsonSerializer.Deserialize<JsonElement>(body));
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        try
        {
            if (command == "check-cache-status")
            {
                var result = await QueryCacheStatusAsync(client, timeout.Token);
                var app = Assert.Single(result.Apps);
                Assert.Equal(CacheOutcome.Unknown, app.Outcome);
                Assert.Equal(CacheReason.UnsupportedDaemon, app.Reason);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<DaemonCommandException>(
                    () => QueryAsync(client, command, timeout.Token));
                Assert.Equal("errors.prefill.requestFailed", failure.StageKey);
            }
        }
        finally
        {
            release.TrySetResult();
        }
        await server;
    }

    [Theory]
    [InlineData("get-owned-games", "[]")]
    [InlineData("get-selected-apps-status", "{\"apps\":[]}")]
    [InlineData("check-cache-status", "{\"apps\":[]}")]
    public async Task Queries_AcceptExplicitEmptyResults(string command, string body)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            if (command == "check-cache-status")
            {
                Assert.Equal("status", request.Type);
                await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                    result: JsonSerializer.SerializeToElement(new
                    {
                        isLoggedIn = true,
                        isInitialized = true,
                        features = new[] { "cacheStatusAppIds" }
                    }));
                request = await ReadRequestAsync(stream, timeout.Token);
            }
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                result: JsonSerializer.Deserialize<JsonElement>(body));
        }, timeout.Token);

        await QueryAsync(client, command, timeout.Token);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedGamesAcceptSteamLibrary(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var games = Enumerable.Range(0, 208).Select(index => new
        {
            appId = (uint)(251570 + index),
            name = $"Game {index} \"Edition\" ®",
            minutesPlayedLast2Weeks = index,
            releaseDate = index % 2 == 0 ? new DateOnly(2024, 7, 25) : (DateOnly?)null
        }).ToList();
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("get-owned-games", request.Type);
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(games));
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        try
        {
            var result = await client.GetOwnedGamesAsync(timeout.Token);
            Assert.Equal(games.Count, result.Count);
            for (var index = 0; index < games.Count; index++)
            {
                Assert.Equal(games[index].appId.ToString(System.Globalization.CultureInfo.InvariantCulture), result[index].AppId);
                Assert.Equal(games[index].name, result[index].Name);
            }
        }
        finally
        {
            release.TrySetResult();
        }
        await server;
    }

    private static Task QueryAsync(IDaemonClient client, string command, CancellationToken cancellationToken)
        => command switch
        {
            "get-owned-games" => client.GetOwnedGamesAsync(cancellationToken),
            "get-selected-apps-status" => client.GetSelectedAppsStatusAsync(cancellationToken: cancellationToken),
            "check-cache-status" => client.CheckCacheStatusAsync(
                [10],
                [new CachedDepotInput { AppId = 10, DepotId = 11, ManifestId = 12 }], cancellationToken),
            _ => throw new ArgumentException("Unknown command", nameof(command))
        };

    private static Task<CacheStatusResult> QueryCacheStatusAsync(
        IDaemonClient client,
        CancellationToken cancellationToken)
        => client.CheckCacheStatusAsync(
            [10],
            [new CachedDepotInput { AppId = 10, DepotId = 11, ManifestId = 12 }],
            cancellationToken);

    [Theory]
    [InlineData("get-owned-games")]
    [InlineData("get-selected-apps-status")]
    [InlineData("check-cache-status")]
    public async Task Queries_CallerCancellationIsNotFailure(string command)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QueryAsync(client, command, cancellation.Token));
    }

    [Theory]
    [InlineData("get-owned-games")]
    [InlineData("get-selected-apps-status")]
    [InlineData("check-cache-status")]
    public async Task Queries_TransportLossIsSafeFailure(string command)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            await ReadRequestAsync(stream, timeout.Token);
        }, timeout.Token);
        if (command == "check-cache-status")
        {
            var result = await QueryCacheStatusAsync(client, timeout.Token);
            var app = Assert.Single(result.Apps);
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.True(app.Reason is CacheReason.Disconnected
                or CacheReason.ConnectionChanged
                or CacheReason.StatusUnavailable);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<DaemonCommandException>(
                () => QueryAsync(client, command, timeout.Token));
            Assert.Equal("errors.prefill.requestFailed", failure.StageKey);
            Assert.Equal("The prefill daemon could not complete the request. Try again.", failure.Message);
        }
        await server;
    }

    [Fact]
    public void ProgressFields_AcceptOldAndCurrentDaemonFrames()
    {
        var old = JsonSerializer.Deserialize<SocketPrefillProgress>("{\"state\":\"completed\"}")!;
        Assert.Null(old.OperationId);
        Assert.Null(old.ErrorCode);
        Assert.Null(old.RequiresLogin);

        var runId = Guid.NewGuid().ToString();
        var current = JsonSerializer.Deserialize<SocketPrefillProgress>(JsonSerializer.Serialize(new
        {
            state = "error",
            operationId = runId,
            errorCode = "auth-lost",
            requiresLogin = true,
            currentAppId = 0
        }))!;
        Assert.Equal(runId, current.OperationId);
        Assert.Equal("auth-lost", current.ErrorCode);
        Assert.True(current.RequiresLogin);
        Assert.Equal("0", current.CurrentAppId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentProtocolAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var id = Guid.NewGuid();
        var instance = Guid.NewGuid().ToString();
        var options = new DaemonRunOptions { AppIds = ["10"], MaxConcurrency = 3 };
        var snapshot = new DaemonRunSnapshot
        {
            OperationId = id.ToString(),
            DaemonInstanceId = instance,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Sequence = 1
        };
        var page = new DaemonOperationPage(snapshot, options, true, 1,
            [new DaemonRunItem { AppId = "10", Depots = [new DepotManifestProgressInfo
                { DepotId = 20, ManifestId = 30, TotalBytes = 40 }] }], null);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("status", statusRequest.Type);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(RunClient.Capabilities(instance), json));
            var start = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("prefill", start.Type);
            Assert.Equal(id.ToString(), start.Id);
            await WriteResponseAsync(stream, start.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new PrefillResult
                { Success = true, RunId = id, DaemonInstanceId = instance, State = "started" }, json));
            var query = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("get-operation", query.Type);
            await WriteResponseAsync(stream, query.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(page, json));
            var cancel = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("cancel-prefill", cancel.Type);
            await WriteResponseAsync(stream, cancel.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(snapshot with { State = "cancelling" }, json));
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);
        try
        {
            Assert.True((await client.GetStatusAsync(timeout.Token))!.SupportsConcurrentPrefill);
            Assert.Equal(id, (await client.PrefillAsync(id, instance, options, cancellationToken: timeout.Token)).RunId);
            var result = await client.GetOperationAsync(id, instance, cancellationToken: timeout.Token);
            Assert.Equal(30UL, Assert.Single(Assert.Single(result.Items).Depots!).ManifestId);
            Assert.Equal("cancelling", (await client.CancelPrefillAsync(id, instance, timeout.Token)).State);
        }
        finally { release.TrySetResult(); }
        await server;
    }

    [Theory]
    [InlineData("auth-lost", "errors.steam.signInLost")]
    [InlineData("game-details-unavailable", "errors.steam.gameDetailsUnavailable")]
    [InlineData(null, "errors.prefill.requestFailed")]
    public async Task PrefillAsync_FailedResultUsesSafeClassification(string? code, string stageKey)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    success = false,
                    errorMessage = "Remote.JobFailure: stack details",
                    errorCode = code,
                    requiresLogin = code == "auth-lost"
                }));
        }, timeout.Token);

        var result = await client.PrefillAsync(cancellationToken: timeout.Token);
        Assert.False(result.Success);
        Assert.Equal(stageKey, result.StageKey);
        Assert.Equal(code == "auth-lost", result.RequiresLogin);
        Assert.DoesNotContain("Remote.JobFailure", result.ErrorMessage!, StringComparison.Ordinal);
        await server;
    }

    [Fact]
    public async Task PrefillDispatchIsConfirmedBeforeItsResponse()
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            read.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token);
        }, timeout.Token);
        var pending = client.PrefillAsync(cancellationToken: timeout.Token,
            onCommandDispatched: () => dispatched.TrySetResult());
        try
        {
            await Task.WhenAll(read.Task, dispatched.Task).WaitAsync(timeout.Token);
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.True((await pending).Success);
        await server;
    }

    [Fact]
    public async Task PendingProgressHandlerDoesNotBlockCommandResponses()
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnProgressUpdate += async _ =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            finished.SetResult();
        };
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var first = await ReadRequestAsync(stream, timeout.Token);
            await WriteFrameAsync(stream, "{\"type\":\"progress\",\"data\":{\"state\":\"downloading\"}}", timeout.Token);
            await WriteResponseAsync(stream, first.Id, true, null, null, timeout.Token);
            var second = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, second.Id, true, null, null, timeout.Token);
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);
        try
        {
            Assert.True((await client.SendCommandAsync("first", cancellationToken: timeout.Token)).Success);
            await entered.Task.WaitAsync(timeout.Token);
            Assert.True((await client.SendCommandAsync("second", cancellationToken: timeout.Token)).Success);
            Assert.False(finished.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await finished.Task.WaitAsync(timeout.Token);
        await server;
    }

    private static bool IsConnected(IDaemonClient client)
        => client switch
        {
            // Both real transports report connectivity through their shared base; anything else here
            // is a fake with no transport to be connected to.
            DaemonClientBase transportClient => transportClient.IsConnected,
            _ => false
        };

    private static async Task<DaemonRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        Assert.InRange(length, 1, 10 * 1024 * 1024);

        var requestBuffer = new byte[length];
        await ReadExactlyAsync(stream, requestBuffer, cancellationToken);
        using var request = JsonDocument.Parse(requestBuffer);
        return new DaemonRequest(
            request.RootElement.GetProperty("id").GetString()!,
            request.RootElement.GetProperty("type").GetString()!);
    }

    private static async Task<JsonElement> ReadRequestWithBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        var requestBuffer = new byte[BitConverter.ToInt32(lengthBuffer, 0)];
        await ReadExactlyAsync(stream, requestBuffer, cancellationToken);
        using var request = JsonDocument.Parse(requestBuffer);
        return request.RootElement.Clone();
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        string requestId,
        bool success,
        string? message,
        string? error,
        CancellationToken cancellationToken,
        bool requiresLogin = false,
        string? errorCode = null,
        JsonElement? result = null)
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            id = requestId,
            success,
            message,
            error,
            errorCode,
            data = result,
            requiresLogin,
            completedAt = DateTime.UtcNow
        });
        await WriteFrameAsync(stream, responseJson, cancellationToken);
    }

    private static async Task WriteFrameAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        var responseBytes = Encoding.UTF8.GetBytes(json);

        await stream.WriteAsync(BitConverter.GetBytes(responseBytes.Length), cancellationToken);
        await stream.WriteAsync(responseBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before a complete frame was received.");
            }

            totalRead += read;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static List<string> PendingCommandIds(DaemonClientBase client)
    {
        var commands = (System.Collections.IEnumerable)typeof(DaemonClientBase)
            .GetField("_pendingCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
        return commands.Cast<object>()
            .Select(command => (string)command.GetType().GetProperty("Key")!.GetValue(command)!)
            .ToList();
    }

    private sealed record DaemonRequest(string Id, string Type);

    private sealed class LoopbackEndpoint : IDisposable
    {
        private readonly Socket _listener;
        private readonly string? _socketPath;

        private LoopbackEndpoint(Socket listener, string? socketPath)
        {
            _listener = listener;
            _socketPath = socketPath;
        }

        public static LoopbackEndpoint Create(bool useTcp)
        {
            if (useTcp)
            {
                var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(4);
                return new LoopbackEndpoint(listener, null);
            }

            var socketPath = Path.Combine(Path.GetTempPath(), $"lcm_{Guid.NewGuid():N}.sock");
            var unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            unixListener.Bind(new UnixDomainSocketEndPoint(socketPath));
            unixListener.Listen(4);
            return new LoopbackEndpoint(unixListener, socketPath);
        }

        public IDaemonClient CreateClient()
        {
            if (_socketPath != null)
            {
                return new SocketDaemonClient(
                    _socketPath,
                    sharedSecret: null,
                    logger: NullLogger<SocketDaemonClient>.Instance);
            }

            var endpoint = (IPEndPoint)_listener.LocalEndPoint!;
            return new TcpDaemonClient(
                IPAddress.Loopback.ToString(),
                endpoint.Port,
                sharedSecret: null,
                logger: NullLogger<TcpDaemonClient>.Instance);
        }

        public ValueTask<Socket> AcceptAsync(CancellationToken cancellationToken)
            => _listener.AcceptAsync(cancellationToken);

        public void Dispose()
        {
            _listener.Dispose();
            if (_socketPath != null)
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
