namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Generic CRUD repository interface for consistent data access patterns.
/// </summary>
/// <typeparam name="TEntity">The entity type</typeparam>
/// <typeparam name="TKey">The primary key type (usually int)</typeparam>
public interface ICrudRepository<TEntity, TKey> where TEntity : class
{
    Task<List<TEntity>> GetAllAsync(CancellationToken ct = default);
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken ct = default);
    Task<TEntity> CreateAsync(TEntity entity, CancellationToken ct = default);
    Task<TEntity> UpdateAsync(TEntity entity, CancellationToken ct = default);
    Task DeleteAsync(TEntity entity, CancellationToken ct = default);
    Task<bool> ExistsAsync(TKey id, CancellationToken ct = default);
}
