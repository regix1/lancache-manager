using System.IO.Compression;
using System.Text.Json.Serialization;
using FluentValidation;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Configuration;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Filters;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using LancacheManager.Validators;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.SignalR; // HubOptions.AddFilter<T>() extension for the HubExceptionFilter
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using OpenTelemetry.Metrics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Read version from VERSION file if not set in environment (for dev mode)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANCACHE_MANAGER_VERSION")))
{
    try
    {
        var versionFilePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "VERSION");
        if (File.Exists(versionFilePath))
        {
            var version = File.ReadAllText(versionFilePath).Trim();
            Environment.SetEnvironmentVariable("LANCACHE_MANAGER_VERSION", version);
        }
        else
        {
            Environment.SetEnvironmentVariable("LANCACHE_MANAGER_VERSION", "dev");
        }
    }
    catch
    {
        Environment.SetEnvironmentVariable("LANCACHE_MANAGER_VERSION", "dev");
    }
}

// Add services to the container
builder.Services.AddControllers(options =>
{
    // Add validation filter for FluentValidation error responses
    options.Filters.Add<ValidationFilter>();
})
.AddJsonOptions(options =>
{
    // Omit null fields from REST JSON payloads to reduce response size on the dashboard hot path.
    // Does NOT affect SignalR serialization - that is configured separately via AddJsonProtocol below.
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

    // Emit camelCase property names for MVC REST payloads to match frontend expectations.
    // Without this, response DTOs without explicit [JsonPropertyName] attributes would serialize
    // as PascalCase and diverge from SignalR (which sets camelCase via PayloadSerializerOptions above).
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});
builder.Services.AddEndpointsApiExplorer();

// Configure FluentValidation for request validation (manual via ValidationFilter)
builder.Services.AddValidatorsFromAssemblyContaining<CreateClientGroupRequestValidator>();

// The documented API is grouped into six domains. The order below is the order the
// reference UI lists them in, and every operation is mapped to exactly one of them.
var documentedDomains = new (string Name, string Description)[]
{
    ("Access", "Sign in, sessions, API keys, and per-user settings."),
    ("Clients", "Cache clients, their groups, and their hostname mappings."),
    ("Cache and Games", "Cached content, game and depot identification, and game artwork."),
    ("Downloads and Reporting", "Download history, dashboard figures, statistics, speeds, events, and logs."),
    ("Prefill", "Platform prefill daemons, their schedules, and their administration."),
    ("System", "Service health, metrics, database maintenance, migrations, and background operations.")
};

// One central controller-to-domain map. Controllers carry no tag attributes and must be listed
// here; documented non-controller endpoints belong to System.
var documentedDomainByController = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["AccountSetup"] = "Access",
    ["Accounts"] = "Access",
    ["ApiKeys"] = "Access",
    ["Auth"] = "Access",
    ["Sessions"] = "Access",
    ["Setup"] = "Access",
    ["SteamApiKeys"] = "Access",
    ["SteamAuth"] = "Access",
    ["Theme"] = "Access",
    ["UserPreferences"] = "Access",
    ["ClientGroups"] = "Clients",
    ["ClientHostnames"] = "Clients",
    ["Cache"] = "Cache and Games",
    ["Depots"] = "Cache and Games",
    ["EpicGameMapping"] = "Cache and Games",
    ["GameImages"] = "Cache and Games",
    ["Games"] = "Cache and Games",
    ["XboxGameMapping"] = "Cache and Games",
    ["Dashboard"] = "Downloads and Reporting",
    ["Downloads"] = "Downloads and Reporting",
    ["Events"] = "Downloads and Reporting",
    ["Logs"] = "Downloads and Reporting",
    ["Speeds"] = "Downloads and Reporting",
    ["Stats"] = "Downloads and Reporting",
    ["BattleNetDaemon"] = "Prefill",
    ["EpicDaemon"] = "Prefill",
    ["PersistentPrefill"] = "Prefill",
    ["PrefillAdmin"] = "Prefill",
    ["RiotDaemon"] = "Prefill",
    ["Schedule"] = "Prefill",
    ["ScheduledPrefillConfig"] = "Prefill",
    ["SteamDaemon"] = "Prefill",
    ["XboxDaemon"] = "Prefill",
    ["Database"] = "System",
    ["DataMigration"] = "System",
    ["DatasourceConfiguration"] = "System",
    ["Gc"] = "System",
    ["Memory"] = "System",
    ["Metrics"] = "System",
    ["Operations"] = "System",
    ["StatusCheck"] = "System",
    ["System"] = "System"
};

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        // The key opens this reference and the document behind it, and no endpoint in the
        // document, so it is offered as a scheme here but never required by an operation.
        document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Api-Key",
            Description = "Enter the LANCache Manager API key. No credential is prefilled. It authorizes this reference and /openapi/v1.json only."
        };
        document.Components.SecuritySchemes["Session"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "LancacheManager.Session",
            Description = "Sign in at POST /api/auth/login with an API key, a username and a password, then send the session cookie it returns."
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Session", document)] = []
        });

        // Replace the per-controller tags the document collects by default with the six
        // domains, added in the order they should be listed.
        document.Tags ??= new HashSet<OpenApiTag>();
        document.Tags.Clear();
        foreach (var (name, description) in documentedDomains)
        {
            document.Tags.Add(new OpenApiTag { Name = name, Description = description });
        }

        return Task.CompletedTask;
    });

    options.AddOperationTransformer((operation, context, _) =>
    {
        // An operation-level empty list overrides the document-level session requirement,
        // so anonymous endpoints are not shown as locked. Everything else inherits it.
        if (context.Description.ActionDescriptor.EndpointMetadata?.OfType<IAllowAnonymous>().Any() == true)
        {
            operation.Security = [];
        }

        var controller = (context.Description.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;
        string domain;
        if (controller == null)
        {
            domain = "System";
        }
        else if (documentedDomainByController.TryGetValue(controller, out var mapped))
        {
            domain = mapped;
        }
        else
        {
            throw new InvalidOperationException($"Controller '{controller}' must be assigned to a documented API domain.");
        }

        operation.Tags = new HashSet<OpenApiTagReference> { new(domain, context.Document) };

        return Task.CompletedTask;
    });
});
builder.Services.AddSignalR(options =>
{
    // Increase timeouts to prevent disconnections during long-running operations (PICS scans, log processing, corruption analysis)
    // PICS scanning can process 740+ batches which takes significant time even with frequent progress updates
    options.KeepAliveInterval = TimeSpan.FromSeconds(10); // Send keepalive every 10 seconds (default: 15)
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(10); // Client timeout after 10 minutes (default: 30s) - generous for slow Steam API responses
    options.HandshakeTimeout = TimeSpan.FromSeconds(30); // Handshake timeout (default: 15)

    // Hub-side parallel to the HTTP GlobalExceptionMiddleware: convert any uncaught (non-HubException)
    // hub exception into a logged, generic HubException so clients get a consistent, non-leaking message.
    options.AddFilter<HubExceptionFilter>();
    // Explicit: never leak internal exception detail to hub clients (the filter provides the message).
    options.EnableDetailedErrors = false;
}).AddJsonProtocol(options =>
{
    // Use camelCase for SignalR JSON serialization to match frontend expectations
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.PayloadSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Configure CORS
// Security:AllowedOrigins can be set to restrict origins (comma-separated list)
// Empty = same-origin only (safe default for reverse-proxy deployments).
// "*"   = any origin, WITHOUT credentials (browsers reject "*" + credentials anyway).
// List  = restricted origins with credentials (e.g. "https://lancache.local,https://admin.lancache.local").
var allowedOrigins = builder.Configuration["Security:AllowedOrigins"];
var corsMode = "same-origin-only";
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        if (string.IsNullOrWhiteSpace(allowedOrigins))
        {
            // Safe default: no cross-origin allowed. Same-origin requests (served by the
            // integrated static-file host or a same-origin reverse proxy) bypass CORS entirely,
            // so this does not break the SPA served from this app. The prior behavior of
            // SetIsOriginAllowed(_ => true) + AllowCredentials() has been removed because it
            // permits any origin to make authenticated cross-site requests.
            corsMode = "same-origin-only";
        }
        else if (allowedOrigins == "*")
        {
            // Wildcard origins - NOT combined with AllowCredentials (browsers reject that pair).
            policy.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
            corsMode = "any-origin-no-credentials";
        }
        else
        {
            // Restricted mode - for internet-exposed instances
            var origins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            policy.WithOrigins(origins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
            corsMode = $"restricted ({origins.Length} origin(s))";
        }
    });
});

