using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the null-result mapping
/// <see cref="CacheSizeNullOutcome.Resolve"/> that <see cref="Controllers.CacheController"/>
/// uses when <c>GetCacheSizeAsync</c> returns null - an active scan wins (report scanning so
/// the frontend polls/waits), else a previously persisted stale result is served instead of an
/// error, and no scan/no cache maps to the expected unavailable state for ordinary reads.
/// Kept dependency-free deliberately so this decision is unit-testable without constructing the
/// controller's full DI graph (13 constructor dependencies) or CacheManagementService.
/// </summary>
public class CacheSizeNullOutcomeTests
{
    [Fact]
    public void Resolve_ActiveScan_ReturnsScanning_RegardlessOfStaleResult()
    {
        var operationId = Guid.NewGuid();
        var stale = new CacheSizeResponse { TotalBytes = 123, IsCached = true };

        var outcome = CacheSizeNullOutcome.Resolve(operationId, stale);

        Assert.Equal(CacheSizeNullOutcomeKind.Scanning, outcome.Kind);
        Assert.Equal(operationId, outcome.ScanOperationId);
        Assert.Null(outcome.StaleResult);
    }

    [Fact]
    public void Resolve_NoActiveScan_StaleResultPresent_ReturnsStale()
    {
        var stale = new CacheSizeResponse { TotalBytes = 456, IsCached = true };

        var outcome = CacheSizeNullOutcome.Resolve(null, stale);

        Assert.Equal(CacheSizeNullOutcomeKind.Stale, outcome.Kind);
        Assert.Same(stale, outcome.StaleResult);
        Assert.Null(outcome.ScanOperationId);
    }

    [Fact]
    public void Resolve_NoActiveScan_NoStaleResult_ReturnsUnavailable()
    {
        var outcome = CacheSizeNullOutcome.Resolve(null, null);

        Assert.Equal(CacheSizeNullOutcomeKind.Unavailable, outcome.Kind);
        Assert.Null(outcome.ScanOperationId);
        Assert.Null(outcome.StaleResult);
    }
}
