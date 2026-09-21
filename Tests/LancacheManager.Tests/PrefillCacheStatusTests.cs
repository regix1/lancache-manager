using System.Text.Json;
using LancacheManager.Controllers.Base;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class PrefillCacheStatusTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ManagerAndDaemonWireShapesUseExactTypesAndPascalCaseEnums()
    {
        const ulong manifestId = ulong.MaxValue - 1;
        var managerJson = JsonSerializer.Serialize(new
        {
            scope = new[] { new CacheAppScope { AppId = 42, Authority = CacheAuthority.Snapshot } },
            cachedDepots = new[]
            {
                new CachedDepotInput { AppId = 42, DepotId = 84, ManifestId = manifestId }
            },
            cachedApps = new[] { new CachedAppInput { AppId = "Case/opaque-id", Revision = "revision-1" } }
        }, Json);

        using (var manager = JsonDocument.Parse(managerJson))
        {
            var scope = Assert.Single(manager.RootElement.GetProperty("scope").EnumerateArray());
            Assert.Equal(JsonValueKind.Number, scope.GetProperty("appId").ValueKind);
            Assert.Equal(42U, scope.GetProperty("appId").GetUInt32());
            Assert.Equal("Snapshot", scope.GetProperty("authority").GetString());
            Assert.Equal(JsonValueKind.String,
                manager.RootElement.GetProperty("cachedApps")[0].GetProperty("appId").ValueKind);
            Assert.Equal("Case/opaque-id",
                manager.RootElement.GetProperty("cachedApps")[0].GetProperty("appId").GetString());
            Assert.Equal(manifestId,
                manager.RootElement.GetProperty("cachedDepots")[0].GetProperty("manifestId").GetUInt64());
        }

        const string daemonJson =
            "{\"version\":2,\"apps\":[{\"appId\":\"Case/opaque-id\",\"isUpToDate\":false,\"outcome\":\"Unknown\",\"reason\":\"NoCacheEvidence\"}]}";
        var daemon = JsonSerializer.Deserialize<CacheStatusResult>(daemonJson, Json)!;
        var normalized = daemon.Normalize(["Case/opaque-id"]);
        var app = Assert.Single(normalized.Apps);
        Assert.Equal(CacheOutcome.Unknown, app.Outcome);
        Assert.Equal(CacheReason.NoCacheEvidence, app.Reason);
        Assert.Null(app.IsUpToDate);

        var returnedJson = JsonSerializer.Serialize(normalized, Json);
        Assert.Contains("\"outcome\":\"Unknown\"", returnedJson, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"NoCacheEvidence\"", returnedJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"appId\":\"one\",\"outcome\":\"Current\"}")]
    [InlineData("{\"appId\":\"one\",\"isUpToDate\":false,\"outcome\":\"Current\"}")]
    [InlineData("{\"appId\":\"one\",\"isUpToDate\":true,\"outcome\":\"Outdated\"}")]
    [InlineData("{\"appId\":\"one\",\"isUpToDate\":true,\"outcome\":\"Unknown\",\"reason\":\"NoCacheEvidence\"}")]
    [InlineData("{\"appId\":\"one\",\"isUpToDate\":false,\"outcome\":\"Unknown\"}")]
    public void TypedRowsRejectConflictingOrMissingFields(string rowJson)
    {
        var status = JsonSerializer.Deserialize<CacheStatusResult>(
            $"{{\"version\":2,\"apps\":[{rowJson}]}}",
            Json)!;

        var app = Assert.Single(status.Normalize(["one"]).Apps);
        Assert.Equal(CacheOutcome.Unknown, app.Outcome);
        Assert.Equal(CacheReason.InvalidResult, app.Reason);
        Assert.Null(app.IsUpToDate);
    }

    [Fact]
    public void TypedRowsRejectInvalidEnumDuplicateAndExtraRows()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CacheStatusResult>(
            "{\"version\":2,\"apps\":[{\"appId\":\"one\",\"isUpToDate\":true,\"outcome\":\"NotAnOutcome\"}]}",
            Json));

        var duplicate = new CacheStatusResult
        {
            Version = 2,
            Apps = [AppCacheStatus.Current("one"), AppCacheStatus.Current("one")]
        };
        Assert.All(duplicate.Normalize(["one"]).Apps,
            app => Assert.Equal(CacheReason.InvalidResult, app.Reason));

        var extra = new CacheStatusResult
        {
            Version = 2,
            Apps = [AppCacheStatus.Current("one"), AppCacheStatus.Current("two")]
        };
        Assert.All(extra.Normalize(["one"]).Apps,
            app => Assert.Equal(CacheReason.InvalidResult, app.Reason));
    }

    [Fact]
    public void TypedUnknownRowRejectsUndefinedNumericReason()
    {
        var status = JsonSerializer.Deserialize<CacheStatusResult>(
            "{\"version\":2,\"apps\":[{\"appId\":\"one\",\"isUpToDate\":false,\"outcome\":\"Unknown\",\"reason\":999}]}",
            Json)!;

        var app = Assert.Single(status.Normalize(["one"]).Apps);
        Assert.Equal(CacheOutcome.Unknown, app.Outcome);
        Assert.Equal(CacheReason.InvalidResult, app.Reason);
        Assert.Null(app.IsUpToDate);
    }

    [Fact]
    public async Task GuestRouteUsesServiceClockAndPreservesTypedStatus()
    {
        var now = new DateTimeOffset(2026, 9, 21, 5, 0, 0, TimeSpan.Zero);
        var clock = new CacheStatusClock(now);
        var (daemon, session, _) = PrefillCacheChangeTests.NewDaemon(NewOptions(), clock);
        daemon.CacheStatus = (_, _, _) => Task.FromResult(new CacheStatusResult
        {
            Version = 2,
            Apps =
            [
                AppCacheStatus.Current("current"),
                AppCacheStatus.Outdated("outdated"),
                AppCacheStatus.Unknown("unknown", CacheReason.NoCacheEvidence)
            ],
            Message = "partial inspection"
        });
        using var cancellation = new CancellationTokenSource();
        var controller = CreateController(daemon, session, cancellation.Token);

        var response = await controller.GetCacheStatusAsync(session.Id, new PrefillCacheStatusRequest
        {
            AppIds = ["current", "outdated", "unknown"]
        });

        var body = Assert.IsType<PrefillCacheStatusResponse>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(now.AddSeconds(120), daemon.CacheStatusExpiresAtUtc);
        Assert.Equal(cancellation.Token, daemon.CacheStatusToken);
        Assert.Equal(["current"], body.UpToDateAppIds);
        Assert.Equal(["outdated"], body.OutdatedAppIds);
        Assert.Equal(["unknown"], body.UnknownAppIds);
        Assert.Equal(3, body.Apps.Count);
        Assert.Equal("partial inspection", body.Message);
    }

    [Fact]
    public async Task GuestRoutePropagatesCallerAbort()
    {
        var (daemon, session, _) = PrefillCacheChangeTests.NewDaemon(NewOptions(), new CacheStatusClock(
            new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero)));
        daemon.CacheStatus = (_, _, cancellationToken) =>
            Task.FromCanceled<CacheStatusResult>(cancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var controller = CreateController(daemon, session, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.GetCacheStatusAsync(
            session.Id,
            new PrefillCacheStatusRequest { AppIds = ["one"] }));
    }

    private static GuestCacheStatusController CreateController(
        TestableSteamDaemonService daemon,
        DaemonSession session,
        CancellationToken cancellationToken)
    {
        var controller = new GuestCacheStatusController(daemon)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestAborted = cancellationToken
                }
            }
        };
        controller.HttpContext.Items["Session"] = new UserSession
        {
            Id = session.UserId,
            SessionType = SessionType.Admin
        };
        return controller;
    }

    private static DbContextOptions<AppDbContext> NewOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"cache_status_route_{Guid.NewGuid():N}")
            .Options;

    private sealed class GuestCacheStatusController(TestableSteamDaemonService daemon)
        : DaemonControllerBase<TestableSteamDaemonService>(
            daemon,
            NullLogger<GuestCacheStatusController>.Instance,
            null!,
            null!,
            "Steam")
    {
        protected override int? ResolveThreadLimit(UserSession session) => null;
    }
}
