namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Result of a short-lived process run via <see cref="ProcessManager.RunAsync"/>.
/// </summary>
public class ProcessCommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;

    /// <summary>The command was stopped at its time limit and never answered, so it has no exit code.</summary>
    public bool TimedOut { get; init; }
}
