namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// The key of a resolver pass that found nothing to do. The candidate values identify the rows:
/// swapping a row changes the id sum, and ending or resuming a session changes the inactive count
/// even when its end stays below the latest inactive end. A continuing active download changes none
/// of these values. The catalog values identify what those rows were matched against. An equal key
/// on the next pass means the same rows were checked against the same catalog, so the resolver does
/// not reload them.
/// </summary>
internal sealed record UnmatchedResolveScan(
    int Candidates,
    int CandidateInactiveCount,
    long CandidateMaxId,
    long CandidateIdSum,
    DateTime? CandidateMaxInactiveEndUtc,
    int Catalog,
    DateTime? CatalogSeenUtc);