// Configure forwarded headers for reverse proxy support (nginx, Cloudflare, Traefik, etc.)
// This ensures we get the real client IP and scheme (X-Forwarded-Proto) instead of the proxy's.
// Three deployment modes:
//   1. Direct HTTP (LAN, no proxy): defaults are fine - no X-Forwarded-* expected.
//   2. nginx/Traefik in front (typical Docker setup): set Security:KnownProxyNetworks
//      to the proxy's network (e.g. "172.16.0.0/12,10.0.0.0/8" for Docker bridges) so
//      X-Forwarded-Proto is honored and Secure cookies activate on HTTPS.
//   3. Fully trusted network (every upstream is trusted to set X-Forwarded-*):
//      set Security:TrustAllProxies=true. NEVER use on internet-exposed hosts.
var trustAllProxies = builder.Configuration.GetValue<bool>("Security:TrustAllProxies");
var knownProxyNetworks = builder.Configuration["Security:KnownProxyNetworks"];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (trustAllProxies)
    {
        // Explicit opt-in: accept forwarded headers from any source.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
    else if (!string.IsNullOrWhiteSpace(knownProxyNetworks))
    {
        // Trust the listed CIDR networks in addition to the loopback default.
        foreach (var cidr in knownProxyNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.IPNetwork.TryParse(cidr, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else
            {
                Console.Error.WriteLine($"WARNING: Security:KnownProxyNetworks entry '{cidr}' is not valid CIDR (e.g. 172.16.0.0/12). Skipping.");
            }
        }
    }
    // else: keep ASP.NET Core defaults (loopback only) - works for direct HTTP and proxies on 127.0.0.1.
});

// Configure API options
builder.Services.Configure<ApiOptions>(
    builder.Configuration.GetSection("ApiOptions"));

// Configure Prefill network options (NetworkMode, LancacheIp, LancacheDnsIp, UseTcp)
// Bound from environment variables of the form Prefill__<Property>
builder.Services.Configure<PrefillNetworkOptions>(
    builder.Configuration.GetSection("Prefill"));

// IMPORTANT: Register path resolver FIRST before anything that depends on it
if (OperatingSystemDetector.IsWindows)
{
    builder.Services.AddSingleton<IPathResolver, WindowsPathResolver>();
    builder.Services.AddSingleton<IMemoryManager, WindowsMemoryManager>();
    Console.WriteLine("Platform: Windows - Using WindowsMemoryManager");
}
else if (OperatingSystemDetector.IsLinux)
{
    builder.Services.AddSingleton<IPathResolver, LinuxPathResolver>();
    builder.Services.AddSingleton<IMemoryManager, LinuxMemoryManager>();
    Console.WriteLine("Platform: Linux - Using LinuxMemoryManager with glibc malloc optimizations");
    // Malloc configuration details are logged by LinuxMemoryManager
}
else
{
    throw new PlatformNotSupportedException($"Unsupported operating system: {OperatingSystemDetector.Description}");
}

// Configure Data Protection for encrypting sensitive data
// Keys are stored in the data directory and are machine-specific
// Determine the path the same way LinuxPathResolver does, but without DI: the container mounts
// /data at the root, and outside one the data directory sits beside Api and Web.
var dataRoot = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
    ? "/data"
    : Path.Combine(FindProjectRootForDataProtection(), "data");

var legacyKeyPath = Path.Combine(dataRoot, "DataProtection-Keys");
var securityDir = Path.Combine(dataRoot, "security");
var dataProtectionKeyPath = Path.Combine(securityDir, "DataProtection-Keys");

// Migrate legacy key path if present
MigrateDataProtectionKeys(legacyKeyPath, securityDir, dataProtectionKeyPath);

// Ensure the directory exists
Directory.CreateDirectory(dataProtectionKeyPath);

if (OperatingSystem.IsLinux())
{
    try
    {
        File.SetUnixFileMode(dataProtectionKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    catch (Exception ex)
    {
        // Log but don't fail startup
        Console.WriteLine($"Warning: Could not set permissions on DataProtection keys directory: {ex.Message}");
    }
}

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));

// On Windows, use DPAPI to encrypt keys at rest
// On Linux/Docker, keys are protected by filesystem permissions (chmod 700)
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

// Log where keys will be stored (will be logged again later with proper ILogger)
Console.WriteLine($"Data Protection keys will be stored in: {dataProtectionKeyPath}");

// Register encryption service for state.json sensitive fields
builder.Services.AddSingleton<SecureStateEncryptionService>();

// Register process manager for tracking and cleaning up spawned processes
builder.Services.AddSingletonHostedService<ProcessManager>();

// Register Rust process helper for common Rust process operations
builder.Services.AddSingleton<RustProcessHelper>();

// Register repositories, with an interface only where consumers depend on the abstraction
builder.Services.AddSingleton<SteamAuthStorageService>();
builder.Services.AddSingleton<IStateService, StateService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<IStatsDataService, StatsDataService>();
builder.Services.AddScoped<IEventsService, EventsService>();
builder.Services.AddScoped<IClientGroupsService, ClientGroupsService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<PathMigrationService>();

// Register in-memory cache with size limit to prevent unbounded growth
builder.Services.AddMemoryCache((MemoryCacheOptions options) =>
{
    options.SizeLimit = 500 * 1024 * 1024; // ~500 MB
});

// Register image caching service
builder.Services.AddSingleton<IImageCacheService, ImageCacheService>();

// Register SignalR notification service
builder.Services.AddSingleton<ISignalRNotificationService, SignalRNotificationService>();

// Register concrete classes (for code that directly references them)
builder.Services.AddSingleton(sp => (StateService)sp.GetRequiredService<IStateService>());
builder.Services.AddScoped(sp => (StatsDataService)sp.GetRequiredService<IStatsDataService>());

// Database configuration - build connection string dynamically from env vars or config file
var baseConnStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

var postgresMode = Environment.GetEnvironmentVariable("POSTGRES_MODE") ?? "embedded";
var pgUser = Environment.GetEnvironmentVariable("POSTGRES_USER");
var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
var pgHost = Environment.GetEnvironmentVariable("POSTGRES_HOST");
var pgPortStr = Environment.GetEnvironmentVariable("POSTGRES_PORT");
var pgDatabase = Environment.GetEnvironmentVariable("POSTGRES_DB");

