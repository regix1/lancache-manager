using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Models.ApiRequests;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// What <c>GET /api/system/setup</c> tells a caller who has not signed in.
///
/// The endpoint answers two different questions in one response. Whether first-run setup is still
/// outstanding has to be answerable before a session exists, because the app asks it on the very
/// first paint to decide between the wizard and the sign-in screen. Where the database lives, which
/// port it listens on, which database name and which username, answers none of that, and sending it
/// to anyone who can reach the port hands out the shape of the deployment for free.
///
/// So the two halves are tested separately: the connection target is gone for a caller with no
/// identity, the completion answer is not, and a signed-in caller still gets both. The third test is
/// the one that keeps this a split rather than a deletion, since the wizard's confirmation screen
/// reads all four fields.
///
/// Whether the installation owns an account belongs to the same anonymous half, and for the same
/// reason: an installation upgraded from a build that had no accounts has none, and every session it
/// had was revoked on that first start, so nothing about it can be learned from a signed-in caller.
///
/// The controller is exercised directly rather than through the pipeline, so the returned
/// <see cref="ObjectResult"/> carries the same body in every hosting environment.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SetupStatusVisibilityTests : IDisposable
{
    private readonly string _root;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly SystemController _controller;

    public SetupStatusVisibilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-setup-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)_pathResolver).Root = _root;

        // A credentials file is what makes the connection target resolvable at all, in either
        // postgres mode, so it stands in for a configured installation.
        File.WriteAllText(
            _pathResolver.GetPostgresCredentialsPath(),
            """{"host":"db.lan","port":5433,"database":"lancache","username":"lancache"}""");

        _stateService = CreateStateService(_root);
        // The three original tests say nothing about accounts, so they run against a database that
        // cannot be opened - which also proves the route still answers when one is not there.
        _controller = NewController(new ThrowingDbContextFactory());
    }

    private SystemController NewController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        AccountClaimWindow? claimWindow = null) =>
        new SystemController(
            _stateService,
            new ConfigurationBuilder().Build(),
            NullLogger<SystemController>.Instance,
            _pathResolver,
            cacheClearingService: null!,
            steamKit2Service: null!,
            datasourceService: null!,
            notifications: null!,
            userPreferencesService: null!,
            capabilityService: null!,
            nginxLogRotationService: null!,
            cacheManagementService: null!,
            dbContextFactory,
            claimWindow ?? new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AnAnonymousCaller_IsNotToldWhereTheDatabaseIs()
    {
        var body = ReadSetupStatus();

        Assert.Null(body.PostgresHost);
        Assert.Null(body.PostgresPort);
        Assert.Null(body.PostgresDatabase);
        Assert.Null(body.PostgresUser);
    }

    [Fact]
    public void AnAnonymousCaller_StillLearnsWhetherSetupIsComplete()
    {
        _stateService.SetSetupCompleted(true);

        var body = ReadSetupStatus();

        Assert.True(body.IsCompleted);
        Assert.True(body.SetupCompleted);
    }

    [Fact]
    public void AnAccountHolder_StillReceivesTheConnectionTarget()
    {
        _controller.HttpContext.Items["Session"] = NewSession();

        var body = ReadSetupStatus();

        Assert.NotNull(body.PostgresHost);
        Assert.NotNull(body.PostgresDatabase);
        Assert.NotNull(body.PostgresUser);
    }

    /// <summary>
    /// The upgrade, as an installation actually arrives at it: setup finished long ago, people were
    /// signed in, and the account table the new build introduced is empty. Sessions are seeded so the
    /// answer cannot come from "somebody is signed in", which is the shortcut that would report an
    /// account here and leave the installation on a sign-in screen it cannot pass.
    /// </summary>
    [Fact]
    public async Task AnInstallationUpgradedWithLiveSessionsAndNoAccounts_SaysItHasNoAccount()
    {
        _stateService.SetSetupCompleted(true);
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.UserSessions.Add(NewSession());
            seed.UserSessions.Add(NewSession());
            await seed.SaveChangesAsync();
        }

        var body = ReadSetupStatus(database.Factory);

        Assert.True(body.IsCompleted);
        Assert.False(body.AccountExists);
        Assert.False(body.MainAdminRecoveryAvailable);
    }

    [Fact]
    public async Task AnInstallationWithAnAccount_SaysSo()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.UserAccounts.Add(new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                PasswordHash = "hash",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var window = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        var controller = NewController(database.Factory, window);
        var body = ReadSetupStatus(controller);

        Assert.True(body.AccountExists);
        Assert.False(body.MainAdminRecoveryAvailable);

        Assert.True(window.OpenRecovery());
        body = ReadSetupStatus(controller);

        Assert.True(body.MainAdminRecoveryAvailable);
    }

    [Fact]
    public async Task AnInstallationWithAnAccount_AfterTheWindowCloses_DoesNotOfferRecovery()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.UserAccounts.Add(new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                PasswordHash = "hash",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var window = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        Assert.True(window.OpenRecovery());
        window.Expire();

        var controller = NewController(database.Factory, window);

        var body = ReadSetupStatus(controller);

        Assert.True(body.AccountExists);
        Assert.False(body.MainAdminRecoveryAvailable);
    }

    /// <summary>
    /// This route is what an installation with no working database reads to find its way to the
    /// credentials step, so an unreadable account table has to leave the answer unknown rather than
    /// fail the response or claim there is no account.
    /// </summary>
    [Fact]
    public void AnUnreadableAccountTable_LeavesTheAnswerUnknown()
    {
        var body = ReadSetupStatus();

        Assert.Null(body.AccountExists);
        Assert.False(body.MainAdminRecoveryAvailable);
    }

    /// <summary>
    /// A guest holds a session, so being signed in is not the test: the connection target and the
    /// folder layout belong to the people who run the installation, and a guest is not one of them.
    /// </summary>
    [Fact]
    public async Task AGuest_IsNotToldWhereTheDatabaseOrTheFoldersAre_ButAnAdminIs()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        host.Application.Services.GetRequiredService<StateService>().SetSetupCompleted(true);
        using var admin = await host.CreateAdminClientAsync();
        using var guest = await CreateGuestClientAsync(host);

        var adminSetup = await admin.GetFromJsonAsync<JsonElement>("/api/system/setup");
        var guestSetup = await guest.GetFromJsonAsync<JsonElement>("/api/system/setup");
        var adminConfig = await admin.GetFromJsonAsync<JsonElement>("/api/system/config");
        var guestConfig = await guest.GetFromJsonAsync<JsonElement>("/api/system/config");

        Assert.False(IsAbsent(adminSetup, "postgresDatabase"));
        foreach (var field in new[] { "postgresHost", "postgresPort", "postgresDatabase", "postgresUser" })
        {
            Assert.True(IsAbsent(guestSetup, field), $"A guest was told {field}.");
        }

        Assert.NotEmpty(TextOf(adminConfig, "cachePath"));
        Assert.NotEmpty(TextOf(adminConfig, "logsPath"));
        Assert.NotEmpty(TextOf(adminConfig, "dataPath"));
        Assert.Empty(TextOf(guestConfig, "cachePath"));
        Assert.Empty(TextOf(guestConfig, "logsPath"));
        Assert.Empty(TextOf(guestConfig, "dataPath"));
        Assert.NotEmpty(guestConfig.GetProperty("dataSources").EnumerateArray().ToList());
    }

    [Fact]
    public async Task AfterSetup_ACallerWithNoSessionGetsNoDatasourcesButStillGetsTheTimeZone()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        host.Application.Services.GetRequiredService<StateService>().SetSetupCompleted(true);
        using var anonymous = host.Application.CreateClient();

        var config = await anonymous.GetFromJsonAsync<JsonElement>("/api/system/config");

        Assert.Empty(config.GetProperty("dataSources").EnumerateArray().ToList());
        Assert.NotEmpty(TextOf(config, "timeZone"));
        Assert.Empty(TextOf(config, "cachePath"));
    }

    /// <summary>
    /// Before setup completes the wizard's log step lists the datasources while nobody can sign in
    /// yet, so the names stay; the paths never go to a caller without an account.
    /// </summary>
    [Fact]
    public async Task BeforeSetup_ACallerWithNoSessionGetsDatasourceNamesButNoPaths()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var anonymous = host.Application.CreateClient();

        var config = await anonymous.GetFromJsonAsync<JsonElement>("/api/system/config");

        var datasources = config.GetProperty("dataSources").EnumerateArray().ToList();
        Assert.NotEmpty(datasources);
        foreach (var datasource in datasources)
        {
            Assert.NotEmpty(TextOf(datasource, "name"));
            Assert.Empty(TextOf(datasource, "cachePath"));
            Assert.Empty(TextOf(datasource, "logsPath"));
        }

        Assert.Empty(TextOf(config, "cachePath"));
        Assert.Empty(TextOf(config, "dataPath"));
    }

    [Fact]
    public async Task TheAvailableGameImagesList_NeedsASession_AndAGuestHasOne()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var anonymous = host.Application.CreateClient();
        using var guest = await CreateGuestClientAsync(host);

        using var withoutSession = await anonymous.GetAsync("/api/game-images/available");
        using var withGuestSession = await guest.GetAsync("/api/game-images/available");

        Assert.Equal(HttpStatusCode.Unauthorized, withoutSession.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withGuestSession.StatusCode);
    }

    /// <summary>
    /// With authentication off every caller shares one admin session, so the target and the folders
    /// stay visible to it.
    /// </summary>
    [Fact]
    public async Task WithAuthenticationOff_TheSharedSessionStillSeesTheDatabaseAndTheFolders()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        host.Application.Services.GetRequiredService<StateService>().SetSetupCompleted(true);
        using var caller = host.Application.CreateClient();

        var setup = await caller.GetFromJsonAsync<JsonElement>("/api/system/setup");
        var config = await caller.GetFromJsonAsync<JsonElement>("/api/system/config");

        Assert.False(IsAbsent(setup, "postgresDatabase"));
        Assert.NotEmpty(TextOf(config, "cachePath"));
        Assert.NotEmpty(TextOf(config, "dataPath"));
    }

    [Fact]
    public async Task TheCacheSliceSize_IsReadAndSavedByAnAccountHolderOnly()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var anonymous = host.Application.CreateClient();
        using var admin = await host.CreateAdminClientAsync();
        const string route = "/api/system/cache-slice-size";
        const long eightMegabytes = 8L * 1024 * 1024;

        using var withoutSession = await anonymous.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutSession.StatusCode);

        using var manual = await admin.PatchAsJsonAsync(route, new CacheSliceSizeRequest { SliceSizeBytes = eightMegabytes });
        Assert.Equal(HttpStatusCode.NoContent, manual.StatusCode);
        var saved = await admin.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal(eightMegabytes, saved.GetProperty("sliceSizeBytes").GetInt64());

        using var auto = await admin.PatchAsJsonAsync(route, new CacheSliceSizeRequest { SliceSizeBytes = null });
        Assert.Equal(HttpStatusCode.NoContent, auto.StatusCode);
        using var autoRead = await admin.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, autoRead.StatusCode);
        Assert.True(IsAbsent(await autoRead.Content.ReadFromJsonAsync<JsonElement>(), "sliceSizeBytes"));

        using var invalid = await admin.PatchAsJsonAsync(route, new CacheSliceSizeRequest { SliceSizeBytes = 1000 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var refusal = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("errors.cache.sliceSizeInvalid", refusal.GetProperty("stageKey").GetString());
    }

    private static bool IsAbsent(JsonElement body, string name) =>
        !body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static string TextOf(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    private static async Task<HttpClient> CreateGuestClientAsync(EndpointAuthorizationHost host)
    {
        var client = host.Application.CreateClient();

        try
        {
            using var scope = host.Application.Services.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var guest = await sessions.CreateGuestSessionAsync(new DefaultHttpContext());
            Assert.NotNull(guest);

            var cookieWriter = new DefaultHttpContext();
            sessions.SetSessionCookie(cookieWriter, guest!.Value.RawToken, DateTime.UtcNow.AddDays(1));
            client.DefaultRequestHeaders.Add(
                "Cookie",
                cookieWriter.Response.Headers.SetCookie.ToString().Split(';')[0]);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static UserSession NewSession() => new()
    {
        Id = Guid.NewGuid(),
        SessionTokenHash = Guid.NewGuid().ToString("N"),
        SessionType = SessionType.Admin,
        CreatedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        LastSeenAtUtc = DateTime.UtcNow
    };

    private SetupStatusResponse ReadSetupStatus() => ReadSetupStatus(_controller);

    private SetupStatusResponse ReadSetupStatus(IDbContextFactory<AppDbContext> dbContextFactory) =>
        ReadSetupStatus(NewController(dbContextFactory));

    private static SetupStatusResponse ReadSetupStatus(SystemController controller)
    {
        var result = controller.GetSetupStatus();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<SetupStatusResponse>(ok.Value);
    }
}
