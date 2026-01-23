using System.Text;
using LancacheManager.Core.Interfaces;
using LancacheManager.Middleware;

namespace LancacheManager.Infrastructure.Extensions;

/// <summary>
/// Extension methods for repository operations
/// </summary>
public static class RepositoryExtensions
{
    /// <summary>
    /// Gets an entity by ID or throws NotFoundException if not found.
    /// </summary>
    public static async Task<TEntity> GetByIdOrThrowAsync<TEntity, TKey>(
        this ICrudRepository<TEntity, TKey> repository,
        TKey id,
        string entityName,
        CancellationToken ct = default) where TEntity : class
    {
        return await repository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException(entityName);
    }

    /// <summary>
    /// Gets an entity by ID or throws NotFoundException with an auto-detected entity name.
    /// </summary>
    public static async Task<TEntity> GetByIdOrThrowAsync<TEntity, TKey>(
        this ICrudRepository<TEntity, TKey> repository,
        TKey id,
        CancellationToken ct = default) where TEntity : class
    {
        var entity = await repository.GetByIdAsync(id, ct);
        if (entity != null)
        {
            return entity;
        }

        var entityName = GetFriendlyTypeName(typeof(TEntity));
        throw new NotFoundException(entityName);
    }

    private static string GetFriendlyTypeName(Type type)
    {
        var name = type.Name;

        if (name.EndsWith("Entity", StringComparison.Ordinal))
        {
            name = name[..^6];
        }

        if (name.EndsWith("Model", StringComparison.Ordinal))
        {
            name = name[..^5];
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                builder.Append(' ');
            }
            builder.Append(c);
        }

        return builder.ToString();
    }
}
