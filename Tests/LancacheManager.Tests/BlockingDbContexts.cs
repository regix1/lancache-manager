using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// A database context source that pauses its first asynchronous create, which suites use to hold a
/// write open while they probe what waits behind it.
/// </summary>
internal sealed class BlockingDbContexts : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;
    private int _asyncCreateCount;

    public BlockingDbContexts(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    public TaskCompletionSource<bool> FirstCreateStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ReleaseFirstCreate { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int AsyncCreateCount => Volatile.Read(ref _asyncCreateCount);

    public AppDbContext CreateDbContext() => new(_options);

    public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _asyncCreateCount) == 1)
        {
            FirstCreateStarted.TrySetResult(true);
            await ReleaseFirstCreate.Task.WaitAsync(cancellationToken);
        }

        return new AppDbContext(_options);
    }
}
