namespace LancacheManager.Models;

/// <summary>
/// What a loop over many items did: how many it finished and how many it skipped after a failure, so the
/// run that called it can report the skipped ones instead of reading as a clean pass.
/// </summary>
public sealed record WorkCount(int Done, int Failed);
