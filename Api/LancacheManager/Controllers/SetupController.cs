using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SetupController : ControllerBase
{
    private const string ConfigPath = "/data/postgres-credentials.json";
    private readonly ILogger<SetupController> _logger;
    private readonly IConfiguration _configuration;

    public SetupController(ILogger<SetupController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public IActionResult GetSetupStatus()
    {
        var needsSetup = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"))
                         && !System.IO.File.Exists(ConfigPath);

        return Ok(new SetupInitStatusResponse
        {
            NeedsSetup = needsSetup,
            Configured = !needsSetup
        });
    }

    [HttpPost("credentials")]
    public async Task<IActionResult> SetCredentials([FromBody] SetupCredentialsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new SetupErrorResponse { Error = "Password is required" });

        var username = string.IsNullOrWhiteSpace(request.Username) ? "lancache" : request.Username;

        // Save to persistent config file
        var config = new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = request.Password
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            // Ensure /data directory exists
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await System.IO.File.WriteAllTextAsync(ConfigPath, json);
            _logger.LogInformation("PostgreSQL credentials saved to {ConfigPath} for user {Username}", ConfigPath, username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write credentials config file");
            return StatusCode(500, new SetupErrorResponse { Error = $"Failed to save config file: {ex.Message}" });
        }

        // Update the PostgreSQL user password via raw SQL using the app's connection string
        try
        {
            var connStr = _configuration.GetConnectionString("DefaultConnection");
            var connBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connStr);
            connBuilder.Username = username;
            using var conn = new Npgsql.NpgsqlConnection(connBuilder.ConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER USER \"{username}\" WITH PASSWORD '{request.Password.Replace("'", "''")}'";
            await cmd.ExecuteNonQueryAsync();
            _logger.LogInformation("PostgreSQL password updated for user {Username}", username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set PostgreSQL password for user {Username}", username);
            return StatusCode(500, new SetupErrorResponse { Error = $"Failed to set PostgreSQL password: {ex.Message}" });
        }

        return Ok(new SetupCredentialsResponse
        {
            Success = true,
            Message = "Credentials saved. Restart the container to apply fully."
        });
    }
}