// Check persistent config file if env vars are missing (production Docker setup page flow)
// Runs pre-DI; cannot use IPathResolver. Uses dataRoot computed above.
// File schema: { username, password, host?, port?, database? }. host/port/database are
// only populated when the user submitted external-mode credentials via the setup UI.
const string PostgresCredentialsFileName = "postgres-credentials.json";
if (string.IsNullOrEmpty(pgPassword) || (postgresMode == "external" && string.IsNullOrEmpty(pgHost)))
{
    var configPath = Path.Combine(dataRoot, "config", PostgresCredentialsFileName);
    if (File.Exists(configPath))
    {
        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (string.IsNullOrEmpty(pgPassword) && root.TryGetProperty("password", out var passElement))
                pgPassword = passElement.GetString();
            if (string.IsNullOrEmpty(pgUser) && root.TryGetProperty("username", out var userElement))
                pgUser = userElement.GetString();
            if (postgresMode == "external")
            {
                if (string.IsNullOrEmpty(pgHost) && root.TryGetProperty("host", out var hostElement))
                    pgHost = hostElement.GetString();
                if (string.IsNullOrEmpty(pgPortStr) && root.TryGetProperty("port", out var portElement))
                    pgPortStr = portElement.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? portElement.GetInt32().ToString()
                        : portElement.GetString();
                if (string.IsNullOrEmpty(pgDatabase) && root.TryGetProperty("database", out var dbElement))
                    pgDatabase = dbElement.GetString();
            }
        }
        catch (Exception ex)
        {
            // No logger exists this early in startup (the host is not built yet), so this uses the
            // same console warning as the proxy-network parse above. Staying silent here is what
            // made an unreadable credentials file look identical to a first run.
            Console.Error.WriteLine(
                $"WARNING: Postgres credentials file '{configPath}' could not be read ({ex.Message}). Continuing without the stored credentials, so the setup page will ask for them again.");
        }
    }
}

// Build connection string with credentials
// Start from base connection string (appsettings) - only override if env vars or config file provided values
var connBuilder = new Npgsql.NpgsqlConnectionStringBuilder(baseConnStr);
if (!string.IsNullOrEmpty(pgUser))
    connBuilder.Username = pgUser;
if (!string.IsNullOrEmpty(pgPassword))
    connBuilder.Password = pgPassword;

// POSTGRES_DB applies in both modes - entrypoint.sh honors it when creating the embedded
// database too, so the connection string has to match. Host/Port stay external-only (the
// embedded path uses a Unix socket and has no TCP port).
if (!string.IsNullOrEmpty(pgDatabase))
    connBuilder.Database = pgDatabase;

if (postgresMode == "external")
{
    if (!string.IsNullOrEmpty(pgHost))
        connBuilder.Host = pgHost;
    if (int.TryParse(pgPortStr, out var pgPort))
        connBuilder.Port = pgPort;
}

// In external mode without credentials, we boot in "setup-only" so the user can submit
// connection details via the UI. Track this so we can skip DB migration on startup.
var externalCredsMissing = postgresMode == "external"
    && (string.IsNullOrEmpty(connBuilder.Host)
        || connBuilder.Host == "/var/run/postgresql"
        || string.IsNullOrEmpty(pgPassword));

// One rule for everything below that reads or writes the database: do not start database work when
// there is no database. Migration is skipped in this mode, so the schema is absent as well as the
// server. Applied at the hosted-service registrations and at the startup session clear, which are
// the two places this process kicks off database work before a request ever arrives.
var databaseAvailable = !externalCredsMissing;

var dbConnectionString = connBuilder.ConnectionString
    + ";Minimum Pool Size=3;Maximum Pool Size=30;Max Auto Prepare=20";

// Register pooled DbContext factory - provides IDbContextFactory<AppDbContext> for singleton services
// and pools context instances to reduce allocation overhead on the dashboard hot path
builder.Services.AddPooledDbContextFactory<AppDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(dbConnectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorCodesToAdd: null  // null means all transient errors including deadlock 40P01
        );
    });
});

// Register scoped AppDbContext resolved from the pooled factory so controllers/services
// that inject AppDbContext directly continue to work
builder.Services.AddScoped<AppDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// Register HttpClientFactory for better HTTP client management
builder.Services.AddHttpClient();

// Register HttpClient for SteamService
builder.Services.AddHttpClient<SteamService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
});

// Register HttpClient for Steam image proxying with shorter timeout
builder.Services.AddHttpClient("SteamImages", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15); // Shorter timeout for image fetches
    client.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
    // Cap the buffered response so a hostile/oversized image URL can't exhaust memory.
    client.MaxResponseContentBufferSize = 16 * 1024 * 1024; // 16 MB
})
// Follow redirects (legitimate image URLs often respond 301/302 for http->https or CDN edge
// hops, and GameImageFetchService treats any non-2xx as failure), but cap the chain at 3 to
// bound the redirect surface - far tighter than the framework default of 50 and with no
// functional regression. The MaxResponseContentBufferSize cap above remains the primary DoS guard.
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { MaxAutomaticRedirections = 3 });

// Register Authentication Services
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddScoped<AuthenticationHelper>();
builder.Services.AddScoped<SessionService>();
// Singleton: it holds no per-request state and takes only the pooled IDbContextFactory and a logger,
// both of which are singletons themselves. Scoped would also make it uninjectable into ApiKeyService,
// which is a singleton and owns the key rotation this records.
builder.Services.AddSingleton<IdentityAuditService>();
// Singleton because the count has to be shared by every request: one per scope would reset on each
// sign-in attempt and never reach the threshold.
builder.Services.AddSingleton<AccountLockout>();
// Hosted so the host constructs it at startup: the window it opens is measured from that moment,
// and a singleton built on first injection would instead start the clock at the first request.
builder.Services.AddSingletonHostedService<AccountClaimWindow>();
// Reads UserAccounts and writes UserSessions, so it only runs where there is a schema to read.
builder.Services.AddDatabaseBackedHostedService<FirstAdminSessionResetService>(databaseAvailable);
builder.Services.AddSingleton<LancacheManager.Core.Services.UserPreferencesService>();
builder.Services.AddSingleton<LancacheManager.Core.Services.GeoIpService>();
builder.Services.AddSingleton<LancacheManager.Core.Services.PublicIpLookupService>();

// ASP.NET Core Authentication (session-based via cookie)
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName, null);

// Password hashing for accounts. Microsoft.Extensions.Identity.Core ships inside the
// Microsoft.AspNetCore.App shared framework, so PasswordHasher<T> needs no package reference.
// AddIdentity is deliberately not called: it sets DefaultAuthenticateScheme, DefaultChallengeScheme
// and DefaultSignInScheme, which would take the default away from the Session scheme registered
// above and change what every [Authorize] in the application means.
builder.Services.Configure<Microsoft.AspNetCore.Identity.PasswordHasherOptions>(options =>
{
    // Both values match the framework defaults today. They are written out anyway because they decide
    // the format every stored hash is written in, and a framework that changed either one underneath
    // us would silently start writing a different format. IterationCount is read only in IdentityV3,
    // which is PBKDF2-HMAC-SHA512; 220,000 is OWASP's figure for that function against the 100,000
    // the framework defaults to.
    options.CompatibilityMode = Microsoft.AspNetCore.Identity.PasswordHasherCompatibilityMode.IdentityV3;
    options.IterationCount = 220_000;
});
builder.Services.AddSingleton<
    Microsoft.AspNetCore.Identity.IPasswordHasher<UserAccount>,
    Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();

