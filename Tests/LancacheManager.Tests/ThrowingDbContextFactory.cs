using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

internal sealed class ThrowingDbContextFactory : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() =>
        throw new InvalidOperationException("context creation failed");

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<AppDbContext>(new InvalidOperationException("context creation failed"));
}
