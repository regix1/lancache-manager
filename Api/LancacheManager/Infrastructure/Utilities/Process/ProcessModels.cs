namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Result of a short-lived process run via <see cref="ProcessManager.RunAsync"/>.
/// </summary>
public class ProcessCommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