// Authorization policies
var authEnabled = builder.Configuration.GetValue<bool>("Security:EnableAuthentication", true);
builder.Services.AddAuthorization(options =>
{
    if (!authEnabled)
    {
        // Authentication disabled via config: open BOTH FallbackPolicy (endpoints without an
        // explicit policy) AND DefaultPolicy (bare [Authorize] controllers use DefaultPolicy, so
        // an open FallbackPolicy alone is NOT enough). RequireAssertion(_ => true) allows anonymous.
        var openPolicy = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build();
        options.FallbackPolicy = openPolicy;
        options.DefaultPolicy = openPolicy;

        // Named policies registered via [Authorize(Policy = "...")] use RequireClaim/RequireAssertion
        // and are NOT covered by Default/FallbackPolicy. With auth disabled an anonymous caller has no
        // SessionType/*PrefillActive claims, so every [Authorize(Policy="AccountHolder")] endpoint would
        // still 403. Open every named policy too so disabling auth truly grants access to ALL endpoints,
        // then return so the secure named-policy definitions below do NOT re-run and override these.
        foreach (var policyName in new[]
                 {
                     "AccountHolder",
                     "GuestAllowed",
                     "SteamPrefillAccess",
                     "EpicPrefillAccess",
                     "BattleNetPrefillAccess",
                     "RiotPrefillAccess",
                     "XboxPrefillAccess",
                     "AnyPrefillAccess"
                 })
        {
            options.AddPolicy(policyName, policy => policy.RequireAssertion(_ => true));
        }

        Console.WriteLine("Authentication DISABLED via Security:EnableAuthentication — all endpoints allow anonymous access");
        return;
    }

    // Secure-by-default: every endpoint requires an authenticated principal unless it
    // explicitly opts out with [AllowAnonymous] (e.g. /health, /api/auth/login, /api/setup/*).
    // This closes the "forgot to add [Authorize]" gap on controllers added later.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Both claim values an account holder can present. CreateTicket lowercases the session type
    // (SessionAuthenticationHandler.cs:162), and a user is answered like an admin everywhere
    // (SessionTypeExtensions.cs:13). A guest presents "guest" and is still refused.
    options.AddPolicy("AccountHolder", policy =>
        policy.RequireClaim("SessionType", "admin", "user"));

    options.AddPolicy("GuestAllowed", policy =>
        policy.RequireAuthenticatedUser());

    options.AddPolicy("SteamPrefillAccess", policy =>
        policy.RequireClaim("SteamPrefillActive", "true"));

    options.AddPolicy("EpicPrefillAccess", policy =>
        policy.RequireClaim("EpicPrefillActive", "true"));

    options.AddPolicy("BattleNetPrefillAccess", policy =>
        policy.RequireClaim("BattleNetPrefillActive", "true"));

    options.AddPolicy("RiotPrefillAccess", policy =>
        policy.RequireClaim("RiotPrefillActive", "true"));

    options.AddPolicy("XboxPrefillAccess", policy =>
        policy.RequireClaim("XboxPrefillActive", "true"));

    options.AddPolicy("AnyPrefillAccess", policy =>
        policy.RequireAssertion(context =>
            context.User.HasClaim("SteamPrefillActive", "true") ||
            context.User.HasClaim("EpicPrefillActive", "true") ||
            context.User.HasClaim("BattleNetPrefillActive", "true") ||
            context.User.HasClaim("RiotPrefillActive", "true") ||
            context.User.HasClaim("XboxPrefillActive", "true")));
});

// Antiforgery. The session cookie is SameSite=Lax (SessionService.cs:808), and Lax still sends a
// cookie less than about two minutes old on a cross-site POST. Every GET /api/auth/status rotates the
// session token and writes the cookie again (AuthController.cs:183), which resets that age, so on a
// session somebody is actually using the window never closes on its own. What closes it is a value
// the page has to read from one cookie and send back in a header, because a page served by another
// origin can do neither.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = AntiforgeryToken.HeaderName;

    // The framework's own cookie holds the other half of the pair and nothing reads it from script,
    // so it keeps the default HttpOnly. Its Secure flag follows the same rule as the session cookie:
    // set on a real HTTPS request, or always when the deployment opts in behind a TLS-terminating
    // proxy, so plain HTTP LAN installations keep working.
    options.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("Security:ForceSecureCookies")
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddSingleton<AntiforgeryToken>();

// One filter covers every controller. Not registered when Security:EnableAuthentication is false:
// that setting means the installation has no access control, so this must not become the one thing
// still turning callers away.
if (authEnabled)
{
    builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        options.Filters.Add<AntiforgeryFilter>());
}

// Register SignalR connection tracking service for targeted messaging
builder.Services.AddSingleton<LancacheManager.Core.Services.ConnectionTrackingService>();

// Register SteamKit2Service for real-time Steam depot mapping
builder.Services.AddDatabaseBackedHostedService<SteamKit2Service>(databaseAvailable);

// Register SteamService as singleton (on-demand game info / depot owner lookups; not scheduled)
builder.Services.AddSingleton<SteamService>();

// Register SteamWebApiService for V2/V1 fallback
builder.Services.AddSingleton<SteamWebApiService>();

// Register DatasourceService for multi-datasource support
builder.Services.AddSingleton<DatasourceService>();
builder.Services.AddSingleton<DatasourceCapabilityService>();

// Register services (repositories already registered above)
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddSingleton<GameCacheDetectionDataService>();
builder.Services.AddSingleton<UnknownGameResolutionService>();
builder.Services.AddSingleton<EvictedDetectionPreservationService>();
builder.Services.AddSingleton<IUnifiedOperationTracker, UnifiedOperationTracker>();
builder.Services.AddSingleton<OperationCancellationService>();
builder.Services.AddSingleton<IOperationConflictChecker, OperationConflictChecker>();
builder.Services.AddSingleton<IOperationQueue, OperationQueueService>();
builder.Services.AddSingleton<PicsDataService>();

// Register cache snapshot service for historical cache size tracking
builder.Services.AddDatabaseBackedHostedService<CacheSnapshotService>(databaseAvailable);

// Register metrics service for Prometheus/Grafana as both singleton and hosted service
builder.Services.AddDatabaseBackedHostedService<LancacheMetricsService>(databaseAvailable);

// Register Rust log processor service (replaces old C# LogProcessingService and LogWatcherService)
builder.Services.AddSingleton<RustLogProcessorService>();

// Register Rust log removal service
builder.Services.AddSingleton<RustLogRemovalService>();

// Register nginx log rotation service (signals nginx to reopen logs after manipulation)
builder.Services.AddSingleton<NginxLogRotationService>();

// Register nginx log rotation hosted service (runs at startup and on schedule)
builder.Services.AddSingletonHostedService<NginxLogRotationHostedService>();

// Register CacheClearingService
builder.Services.AddDatabaseBackedHostedService<CacheClearingService>(databaseAvailable);

// Register OperationHistoryCleanupService (cleans up expired cache clear operation records)
builder.Services.AddSingletonHostedService<OperationHistoryCleanupService>();

// Register GameCacheDetectionService
builder.Services.AddSingleton<GameCacheDetectionService>();

// Register CorruptionDetectionService
builder.Services.AddSingleton<CorruptionDetectionService>();

// Register PrefillSessionService for ban management and session persistence
builder.Services.AddSingleton<PrefillSessionService>();

// Register PrefillCacheService for tracking cached depots across sessions
builder.Services.AddSingleton<PrefillCacheService>();

// Docker operation seam for the prefill daemon services. Each daemon resolves this factory and creates
// its own gateway instance, preserving the previous one-Docker-client-per-service model.
builder.Services.AddSingleton<LancacheManager.Core.Interfaces.IPrefillContainerGatewayFactory, LancacheManager.Infrastructure.Services.DockerPrefillContainerGatewayFactory>();

