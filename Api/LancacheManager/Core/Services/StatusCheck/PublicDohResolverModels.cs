using System.Net;

namespace LancacheManager.Core.Services.StatusCheck;

internal sealed record DohResolutionResult(
    IReadOnlyList<IPAddress> Addresses,
    int TotalAddresses,
    bool TooManyAddresses,
    string? FailureReason);
