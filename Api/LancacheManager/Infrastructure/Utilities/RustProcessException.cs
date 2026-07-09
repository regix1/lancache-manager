namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Thrown when a Rust child process exits with a non-zero code. Produced centrally by
/// <see cref="ProcessExecutionResult.EnsureSuccess"/> to replace the scattered
/// <c>throw new Exception($"... exit code {result.ExitCode}: {result.Error}")</c> sites, so callers
/// can <c>catch (RustProcessException)</c> and branch/log on the structured fields.
///
/// The user-facing <see cref="System.Exception.Message"/> stays a safe, generic sentence
/// ("The &lt;tool&gt; process failed (exit &lt;code&gt;)") and NEVER contains raw stderr — the real
/// process output is preserved on <see cref="Stderr"/> for logging only.
/// </summary>
public class RustProcessException : Exception
{
    /// <summary>The non-zero exit code the Rust process terminated with.</summary>
    public int ExitCode { get; }

    /// <summary>
    /// Raw stderr captured from the process. For diagnostics/logging ONLY — deliberately kept out of
    /// <see cref="System.Exception.Message"/> so it never leaks to the client. May be null/empty.
    /// </summary>
    public string? Stderr { get; }

    /// <summary>The Rust tool/binary name that failed (e.g. "cache_cleaner", "corruption_manager").</summary>
    public string Tool { get; }

    /// <summary>
    /// Creates a Rust-process failure. <paramref name="context"/> is an optional caller-supplied,
    /// user-safe descriptor (e.g. a datasource or service name) folded into the message; it must
    /// never be raw stderr. Pass the process stderr via <paramref name="stderr"/> instead — it is
    /// retained for logging but excluded from the message.
    /// </summary>
    public RustProcessException(string tool, int exitCode, string? stderr = null, string? context = null)
        : base(BuildMessage(tool, exitCode, context))
    {
        Tool = tool;
        ExitCode = exitCode;
        Stderr = stderr;
    }

    private static string BuildMessage(string tool, int exitCode, string? context)
    {
        return string.IsNullOrWhiteSpace(context)
            ? $"The {tool} process failed (exit {exitCode})"
            : $"The {tool} process failed for {context} (exit {exitCode})";
    }
}