// Register SteamDaemonService for secure daemon-based prefill management
builder.Services.AddDatabaseBackedHostedService<SteamDaemonService>(databaseAvailable);

// Register EpicPrefillDaemonService for Epic Games daemon-based prefill management
builder.Services.AddDatabaseBackedHostedService<EpicPrefillDaemonService>(databaseAvailable);

// Register BattleNetDaemonService for anonymous Battle.net daemon-based prefill management
builder.Services.AddDatabaseBackedHostedService<BattleNetDaemonService>(databaseAvailable);

// Register RiotDaemonService for anonymous Riot daemon-based prefill management
builder.Services.AddDatabaseBackedHostedService<RiotDaemonService>(databaseAvailable);

// Periodically re-checks Battle.net/Riot Docker connectivity into the activity registry, independent of
// daemon session activity (see DaemonConnectivityReconciler doc comment).
builder.Services.AddDatabaseBackedHostedService<DaemonConnectivityReconciler>(databaseAvailable);

// Register XboxPrefillDaemonService for login-required Xbox / Microsoft Store daemon-based prefill management
builder.Services.AddDatabaseBackedHostedService<XboxPrefillDaemonService>(databaseAvailable);

// Register EpicApiDirectClient for direct HTTP calls to Epic APIs (no Docker needed)
builder.Services.AddHttpClient<EpicApiDirectClient>();

// Register EpicAuthStorageService for Epic credential persistence
builder.Services.AddSingleton<EpicAuthStorageService>();

// Register unified EpicMappingService for game discovery, mapping, and scheduling
builder.Services.AddDatabaseBackedHostedService<EpicMappingService>(databaseAvailable);

// Register BattleNetMappingService for re-mapping existing Blizzard downloads to game
// names from the single-sourced TACT catalog (anonymous/static - no login, no schedule).
builder.Services.AddSingleton<LancacheManager.Core.Services.BattleNet.BattleNetMappingService>();
// One-shot startup resolve so a TACT catalog shipped in a new build renames existing
// unnamed Blizzard downloads immediately instead of waiting for the next ingest batch.
builder.Services.AddDatabaseBackedHostedService<LancacheManager.Infrastructure.Services.BattleNetMappingStartupResolveService>(databaseAvailable);

// Register XboxApiDirectClient for direct HTTP calls to the public Microsoft Store DisplayCatalog
// (no auth, no Docker) - used to fetch Xbox game banner art by ProductId at mapping time.
builder.Services.AddHttpClient<LancacheManager.Core.Services.Xbox.XboxApiDirectClient>();

// Register XboxMappingService for re-tagging existing wsus downloads to Xbox titles by matching
// the per-file CDN path fragments the authenticated daemon contributed (backfill of INACTIVE rows).
builder.Services.AddSingleton<LancacheManager.Core.Services.Xbox.XboxMappingService>();

// Register XboxAuthClient - the typed HttpClient for the manager-side, daemon-free Xbox MSA device-code
// login + catalog harvest (mirrors EpicApiDirectClient; no Docker, no prefill container).
builder.Services.AddHttpClient<LancacheManager.Core.Services.Xbox.XboxAuthClient>();

// Register XboxAuthStorageService - encrypted persistence of the Xbox MSA refresh token + device key
// (mirrors EpicAuthStorageService; ENC2: via SecureStateEncryptionService).
builder.Services.AddSingleton<LancacheManager.Infrastructure.Services.XboxAuthStorageService>();

// Register XboxCatalogMappingService - the SCHEDULED Xbox catalog mapping service (mirrors
// EpicMappingService's scheduling idea). Keeps XboxCdnPatterns/XboxGameMappings populated on a
// runtime-configurable schedule + manual trigger + on-authentication nudge, decoupled from prefill,
// by re-reading the daemon's already-authenticated session. Surfaces on the Schedules page as
// "xboxMapping" (auto-discovered by ServiceScheduleRegistry as a ConfigurableScheduledService).
builder.Services.AddDatabaseBackedHostedService<LancacheManager.Core.Services.Xbox.XboxCatalogMappingService>(databaseAvailable);

// Register GcScheduledService - runs on a user-configurable interval (managed through the
// unified Schedules page) and performs aggressive GC when the working set exceeds the
// configured threshold. Surfaces on the unified Schedules page as "performanceOptimization"
// only when IsScheduleVisible() returns true.
builder.Services.AddSingletonHostedService<GcScheduledService>();

// Register Scheduled Prefill - orchestrates prefill runs across all enabled services on a
// user-configurable interval (managed through the unified Schedules page) as "scheduledPrefill".
builder.Services.AddDatabaseBackedHostedService<ScheduledPrefillService>(databaseAvailable);

// Register PersistentSessionExpiryService - consolidated per-minute expiry/stall reaper across all 5
// prefill daemon platforms, replacing 5 independent per-daemon Timers. Hardcoded 1-minute cadence
// (infra polling, not user-facing), not on the Schedules page (matches the prior mechanism).
builder.Services.AddDatabaseBackedHostedService<PersistentSessionExpiryService>(databaseAvailable);

// Register OperationStateService
builder.Services.AddSingletonHostedService<OperationStateService>();

// Register background services
builder.Services.AddDatabaseBackedHostedService<LiveLogMonitorService>(databaseAvailable);
builder.Services.AddDatabaseBackedHostedService<DownloadCleanupService>(databaseAvailable);
builder.Services.AddDatabaseBackedHostedService<CacheReconciliationService>(databaseAvailable);
builder.Services.AddDatabaseBackedHostedService<CacheSizeScanScheduledService>(databaseAvailable);
builder.Services.AddDatabaseBackedHostedService<GameImageFetchService>(databaseAvailable);
builder.Services.AddHostedService<DirectoryPermissionMonitorService>();

// Register RustSpeedTrackerService for real-time per-game download speed monitoring (uses Rust for faster parsing)
// Database-backed through its child process rather than its own constructor: the Rust tracker
// connects to the same database this process may have just decided it does not have.
builder.Services.AddDatabaseBackedHostedService<RustSpeedTrackerService>(databaseAvailable);

// Register GameDetectionService - runs scheduled game cache detection. Whether it
// also runs at startup is user-controlled via the Schedules UI (DefaultRunOnStartup = false).
builder.Services.AddDatabaseBackedHostedService<GameDetectionService>(databaseAvailable);

// Register dashboard batch service - shared compute behind /api/dashboard/batch. Lives as a
// singleton so the controller is a thin pass-through AND the warmer below can pre-populate
// the IMemoryCache on startup (first user request after restart hits a warm cache).
builder.Services.AddSingleton<IDashboardBatchService, DashboardBatchService>();

// Register dashboard cache warmer - calls GetBatchAsync(null,null,null) once at startup and
// once per interval (RunOnStartup=true) so the first /api/dashboard/batch user request does
// NOT pay the cold DB connection pool + 9 parallel queries penalty.
builder.Services.AddDatabaseBackedHostedService<DashboardCacheWarmerService>(databaseAvailable);

// Register service schedule registry - collects all ScheduledBackgroundService / ConfigurableScheduledService instances
builder.Services.AddSingleton<IServiceScheduleRegistry, ServiceScheduleRegistry>();

// Unified activity/presence registry - single source of truth for every "green status dot" across
// schedules, tracked operations, sessions, prefill, integrations and downloads (broadcasts ActivityUpdated)
builder.Services.AddSingleton<IActivityRegistry, ActivityRegistry>();

