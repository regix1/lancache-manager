using System.Text.Json.Serialization;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Schema for the legacy log-rotation-settings.json file. Used only for one-time
/// migration into state.json - after migration this file is deleted and never
/// re-created. Do not reference outside the migration code path.
/// </summary>
internal class LegacyLogRotationSettings
{
    public int ScheduleHours { get; set; } = 24;
}

/// <summary>
/// Action that can make nginx log reopen available to the manager.
/// </summary>
[JsonConverter(typeof(NginxReopenHintJsonConverter))]
public enum NginxReopenHint
{
    None,
    GrantSignalPrivilege,
    EnablePidHost,
    MountDockerSocket,
    UseLinuxManager
}

[JsonConverter(typeof(NginxReopenRequirementJsonConverter))]
public enum NginxReopenRequirement
{
    NotRequired,
    Required,
    Unknown
}

[JsonConverter(typeof(NginxReopenStatusJsonConverter))]
public enum NginxReopenStatus
{
    NotRequired,
    Succeeded,
    Failed
}

public enum NginxWriterKind
{
    Host,
    Docker
}

public sealed record NginxFileIdentity(ulong First, ulong Second);

public sealed record NginxWriterIdentity(
    NginxWriterKind Kind,
    string Name,
    int ProcessId,
    string StartIdentity);

public sealed record NginxWriterMatch(
    NginxWriterIdentity Writer,
    bool Matches);

public sealed record NginxPublicationExpectation(
    string TargetPath,
    NginxFileIdentity OriginalIdentity);

public sealed record NginxPublicationCheckFile(
    bool Valid,
    IReadOnlyList<NginxPublicationExpectation> Files);

public sealed record NginxPublicationRecord(
    string TargetPath,
    NginxFileIdentity OriginalIdentity,
    NginxFileIdentity? TemporaryIdentity,
    NginxFileIdentity? PublishedIdentity,
    bool Changed,
    bool Deleted);

public sealed record NginxPublicationResult(
    bool Success,
    IReadOnlyList<NginxPublicationRecord> Files);

public sealed class NginxHeldProof : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal NginxHeldProof(string path, NginxFileIdentity identity, Func<bool> validate, Action release)
    {
        Path = path;
        Identity = identity;
        Validate = validate;
        _release = release;
    }

    public string Path { get; }
    public NginxFileIdentity Identity { get; }
    internal Func<bool> Validate { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }
    }
}

public sealed class NginxReopenCheck : IAsyncDisposable
{
    private int _disposed;
    internal NginxReopenCheck(
        IReadOnlyList<string> datasourceNames,
        IReadOnlyList<string> affectedPaths,
        IReadOnlyDictionary<string, NginxFileIdentity> originalIdentities,
        IReadOnlyList<NginxWriterIdentity> writers,
        NginxReopenRequirement requirement,
        IReadOnlyList<NginxHeldProof> proofs,
        string? checkPath,
        string? resultPath,
        bool expectsPublication)
    {
        DatasourceNames = datasourceNames;
        AffectedPaths = affectedPaths;
        OriginalIdentities = originalIdentities;
        Writers = writers;
        Requirement = requirement;
        Proofs = proofs;
        CheckPath = checkPath;
        ResultPath = resultPath;
        ExpectsPublication = expectsPublication;
    }

    public IReadOnlyList<string> DatasourceNames { get; }
    public IReadOnlyList<string> AffectedPaths { get; }
    public IReadOnlyDictionary<string, NginxFileIdentity> OriginalIdentities { get; }
    public IReadOnlyList<NginxWriterIdentity> Writers { get; }
    public NginxReopenRequirement Requirement { get; }
    internal IReadOnlyList<NginxHeldProof> Proofs { get; }
    internal string? CheckPath { get; }
    internal string? ResultPath { get; }
    internal bool ExpectsPublication { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Exception? cleanupError = null;
        foreach (var proof in Proofs)
        {
            try
            {
                proof.Dispose();
            }
            catch (Exception error)
            {
                cleanupError ??= error;
            }
        }

        foreach (var path in new[]
        {
            CheckPath,
            CheckPath is null ? null : CheckPath + ".new",
            ResultPath,
            ResultPath is null ? null : ResultPath + ".new"
        }.Where(path => path is not null))
        {
            try
            {
                File.Delete(path!);
            }
            catch (Exception error)
            {
                cleanupError ??= error;
            }
        }

        if (cleanupError is not null)
        {
            return ValueTask.FromException(cleanupError);
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Current nginx reopen availability and the applicable remedy when unavailable.
/// </summary>
public sealed record NginxReopenAvailability(
    bool Available,
    NginxReopenHint Hint,
    NginxReopenRequirement Requirement = NginxReopenRequirement.Unknown,
    bool CheckOnAction = false);

public sealed class LogRotationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public bool DockerSocketMissing { get; init; }
    public NginxReopenRequirement Requirement { get; init; }
    public NginxReopenStatus Status { get; init; }
    public bool PartialPhysicalEffects { get; init; }

    public static LogRotationResult NotRequired() => new()
    {
        Success = true,
        Requirement = NginxReopenRequirement.NotRequired,
        Status = NginxReopenStatus.NotRequired
    };

    public static LogRotationResult Succeeded(NginxReopenRequirement requirement = NginxReopenRequirement.Required) => new()
    {
        Success = true,
        Requirement = requirement,
        Status = requirement == NginxReopenRequirement.NotRequired
            ? NginxReopenStatus.NotRequired
            : NginxReopenStatus.Succeeded
    };

    public static LogRotationResult Failed(
        string message,
        NginxReopenRequirement requirement = NginxReopenRequirement.Required,
        bool dockerSocketMissing = false,
        bool partialPhysicalEffects = false) => new()
    {
        Success = false,
        ErrorMessage = message,
        DockerSocketMissing = dockerSocketMissing,
        Requirement = requirement,
        Status = NginxReopenStatus.Failed,
        PartialPhysicalEffects = partialPhysicalEffects
    };

    public static LogRotationResult Failed(string message, bool dockerSocketMissing) =>
        Failed(message, NginxReopenRequirement.Required, dockerSocketMissing);
}

internal enum DockerProbeFailure
{
    None,
    SocketMissing,
    NoNginxContainer,
    Unknown
}

internal enum HostProbeFailure
{
    None,
    PidNotVisible,
    SignalDenied,
    Unknown
}

internal sealed record DockerProbeResult(bool Available, DockerProbeFailure Failure)
{
    public static DockerProbeResult AvailableResult { get; } = new(true, DockerProbeFailure.None);
    public static DockerProbeResult Unknown { get; } = new(false, DockerProbeFailure.Unknown);
}

internal sealed record HostProbeResult(bool Available, HostProbeFailure Failure)
{
    public static HostProbeResult AvailableResult { get; } = new(true, HostProbeFailure.None);
    public static HostProbeResult Unknown { get; } = new(false, HostProbeFailure.Unknown);
}

internal sealed record AvailabilityDetection(bool Available, NginxReopenHint? DetectedHint)
{
    public static AvailabilityDetection AvailableResult { get; } = new(true, null);

    public static AvailabilityDetection Unavailable(NginxReopenHint? hint = null) =>
        new(false, hint);
}

internal sealed record AvailabilityCacheEntry(
    AvailabilityDetection Detection,
    DateTimeOffset CheckedAt);
