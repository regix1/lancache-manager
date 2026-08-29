using LancacheManager.Validators;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Buffers;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Security;
using System.Text.RegularExpressions;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SetupController : ControllerBase
{
    private static readonly SearchValues<char> _disallowedPasswordChars = SearchValues.Create("\\\r\n\0");

    private static readonly string[] _blockedPasswords =
        { "lancache", "password", "12345678", "admin123", "qwerty123", "lancache1", "lancache123" };

    private readonly ILogger<SetupController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AuthenticationHelper _authenticationHelper;
    private readonly IConfiguration _configuration;
    private readonly IAntiforgery _antiforgery;
    private readonly AccountClaimWindow _claimWindow;

    public SetupController(
        ILogger<SetupController> logger,
        IPathResolver pathResolver,
        IDbContextFactory<AppDbContext> dbContextFactory,
        AuthenticationHelper authenticationHelper,
        IConfiguration configuration,
        IAntiforgery antiforgery,
        AccountClaimWindow claimWindow)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _dbContextFactory = dbContextFactory;
        _authenticationHelper = authenticationHelper;
        _configuration = configuration;
        _antiforgery = antiforgery;
        _claimWindow = claimWindow;
    }

    /// <summary>
    /// The two endpoints below take the API key as their only proof while authentication is disabled.
    ///
    /// Turning Security:EnableAuthentication off opens the fallback, default and named authorization
    /// policies alike AND gives every request an admin session, so a caller that presented nothing at
    /// all arrives here reading as authenticated. Trusting the principal in that state hands the
    /// statements these two endpoints run to anyone who can reach the port, which is why the flag is
    /// read here rather than ignored: with it off the principal proves nothing and only the key counts.
    ///
    /// The key is also what keeps these reachable at all. A session cannot be created while the
    /// database is unreachable, because logging in writes a row to UserSessions, so requiring one made
    /// them unusable in exactly the broken-install case they exist to repair. The key lives in a file
    /// and is validated without touching the database.
    ///
    /// The key is read from the request (the X-Api-Key header, or Authorization: Bearer) and is
    /// checked first, so a caller carrying one is answered exactly as it always was and the bootstrap
    /// and repair paths keep working with no session and no antiforgery token. A caller admitted on a
    /// session instead is holding nothing but cookies, which a browser attaches to a request another
    /// origin caused, so that caller is asked for the antiforgery token like every other write. Both
    /// endpoints opt out of the global check (Infrastructure/Filters/AntiforgeryFilter.cs) because the
    /// opt-out is per route and cannot tell the two callers apart.
    ///
    /// The session has to be an account holder's, not merely an authenticated one. A guest session is
    /// authenticated, and anybody who can reach the port can start one and then read the antiforgery
    /// token off the anonymous status endpoint, which would leave the two statements below reachable
    /// with nothing proved at all. AccountSetupController accepts no session at all for the first-admin
    /// endpoint, and shares the reason: a guest must not be able to claim the installation.
    ///
    /// Returns null when the caller may proceed.
    /// </summary>
    private async Task<ObjectResult?> RequireApiKeyAsync()
    {
        var apiKeyResult = _authenticationHelper.ValidateApiKey(HttpContext);
        if (apiKeyResult.IsAuthenticated)
        {
            return null;
        }

        var authenticationEnabled = _configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (authenticationEnabled && HttpContext.GetUserSession()?.SessionType.IsAccountHolder() == true)
        {
            if (await _antiforgery.IsRequestValidAsync(HttpContext))
            {
                return null;
            }

            return BadRequest(ApiResponse.Error(AntiforgeryToken.MissingTokenMessage));
        }

        return StatusCode(
            apiKeyResult.StatusCode,
            ApiResponse.Error(apiKeyResult.ErrorMessage ?? "API key required"));
    }

    /// <summary>
    /// Sets the embedded PostgreSQL password. The new password must be at least 12 characters
    /// and use three character classes.
    /// </summary>
    /// <remarks>
    /// Anonymous at the routing layer but never open: the API key is checked first, and a session
    /// is consulted only when no valid key was presented, and then only together with the
    /// antiforgery token. A session cannot be created while the database is unreachable, because
    /// logging in writes a row to UserSessions, so requiring one made this endpoint unusable in
    /// exactly the broken-install case it exists to repair. The API key lives in a file and is
    /// validated without touching the database, which makes it the only proof of possession left
    /// during an outage. It stays gated because the statement below runs ALTER USER against a role
    /// that was created WITH SUPERUSER.
    ///
    /// See <see cref="RequireApiKeyAsync"/> for why the session is only accepted while authentication
    /// is enabled, and for the antiforgery token the session caller is asked for.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    // Out of the global antiforgery check so the API key on its own still reaches a repair endpoint
    // that has to work when the database is down and no session or token can exist. The key is read
    // from the request headers, and a page on another origin can set neither those nor read the key.
    // The check is not dropped for the session caller: this endpoint also accepts one, and
    // RequireApiKeyAsync asks that caller for the token, which the opt-out cannot do per route.
    [IgnoreAntiforgeryToken]
    [HttpPost("credentials")]
    [ProducesResponseType(typeof(SetupCredentialsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetupCredentialsResponse>> SetCredentialsAsync([FromBody] SetupCredentialsRequest request)
    {
        var denied = await RequireApiKeyAsync();
        if (denied != null)
        {
            return denied;
        }

        // In external mode the user-managed Postgres isn't ours to ALTER. Route them
        // to the external endpoint, which validates and persists a connection-only config.
        var mode = Environment.GetEnvironmentVariable("POSTGRES_MODE") ?? "embedded";
        if (mode == "external")
        {
            return BadRequest(ApiResponse.Error(
                "POSTGRES_MODE=external is set. Use POST /api/setup/external to configure the external database connection."));
        }

        var passwordProblem = CheckNewRolePassword(request.Password);
        if (passwordProblem != null)
            return BadRequest(ApiResponse.Error(passwordProblem));

        // Connect with the settings the app resolved at startup, not the raw appsettings string.
        // Program.cs layers POSTGRES_USER and POSTGRES_DB over that base, so reading it back here
        // is what makes a custom role or database name reach the ALTER USER below instead of
        // connecting as the appsettings default and failing with a role that does not exist.
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var connectionSettings = new Npgsql.NpgsqlConnectionStringBuilder(
            dbContext.Database.GetConnectionString());

        // A database that cannot be reached is the normal case on this endpoint, not the exception,
        // so the caller must not sit through Npgsql's 15 second default before being told. The
        // connection string resolved at startup carries no timeout of its own, and the external
        // endpoint already uses these values for the same reason.
        connectionSettings.Timeout = 10;
        connectionSettings.CommandTimeout = 10;

        // No username submitted means "the role this installation already runs as", which is the
        // one the entrypoint created from POSTGRES_USER.
        var username = string.IsNullOrWhiteSpace(request.Username)
            ? connectionSettings.Username ?? string.Empty
            : request.Username.Trim();

        var usernameProblem = CheckUsername(username);
        if (usernameProblem != null)
            return BadRequest(ApiResponse.Error(usernameProblem));

        if (string.Equals(request.Password, username, StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiResponse.Error("Password cannot be the same as the username"));

        var configPath = _pathResolver.GetPostgresCredentialsPath();

        // Update the PostgreSQL user password first. Persisting credentials before this
        // can leave the system in a broken partial state if ALTER USER fails.
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(connectionSettings.ConnectionString);
            await conn.OpenAsync();

            string alterUserSql;
            using (var buildSql = conn.CreateCommand())
            {
                // ALTER USER is a PostgreSQL utility statement, so bind parameters can't be
                // used directly for PASSWORD. Build the statement server-side with format()
                // so both the identifier and literal are escaped safely. The raw value goes in:
                // %L quotes whatever it is handed, so doubling the quotes here as well would
                // store a password containing ' with two of them and leave the role's password
                // different from the one written to the credentials file below.
                buildSql.CommandText =
                    "SELECT format('ALTER USER %I WITH PASSWORD %L', @username, @password)";
                buildSql.Parameters.AddWithValue("username", username);
                buildSql.Parameters.AddWithValue("password", request.Password);
                alterUserSql = (string?)await buildSql.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("Failed to build ALTER USER statement.");
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = alterUserSql;
            await cmd.ExecuteNonQueryAsync();
            _logger.LogInformation("PostgreSQL password updated for user {Username}", username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set PostgreSQL password for user {Username}", username);
            return StatusCode(500, ApiResponse.Error("Failed to set PostgreSQL password"));
        }

        // Save credentials only after ALTER USER succeeds.
        var config = new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = request.Password
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = configPath + ".tmp";

            await WriteOwnerOnlyFileAsync(tempPath, json);

            System.IO.File.Move(tempPath, configPath, true);

            // Restrict to owner read/write only on POSIX. On Windows, ACLs are managed separately
            // and File.SetUnixFileMode is not supported.
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    System.IO.File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception modeEx)
                {
                    // Non-fatal: the file is still written. Log and continue.
                    _logger.LogWarning(modeEx, "Failed to set 0600 permissions on {ConfigPath}", configPath);
                }
            }

            _logger.LogInformation("PostgreSQL credentials saved to {ConfigPath} for user {Username}", configPath, username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write credentials config file");
            return StatusCode(500, ApiResponse.Error("Failed to save credentials file"));
        }

        return Ok(new SetupCredentialsResponse
        {
            Success = true,
            Message = "Credentials saved. Restart the container to apply fully."
        });
    }

    /// <summary>
    /// Configures an external PostgreSQL connection.
    /// </summary>
    /// <remarks>
    /// Validates the supplied connection details by opening a real connection, then persists
    /// them to postgres-credentials.json. Used in the cold-start UI fallback where
    /// POSTGRES_MODE=external is set but no env-var connection details were provided - the user
    /// supplies them via the wizard and then restarts the container.
    ///
    /// Gated the same way as SetCredentialsAsync: the API key first, and a session only when no
    /// valid key was presented and the antiforgery token comes with it. What this writes is the
    /// connection every process in the container rebuilds its database settings from on the next
    /// start, so leaving it open would let anyone who can reach the port repoint the whole
    /// installation at a database of their choosing.
    ///
    /// The API key is what keeps this reachable at all. External mode without credentials boots
    /// with no database (see Program.cs), so no session can be created or validated in the state
    /// this screen appears in, while the key is a file that is read without touching the database.
    /// See <see cref="RequireApiKeyAsync"/> for why the session is only accepted while authentication
    /// is enabled, and for the antiforgery token the session caller is asked for.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    // Out of the global antiforgery check for the same reason as the credentials endpoint above: the
    // key in the request headers is the proof, and this runs in the state where there is no database
    // to hold a session. The session caller it also accepts is asked for the token inside
    // RequireApiKeyAsync.
    [IgnoreAntiforgeryToken]
    [HttpPost("external")]
    [ProducesResponseType(typeof(SetExternalDbCredentialsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetExternalDbCredentialsResponse>> SetExternalCredentialsAsync([FromBody] SetExternalDbCredentialsRequest request)
    {
        var denied = await RequireApiKeyAsync();
        if (denied != null)
        {
            return denied;
        }

        // Second lock, the same one first-admin creation and password recovery are held to. This
        // endpoint decides which database every process in the container rebuilds its settings
        // from, so a key that leaked on its own could otherwise repoint the whole installation at a
        // server the caller controls, and the claim window that opens on the next start would let
        // them take the fresh database as its first administrator. Requiring the window means that
        // also costs a restart, which needs the host the key is stored on.
        if (!_claimWindow.IsOpen)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse.Error(
                    "The window for changing the database connection has closed. Restart the application to reopen it."));
        }

        var mode = Environment.GetEnvironmentVariable("POSTGRES_MODE") ?? "embedded";
        if (mode != "external")
        {
            return BadRequest(ApiResponse.Error(
                "External-mode endpoint called while POSTGRES_MODE is not 'external'. Set POSTGRES_MODE=external in your environment first."));
        }

        if (string.IsNullOrWhiteSpace(request.Host))
            return BadRequest(ApiResponse.Error("Host is required"));

        if (request.Port <= 0 || request.Port > 65535)
            return BadRequest(ApiResponse.Error("Port must be between 1 and 65535"));

        if (string.IsNullOrWhiteSpace(request.Database))
            return BadRequest(ApiResponse.Error("Database name is required"));

        // The same username rule as the embedded path, and for a sharper reason than tidiness: this
        // name is persisted to the credentials file, and entrypoint.sh reads it back and interpolates
        // it into a shell command and an ALTER ROLE statement on the next start. Anything outside
        // letters, numbers and underscores would be running as the postgres superuser by then, so it
        // must never reach the file.
        var username = (request.Username ?? string.Empty).Trim();
        var usernameProblem = CheckUsername(username);
        if (usernameProblem != null)
            return BadRequest(ApiResponse.Error(usernameProblem));

        var passwordProblem = CheckPassword(request.Password);
        if (passwordProblem != null)
            return BadRequest(ApiResponse.Error(passwordProblem));

        // Validate the supplied credentials by attempting a real connection with a short timeout.
        // We intentionally don't run ALTER USER - the external Postgres isn't ours to manage.
        var validationBuilder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = request.Host.Trim(),
            Port = request.Port,
            Database = request.Database.Trim(),
            Username = username,
            Password = request.Password,
            Timeout = 10,
            CommandTimeout = 10
        };

        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(validationBuilder.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Asking whether the role can create proves the connection and the one right the schema
            // needs, where SELECT 1 only proved the connection. The schema installs the citext
            // extension, and PostgreSQL requires CREATE on the database itself for that - CREATE on
            // schema public is not enough, and is the natural thing to grant since PostgreSQL 15
            // stopped granting it by default. Refusing here leaves the operator on this page;
            // accepting a role that cannot create means the next start throws in database
            // initialization, before app.Run(), so the container restarts into the same failure with
            // no page left to serve.
            cmd.CommandText = "SELECT has_database_privilege(current_user, current_database(), 'CREATE')";
            if (await cmd.ExecuteScalarAsync() is false)
            {
                return BadRequest(ApiResponse.Error(
                    $"The role '{username}' cannot create objects in '{request.Database.Trim()}'",
                    "The schema installs the citext extension, which PostgreSQL only allows for a role holding CREATE on the database. Grant it with GRANT CREATE ON DATABASE, or ask a database administrator to run CREATE EXTENSION citext in that database first."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External DB credential validation failed for {Host}:{Port}", request.Host, request.Port);
            return BadRequest(ApiResponse.Error(
                $"Could not connect to {request.Host}:{request.Port}/{request.Database}",
                ex.Message));
        }

        // Persist to postgres-credentials.json with extended schema (host/port/database).
        // Same atomic rename pattern as SetCredentialsAsync.
        var configPath = _pathResolver.GetPostgresCredentialsPath();
        var config = new Dictionary<string, object>
        {
            ["username"] = username,
            ["password"] = request.Password,
            ["host"] = request.Host.Trim(),
            ["port"] = request.Port,
            ["database"] = request.Database.Trim()
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = configPath + ".tmp";

            await WriteOwnerOnlyFileAsync(tempPath, json);

            System.IO.File.Move(tempPath, configPath, true);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    System.IO.File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception modeEx)
                {
                    _logger.LogWarning(modeEx, "Failed to set 0600 permissions on {ConfigPath}", configPath);
                }
            }

            _logger.LogInformation(
                "External PostgreSQL credentials saved to {ConfigPath} (target {Host}:{Port}/{Database} as {Username})",
                configPath, request.Host, request.Port, request.Database, username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write external credentials config file");
            return StatusCode(500, ApiResponse.Error("Failed to save credentials file"));
        }

        return Ok(new SetExternalDbCredentialsResponse
        {
            Success = true,
            Message = "External database credentials saved. Restart the container to apply.",
            RestartRequired = true
        });
    }

    /// <summary>
    /// The password an existing PostgreSQL server is already using. The external endpoint has to
    /// accept that value so a role that was created before these rules still connects. A new
    /// embedded role password goes through <see cref="CheckNewRolePassword"/> instead.
    ///
    /// The character rule is the one with teeth: the value is written to postgres-credentials.json,
    /// and the .NET app, the Rust binaries and entrypoint.sh all rebuild their connection settings by
    /// reading that file back, which a backslash or a control character does not survive intact.
    ///
    /// The length ceiling is here rather than beside the embedded rules so both endpoints stop at the
    /// same place: the value ends up in the same file whichever of the two wrote it.
    /// </summary>
    private static string? CheckPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return "Password is required";

        if (password.Length < 8)
            return "Password must be at least 8 characters";

        if (password.Length > 256)
            return "Password cannot exceed 256 characters";

        if (password.AsSpan().IndexOfAny(_disallowedPasswordChars) >= 0)
            return "Password contains disallowed characters.";

        if (_blockedPasswords.Contains(password, StringComparer.OrdinalIgnoreCase))
            return "This password is too common. Please choose a more secure password.";

        return null;
    }

    /// <summary>
    /// The password written onto the embedded role by ALTER USER. That role is created WITH
    /// SUPERUSER, so this matches the account-password classes rather than the 8-character floor
    /// <see cref="CheckPassword"/> keeps for an already-running external server.
    /// </summary>
    private static string? CheckNewRolePassword(string password)
    {
        var existing = CheckPassword(password);
        if (existing != null)
        {
            return existing;
        }

        if (password.Length < PasswordRules.AccountMinimumLength)
            return PasswordRules.MinimumLengthMessage;

        if (!PasswordRules.UsesThreeCharacterClasses(password))
        {
            return PasswordRules.CharacterClassesMessage;
        }

        return null;
    }

    /// <summary>
    /// The username rule both setup endpoints apply. Returns the sentence to send back, or null when
    /// the name passes.
    ///
    /// The character set is narrow because this name outlives the request: it is persisted to
    /// postgres-credentials.json, and entrypoint.sh reads it back on the next start and interpolates
    /// it into a shell command and an ALTER ROLE statement that runs with superuser rights. The
    /// embedded path also puts it through format('%I'), which is safe on its own but is not the only
    /// place the value ends up.
    /// </summary>
    private static string? CheckUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "Username is required";

        if (!Regex.IsMatch(username, "^[A-Za-z0-9_]+$"))
            return "Username may only contain letters, numbers, and underscores";

        return null;
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> with owner-only (0600)
    /// permissions applied BEFORE the bytes are written, so a plaintext password never has a
    /// world-readable window on POSIX. On Windows UnixCreateMode is unsupported (throws), so we
    /// fall back to a plain write there - ACLs are managed separately. Callers are expected to
    /// File.Move this into place and re-apply SetUnixFileMode(0600) post-move as defense-in-depth.
    /// </summary>
    private static async Task WriteOwnerOnlyFileAsync(string path, string contents)
    {
        if (!OperatingSystem.IsWindows())
        {
            var tempStreamOptions = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            };
            await using var tempStream = new FileStream(path, tempStreamOptions);
            await using var tempWriter = new StreamWriter(tempStream);
            await tempWriter.WriteAsync(contents);
        }
        else
        {
            await System.IO.File.WriteAllTextAsync(path, contents);
        }
    }
}