// Periodically reconciles the UserSession/Present activity set against the database, so a session that
// predates this process (e.g. survived a restart) still gets tracked instead of being permanently absent
// from the registry (see UserSessionActivityReconciler doc comment).
builder.Services.AddDatabaseBackedHostedService<UserSessionActivityReconciler>(databaseAvailable);

// Register Status Check (DNS/cache reachability and empirical content-path diagnostics) services -
// ILancacheEnvFileReader is also consumed by
// CacheManagementService.ReadCacheSizeFromEnvFile (root-cause reuse of the .env discovery chain).
// ILancacheEnvironmentSource (contract amendment v1.2) composes Docker-inspect + the file reader
// into the codebase's established two-tier env lookup for LANCACHE_IP/DISABLE_*/CACHE_DOMAINS_*/NOFETCH.
builder.Services.AddSingleton<LancacheManager.Core.Interfaces.ILancacheEnvFileReader, LancacheManager.Core.Services.StatusCheck.LancacheEnvFileReader>();
builder.Services.AddSingleton<LancacheManager.Core.Interfaces.ILancacheEnvironmentSource, LancacheManager.Core.Services.StatusCheck.LancacheEnvironmentSource>();
builder.Services.AddSingleton<LancacheManager.Core.Interfaces.ICacheDomainsService, LancacheManager.Core.Services.StatusCheck.CacheDomainsService>();
builder.Services.AddSingleton<LancacheManager.Core.Interfaces.ILancacheServerLocator, LancacheManager.Core.Services.StatusCheck.LancacheServerLocator>();
builder.Services.AddSingleton<LancacheManager.Core.Interfaces.IContentPathCheckService, LancacheManager.Core.Services.StatusCheck.ContentPathCheckService>();
builder.Services.AddSingleton<LancacheManager.Core.Interfaces.IStatusCheckService, LancacheManager.Core.Services.StatusCheck.StatusCheckService>();

// Reverse-DNS names for client addresses. Singleton so its per-address cache and selected primary
// resolver are shared by every request instead of being rebuilt per call.
builder.Services.AddSingleton<LancacheManager.Core.Interfaces.IClientHostnameService, LancacheManager.Core.Services.ClientHostnameService>();

// Configure OpenTelemetry Metrics for Prometheus + Grafana
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddPrometheusExporter()
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("LancacheManager"); // For custom business metrics
    });

// Configure Rate Limiting for auth endpoints
// Protects against brute force attacks on login/device registration
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A rejected request otherwise completes with no content at all, so a caller that reads the
    // response as JSON reads a parse failure instead of the reason it was refused. Set on the
    // options rather than on a policy, so every policy registered here answers the same way.
    options.OnRejected = async (context, cancellationToken) =>
    {
        // A fixed window reports how long is left before it reopens. A limiter that reports nothing
        // leaves the header off rather than sending a guess the caller would wait on.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsJsonAsync(new RateLimitExceededResponse
        {
            StageKey = "errors.rateLimit.tooManyRequests",
            Error = "Too many requests from this address. Wait for the limit to reset and try again."
        }, cancellationToken);
    };

    // Rate limit for authentication endpoints (login, guest sessions, key regeneration, setup repair)
    options.AddPolicy("auth", context => CallerWindow(context, permitLimit: 5, TimeSpan.FromMinutes(1)));

    // Rate limit for account creation and editing. Its own budget rather than the auth one, because
    // the window is partitioned on the caller address alone: an admin adding half a dozen accounts in
    // a minute would otherwise spend the sign-in permits for that address, and behind a reverse proxy
    // that is not on the trusted list every visitor arrives as the same address.
    options.AddPolicy("accounts", context => CallerWindow(context, permitLimit: 10, TimeSpan.FromMinutes(1)));

    // Rate limit for Steam auth (more lenient due to 2FA flows)
    options.AddPolicy("steam-auth", context => CallerWindow(context, permitLimit: 10, TimeSpan.FromMinutes(5)));
});

// One window per caller address. A window with no partition key is a single bucket for the whole
// installation, so five wrong logins from anywhere at all hold the login endpoint shut for every
// other user until the minute is out.
//
// Which address that is comes from the forwarded-headers configuration above: UseForwardedHeaders
// runs before the limiter, so RemoteIpAddress is the client address out of X-Forwarded-For when the
// immediate peer is a trusted proxy, and the peer's own address when it is not. The default trusts
// loopback only, so a reverse proxy on another host or in another container is not trusted and
// everything it forwards arrives as that one proxy address, sharing a single window again.
// Security:KnownProxyNetworks is the setting that fixes it.
//
// Kestrel reports no remote address at all for a request that arrived over a Unix socket. Those
// requests are not throttled, rather than sharing one window with every other address-less caller,
// which would be the same lockout once more; reaching that socket already means access to the
// host.
static RateLimitPartition<string> CallerWindow(HttpContext context, int permitLimit, TimeSpan window)
{
    if (context.Connection.RemoteIpAddress is not { } address)
    {
        return RateLimitPartition.GetNoLimiter(string.Empty);
    }

    return RateLimitPartition.GetFixedWindowLimiter(address.ToString(), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueLimit = 0
    });
}

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Apply additional filtering to reduce log noise
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Extensions.Http.DefaultHttpClientFactory", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Infrastructure.Platform.WindowsPathResolver", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Security.ApiKeyService", LogLevel.Information);
builder.Logging.AddFilter("LancacheManager.Infrastructure.Services.RustLogProcessorService", LogLevel.Information);
builder.Logging.AddFilter("LancacheManager.Core.Services.CacheManagementService", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Core.Services.SteamKit2.PicsDataService", LogLevel.Information);

builder.Logging.SetMinimumLevel(LogLevel.Information);

// Configure response compression (Brotli + Gzip) to reduce payload sizes
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

var app = builder.Build();

// Log the effective CORS posture once at startup so operators can tell at a glance whether
// they are running the safe default or an opted-in permissive configuration.
if (string.IsNullOrWhiteSpace(allowedOrigins))
{
    app.Logger.LogWarning(
        "CORS: Security:AllowedOrigins is empty - defaulting to same-origin-only. " +
        "Set Security:AllowedOrigins to a comma-separated origin list (e.g. \"https://lancache.local\") " +
        "or \"*\" (no credentials) if cross-origin browser access is required.");
}
else if (allowedOrigins == "*")
{
    app.Logger.LogWarning(
        "CORS: Security:AllowedOrigins is \"*\". Any origin may call the API, but AllowCredentials() " +
        "is intentionally NOT set (browsers reject that combination). Set an explicit origin list for " +
        "cookie-authenticated cross-origin requests.");
}
else
{
    app.Logger.LogInformation("CORS: restricted to configured Security:AllowedOrigins list ({Mode}).", corsMode);
}

if (trustAllProxies)
{
    app.Logger.LogWarning(
        "ForwardedHeaders: Security:TrustAllProxies=true - KnownProxies/KnownIPNetworks cleared. " +
        "Any upstream can spoof X-Forwarded-For. Only enable this on trusted networks.");
}
else if (!string.IsNullOrWhiteSpace(knownProxyNetworks))
{
    app.Logger.LogInformation(
        "ForwardedHeaders: trusting proxy networks {Networks} (in addition to loopback defaults).", knownProxyNetworks);
}
else
{
    app.Logger.LogInformation(
        "ForwardedHeaders: trusting loopback proxies only. " +
        "If running behind nginx/Traefik on a different IP (Docker bridge, LAN), set Security:KnownProxyNetworks " +
        "(e.g. \"172.16.0.0/12,10.0.0.0/8\") so cookies are correctly marked Secure on HTTPS and the sign-in rate " +
        "limit sees each client instead of counting everyone against the proxy's address.");
}

// IMPORTANT: Apply database migrations FIRST before any service tries to access the database
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var pathResolver = scope.ServiceProvider.GetRequiredService<IPathResolver>();
    var pathMigrationService = scope.ServiceProvider.GetRequiredService<PathMigrationService>();

    var migrationResult = pathMigrationService.MigrateLegacyDataLayout();
    if (migrationResult.FilesMoved > 0 || migrationResult.DirectoriesMoved > 0)
    {
        logger.LogInformation("Migrated legacy data layout (files: {FilesMoved}, dirs: {DirsMoved})",
            migrationResult.FilesMoved, migrationResult.DirectoriesMoved);
    }

    // On Windows dev, ensure PostgreSQL Docker container is running before attempting migration.
    // Skip in external mode: the user runs their own DB, we shouldn't auto-create one.
    if (postgresMode != "external")
    {
        await WindowsPostgresManager.EnsurePostgresRunningAsync(dbConnectionString, logger);
    }

    // In external mode without credentials, the DB is unreachable on purpose. Boot the
    // web host so the user can submit connection details via the setup wizard, then
    // ask them to restart. Skip migration entirely - there is no DB to migrate yet.
    if (externalCredsMissing)
    {
        logger.LogWarning(
            "POSTGRES_MODE=external but no host/password configured. Starting in setup-only mode; submit DB credentials via the UI and restart the container.");
    }
    else
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            logger.LogInformation("Checking database migrations...");

            // Allow long-running migrations (e.g., column type changes that rewrite large tables)
            dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

            // This will create the database if it doesn't exist and apply all pending migrations
            await dbContext.Database.MigrateAsync();
            await DatabaseSchemaFixer.ApplyPostMigrationAsync(dbContext, logger);

            logger.LogInformation("Database migrations applied successfully");

            // Verify connection
            var canConnect = await dbContext.Database.CanConnectAsync();
            if (!canConnect)
            {
                throw new Exception("Cannot connect to database after migration");
            }

            // Note: LancacheMetricsService will start automatically as IHostedService

            logger.LogInformation("Database initialization complete");

            // CachedDetectionSummary is derived data rather than schema state. Rebuild it only
            // when older persisted detection rows exist without their singleton summary.
            var detectionDataService = scope.ServiceProvider.GetRequiredService<GameCacheDetectionDataService>();
            await detectionDataService.ReconcileMissingDiskSummaryAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed");
            throw; // Fail fast if database init fails
        }
    }

    // Migrate operation files from old data directory to new operations subdirectory
    var migratedCount = pathResolver.MigrateOperationFiles();
    if (migratedCount > 0)
    {
        logger.LogInformation("Migrated {Count} operation files to operations directory", migratedCount);
    }

    // Clean up old operation progress files (cache_clear, corruption_removal, etc.)
    var cleanedCount = pathResolver.CleanupOperationFiles(maxAgeHours: 24);
    if (cleanedCount > 0)
    {
        logger.LogInformation("Cleaned up {Count} old operation files on startup", cleanedCount);
    }
}

