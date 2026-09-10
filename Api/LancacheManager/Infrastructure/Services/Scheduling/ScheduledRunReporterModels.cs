using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Optional adapters for producers that must preserve additive platform-specific fields while using
/// the canonical scheduled-run lifecycle. The complete adapter must retain the
/// <see cref="IOperationComplete"/> terminal contract.
/// </summary>
public sealed record ScheduledRunPayloadFactories(
    Func<ScheduledRunStartedEvent, object> Started,
    Func<ScheduledRunProgressEvent, object> Progress,
    Func<ScheduledRunCompleteEvent, IOperationComplete> Complete);
