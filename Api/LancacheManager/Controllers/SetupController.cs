using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Core.Interfaces;

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

    [HttpGet("status")]
    public IActionResult GetSetupStatus()
    {
        var configPath = _pathResolver.GetPostgresCredentialsPath();
        var needsSetup = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"))
                         && !System.IO.File.Exists(configPath);

        return Ok(new SetupInitStatusResponse
        {
            NeedsSetup = needsSetup,
            Configured = !needsSetup
        });
    }

    [HttpPost("credentials")]
    public async Task<IActionResult> SetCredentialsAsync([FromBody] SetupCredentialsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new SetupErrorResponse { Error = "Password is required" });

        var username = string.IsNullOrWhiteSpace(request.Username) ? "lancache" : request.Username;

        var configPath = _pathResolver.GetPostgresCredentialsPath();

        // Save to persistent config file
        var config = new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = request.Password
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            // Ensure config directory exists
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await System.IO.File.WriteAllTextAsync(configPath, json);
            _logger.LogInformation("PostgreSQL credentials saved to {ConfigPath} for user {Username}", configPath, username);
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