// First start (no api_key.txt) prints the key. Later restarts only print the file path.
var apiKeyService = app.Services.GetRequiredService<ApiKeyService>();
// The flag is only set once the key file has been read or created, and the argument below is read
// before DisplayApiKey runs that read itself, so without this line a first start masks the key. [1]
apiKeyService.GetApiKey();
apiKeyService.DisplayApiKey(app.Configuration, revealKey: apiKeyService.WasNewKeyGenerated);

// If a new API key was generated (data folder was deleted), invalidate all old sessions
// so existing browser cookies cannot authenticate against the new key. The sessions being
// cleared live in the database, and this call sits outside every try, so on a setup-only boot
// an unguarded delete takes the process down before the user can reach the setup screen. No
// database means there are no sessions to clear, which is the outcome rather than an error.
if (apiKeyService.WasNewKeyGenerated && databaseAvailable)
{
    using var startupScope = app.Services.CreateScope();
    var sessionService = startupScope.ServiceProvider.GetRequiredService<SessionService>();
    await sessionService.ClearAllSessionsAsync();
}

// MUST be first: Handle forwarded headers from reverse proxies (nginx, Cloudflare, etc.)
// This ensures HttpContext.Connection.RemoteIpAddress returns the real client IP
app.UseForwardedHeaders();

// A proxy that terminates TLS but is not in KnownProxies leaves Request.Scheme as http, because
// UseForwardedHeaders above ignores X-Forwarded-Proto from an address it does not trust. Setting
// Security:ForceSecureCookies is the operator stating that TLS ends upstream, so apply that statement
// to the scheme once here and let everything downstream read one answer to "is this connection
// secure" instead of each cookie deciding for itself.
//
// The framework's antiforgery cookie is why this has to happen before the request reaches MVC:
// AddAntiforgery gives it CookieSecurePolicy.Always under the same setting, and the framework throws
// InvalidOperationException whenever that policy meets a request it considers plain HTTP. That throw
// lands on GET /api/auth/status, which issues the token and is the first call the page makes, so the
// whole app answers 500 on load rather than only refusing writes.
if (app.Configuration.GetValue<bool>("Security:ForceSecureCookies"))
{
    app.Use((context, next) =>
    {
        context.Request.Scheme = Uri.UriSchemeHttps;
        return next(context);
    });
}

// Security headers - applied to every HTTP response (API, SignalR negotiate, static files,
// SPA fallback). Placed before UseStaticFiles so OnPrepareResponse overrides do not drop these.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // OWASP-recommended denial of sensor/device features the admin UI never uses.
    headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    // Content-Security-Policy is REPORT-ONLY for now: it surfaces violations without blocking,
    // so it cannot break the SPA bundle. Promote to the enforcing "Content-Security-Policy"
    // header once this has run clean in production for a while.
    //   script-src pins the single inline bootstrap script in index.html (the anti-FOUC theme
    //     preloader) by sha256 hash, so scripts stay locked down without 'unsafe-inline'.
    //     Regenerate this hash if that <script> block ever changes.
    //   style-src needs 'unsafe-inline': React and chart.js mutate element styles at runtime,
    //     which CSP hashes/nonces cannot cover.
    //   img-src needs blob: for the runtime-generated themed SVG favicon (Web/src/utils/favicon.ts).
    headers["Content-Security-Policy-Report-Only"] =
        "default-src 'self'; " +
        "script-src 'self' 'sha256-SAxvfk+K4MeDHgAOLmC2tPmuwi84LZJI2iuI05nbePc='; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' https: data: blob:; connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none'; object-src 'none'; base-uri 'self'";
    await next();
});

app.UseCors("AllowAll");

// Response compression - skip for WebSocket/SignalR to avoid corrupting frames
app.UseWhen(
    context => !context.Request.Headers.ContainsKey("Upgrade")
            && !context.Request.Path.StartsWithSegments("/hubs"),
    appBuilder => appBuilder.UseResponseCompression()
);

// Global exception handler - must run early to catch all exceptions
app.UseGlobalExceptionHandler();

// GC management now runs as a scheduled BackgroundService (GcScheduledService) rather than
// a request-pipeline middleware - see Infrastructure/Services/GcScheduledService.cs.

// Serve static files (UseDefaultFiles rewrites / to /index.html for faster static serving)
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? string.Empty;
        var headers = ctx.Context.Response.Headers;
        if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) || path == "/" || path.EndsWith("/"))
        {
            headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            headers["Pragma"] = "no-cache";
            headers["Expires"] = "0";
        }
        else if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
        {
            headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
    }
});

