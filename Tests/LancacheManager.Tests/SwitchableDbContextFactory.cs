using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Refuses to hand out a context until <see cref="Recover"/> is called, and again after <see cref="Fail"/>, and
/// counts how many times it was asked. The count is what says whether the outage path stopped retrying.
/// </summary>
internal sealed class SwitchableDbContextFactory : IDbContextFactory<AppDbContext>
{
    private IDbContextFactory<AppDbContext>? _working;

    public int CreateCount { get; private set; }

    public void Recover(IDbContextFactory<AppDbContext> working) => _working = working;

    /// <summary>A test that needs the outage back after a recovery calls this.</summary>
    public void Fail() => _working = null;

    public AppDbContext CreateDbContext()
    {
        CreateCount++;
        return _working?.CreateDbContext()
            ?? throw new InvalidOperationException("context creation failed");
    }

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        CreateCount++;
        return _working?.CreateDbContextAsync(cancellationToken)
            ?? Task.FromException<AppDbContext>(
                new InvalidOperationException("context creation failed"));
    }
}
