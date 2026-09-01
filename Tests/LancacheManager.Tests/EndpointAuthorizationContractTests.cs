using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

[CollectionDefinition(nameof(EndpointAuthorizationCollection), DisableParallelization = true)]
public sealed class EndpointAuthorizationCollection
{
}

[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class EndpointAuthorizationContractTests
{
    private static readonly IReadOnlyDictionary<string, string> PrefillClaims =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SteamPrefillAccess"] = "SteamPrefillActive",
            ["EpicPrefillAccess"] = "EpicPrefillActive",
            ["BattleNetPrefillAccess"] = "BattleNetPrefillActive",
            ["RiotPrefillAccess"] = "RiotPrefillActive",
            ["XboxPrefillAccess"] = "XboxPrefillActive"
        };

    private static readonly HashSet<string> KnownPolicies =
    [
        "AccountHolder",
        "GuestAllowed",
        "AnyPrefillAccess",
        .. PrefillClaims.Keys
    ];

    private static readonly Dictionary<string, EndpointAccess> SpecialRoutes =
        new Dictionary<string, EndpointAccess>(StringComparer.Ordinal)
        {
            ["/health"] = EndpointAccess.Public,
            ["/api/version"] = EndpointAccess.Public,
            ["/metrics"] = EndpointAccess.Public,
            ["/scalar/{documentName?}"] = EndpointAccess.Admin,
            ["/scalar/scalar.js"] = EndpointAccess.Admin,
            ["/scalar/scalar.aspnetcore.js"] = EndpointAccess.Admin,
            ["/scalar/favicon.svg"] = EndpointAccess.Admin,
            ["/openapi/{documentName}.json"] = EndpointAccess.Admin,
            ["{*path:nonfile}"] = EndpointAccess.Public
        };

    /// <summary>
    /// Actions that answer on more than one route, with how many. The banner routes each take an
    /// optional trailing version segment as a second template, and the versionless one stays because
    /// it is anonymous and documented for callers outside this repo. Every registration is still
    /// checked for its access level individually; this only records how many there are, so a third
    /// route on one of these methods has to be declared here deliberately instead of passing unseen.
    /// </summary>
    private static readonly Dictionary<string, int> MultiRouteActions =
        new(StringComparer.Ordinal)
        {
            ["GameImagesController.GetHeaderImage"] = 2,
            ["GameImagesController.GetEpicHeaderImage"] = 2,
            ["GameImagesController.GetNameKeyedHeaderImage"] = 2
        };

    private static readonly HashSet<string> PublicActions = new(StringComparer.Ordinal)
    {
        // Account-setup routes are anonymous on purpose and read the API key from the request body.
        // Creating the first admin happens before any account exists to sign in as; opening and
        // completing recovery are the way back in when that row still exists and nobody can sign
        // in. After a wipe the row is gone and first-admin is the way back, so an [Authorize] on
        // these routes would be a lockout rather than a guard.
        "AccountSetupController.CreateFirstAdmin",
        "AccountSetupController.OpenMainAdminRecovery",
        "AccountSetupController.RecoverMainAdminPassword",
        "AuthController.GetStatus",
        "AuthController.Login",
        "AuthController.StartGuest",
        "AuthController.Logout",
        "GameImagesController.GetHeaderImage",
        "GameImagesController.GetEpicHeaderImage",
        "GameImagesController.GetNameKeyedHeaderImage",
        "GameImagesController.GetCacheVersion",
        "GameImagesController.GetAvailableImageIds",
        "SetupController.SetCredentials",
        "SetupController.SetExternalCredentials",
        "SystemController.GetConfig",
        "SystemController.GetSetupStatus",
        "SystemController.GetRefreshRate",
        "SystemController.GetDefaultGuestRefreshRate",
        "ThemeController.GetThemes",
        "ThemeController.GetTheme",
        "ThemeController.GetDefaultGuestTheme"
    };

    private static readonly HashSet<string> AdminControllers = new(StringComparer.Ordinal)
    {
        "AccountsController",
        "ApiKeysController",
        "DataMigrationController",
        "DatabaseController",
        "EpicGameMappingController",
        "GamesController",
        "LogsController",
        "MemoryController",
        "MetricsController",
        "OperationsController",
        "PersistentPrefillController",
        "ScheduledPrefillConfigController",
        "StatusCheckController",
        "SteamApiKeysController",
        "SteamAuthController",
        "XboxGameMappingController"
    };

    private static readonly HashSet<string> AdminActions = new(StringComparer.Ordinal)
    {
        "AuthController.GetGuestDuration",
        "AuthController.SetGuestDuration",
        "AuthController.SetGuestLock",
        "AuthController.SetGuestPrefillConfig",
        "AuthController.SetEpicPrefillConfig",
        "AuthController.SetBattleNetPrefillConfig",
        "AuthController.SetRiotPrefillConfig",
        "AuthController.SetXboxPrefillConfig",
        "AuthController.ToggleGuestPrefill",
        "CacheController.GetCacheInfo",
        "CacheController.GetCacheSize",
        "CacheController.GetCacheSizeScanStatus",
        "CacheController.ClearAllCache",
        "CacheController.ClearDatasourceCache",
        "CacheController.GetActiveOperations",
        "CacheController.GetCachedCorruption",
        "CacheController.GetCorruptionHistory",
        "CacheController.GetCorruptionHistoryDetails",
        "CacheController.DeleteCorruptionHistory",
        "CacheController.StartCorruptionDetection",
        "CacheController.GetCorruptionDetectionStatus",
        "CacheController.GetCorruptionDetails",
        "CacheController.RemoveCorruptedChunks",
        "CacheController.RemoveAllCorruptedChunks",
        "CacheController.ClearServiceCache",
        "CacheController.StartServiceCacheFileCount",
        "CacheController.StartGameCacheFileCount",
        "CacheController.StartEpicGameCacheFileCount",
        "CacheController.StartNamedGameCacheFileCount",
        "CacheController.GetCacheFileCountStatus",
        "CacheController.GetAllActiveRemovals",
        "CacheController.RemoveAllEvicted",
        "CacheController.RemoveEvictedForEntity",
        "CacheController.RemoveEvictedForNamedGame",
        "ClientGroupsController.GetAll",
        "ClientGroupsController.GetById",
        "ClientGroupsController.GetMapping",
        "ClientGroupsController.Create",
        "ClientGroupsController.SetMembers",
        "ClientGroupsController.Update",
        "ClientGroupsController.Delete",
        "ClientHostnamesController.ResolveAddresses",
        "ClientHostnamesController.SetEnabled",
        "ClientHostnamesController.SetSettings",
        "DatasourceConfigurationController.SetCacheSize",
        "DepotsController.GetDepotStatus",
        "DepotsController.StartDepotRebuild",
        "DepotsController.GetRebuildProgress",
        "DepotsController.CancelRebuild",
        "DepotsController.CheckIncremental",
        "DepotsController.ImportDepotMappings",
        "DepotsController.ApplyDepotMappings",
        "DepotsController.SetCrawlInterval",
        "DepotsController.SetCrawlMode",
        "EventsController.Create",
        "EventsController.Update",
        "EventsController.Delete",
        "EventsController.TagDownload",
        "EventsController.UntagDownload",
        "GameImagesController.ClearImageCache",
        "PrefillAdminController.GetSessions",
        "PrefillAdminController.GetActiveSessions",
        "PrefillAdminController.GetSessionHistory",
        "PrefillAdminController.Terminate",
        "PrefillAdminController.TerminateAll",
        "PrefillAdminController.GetBans",
        "PrefillAdminController.BanBySession",
        "PrefillAdminController.BanByUsername",
        "PrefillAdminController.LiftBan",
        "PrefillAdminController.ClearAllCache",
        "PrefillAdminController.ClearAppCache",
        "ScheduleController.GetAll",
        "ScheduleController.GetByKey",
        "ScheduleController.GetRunStatus",
        "ScheduleController.SetInterval",
        "ScheduleController.SetCustomSchedule",
        "ScheduleController.SetRunOnStartup",
        "ScheduleController.SetNotificationMode",
        "ScheduleController.SetNotificationDisplayMode",
        "ScheduleController.TriggerRun",
        "ScheduleController.ResetToDefaults",
        "ScheduleController.TriggerAll",
        "SessionsController.GetAll",
        "SessionsController.Revoke",
        "SessionsController.Delete",
        "SessionsController.ResetToDefaults",
        "SessionsController.ClearGuests",
        "StatsController.GetClients",
        "StatsController.GetExcludedClients",
        "StatsController.GetEvictionSettings",
        "StatsController.EvictionScanStatus",
        "StatsController.UpdateExcludedClients",
        "StatsController.UpdateEvictionSettings",
        "StatsController.Reconcile",
        "StatsController.ResetEvictions",
        "ThemeController.UploadTheme",
        "ThemeController.DeleteTheme",
        "ThemeController.CleanupThemes",
        "ThemeController.SetDefaultGuestTheme",
        "UserPreferencesController.GetForSession",
        "UserPreferencesController.SaveForSession",
        "SystemController.UpdateSetupStatus",
        "SystemController.IsRsyncAvailable",
        "SystemController.SetCacheDeleteMode",
        "SystemController.SetRefreshRate",
        "SystemController.SetDefaultGuestRefreshRate",
        "SystemController.SetGuestRefreshRateLock",
        "SystemController.SetAllowedTimeFormats",
        "SystemController.SetDefaultGuestClock",
        "SystemController.SetDefaultGuestPreference",
        "SystemController.SetPrefillDefaults"
    };

    private static readonly IReadOnlyDictionary<string, string> PrefillControllers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BattleNetDaemonController"] = "BattleNetPrefillAccess",
            ["EpicDaemonController"] = "EpicPrefillAccess",
            ["RiotDaemonController"] = "RiotPrefillAccess",
            ["SteamDaemonController"] = "SteamPrefillAccess",
            ["XboxDaemonController"] = "XboxPrefillAccess"
        };

    private static readonly HashSet<string> AnyPrefillActions = new(StringComparer.Ordinal)
    {
        "PrefillAdminController.GetCachedApps"
    };

    private static readonly string[] SharedDaemonActions =
    [
        "GetOwnedGames",
        "GetCacheStatus",
        "SetSelectedApps",
        "StartPrefill"
    ];

    private static readonly string[] RemovedDaemonActions =
    [
        "GetAllSessions",
        "GetMySessions",
        "GetSession",
        "CreateSession",
        "GetSessionStatus",
        "StartLogin",
        "ProvideCredential",
        "WaitForChallenge",
        "TerminateSession"
    ];

    private static readonly HashSet<string> StatusDaemonControllers = new(StringComparer.Ordinal)
    {
        "BattleNetDaemonController",
        "RiotDaemonController"
    };

    private static readonly string[] DomainTags =
    [
        "Access",
        "Clients",
        "Cache and Games",
        "Downloads and Reporting",
        "Prefill",
        "System"
    ];

    private static readonly string[] OperationMethods =
    [
        "get",
        "put",
        "post",
        "delete",
        "options",
        "head",
        "patch",
        "trace"
    ];

    private static readonly string[] DaemonSchemas =
    [
        "DaemonSessionDto",
        "DaemonStatus",
        "CredentialChallenge",
        "PrefillResult",
        "ClearCacheResult",
        "AppStatus",
        "SelectedAppsStatus",
        "CacheStatusResult",
        "CommandRequest",
        "CommandResponse",
        "NetworkDiagnostics",
        "DnsTestResult"
    ];

    /// <summary>
    /// The policy names live in three lists that have to stay identical: the definitions in
    /// <c>Program.cs</c>, the array <c>Program.cs</c> walks when <c>Security:EnableAuthentication</c>
    /// is false, and <see cref="KnownPolicies"/>. A name missing from that array still compiles and
    /// still starts, and then every route carrying the policy answers 403 with authentication turned
    /// off, because a named policy is not covered by the open Default/Fallback policies.
    /// </summary>
    [Fact]
    public void ThePolicyNamesAgreeAcrossTheirThreeLists()
    {
        var program = File.ReadAllText(Path.Combine(
            EndpointAuthorizationHost.FindRepositoryRoot(), "Api", "LancacheManager", "Program.cs"));

        var authorization = Regex.Match(
            program,
            @"builder\.Services\.AddAuthorization\(options =>\r?\n\{(?<body>.*?)\r?\n\}\);",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(
            authorization.Success,
            "the AddAuthorization block was not found in Program.cs - if it moved, point this test at "
            + "its new home rather than deleting the check");

        var body = authorization.Groups["body"].Value;

        var openedWhenAuthenticationIsDisabled = Regex.Match(
            body,
            @"foreach \(var policyName in new\[\]\s*\{(?<names>[^}]*)\}\)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(
            openedWhenAuthenticationIsDisabled.Success,
            "the array of policy names opened when Security:EnableAuthentication is false was not found "
            + "in Program.cs - if it moved, point this test at its new home rather than deleting the check");

        var opened = PolicyNames(openedWhenAuthenticationIsDisabled.Groups["names"].Value, @"""(?<name>[^""]+)""");
        var defined = PolicyNames(body, @"options\.AddPolicy\(""(?<name>[^""]+)""");
        var known = KnownPolicies.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.True(
            defined.SequenceEqual(known, StringComparer.Ordinal),
            $"Program.cs defines [{string.Join(", ", defined)}] but {nameof(KnownPolicies)} lists "
            + $"[{string.Join(", ", known)}]. Add the policy to both, or this contract stops covering it.");

        Assert.True(
            defined.SequenceEqual(opened, StringComparer.Ordinal),
            $"Program.cs defines [{string.Join(", ", defined)}] but opens [{string.Join(", ", opened)}] "
            + "when Security:EnableAuthentication is false. Every route carrying a policy missing from "
            + "that array answers 403 with authentication turned off.");
    }

    /// <summary>
    /// The policy is named for what it actually asks: whether the caller holds an account. Both account
    /// roles are admitted; a guest holds a session with no account behind it and is refused, as is a
    /// caller with no session at all.
    /// </summary>
    [Fact]
    public async Task TheAccountHolderPolicyAdmitsBothAccountRolesAndRefusesAGuest()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        var services = host.Application.Services;
        var policy = await RequiredPolicyAsync(services.GetRequiredService<IAuthorizationPolicyProvider>(), "AccountHolder");
        var authorizationService = services.GetRequiredService<IAuthorizationService>();

        Assert.True((await authorizationService.AuthorizeAsync(Principal(SessionType.Admin), null, policy)).Succeeded);
        Assert.True((await authorizationService.AuthorizeAsync(Principal(SessionType.User), null, policy)).Succeeded);
        Assert.False((await authorizationService.AuthorizeAsync(Principal(SessionType.Guest), null, policy)).Succeeded);
        Assert.False((await authorizationService.AuthorizeAsync(Anonymous(), null, policy)).Succeeded);
    }

    private static string[] PolicyNames(string source, string pattern)
    {
        return Regex
            .Matches(source, pattern, RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["name"].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public async Task RegisteredEndpointsHonorTheirAuthorizationMetadata()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        var services = host.Application.Services;
        var endpointDataSource = services.GetRequiredService<EndpointDataSource>();
        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorizationService = services.GetRequiredService<IAuthorizationService>();
        var authorizationOptions = services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var endpoints = endpointDataSource.Endpoints;

        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() != null);

        var accessByEndpoint = endpoints.ToDictionary(
            endpoint => endpoint,
            endpoint => Classify(endpoint));

        AssertExpectedAccessContract(endpoints, accessByEndpoint);
        AssertRequiredEndpointDispositions(endpoints, accessByEndpoint);

        foreach (var endpoint in endpoints)
        {
            var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var unknownPolicies = authorization
                .Select(item => item.Policy)
                .Where(policy => !string.IsNullOrEmpty(policy) && !KnownPolicies.Contains(policy))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                unknownPolicies.Length == 0,
                $"Endpoint '{endpoint.DisplayName}' uses an unrecognized authorization policy: {string.Join(", ", unknownPolicies)}.");

            switch (accessByEndpoint[endpoint])
            {
                case EndpointAccess.Public:
                    Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
                    break;

                case EndpointAccess.Authenticated:
                    await AssertAuthorizationAsync(
                        endpoint,
                        authorization,
                        policyProvider,
                        authorizationOptions,
                        authorizationService,
                        anonymousAllowed: false,
                        guestAllowed: true,
                        adminAllowed: true);
                    break;

                case EndpointAccess.Admin:
                    await AssertAuthorizationAsync(
                        endpoint,
                        authorization,
                        policyProvider,
                        authorizationOptions,
                        authorizationService,
                        anonymousAllowed: false,
                        guestAllowed: false,
                        adminAllowed: true);
                    break;

                case EndpointAccess.Prefill:
                case EndpointAccess.AdminPrefill:
                case EndpointAccess.AnyPrefill:
                    var claims = ClaimsFor(authorization);
                    var policy = await ResolvePolicyAsync(endpoint, authorization, policyProvider, authorizationOptions);

                    Assert.False((await authorizationService.AuthorizeAsync(Anonymous(), endpoint, policy)).Succeeded);
                    Assert.False((await authorizationService.AuthorizeAsync(Principal(SessionType.Guest), endpoint, policy)).Succeeded);
                    Assert.False((await authorizationService.AuthorizeAsync(Principal(SessionType.Admin), endpoint, policy)).Succeeded);

                    var guestAllowed = accessByEndpoint[endpoint] != EndpointAccess.AdminPrefill;
                    Assert.Equal(guestAllowed, (await authorizationService.AuthorizeAsync(Principal(SessionType.Guest, claims), endpoint, policy)).Succeeded);
                    Assert.True((await authorizationService.AuthorizeAsync(Principal(SessionType.Admin, claims), endpoint, policy)).Succeeded);
                    break;

                default:
                    throw new InvalidOperationException($"Endpoint '{endpoint.DisplayName}' has no authorization disposition.");
            }
        }

        await AssertPrefillPoliciesAsync(policyProvider, authorizationService);
    }

    [Fact]
    public async Task DocumentationRoutesAllowAnonymousAccessWhenAuthenticationIsDisabled()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var isolationClient = host.Application.CreateClient();
        using var client = host.Application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await host.AssertIsolationAsync(isolationClient);

        using (var scalarRedirect = await client.GetAsync("/scalar"))
        {
            Assert.Equal(System.Net.HttpStatusCode.Redirect, scalarRedirect.StatusCode);
            Assert.Equal("scalar/", scalarRedirect.Headers.Location?.OriginalString);
        }

        foreach (var path in new[] { "/scalar/", "/openapi/v1.json" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task DocumentationRoutesRejectAnonymousAndGuestSessionsWhenAuthenticationIsEnabled()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: true);
        using var isolationClient = host.Application.CreateClient();
        using var anonymousClient = host.Application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var guestClient = host.Application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await host.AssertIsolationAsync(isolationClient);

        foreach (var path in new[] { "/scalar", "/openapi/v1.json" })
        {
            using var response = await anonymousClient.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                $"Anonymous request to '{path}' returned {(int)response.StatusCode} {response.StatusCode}: {body}");
        }

        // The host boots an unfinished installation, and a guest session is refused there because a
        // guest cannot complete the wizard. What this test needs is a guest session, not an
        // unfinished install, so setup is marked complete first. It writes to the temporary data
        // directory the host isolates, which is why the isolation check above still holds.
        host.Application.Services.GetRequiredService<IStateService>().SetSetupCompleted(true);

        // And an installation with an owner, for the same reason: a guest session is refused while
        // authentication is on and no account exists, and what this test needs is a guest session.
        await host.NewAccountAsync();

        // Starting a guest session changes something, so it carries the antiforgery token a browser
        // would already hold from the status call the page makes before showing the sign-in screen.
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(guestClient);
        using var guestResponse = await guestClient.PostAsync("/api/auth/guest", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, guestResponse.StatusCode);

        foreach (var path in new[] { "/scalar", "/openapi/v1.json" })
        {
            using var response = await guestClient.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task DocumentationRoutesAllowAnAdminSession()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: true);
        using var isolationClient = host.Application.CreateClient();

        await host.AssertIsolationAsync(isolationClient);

        var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();
        using var client = await host.CreateAdminClientAsync();

        using (var scalarRedirect = await client.GetAsync("/scalar"))
        {
            Assert.Equal(System.Net.HttpStatusCode.Redirect, scalarRedirect.StatusCode);
            Assert.Equal("scalar/", scalarRedirect.Headers.Location?.OriginalString);
        }

        using var scalarResponse = await client.GetAsync("/scalar/");
        Assert.Equal(System.Net.HttpStatusCode.OK, scalarResponse.StatusCode);
        var scalar = await scalarResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"url\":\"openapi/v1.json\"", scalar, StringComparison.Ordinal);
        Assert.Contains("ApiKey", scalar, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, scalar, StringComparison.Ordinal);

        using var documentResponse = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(System.Net.HttpStatusCode.OK, documentResponse.StatusCode);
        var document = await documentResponse.Content.ReadFromJsonAsync<JsonElement>();
        var apiKeyScheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", apiKeyScheme.GetProperty("type").GetString());
        Assert.Equal("header", apiKeyScheme.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", apiKeyScheme.GetProperty("name").GetString());

        var schemeDescription = apiKeyScheme.TryGetProperty("description", out var schemeText) ? schemeText.GetString() : null;
        Assert.True(
            !string.IsNullOrWhiteSpace(schemeDescription),
            "The ApiKey scheme has no description telling the caller which key to enter.");
        Assert.Contains("LANCache Manager", schemeDescription!, StringComparison.Ordinal);
        Assert.Contains("No credential is prefilled.", schemeDescription!, StringComparison.Ordinal);

        var sessionScheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Session");
        Assert.Equal("apiKey", sessionScheme.GetProperty("type").GetString());
        Assert.Equal("cookie", sessionScheme.GetProperty("in").GetString());
        Assert.Equal("LancacheManager.Session", sessionScheme.GetProperty("name").GetString());

        // The key opens the reference and nothing behind it, so the document must ask every
        // endpoint for the session cookie and must never advertise the header as a way in.
        Assert.All(
            document.GetProperty("security").EnumerateArray(),
            requirement =>
            {
                Assert.True(requirement.TryGetProperty("Session", out _));
                Assert.False(requirement.TryGetProperty("ApiKey", out _));
            });

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("security", out var operationSecurity))
                {
                    continue;
                }

                Assert.All(
                    operationSecurity.EnumerateArray(),
                    requirement => Assert.False(
                        requirement.TryGetProperty("ApiKey", out _),
                        $"{operation.Name.ToUpperInvariant()} {path.Name} tells the caller to send X-Api-Key, which no endpoint accepts as its credential."));
            }
        }
    }

    [Fact]
    public async Task DocumentationRoutesAcceptDirectApiKeyAuthentication()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: true);
        using var isolationClient = host.Application.CreateClient();
        using var validClient = host.Application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var invalidClient = host.Application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await host.AssertIsolationAsync(isolationClient);

        var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();
        validClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        foreach (var path in new[] { "/scalar/", "/openapi/v1.json" })
        {
            using var response = await validClient.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        invalidClient.DefaultRequestHeaders.Add("X-Api-Key", "invalid-api-key");

        foreach (var path in new[] { "/scalar/", "/openapi/v1.json" })
        {
            using var response = await invalidClient.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task DaemonControllersExposeOnlyTheSharedPrefillActions()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        var actions = host.Application.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToArray();

        Assert.NotEmpty(actions);

        foreach (var controller in PrefillControllers.Keys)
        {
            var routed = actions
                .Where(action => action.ControllerTypeInfo.Name == controller)
                .Select(action => action.ActionName)
                .ToHashSet(StringComparer.Ordinal);

            Assert.NotEmpty(routed);

            foreach (var removed in RemovedDaemonActions)
            {
                Assert.True(
                    !routed.Contains(removed),
                    $"'{controller}.{removed}' is still routed. The session lifecycle is served by the prefill hub and by PrefillAdminController.GetActiveSessions.");
            }

            foreach (var shared in SharedDaemonActions)
            {
                Assert.True(
                    routed.Contains(shared),
                    $"'{controller}.{shared}' is missing. The live prefill flow calls it over REST.");
            }

            var statusExpected = StatusDaemonControllers.Contains(controller);
            Assert.True(
                statusExpected == routed.Contains("GetStatus"),
                $"'{controller}.GetStatus' routed is {routed.Contains("GetStatus")} but should be {statusExpected}.");

            Assert.Equal(
                DaemonActions(controller).OrderBy(identity => identity, StringComparer.Ordinal).ToArray(),
                routed.Select(action => $"{controller}.{action}").OrderBy(identity => identity, StringComparer.Ordinal).ToArray());
        }

        var adminActions = actions
            .Where(action => action.ControllerTypeInfo.Name == "PrefillAdminController")
            .Select(action => action.ActionName)
            .ToArray();

        Assert.Contains("GetActiveSessions", adminActions);
    }

    [Fact]
    public async Task DocumentedOperationsCarryExactlyOneOfTheSixOrderedDomainTags()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: true);
        using var isolationClient = host.Application.CreateClient();

        await host.AssertIsolationAsync(isolationClient);

        using var client = await host.CreateAdminClientAsync();
        var document = await ReadOpenApiDocumentAsync(client);

        Assert.True(document.TryGetProperty("tags", out var tags), "The document declares no root tags.");
        Assert.Equal(
            DomainTags,
            tags.EnumerateArray().Select(tag => tag.GetProperty("name").GetString() ?? string.Empty).ToArray());

        foreach (var tag in tags.EnumerateArray())
        {
            var name = tag.GetProperty("name").GetString();
            var description = tag.TryGetProperty("description", out var text) ? text.GetString() : null;

            Assert.True(
                !string.IsNullOrWhiteSpace(description),
                $"Root tag '{name}' has no description.");
        }

        foreach (var (method, path, operation) in Operations(document))
        {
            var identity = $"{method.ToUpperInvariant()} {path}";

            Assert.True(operation.TryGetProperty("tags", out var operationTags), $"Operation '{identity}' carries no tag.");

            var names = operationTags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray();

            Assert.True(
                names.Length == 1,
                $"Operation '{identity}' carries {names.Length} tags: {string.Join(", ", names)}.");
            Assert.True(
                DomainTags.Contains(names[0], StringComparer.Ordinal),
                $"Operation '{identity}' carries tag '{names[0]}', which is not one of the six domain tags.");
        }
    }

    [Fact]
    public async Task AnonymousOperationsDeclareAnEmptySecurityRequirement()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: true);
        using var isolationClient = host.Application.CreateClient();

        await host.AssertIsolationAsync(isolationClient);

        using var client = await host.CreateAdminClientAsync();
        var document = await ReadOpenApiDocumentAsync(client);

        var anonymousKeys = OperationKeys(host.Application.Services, anonymous: true);
        var protectedKeys = OperationKeys(host.Application.Services, anonymous: false);

        Assert.NotEmpty(anonymousKeys);
        Assert.NotEmpty(protectedKeys);

        var anonymousChecked = 0;
        var protectedChecked = 0;

        foreach (var (method, path, operation) in Operations(document))
        {
            var identity = $"{method.ToUpperInvariant()} {path}";
            var key = OperationKey(method, path);
            var declared = operation.TryGetProperty("security", out var security);

            if (anonymousKeys.Contains(key))
            {
                Assert.True(
                    declared,
                    $"Anonymous operation '{identity}' inherits the document API key requirement instead of declaring its own empty security list.");
                Assert.True(
                    security.ValueKind == JsonValueKind.Array && security.GetArrayLength() == 0,
                    $"Anonymous operation '{identity}' declares security '{security}' instead of an empty list.");
                anonymousChecked++;
                continue;
            }

            if (!protectedKeys.Contains(key))
            {
                continue;
            }

            if (declared)
            {
                Assert.True(
                    security.ValueKind == JsonValueKind.Array && security.GetArrayLength() > 0,
                    $"Protected operation '{identity}' declares an empty security list, which marks it as open to anyone.");
                Assert.Contains(security.EnumerateArray(), requirement => requirement.TryGetProperty("Session", out _));
            }

            protectedChecked++;
        }

        Assert.True(anonymousChecked > 0, "No documented operation matched a runtime endpoint that allows anonymous callers.");
        Assert.True(protectedChecked > 0, "No documented operation matched a protected runtime endpoint.");
    }

    [Fact]
    public async Task DaemonSchemasDropTheLiveEndTimeAndDescribeTheirNullableProperties()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: true);
        using var isolationClient = host.Application.CreateClient();

        await host.AssertIsolationAsync(isolationClient);

        using var client = await host.CreateAdminClientAsync();
        var document = await ReadOpenApiDocumentAsync(client);

        var schemas = document.GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.TryGetProperty("DaemonSessionDto", out var sessionSchema), "The document no longer publishes the live daemon session schema.");
        Assert.True(
            !sessionSchema.GetProperty("properties").TryGetProperty("endedAt", out _),
            "The live daemon session schema still publishes 'endedAt'; a live session is removed before it can carry an end time.");

        var described = 0;

        foreach (var schemaName in DaemonSchemas)
        {
            if (!schemas.TryGetProperty(schemaName, out var schema)
                || !schema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (!AllowsNull(property.Value))
                {
                    continue;
                }

                var description = property.Value.TryGetProperty("description", out var text) ? text.GetString() : null;

                Assert.True(
                    !string.IsNullOrWhiteSpace(description),
                    $"Nullable property '{schemaName}.{property.Name}' has no description saying when the value is absent.");
                described++;
            }
        }

        Assert.True(described > 0, "The document exposed no nullable daemon property to check.");

        var serializerOptions = host.Application.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value
            .JsonSerializerOptions;
        var json = JsonSerializer.Serialize(
            new DaemonSessionDto { Id = "endpoint-authorization", Platform = "Steam" },
            serializerOptions);

        Assert.Contains("\"platform\":\"Steam\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"errorMessage\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"lastPrefillStatus\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kept on purpose, and this test is where that decision lives rather than in a note somebody has
    /// to find. Nothing inside this repo calls <c>/api/downloads/with-associations</c>: not
    /// <c>Web/src</c>, not <c>Web/scripts</c>, not <c>Tests</c>, not the metrics surface. That makes
    /// it unused here, not dead. It is documented for callers outside this repo, it is reachable by
    /// any signed-in user including a guest, and it is the only route that returns a download joined
    /// to its event tags, so removing it would remove a capability rather than a duplicate. Deleting
    /// a documented outward-facing endpoint is not reversible for whoever is already calling it.
    /// </summary>
    [Fact]
    public void TheDownloadsWithAssociationsRouteIsKeptForCallersOutsideThisRepo()
    {
        var templates = typeof(DownloadsController)
            .GetMethod(nameof(DownloadsController.GetWithEventsAsync))!
            .GetCustomAttributes<HttpGetAttribute>()
            .Select(attribute => attribute.Template)
            .ToList();

        Assert.Contains("with-associations", templates);
    }

    private static void AssertExpectedAccessContract(
        IReadOnlyList<Endpoint> endpoints,
        IReadOnlyDictionary<Endpoint, EndpointAccess> accessByEndpoint)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var registrations = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var endpoint in endpoints)
        {
            var endpointAccess = ExpectedAccess(endpoint);
            var identity = IdentityFor(endpoint);

            // Checked per endpoint, so an action answering on several routes has every one of them
            // held to the same access. This is the assertion that keeps the second banner route's
            // authorization covered rather than folded into the first.
            Assert.True(
                endpointAccess == accessByEndpoint[endpoint],
                $"Endpoint '{identity}' is expected to be {endpointAccess}, but runtime metadata is {accessByEndpoint[endpoint]}.");

            if (endpointAccess == EndpointAccess.Authenticated)
            {
                continue;
            }

            registrations[identity] = registrations.GetValueOrDefault(identity) + 1;
            expected.Add(identity);

            if (endpointAccess is EndpointAccess.Prefill or EndpointAccess.AdminPrefill)
            {
                var controller = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.Name
                    ?? throw new InvalidOperationException($"Prefill endpoint '{endpoint.DisplayName}' has no controller action identity.");
                var policy = PrefillControllers[controller];
                Assert.Contains(policy, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(item => item.Policy));
            }
        }

        foreach (var (identity, count) in registrations)
        {
            var declared = MultiRouteActions.GetValueOrDefault(identity, 1);
            Assert.True(
                count == declared,
                $"Endpoint identity '{identity}' answers on {count} routes; the contract declares {declared}. "
                    + "Add it to MultiRouteActions with its route count, or remove the extra route.");
        }

        Assert.Equal(ExpectedContractIdentities(), expected);
    }

    private static EndpointAccess ExpectedAccess(Endpoint endpoint)
    {
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        if (route != null && SpecialRoutes.TryGetValue(route, out var routeAccess))
        {
            return routeAccess;
        }

        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action == null)
        {
            return EndpointAccess.Authenticated;
        }

        var identity = $"{action.ControllerTypeInfo.Name}.{action.ActionName}";
        if (PublicActions.Contains(identity))
        {
            return EndpointAccess.Public;
        }

        if (AnyPrefillActions.Contains(identity))
        {
            return EndpointAccess.AnyPrefill;
        }

        if (PrefillControllers.ContainsKey(action.ControllerTypeInfo.Name))
        {
            return action.ActionName == "GetAllSessions"
                ? EndpointAccess.AdminPrefill
                : EndpointAccess.Prefill;
        }

        if (AdminControllers.Contains(action.ControllerTypeInfo.Name) || AdminActions.Contains(identity))
        {
            return EndpointAccess.Admin;
        }

        return EndpointAccess.Authenticated;
    }

    private static HashSet<string> ExpectedContractIdentities()
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        identities.UnionWith(SpecialRoutes.Where(item => item.Value != EndpointAccess.Authenticated).Select(item => item.Key));
        identities.UnionWith(PublicActions);
        identities.UnionWith(AdminActions);
        identities.UnionWith(AnyPrefillActions);

        foreach (var controller in AdminControllers)
        {
            identities.UnionWith(ActionIdentities(controller));
        }

        foreach (var controller in PrefillControllers.Keys)
        {
            identities.UnionWith(ActionIdentities(controller));
        }

        return identities;
    }

    private static IEnumerable<string> ActionIdentities(string controller)
    {
        return controller switch
        {
            "AccountsController" => ["AccountsController.GetAccounts", "AccountsController.GetAccount", "AccountsController.CreateAccount", "AccountsController.EditAccount", "AccountsController.SetRole", "AccountsController.SetDisabled", "AccountsController.DeleteAccount", "AccountsController.WipeAccounts"],
            "ApiKeysController" => ["ApiKeysController.GetStatus", "ApiKeysController.RegenerateApiKey"],
            "DataMigrationController" => ["DataMigrationController.ImportLancacheManager", "DataMigrationController.GetImportStatus", "DataMigrationController.ValidateConnection"],
            "DatabaseController" => ["DatabaseController.ResetDatabase", "DatabaseController.ResetSelectedTables", "DatabaseController.GetDatabaseResetStatus", "DatabaseController.GetLogCount"],
            "EpicGameMappingController" => ["EpicGameMappingController.GetAllMappings", "EpicGameMappingController.GetStats", "EpicGameMappingController.GetAuthStatus", "EpicGameMappingController.StartLogin", "EpicGameMappingController.Logout", "EpicGameMappingController.CompleteAuth", "EpicGameMappingController.GetScheduleStatus", "EpicGameMappingController.CancelRefresh", "EpicGameMappingController.UpdateScheduleInterval", "EpicGameMappingController.SearchGames"],
            "GamesController" => ["GamesController.RemoveGameFromCache", "GamesController.RemoveEpicGameFromCache", "GamesController.RemoveNamedGameFromCache", "GamesController.DetectGames", "GamesController.GetActiveDetection", "GamesController.GetCachedDetection"],
            "LogsController" => ["LogsController.GetLogInfo", "LogsController.GetServiceCounts", "LogsController.GetServiceCountsByDatasource", "LogsController.ResetLogPosition", "LogsController.GetLogPositions", "LogsController.ResetDatasourceLogPosition", "LogsController.ProcessAllLogs", "LogsController.ProcessDatasourceLogs", "LogsController.GetProcessingStatus", "LogsController.RemoveServiceLogsFromDatasource", "LogsController.DeleteLogFile", "LogsController.GetRemovalStatus"],
            "MemoryController" => ["MemoryController.GetMemoryStats"],
            "MetricsController" => ["MetricsController.GetInterval", "MetricsController.SetInterval", "MetricsController.GetGameLimit", "MetricsController.SetGameLimit", "MetricsController.GetSecurity", "MetricsController.SetSecurity"],
            "OperationsController" => ["OperationsController.GetOperationStatus", "OperationsController.GetWaitingOperations", "OperationsController.CancelOperation", "OperationsController.ForceKill"],
            "PersistentPrefillController" => ["PersistentPrefillController.Start", "PersistentPrefillController.Stop", "PersistentPrefillController.CleanupEditSession", "PersistentPrefillController.List", "PersistentPrefillController.GetGames", "PersistentPrefillController.SetSelectedApps", "PersistentPrefillController.StartPrefill", "PersistentPrefillController.CancelPrefill", "PersistentPrefillController.StartLogin", "PersistentPrefillController.ProvideCredential", "PersistentPrefillController.GetChallenge", "PersistentPrefillController.CancelLogin", "PersistentPrefillController.Logout", "PersistentPrefillController.ClearLogins", "PersistentPrefillController.GetValidity", "PersistentPrefillController.SetValidity"],
            "ScheduledPrefillConfigController" => ["ScheduledPrefillConfigController.GetConfig", "ScheduledPrefillConfigController.GetSchedule", "ScheduledPrefillConfigController.SetConfig", "ScheduledPrefillConfigController.GetRunStatus"],
            "StatusCheckController" => ["StatusCheckController.GetState", "StatusCheckController.SetResolverMode", "StatusCheckController.Run", "StatusCheckController.TestDomain", "StatusCheckController.RefreshDomains", "StatusCheckController.GetDomains"],
            "SteamApiKeysController" => ["SteamApiKeysController.GetStatus", "SteamApiKeysController.TestKey", "SteamApiKeysController.SaveKey", "SteamApiKeysController.RemoveKey"],
            "SteamAuthController" => ["SteamAuthController.GetStatus", "SteamAuthController.Login", "SteamAuthController.CancelLogin", "SteamAuthController.SetMode", "SteamAuthController.Logout"],
            "XboxGameMappingController" => ["XboxGameMappingController.GetAllMappings", "XboxGameMappingController.GetStats", "XboxGameMappingController.GetAuthStatus", "XboxGameMappingController.StartLogin", "XboxGameMappingController.CancelLogin", "XboxGameMappingController.Logout", "XboxGameMappingController.SearchGames"],
            "BattleNetDaemonController" => DaemonActions("BattleNetDaemonController"),
            "EpicDaemonController" => DaemonActions("EpicDaemonController"),
            "RiotDaemonController" => DaemonActions("RiotDaemonController"),
            "SteamDaemonController" => DaemonActions("SteamDaemonController"),
            "XboxDaemonController" => DaemonActions("XboxDaemonController"),
            _ => throw new InvalidOperationException($"No endpoint identities are maintained for '{controller}'.")
        };
    }

    private static IEnumerable<string> DaemonActions(string controller)
    {
        var identities = SharedDaemonActions.Select(action => $"{controller}.{action}");

        return StatusDaemonControllers.Contains(controller)
            ? identities.Append($"{controller}.GetStatus")
            : identities;
    }

    private static string IdentityFor(Endpoint endpoint)
    {
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        if (route != null && SpecialRoutes.ContainsKey(route))
        {
            return route;
        }

        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        return action == null
            ? endpoint.DisplayName ?? "<unknown>"
            : $"{action.ControllerTypeInfo.Name}.{action.ActionName}";
    }

    private static void AssertRequiredEndpointDispositions(
        IReadOnlyList<Endpoint> endpoints,
        IReadOnlyDictionary<Endpoint, EndpointAccess> accessByEndpoint)
    {
        var routes = endpoints.OfType<RouteEndpoint>().ToArray();

        Assert.Equal(EndpointAccess.Public, accessByEndpoint[Assert.Single(routes, route => route.RoutePattern.RawText == "/health")]);
        Assert.Equal(EndpointAccess.Public, accessByEndpoint[Assert.Single(routes, route => route.RoutePattern.RawText == "/api/version")]);
        Assert.Equal(EndpointAccess.Public, accessByEndpoint[Assert.Single(routes, route => route.RoutePattern.RawText == "/metrics")]);

        var hubHandshakes = routes
            .Where(route => route.RoutePattern.RawText?.StartsWith("/hubs/", StringComparison.Ordinal) == true
                            && route.RoutePattern.RawText.EndsWith("/negotiate", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(6, hubHandshakes.Length);
        Assert.All(hubHandshakes, endpoint => Assert.NotEqual(EndpointAccess.Public, accessByEndpoint[endpoint]));

        var setupEndpoints = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(SetupController))
            .ToArray();
        Assert.NotEmpty(setupEndpoints);
        Assert.All(setupEndpoints, endpoint => Assert.Equal(EndpointAccess.Public, accessByEndpoint[endpoint]));

        var spaFallback = Assert.Single(routes, route => route.RoutePattern.RawText?.Contains("path:nonfile", StringComparison.Ordinal) == true);
        Assert.Equal(EndpointAccess.Public, accessByEndpoint[spaFallback]);

        var documentationRoutes = new[]
        {
            "/scalar/{documentName?}",
            "/scalar/scalar.js",
            "/scalar/scalar.aspnetcore.js",
            "/scalar/favicon.svg",
            "/openapi/{documentName}.json"
        };

        foreach (var routePattern in documentationRoutes)
        {
            var documentationRoute = Assert.Single(routes, route => route.RoutePattern.RawText == routePattern);
            Assert.Equal(EndpointAccess.Admin, accessByEndpoint[documentationRoute]);
            Assert.Contains("AccountHolder", documentationRoute.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(item => item.Policy));
            Assert.Null(documentationRoute.Metadata.GetMetadata<IAllowAnonymous>());
        }

        var swaggerRoutes = routes
            .Where(route => route.RoutePattern.RawText?.StartsWith("/swagger", StringComparison.Ordinal) == true)
            .Select(route => route.RoutePattern.RawText)
            .ToArray();

        Assert.True(
            swaggerRoutes.Length == 0,
            $"The legacy Swagger redirect is still mapped: {string.Join(", ", swaggerRoutes)}. The documentation surface is '/scalar' and '/openapi/v1.json'.");
    }

    private static async Task AssertAuthorizationAsync(
        Endpoint endpoint,
        IReadOnlyList<IAuthorizeData> authorization,
        IAuthorizationPolicyProvider policyProvider,
        AuthorizationOptions authorizationOptions,
        IAuthorizationService authorizationService,
        bool anonymousAllowed,
        bool guestAllowed,
        bool adminAllowed)
    {
        var policy = await ResolvePolicyAsync(endpoint, authorization, policyProvider, authorizationOptions);

        Assert.Equal(anonymousAllowed, (await authorizationService.AuthorizeAsync(Anonymous(), endpoint, policy)).Succeeded);
        Assert.Equal(guestAllowed, (await authorizationService.AuthorizeAsync(Principal(SessionType.Guest), endpoint, policy)).Succeeded);
        Assert.Equal(adminAllowed, (await authorizationService.AuthorizeAsync(Principal(SessionType.Admin), endpoint, policy)).Succeeded);
    }

    private static async Task AssertPrefillPoliciesAsync(
        IAuthorizationPolicyProvider policyProvider,
        IAuthorizationService authorizationService)
    {
        foreach (var (policyName, claim) in PrefillClaims)
        {
            var policy = await RequiredPolicyAsync(policyProvider, policyName);

            Assert.False((await authorizationService.AuthorizeAsync(Principal(SessionType.Guest), null, policy)).Succeeded);
            Assert.True((await authorizationService.AuthorizeAsync(Principal(SessionType.Guest, [claim]), null, policy)).Succeeded);
        }

        var anyPrefill = await RequiredPolicyAsync(policyProvider, "AnyPrefillAccess");
        Assert.False((await authorizationService.AuthorizeAsync(Principal(SessionType.Guest), null, anyPrefill)).Succeeded);
        Assert.True((await authorizationService.AuthorizeAsync(Principal(SessionType.Guest, ["SteamPrefillActive"]), null, anyPrefill)).Succeeded);
    }

    private static async Task<AuthorizationPolicy> ResolvePolicyAsync(
        Endpoint endpoint,
        IReadOnlyList<IAuthorizeData> authorization,
        IAuthorizationPolicyProvider policyProvider,
        AuthorizationOptions authorizationOptions)
    {
        if (authorization.Count == 0)
        {
            return authorizationOptions.FallbackPolicy
                ?? throw new InvalidOperationException($"Endpoint '{endpoint.DisplayName}' has no fallback authorization policy.");
        }

        return await AuthorizationPolicy.CombineAsync(policyProvider, authorization)
            ?? throw new InvalidOperationException($"Endpoint '{endpoint.DisplayName}' has no effective authorization policy.");
    }

    private static async Task<AuthorizationPolicy> RequiredPolicyAsync(
        IAuthorizationPolicyProvider policyProvider,
        string policyName)
    {
        return await policyProvider.GetPolicyAsync(policyName)
            ?? throw new InvalidOperationException($"Required authorization policy '{policyName}' was not registered.");
    }

    private static EndpointAccess Classify(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            return EndpointAccess.Public;
        }

        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(item => item.Policy)
            .Where(policy => !string.IsNullOrEmpty(policy))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var platformPrefill = policies.Overlaps(PrefillClaims.Keys);
        if (policies.Contains("AccountHolder") && platformPrefill)
        {
            return EndpointAccess.AdminPrefill;
        }

        if (policies.Contains("AccountHolder"))
        {
            return EndpointAccess.Admin;
        }

        if (platformPrefill)
        {
            return EndpointAccess.Prefill;
        }

        return policies.Contains("AnyPrefillAccess")
            ? EndpointAccess.AnyPrefill
            : EndpointAccess.Authenticated;
    }

    private static HashSet<string> ClaimsFor(IReadOnlyList<IAuthorizeData> authorization)
    {
        var policies = authorization
            .Select(item => item.Policy)
            .Where(policy => !string.IsNullOrEmpty(policy))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var claims = policies
            .Select(policy => PrefillClaims.GetValueOrDefault(policy))
            .Where(claim => claim != null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        if (claims.Count == 0 && policies.Contains("AnyPrefillAccess"))
        {
            claims.Add("SteamPrefillActive");
        }

        return claims;
    }

    private static async Task<JsonElement> ReadOpenApiDocumentAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<(string Method, string Path, JsonElement Operation)> Operations(JsonElement document)
    {
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!OperationMethods.Contains(operation.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return (operation.Name, path.Name, operation.Value);
            }
        }
    }

    private static HashSet<string> OperationKeys(IServiceProvider services, bool anonymous)
    {
        return services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Where(description => description.ActionDescriptor.EndpointMetadata.Any(item => item is IAllowAnonymous) == anonymous)
            .Select(description => OperationKey(description.HttpMethod, description.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string OperationKey(string? method, string? route)
    {
        return $"{method?.ToUpperInvariant()} /{NormalizeRoute(route)}";
    }

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrEmpty(route))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(route.Length);
        var inParameter = false;
        var inSuffix = false;

        foreach (var character in route)
        {
            switch (character)
            {
                case '{':
                    inParameter = true;
                    inSuffix = false;
                    normalized.Append(character);
                    break;

                case '}':
                    inParameter = false;
                    inSuffix = false;
                    normalized.Append(character);
                    break;

                case ':' or '=' or '?' when inParameter:
                    inSuffix = true;
                    break;

                default:
                    if (!inSuffix)
                    {
                        normalized.Append(character);
                    }

                    break;
            }
        }

        return normalized.ToString().Trim('/');
    }

    private static bool AllowsNull(JsonElement schema)
    {
        return schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.Array
            && type.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.String
                                                  && string.Equals(entry.GetString(), "null", StringComparison.Ordinal));
    }

    private static ClaimsPrincipal Anonymous() => new();

    private static ClaimsPrincipal Principal(SessionType sessionType, IEnumerable<string>? prefillClaims = null)
    {
        var sessionTypeClaim = sessionType.ToString().ToLowerInvariant();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.Empty.ToString()),
            new(ClaimTypes.Role, sessionTypeClaim),
            new("SessionType", sessionTypeClaim)
        };

        if (prefillClaims != null)
        {
            claims.AddRange(prefillClaims.Select(claim => new Claim(claim, "true")));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, SessionAuthenticationHandler.SchemeName));
    }

    private enum EndpointAccess
    {
        Public,
        Authenticated,
        Admin,
        Prefill,
        AdminPrefill,
        AnyPrefill
    }
}