// A browser opening /scalar without a session gets a bare 401 and renders a blank page,
// because the session handler answers every unauthenticated request that way for the SPA's
// benefit. Send navigations to the app root instead, where the sign-in screen lives, and
// leave the 401 in place for callers that did not ask for HTML so scripts still see it.
// This has to sit before UseAuthorization: that middleware writes the 401 and stops, so
// anything registered after it never runs on a failed authorization. The redirect is
// relative, which keeps it correct behind whatever host port the deployment publishes.
app.Use(async (context, next) =>
{
    await next();

    if (context.Response.StatusCode != StatusCodes.Status401Unauthorized ||
        context.Response.HasStarted ||
        !HttpMethods.IsGet(context.Request.Method) ||
        !context.Request.Path.StartsWithSegments("/scalar"))
    {
        return;
    }

    var wantsHtml = context.Request.Headers.Accept
        .Any(value => value is not null && value.Contains("text/html", StringComparison.OrdinalIgnoreCase));

    if (wantsHtml)
    {
        context.Response.Redirect("/");
    }
});

app.UseRouting();

// Rate limiting - must be after routing to access endpoint metadata
app.UseRateLimiter();

// ASP.NET Core authentication & authorization pipeline
// SessionAuthenticationHandler populates HttpContext.Items["Session"] for backward compatibility.
app.UseAuthentication();
app.UseAuthorization();

// MetricsAuthenticationMiddleware applies the optional API-key gate to /metrics.
// The endpoint itself is marked .AllowAnonymous() so UseAuthorization's FallbackPolicy
// does not reject it; this middleware then enforces RequireAuthForMetrics when enabled.
app.UseMiddleware<MetricsAuthenticationMiddleware>();

app.MapOpenApi().RequireAuthorization("AccountHolder");
app.MapScalarApiReference("/scalar", options =>
{
    options.WithOpenApiRoutePattern("/openapi/{documentName}.json");

    // Selects the documented header scheme in the reference UI. It never carries a value:
    // the caller types their own key and nothing is prefilled or persisted.
    options.AddPreferredSecuritySchemes(["ApiKey"]);

    // Keeps the sidebar and search to just method and path. The default shows each
    // operation's full summary text there, which turns the sidebar into a wall of
    // paragraphs; summaries and remarks still appear in full on the main detail panel.
    options.WithOperationTitleSource(OperationTitleSource.Path);

    // Without this, tags list in whatever order the controllers happen to be scanned
    // in, which reads as random. Alphabetical keeps the group list predictable to scan.
    options.SortTagsAlphabetically();

    // Groups each tag's endpoints by HTTP method (every GET together, then every POST,
    // and so on) instead of by path, so read-only calls are easy to tell apart from
    // the ones that change something.
    options.SortOperationsByMethod();
})
    .RequireAuthorization("AccountHolder")
    .Finally(endpointBuilder =>
    {
        for (var index = endpointBuilder.Metadata.Count - 1; index >= 0; index--)
        {
            if (endpointBuilder.Metadata[index] is IAllowAnonymous)
            {
                endpointBuilder.Metadata.RemoveAt(index);
            }
        }
    });

// Map endpoints
app.MapControllers();
app.MapHub<DownloadHub>("/hubs/downloads");
app.MapHub<SteamDaemonHub>("/hubs/steam-daemon");
app.MapHub<EpicPrefillDaemonHub>("/hubs/epic-prefill-daemon");
app.MapHub<BattleNetDaemonHub>("/hubs/battlenet-prefill-daemon");
app.MapHub<RiotDaemonHub>("/hubs/riot-prefill-daemon");
app.MapHub<XboxPrefillDaemonHub>("/hubs/xbox-prefill-daemon");

// Map Prometheus metrics endpoint for Grafana.
// AllowAnonymous bypasses the FallbackPolicy; MetricsAuthenticationMiddleware enforces
// the optional API-key check when Security:RequireAuthForMetrics is true.
app.MapPrometheusScrapingEndpoint().AllowAnonymous();

// Liveness. This is what the image's HEALTHCHECK probes, so it always answers 200 while the
// process is up: a setup-only boot is a container that needs a human, not a container to kill,
// and a non-2xx here gets it restarted by Swarm, a Kubernetes liveness probe or an autoheal
// sidecar before anyone can finish the wizard. The setup state is reported in the body instead.
// The flag is fixed for the life of the process, which matches the setup flow: credentials
// submitted through the wizard only take effect on restart.
app.MapGet("/health", () => Results.Ok(new HealthResponse
{
    Status = externalCredsMissing ? "setup-required" : "healthy",
    SetupRequired = externalCredsMissing,
    Timestamp = DateTime.UtcNow,
    Service = "LancacheManager",
    Version = Environment.GetEnvironmentVariable("LANCACHE_MANAGER_VERSION") ?? "dev"
})).AllowAnonymous();

// Version endpoint
app.MapGet("/api/version", () =>
{
    var version = Environment.GetEnvironmentVariable("LANCACHE_MANAGER_VERSION") ?? "dev";
    return Results.Ok(new LancacheManager.Models.VersionResponse { Version = version });
}).AllowAnonymous();

// Fallback to index.html for client-side routing
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/hubs") ||
        context.Request.Path.StartsWithSegments("/metrics"))
    {
        context.Response.StatusCode = 404;
        return;
    }

    var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(indexPath);
    }
    else
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("index.html not found");
    }
}).AllowAnonymous();

// Log depot count for diagnostics (non-blocking background task after scope is disposed)
_ = Task.Run(async () =>
{
    try
    {
        // Small delay to ensure app.Services is fully ready
        await Task.Delay(100);

        using var backgroundScope = app.Services.CreateScope();
        var backgroundContext = backgroundScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var backgroundLogger = backgroundScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        var depotCount = await backgroundContext.SteamDepotMappings.CountAsync();
        backgroundLogger.LogInformation("Database has {DepotCount} depot mappings", depotCount);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to check depot count: {ex.Message}");
    }
});

app.Run();

// Runs pre-DI; cannot use IPathResolver. Mirrors PathResolverBase.GetDataProtectionKeysPath()
static string FindProjectRootForDataProtection()
{
    var currentDir = Directory.GetCurrentDirectory();

    // Search up the directory tree
    var dir = new DirectoryInfo(currentDir);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Api")) &&
            Directory.Exists(Path.Combine(dir.FullName, "Web")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException($"Could not find project root from: {currentDir}");
}

// Helper function to migrate Data Protection keys from legacy path to new security directory
static void MigrateDataProtectionKeys(string legacyKeyPath, string securityDir, string dataProtectionKeyPath)
{
    try
    {
        if (!Directory.Exists(legacyKeyPath))
            return;

        Directory.CreateDirectory(securityDir);

        if (!Directory.Exists(dataProtectionKeyPath))
        {
            Directory.Move(legacyKeyPath, dataProtectionKeyPath);
            Console.WriteLine($"Migrated Data Protection keys to: {dataProtectionKeyPath}");
            return;
        }

        // Target directory already exists - merge files individually
        foreach (var file in Directory.GetFiles(legacyKeyPath))
        {
            var destFile = Path.Combine(dataProtectionKeyPath, Path.GetFileName(file));
            if (!File.Exists(destFile))
            {
                File.Move(file, destFile);
            }
        }

        if (Directory.GetFileSystemEntries(legacyKeyPath).Length == 0)
        {
            Directory.Delete(legacyKeyPath);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to migrate Data Protection keys: {ex.Message}");
    }
}

