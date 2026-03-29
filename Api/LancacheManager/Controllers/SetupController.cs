using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using System.Text.RegularExpressions;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SetupController : ControllerBase
{
    private readonly ILogger<SetupController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPathResolver _pathResolver;

    public SetupController(ILogger<SetupController> logger, IConfiguration configuration, IPathResolver pathResolver)
    {
        _logger = logger;
        _configuration = configuration;
        _pathResolver = pathResolver;
    }

    [Authorize]
    [HttpPost("credentials")]
    public async Task<IActionResult> SetCredentialsAsync([FromBody] SetupCredentialsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new SetupErrorResponse { Error = "Password is required" });

        if (request.Password.Length < 8)
            return BadRequest(new SetupErrorResponse { Error = "Password must be at least 8 characters" });

        var blockedPasswords = new[] { "lancache", "password", "12345678", "admin123", "qwerty123", "lancache1", "lancache123" };
        if (blockedPasswords.Contains(request.Password.ToLowerInvariant()))
            return BadRequest(new SetupErrorResponse { Error = "This password is too common. Please choose a more secure password." });

        var username = string.IsNullOrWhiteSpace(request.Username) ? "lancache" : request.Username.Trim();
        if (!Regex.IsMatch(username, "^[A-Za-z0-9_]+$"))
        {
            return BadRequest(new SetupErrorResponse { Error = "Username may only contain letters, numbers, and underscores" });
        }

        if (string.Equals(request.Password, username, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new SetupErrorResponse { Error = "Password cannot be the same as the username" });

        var configPath = _pathResolver.GetPostgresCredentialsPath();

        // Update the PostgreSQL user password first. Persisting credentials before this
        // can leave the system in a broken partial state if ALTER USER fails.
        try
        {
            var connStr = _configuration.GetConnectionString("DefaultConnection");
            using var conn = new Npgsql.NpgsqlConnection(connStr);
            await conn.OpenAsync();

            string alterUserSql;
            using (var buildSql = conn.CreateCommand())
            {
                // ALTER USER is a PostgreSQL utility statement, so bind parameters can't be
                // used directly for PASSWORD. Build the statement server-side with format()
                // so both the identifier and literal are escaped safely.
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
            return StatusCode(500, new SetupErrorResponse { Error = "Failed to set PostgreSQL password" });
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
            await System.IO.File.WriteAllTextAsync(tempPath, json);
            System.IO.File.Move(tempPath, configPath, true);

            _logger.LogInformation("PostgreSQL credentials saved to {ConfigPath} for user {Username}", configPath, username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write credentials config file");
            return StatusCode(500, new SetupErrorResponse { Error = "Failed to save credentials file" });
        }

        return Ok(new SetupCredentialsResponse
        {
            Success = true,
            Message = "Credentials saved. Restart the container to apply fully."
        });
    }
}
