using System.Diagnostics;
using System.Net.Sockets;
using Npgsql;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// On Windows dev environments, ensures a PostgreSQL Docker container is running
/// before the application attempts to connect. Starts existing stopped containers
/// or creates a new one if needed.
/// </summary>
public static class WindowsPostgresManager
{
    private const string ContainerName = "lancache-postgres";
    private const string PostgresImage = "postgres:16";
    private const int MaxWaitSeconds = 30;

    /// <summary>
    /// Ensures PostgreSQL is reachable. On Windows, if not reachable, attempts to
    /// start or create a Docker container automatically.
    /// </summary>
    public static async Task EnsurePostgresRunningAsync(string connectionString, ILogger logger)
    {
        if (!OperatingSystemDetector.IsWindows)
            return;

        var connBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = connBuilder.Host ?? "localhost";
        var port = connBuilder.Port;

        // Already running — nothing to do
        if (await IsPostgresReachableAsync(host, port))
        {
            logger.LogInformation("PostgreSQL is already running on {Host}:{Port}", host, port);
            return;
        }

        logger.LogInformation("PostgreSQL not reachable on {Host}:{Port}, attempting to start via Docker...", host, port);

        if (!await IsDockerAvailableAsync(logger))
        {
            logger.LogError("Docker is not available. Please install Docker Desktop or start the Docker service.");
            return;
        }

        // Try starting any existing postgres container that maps to our port
        if (await TryStartExistingContainerAsync(port, logger))
        {
            await WaitForPostgresAsync(host, port, logger);
            return;
        }

        // No existing container found — create one
        logger.LogInformation("No existing PostgreSQL container found for port {Port}. Creating '{Container}' ...",
            port, ContainerName);

        await CreateContainerAsync(connBuilder, logger);
        await WaitForPostgresAsync(host, port, logger);

        logger.LogInformation("PostgreSQL container '{Container}' is ready on {Host}:{Port}",
            ContainerName, host, port);
    }

    private static async Task<bool> IsPostgresReachableAsync(string host, int port)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsDockerAvailableAsync(ILogger logger)
    {
        try
        {
            var result = await RunDockerCommandAsync("info --format {{.ServerVersion}}");
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Docker check failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Looks for any stopped postgres container whose port mapping includes our target port,
    /// or falls back to the well-known container name.
    /// </summary>
    private static async Task<bool> TryStartExistingContainerAsync(int port, ILogger logger)
    {
        // First check for our named container
        var inspect = await RunDockerCommandAsync($"inspect --format {{{{.State.Status}}}} {ContainerName}");
        if (inspect.ExitCode == 0)
        {
            var status = inspect.Output.Trim();
            if (status == "running")
            {
                logger.LogInformation("Container '{Container}' is already running", ContainerName);
                return true;
            }

            logger.LogInformation("Starting existing container '{Container}' (was {Status})...", ContainerName, status);
            var start = await RunDockerCommandAsync($"start {ContainerName}");
            return start.ExitCode == 0;
        }

        // Search for any stopped postgres container mapping to our port
        var search = await RunDockerCommandAsync(
            $"ps -a --filter ancestor={PostgresImage} --format {{{{.Names}}}}");
        if (search.ExitCode == 0 && !string.IsNullOrWhiteSpace(search.Output))
        {
            var containerName = search.Output.Trim().Split('\n')[0].Trim();
            logger.LogInformation("Found existing postgres container '{Container}', starting...", containerName);
            var start = await RunDockerCommandAsync($"start {containerName}");
            return start.ExitCode == 0;
        }

        // Also search for any postgres image variant (e.g., postgres:15, postgres:17)
        var searchAny = await RunDockerCommandAsync(
            "ps -a --filter ancestor=postgres --format {{.Names}}");
        if (searchAny.ExitCode == 0 && !string.IsNullOrWhiteSpace(searchAny.Output))
        {
            var containerName = searchAny.Output.Trim().Split('\n')[0].Trim();
            logger.LogInformation("Found existing postgres container '{Container}', starting...", containerName);
            var start = await RunDockerCommandAsync($"start {containerName}");
            return start.ExitCode == 0;
        }

        return false;
    }

    private static async Task CreateContainerAsync(NpgsqlConnectionStringBuilder conn, ILogger logger)
    {
        var user = conn.Username ?? "lancache";
        var password = conn.Password ?? "lancache";
        var database = conn.Database ?? "lancache";
        var port = conn.Port;

        var args = $"run -d --name {ContainerName} " +
                   $"-e POSTGRES_USER={user} " +
                   $"-e POSTGRES_PASSWORD={password} " +
                   $"-e POSTGRES_DB={database} " +
                   $"-p {port}:5432 " +
                   $"--restart unless-stopped " +
                   $"{PostgresImage}";

        var result = await RunDockerCommandAsync(args);
        if (result.ExitCode != 0)
        {
            logger.LogError("Failed to create PostgreSQL container: {Error}", result.Error);
            throw new InvalidOperationException(
                $"Failed to create PostgreSQL Docker container. Exit code: {result.ExitCode}. Error: {result.Error}");
        }

        logger.LogInformation("Created PostgreSQL container '{Container}'", ContainerName);
    }

    private static async Task WaitForPostgresAsync(string host, int port, ILogger logger)
    {
        logger.LogInformation("Waiting for PostgreSQL to accept connections on {Host}:{Port}...", host, port);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < MaxWaitSeconds)
        {
            if (await IsPostgresReachableAsync(host, port))
            {
                logger.LogInformation("PostgreSQL is accepting connections (took {Elapsed:F1}s)", sw.Elapsed.TotalSeconds);
                return;
            }

            await Task.Delay(500);
        }

        logger.LogWarning("PostgreSQL did not become reachable within {Seconds}s — migration may fail", MaxWaitSeconds);
    }

    private static async Task<DockerResult> RunDockerCommandAsync(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new DockerResult(process.ExitCode, output, error);
    }

    private sealed record DockerResult(int ExitCode, string Output, string Error);
}
