using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Buffers;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
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

    private readonly ILogger<SetupController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AuthenticationHelper _authenticationHelper;

    public SetupController(
        ILogger<SetupController> logger,
        IPathResolver pathResolver,
        IDbContextFactory<AppDbContext> dbContextFactory,
        AuthenticationHelper authenticationHelper)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _dbContextFactory = dbContextFactory;
        _authenticationHelper = authenticationHelper;
    }

    /// <summary>
    /// POST /api/setup/credentials - Set the embedded PostgreSQL password.
    ///
    /// Anonymous at the routing layer but never open: a session is accepted when the caller has
    /// one, and the API key otherwise. A session cannot be created while the database is
    /// unreachable, because logging in writes a row to UserSessions, so requiring one made this
    /// endpoint unusable in exactly the broken-install case it exists to repair. The API key lives
    /// in a file and is validated without touching the database, which makes it the only proof of
    /// possession left during an outage. It stays gated because the statement below runs
    /// ALTER USER against a role that was created WITH SUPERUSER.
    ///
    /// The gate deliberately ignores Security:EnableAuthentication. Turning that flag off opens
    /// the fallback, default and named authorization policies alike, so reading it here would
    /// hand the ALTER USER below to anyone who can reach the API. [36]
    /// </summary>
    [AllowAnonymous]
    [HttpPost("credentials")]
    public async Task<IActionResult> SetCredentialsAsync([FromBody] SetupCredentialsRequest request)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            var apiKeyResult = _authenticationHelper.ValidateApiKey(HttpContext);
            if (!apiKeyResult.IsAuthenticated)
            {
                return StatusCode(
                    apiKeyResult.StatusCode,
                    ApiResponse.Error(apiKeyResult.ErrorMessage ?? "API key required"));
            }
        }

        // In external mode the user-managed Postgres isn't ours to ALTER. Route them
        // to the external endpoint, which validates and persists a connection-only config.
        var mode = Environment.GetEnvironmentVariable("POSTGRES_MODE") ?? "embedded";
        if (mode == "external")
        {
            return BadRequest(ApiResponse.Error(
                "POSTGRES_MODE=external is set. Use POST /api/setup/external to configure the external database connection."));
        }

        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(ApiResponse.Error("Password is required"));

        if (request.Password.Length < 8)
            return BadRequest(ApiResponse.Error("Password must be at least 8 characters"));

        // Reject passwords containing characters that cannot be safely serialized into an
        // ALTER USER ... PASSWORD '...' SQL literal (backslash is not standard-conforming in
        // Postgres string literals without E'', and control characters terminate the literal
        // on some drivers). Reject before any SQL is built.
        if (request.Password.AsSpan().IndexOfAny(_disallowedPasswordChars) >= 0)
            return BadRequest(ApiResponse.Error("Password contains disallowed characters."));

        var blockedPasswords = new[] { "lancache", "password", "12345678", "admin123", "qwerty123", "lancache1", "lancache123" };
        if (blockedPasswords.Contains(request.Password.ToLowerInvariant()))
            return BadRequest(ApiResponse.Error("This password is too common. Please choose a more secure password."));

        // Connect with the settings the app resolved at startup, not the raw appsettings string.
        // Program.cs layers POSTGRES_USER and POSTGRES_DB over that base, so reading it back here
        // is what makes a custom role or database name reach the ALTER USER below instead of
        // connecting as the appsettings default and failing with a role that does not exist. [35]
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var connectionSettings = new Npgsql.NpgsqlConnectionStringBuilder(
            dbContext.Database.GetConnectionString());

        // No username submitted means "the role this installation already runs as", which is the
        // one the entrypoint created from POSTGRES_USER.
        var username = string.IsNullOrWhiteSpace(request.Username)
            ? connectionSettings.Username
            : request.Username.Trim();

        if (string.IsNullOrWhiteSpace(username))
            return BadRequest(ApiResponse.Error("Username is required"));

        if (!Regex.IsMatch(username, "^[A-Za-z0-9_]+$"))
        {
            return BadRequest(ApiResponse.Error("Username may only contain letters, numbers, and underscores"));
        }

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
    /// POST /api/setup/external - Configure an external PostgreSQL connection.
    /// Validates the supplied connection details by opening a real connection, then
    /// persists them to postgres-credentials.json. Used in the cold-start UI fallback
    /// where POSTGRES_MODE=external is set but no env-var connection details were
    /// provided - the user supplies them via the wizard and then restarts the container.
    ///
    /// Gated the same way as SetCredentialsAsync: a session when the caller has one, the API key
    /// otherwise. What this writes is the connection every process in the container rebuilds its
    /// database settings from on the next start, so leaving it open would let anyone who can reach
    /// the port repoint the whole installation at a database of their choosing.
    ///
    /// The API key is what keeps this reachable at all. External mode without credentials boots
    /// with no database (see Program.cs), so no session can be created or validated in the state
    /// this screen appears in, while the key is a file that is read without touching the database.
    /// Security:EnableAuthentication is deliberately not consulted: turning that flag off opens the
    /// authorization policies, and reading it here would hand this endpoint to anyone with it off.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("external")]
    public async Task<IActionResult> SetExternalCredentialsAsync([FromBody] SetExternalDbCredentialsRequest request)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            var apiKeyResult = _authenticationHelper.ValidateApiKey(HttpContext);
            if (!apiKeyResult.IsAuthenticated)
            {
                return StatusCode(
                    apiKeyResult.StatusCode,
                    ApiResponse.Error(apiKeyResult.ErrorMessage ?? "API key required"));
            }
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

        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(ApiResponse.Error("Username is required"));

        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(ApiResponse.Error("Password is required"));

        // The embedded path's character rule applies here too. This password is persisted to the
        // credentials file and every other process rebuilds its own connection settings from that
        // file, and a backslash or a control character does not survive that round trip intact.
        // Reject before a connection is attempted or anything is written. [38]
        if (request.Password.AsSpan().IndexOfAny(_disallowedPasswordChars) >= 0)
            return BadRequest(ApiResponse.Error("Password contains disallowed characters."));

        // Validate the supplied credentials by attempting a real connection with a short timeout.
        // We intentionally don't run ALTER USER - the external Postgres isn't ours to manage.
        var validationBuilder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = request.Host.Trim(),
            Port = request.Port,
            Database = request.Database.Trim(),
            Username = request.Username.Trim(),
            Password = request.Password,
            Timeout = 10,
            CommandTimeout = 10
        };

        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(validationBuilder.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
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
            ["username"] = request.Username.Trim(),
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
                configPath, request.Host, request.Port, request.Database, request.Username);
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