internal sealed class EndpointAuthorizationHost : IDisposable
{
    /// <summary>
    /// The database every host in this suite talks to. With no name set the connection string falls
    /// back to the one in appsettings, which is the installation the developer runs, and these tests
    /// create accounts, open sessions and clear accounts out again, so borrowing it takes real rows
    /// with it. Naming a database of the suite's own is what keeps all of that off it.
    /// </summary>
    private const string DatabaseName = "lancache_endpoint_authorization_tests";

    private static bool _databaseReady;

    private static readonly string[] EnvironmentVariables =
    [
        "POSTGRES_MODE",
        "POSTGRES_HOST",
        "POSTGRES_PASSWORD",
        "POSTGRES_USER",
        "POSTGRES_PORT",
        "POSTGRES_DB",
        "LANCACHE_MANAGER_VERSION",
        "Security__EnableAuthentication"
    ];

    private readonly string _originalCurrentDirectory;
    private readonly string _repositoryDataDirectory;
    private readonly IReadOnlyList<PathState> _repositoryData;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly string _temporaryRoot;
    private WebApplicationFactory<SetupController>? _application;
    private bool _disposed;

    public EndpointAuthorizationHost(bool authenticationEnabled = true)
    {
        _originalCurrentDirectory = Directory.GetCurrentDirectory();
        var repositoryRoot = FindRepositoryRoot();
        var apiRoot = Path.Combine(repositoryRoot, "Api", "LancacheManager");
        _repositoryDataDirectory = Path.Combine(repositoryRoot, "data");
        _repositoryData = Capture(_repositoryDataDirectory);
        _environment = EnvironmentVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"lm-endpoint-authorization-{Guid.NewGuid():N}");

