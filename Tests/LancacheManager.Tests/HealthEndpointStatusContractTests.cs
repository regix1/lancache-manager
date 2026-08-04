using System.Text.RegularExpressions;

namespace LancacheManager.Tests;

/// <summary>
/// What the image's HEALTHCHECK is allowed to conclude about a container that booted without a
/// database.
///
/// A setup-only boot happens whenever external mode has no credentials yet, which is the state the
/// setup wizard exists to get the user out of. The container is up, serving, and waiting for a
/// person. If the path the HEALTHCHECK probes answers with a non-2xx there, Docker marks the
/// container unhealthy after three tries, and Swarm, a Kubernetes liveness probe or an autoheal
/// sidecar restarts it before anyone can finish the wizard; a compose file using
/// <c>depends_on: condition: service_healthy</c> waits forever. So the probed path reports the
/// setup state in its body and keeps its status at 200. [32]
/// </summary>
public class HealthEndpointStatusContractTests
{
    [Fact]
    public void TheProbedPathIsMappedInProgram()
    {
        Assert.Contains(
            $"app.MapGet(\"{ProbedPath()}\"",
            ReadRepoFile("Api", "LancacheManager", "Program.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbedPathNeverAnswersWithAFailureStatus()
    {
        var handler = EndpointBody(ProbedPath());

        Assert.DoesNotContain("StatusCodes.Status5", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusCodes.Status4", handler, StringComparison.Ordinal);
        Assert.Contains("Results.Ok", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbedPathStillReportsThatSetupIsOutstanding()
    {
        var handler = EndpointBody(ProbedPath());

        Assert.Contains("externalCredsMissing", handler, StringComparison.Ordinal);
        Assert.Contains("setup-required", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// The URL path the image's HEALTHCHECK asks for, read out of the Dockerfile rather than
    /// assumed, so moving the probe moves what these tests check.
    /// </summary>
    private static string ProbedPath()
    {
        var command = HealthCheckCommand();
        var url = Regex.Match(command, @"https?://[^\s""']+");
        Assert.True(url.Success, $"No URL in the HEALTHCHECK command: {command}");

        return new Uri(url.Value).AbsolutePath;
    }

    private static string HealthCheckCommand()
    {
        var dockerfile = ReadRepoFile("Dockerfile");
        var directive = Regex.Match(
            dockerfile,
            @"^HEALTHCHECK(?:[^\n]*\\\s*\n)*[^\n]*$",
            RegexOptions.Multiline);

        Assert.True(directive.Success, "The Dockerfile has no HEALTHCHECK directive");

        return directive.Value;
    }

    /// <summary>
    /// The text of one minimal-API handler: everything from its <c>app.MapGet</c> to the start of
    /// the next endpoint, so a status code written for a different route cannot satisfy or break
    /// these assertions.
    /// </summary>
    private static string EndpointBody(string route)
    {
        var program = ReadRepoFile("Api", "LancacheManager", "Program.cs");
        var start = program.IndexOf($"app.MapGet(\"{route}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Program.cs does not map {route}");

        var next = program.IndexOf("app.Map", start + 1, StringComparison.Ordinal);

        return next >= 0 ? program[start..next] : program[start..];
    }

    private static string ReadRepoFile(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, .. pathSegments]));
    }
}
