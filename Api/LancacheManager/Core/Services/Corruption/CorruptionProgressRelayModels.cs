using LancacheManager.Models;

namespace LancacheManager.Core.Services;

internal sealed record CorruptionRelayDecision(
    OperationProgressSnapshot Snapshot,
    bool IsNew,
    bool ShouldEmit);
