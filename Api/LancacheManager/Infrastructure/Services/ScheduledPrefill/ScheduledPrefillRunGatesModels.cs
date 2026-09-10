namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// How a single service's scheduled run ended, distinguishing prerequisite skips from genuine
/// failures so <see cref="ScheduledPrefillRunGates.EvaluateRunOutcome"/> can report the run
/// honestly: <see cref="Ran"/> = engaged the persistent container and finished its prefill;
/// <see cref="NeedsLogin"/> = skipped because the container is missing or logged out;
/// <see cref="Skipped"/> = skipped for a non-login reason (no daemon, busy, prefill already
/// running); <see cref="Failed"/> = engaged (or tried to) and genuinely failed.
/// </summary>
public enum ScheduledPrefillServiceRunResult
{
    Ran,
    NeedsLogin,
    Skipped,
    Failed,

    /// <summary>
    /// The user stopped this service's prefill while it was running (from the prefill modal, which
    /// cancels the DAEMON session rather than this run's cancellation token). Distinct from
    /// <see cref="Ran"/>: a stopped prefill did not finish, so it must not stamp the genuine
    /// "Last run", and distinct from <see cref="Failed"/>: nothing went wrong.
    /// </summary>
    Cancelled
}

/// <summary>
/// Immutable result of evaluating a completed scheduled-prefill run: whether it succeeded overall,
/// plus an optional human-readable reason when it did not, and the i18n key naming that same reason
/// so the notification card can show it in the reader's language.
/// </summary>
public readonly record struct ScheduledPrefillRunOutcome(bool Success, string? Error, string? StageKey = null);