        try
        {
            Assert.True(Directory.Exists(apiRoot), $"API content root '{apiRoot}' was not found.");
            Directory.CreateDirectory(Path.Combine(_temporaryRoot, "Api"));
            Directory.CreateDirectory(Path.Combine(_temporaryRoot, "Web"));

            Environment.SetEnvironmentVariable("POSTGRES_MODE", "external");
            Environment.SetEnvironmentVariable("POSTGRES_HOST", null);
            Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", null);
            Environment.SetEnvironmentVariable("POSTGRES_USER", null);
            Environment.SetEnvironmentVariable("POSTGRES_PORT", null);
            Environment.SetEnvironmentVariable("POSTGRES_DB", DatabaseName);
            Environment.SetEnvironmentVariable("LANCACHE_MANAGER_VERSION", "endpoint-authorization-test");
            Environment.SetEnvironmentVariable("Security__EnableAuthentication", authenticationEnabled.ToString());
            Directory.SetCurrentDirectory(_temporaryRoot);

            _application = new WebApplicationFactory<SetupController>()
                .WithWebHostBuilder(builder => builder.UseContentRoot(apiRoot));

            EnsureDatabase(_application);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public WebApplicationFactory<SetupController> Application => _application
        ?? throw new ObjectDisposedException(nameof(EndpointAuthorizationHost));

    public async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = Application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        try
        {
            var apiKey = Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();
            var (username, password) = await NewAccountAsync();

            // Signing in is itself a request that changes something, so it needs the antiforgery token
            // a browser would already be holding from the status call the page makes on load.
            await PrimeAntiforgeryAsync(client);
            using var loginResponse = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest { ApiKey = apiKey, Username = username, Password = password });
            Assert.Equal(System.Net.HttpStatusCode.OK, loginResponse.StatusCode);

            // The token belongs to the caller it was issued to, and the caller has just changed from
            // nobody to this account, so the one taken above no longer matches.
            await PrimeAntiforgeryAsync(client);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the status endpoint and puts the antiforgery token it answers with on the client, which is
    /// what the page does with the cookie the same call writes. Every request that changes something is
    /// refused without it, and the token is tied to the caller it was issued to, so this runs again
    /// after anything that changes who the client is.
    /// </summary>
    public static async Task PrimeAntiforgeryAsync(HttpClient client)
    {
        using var status = await client.GetAsync("/api/auth/status");
        Assert.True(
            status.StatusCode == System.Net.HttpStatusCode.OK,
            $"GET /api/auth/status answered {(int)status.StatusCode}: {await status.Content.ReadAsStringAsync()}");

        var token = AntiforgeryTokenFrom(status);
        client.DefaultRequestHeaders.Remove(AntiforgeryToken.HeaderName);
        client.DefaultRequestHeaders.Add(AntiforgeryToken.HeaderName, token);
    }

    /// <summary>
    /// The antiforgery token out of a response's Set-Cookie headers, the way script reads it out of
    /// document.cookie.
    /// </summary>
    public static string AntiforgeryTokenFrom(HttpResponseMessage response)
    {
        var prefix = $"{AntiforgeryToken.CookieName}=";
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))
            : null;

