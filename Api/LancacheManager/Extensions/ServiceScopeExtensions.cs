using LancacheManager.Infrastructure.Data;

namespace LancacheManager.Extensions;

/// <summary>
/// A disposable wrapper that creates a scoped AppDbContext from an IServiceScopeFactory.
/// Disposing this object disposes the underlying scope (and therefore the DbContext).
/// </summary>
public sealed class ScopedDbContext : IDisposable
{
    private readonly IServiceScope _scope;

    public AppDbContext DbContext { get; }

    public ScopedDbContext(IServiceScopeFactory scopeFactory)
    {
        _scope = scopeFactory.CreateScope();
        DbContext = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    public ScopedDbContext(IServiceProvider serviceProvider)
    {
        _scope = serviceProvider.CreateScope();
        DbContext = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    public void Dispose() => _scope.Dispose();
}

public static class ServiceScopeExtensions
{
    /// <summary>
    /// Creates a new service scope and resolves an AppDbContext from it.
    /// The returned ScopedDbContext is disposable — disposing it disposes the scope.
    /// Usage: using var scopedDb = _scopeFactory.CreateScopedDbContext();
    /// </summary>
    public static ScopedDbContext CreateScopedDbContext(this IServiceScopeFactory scopeFactory)
        => new ScopedDbContext(scopeFactory);

    /// <summary>
    /// Creates a new service scope and resolves an AppDbContext from it.
    /// The returned ScopedDbContext is disposable — disposing it disposes the scope.
    /// Usage: using var scopedDb = serviceProvider.CreateScopedDbContext();
    /// </summary>
    public static ScopedDbContext CreateScopedDbContext(this IServiceProvider serviceProvider)
        => new ScopedDbContext(serviceProvider);
}
