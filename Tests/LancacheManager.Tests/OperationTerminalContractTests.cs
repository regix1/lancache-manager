using LancacheManager.Core.Interfaces;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Two drift guards over the terminal contract. Both cover failures that already happened and that
/// nothing else can catch: they are agreements between places the compiler cannot compare.
/// </summary>
public sealed partial class OperationTerminalContractTests
{
    /// <summary>
    /// The backend decides terminal state in <see cref="OperationStatusExtensions.IsTerminal"/>; the
    /// notification bar decides it in its own hand-written TERMINAL_STATUSES list. The TypeScript
    /// status type is an alias of the backend enum, so the compiler checks the VALUES, but nothing
    /// checks that the terminal list is complete. Add a fifth terminal status to one side and the
    /// card silently stops being dismissible. That is how Skipped had to be added, by hand, twice.
    /// </summary>
    [Fact]
    public void TerminalStatusList_MatchesTheBackendTerminalStates()
    {
        var backend = Enum.GetValues<OperationStatus>()
            .Where(status => status.IsTerminal())
            .Select(status => status.ToWireString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var frontend = ReadFrontendTerminalStatuses();

        Assert.Equal(backend, frontend);
    }

    /// <summary>
    /// A terminal record may hide interface members it does not put on the wire, which is deliberate
    /// (see <see cref="IOperationComplete"/>). <c>Cancelled</c> is the one member that may not be
    /// hidden: an explicit implementation is invisible to the serializer, so the browser reads it as
    /// false and routes a cancelled run down the FAILED branch. Both cache scans shipped that way,
    /// hardcoded to <c>false</c> with a comment claiming the scan had no cancellation concept, while
    /// their cards carried a cancel button.
    /// </summary>
    [Fact]
    public void EveryTerminalRecord_SerializesCancelled()
    {
        var offenders = TerminalRecordTypes()
            .Where(type => !SerializesCancelled(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Proves the check above can actually fail. Without this a detector that silently matched
    /// nothing would pass forever and read as a guarantee. <see cref="HiddenCancelledProbe"/> hides
    /// Cancelled exactly the way the two cache scans did.
    /// </summary>
    [Fact]
    public void CancelledDetector_CatchesAnExplicitImplementation()
    {
        Assert.False(SerializesCancelled(typeof(HiddenCancelledProbe)));
        Assert.True(SerializesCancelled(typeof(VisibleCancelledProbe)));
    }

    /// <summary>
    /// A cancelled operation that carries no error must not describe itself as a failure. Callers
    /// used to pass a message purely to dodge the old "Operation failed" default, and the message
    /// they reached for was an attribution the code cannot support.
    /// </summary>
    [Fact]
    public void CancelledOperationWithNoError_DoesNotReadAsAFailure()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);

        var operationId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        tracker.CompleteOperation(operationId, success: false, cancelled: true);

        var operation = tracker.GetOperation(operationId);
        Assert.Equal(OperationStatus.Cancelled, operation!.Status);
        Assert.DoesNotContain("failed", operation.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Nothing may claim a cancellation came from a person. Every run links its token to the host's
    /// shutdown token, so a container restart reaches the same catch block as a click and would be
    /// recorded as a user action. This scans sources rather than behaviour because the claim is the
    /// text itself, and it came back repeatedly after being removed from the spots that were noticed.
    /// </summary>
    [Fact]
    public void NoSourceClaimsACancellationCameFromAUser()
    {
        var api = Path.Combine(FindRepositoryRoot(), "Api");
        var offenders = Directory
            .EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path)
                .Contains("cancelled by user", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> TerminalRecordTypes() =>
        typeof(IOperationComplete).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IOperationComplete).IsAssignableFrom(type));

    /// <summary>
    /// True when <c>Cancelled</c> reaches the wire: a public instance property that is not ignored.
    /// An explicit interface implementation is private, so it does not survive this check.
    /// </summary>
    private static bool SerializesCancelled(Type type)
    {
        var property = type.GetProperty(
            nameof(IOperationComplete.Cancelled),
            BindingFlags.Public | BindingFlags.Instance);

        return property != null && property.GetCustomAttribute<JsonIgnoreAttribute>() == null;
    }

    private static string[] ReadFrontendTerminalStatuses()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), "Web", "src", "contexts", "notifications", "notificationStatus.ts");
        var declaration = TerminalStatusesRegex().Match(File.ReadAllText(path));
        Assert.True(
            declaration.Success,
            $"TERMINAL_STATUSES declaration not found in {path}. If it moved or was renamed, this guard "
                + "must be pointed at its new home rather than deleted.");

        return QuotedNameRegex()
            .Matches(declaration.Groups[1].Value)
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    [GeneratedRegex(@"TERMINAL_STATUSES[^=]*=\s*\[(.*?)\]", RegexOptions.Singleline)]
    private static partial Regex TerminalStatusesRegex();

    [GeneratedRegex(@"'([a-zA-Z]+)'")]
    private static partial Regex QuotedNameRegex();

    /// <summary>Hides Cancelled behind an explicit implementation, so it never serializes.</summary>
    private sealed record HiddenCancelledProbe : IOperationComplete
    {
        public Guid? OperationId => null;
        public bool Success => false;
        public OperationStatus Status => OperationStatus.Failed;
        public string? Error => null;
        bool IOperationComplete.Cancelled => false;
    }

    /// <summary>Carries Cancelled as an ordinary property, the shape every wire record needs.</summary>
    private sealed record VisibleCancelledProbe : IOperationComplete
    {
        public Guid? OperationId => null;
        public bool Success => false;
        public OperationStatus Status => OperationStatus.Cancelled;
        public string? Error => null;
        public bool Cancelled => true;
    }
}