        Assert.NotNull(cookie);
        return Uri.UnescapeDataString(cookie!.Substring(prefix.Length).Split(';')[0]);
    }

    /// <summary>
    /// One admin account to sign in as, hashed by the application's own hasher so the sign-in accepts
    /// it. Signing in takes the API key, a username and a password, so a host that hands out a
    /// signed-in client has to hand out an account too. Each call gets a name of its own because the
    /// username index is unique and one host mints several clients.
    /// </summary>
    public async Task<(string Username, string Password)> NewAccountAsync()
    {
        const string password = "Endpoint-Contract-9";

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = $"endpoint-contract-{Guid.NewGuid():N}",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };
        account.PasswordHash = Application.Services
            .GetRequiredService<IPasswordHasher<UserAccount>>()
            .HashPassword(account, password);

        var dbContextFactory = Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var context = await dbContextFactory.CreateDbContextAsync();
        context.UserAccounts.Add(account);
        await context.SaveChangesAsync();

        return (account.Username, password);
    }

    public async Task AssertIsolationAsync(HttpClient client)
    {
        var pathResolver = Application.Services.GetRequiredService<IPathResolver>();
        Assert.Equal(Path.Combine(_temporaryRoot, "data"), pathResolver.GetDataDirectory());

        var health = await client.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("setup-required", health.GetProperty("status").GetString());
        Assert.True(health.GetProperty("setupRequired").GetBoolean());

        AssertRepositoryDataUnchanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _application?.Dispose();
            AssertRepositoryDataUnchanged();
        }
        finally
        {
            Directory.SetCurrentDirectory(_originalCurrentDirectory);
            foreach (var (name, value) in _environment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            if (Directory.Exists(_temporaryRoot))
            {
                DeleteTemporaryRoot();
            }
        }
    }

    /// <summary>
    /// Disposing the host stops its background services, but one can still be finishing a write under
    /// data/operations while the recursive delete is walking that directory, and a file that appears
    /// mid-walk fails the whole delete. Retrying outlasts that last flush.
    /// </summary>
    private void DeleteTemporaryRoot()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(_temporaryRoot, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Creates the suite's database and brings its schema up to date, the first time a host is built.
    /// Startup does not do it here: external mode with no credentials is the setup-required state
    /// these tests boot into, and that state skips migration by design, so nothing else would. The
    /// classes that build a host all share one collection that runs on its own, so the first build is
    /// the only one that finds the flag unset.
    /// </summary>
    private static void EnsureDatabase(WebApplicationFactory<SetupController> application)
    {
        if (_databaseReady)
        {
            return;
        }

        using var context = application.Services
            .GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContext();
        context.Database.Migrate();
        _databaseReady = true;
    }

    private void AssertRepositoryDataUnchanged()
    {
        Assert.Equal(_repositoryData, Capture(_repositoryDataDirectory));
    }

    private static PathState[] Capture(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [new PathState("<missing>", 0, DateTime.MinValue, true)];
        }

        return Directory
            .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Append(directory)
            .Select(path => Directory.Exists(path)
                ? new PathState(Path.GetRelativePath(directory, path), 0, Directory.GetLastWriteTimeUtc(path), true)
                : new PathState(Path.GetRelativePath(directory, path), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), false))
            .OrderBy(state => state.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string FindRepositoryRoot()
    {
        foreach (var startingDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startingDirectory); directory != null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Api"))
                    && Directory.Exists(Path.Combine(directory.FullName, "Web")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private sealed record PathState(string RelativePath, long Length, DateTime LastWriteTimeUtc, bool IsDirectory);
}
