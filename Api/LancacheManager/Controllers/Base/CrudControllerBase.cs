using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers.Base;

/// <summary>
/// Base controller providing standard CRUD operations with exception-based error handling.
/// Override methods or add attributes in derived controllers for custom behavior and auth.
/// </summary>
/// <typeparam name="TEntity">Entity type</typeparam>
/// <typeparam name="TDto">DTO type for responses</typeparam>
/// <typeparam name="TCreateRequest">Request type for creation</typeparam>
/// <typeparam name="TUpdateRequest">Request type for updates</typeparam>
/// <typeparam name="TKey">Primary key type (usually int)</typeparam>
public abstract class CrudControllerBase<TEntity, TDto, TCreateRequest, TUpdateRequest, TKey> : ControllerBase
    where TEntity : class
    where TDto : class
    where TCreateRequest : class
    where TUpdateRequest : class
{
    protected readonly ICrudRepository<TEntity, TKey> Repository;
    protected readonly ISignalRNotificationService Notifications;
    protected readonly ILogger Logger;

    /// <summary>
    /// The display name for this resource type (e.g., "Client group", "Event")
    /// </summary>
    protected abstract string ResourceName { get; }

    protected CrudControllerBase(
        ICrudRepository<TEntity, TKey> repository,
        ISignalRNotificationService notifications,
        ILogger logger)
    {
        Repository = repository;
        Notifications = notifications;
        Logger = logger;
    }

    // ===== Abstract Methods - Must be implemented by child controllers =====

    /// <summary>Convert entity to DTO for API responses</summary>
    protected abstract TDto ToDto(TEntity entity);

    /// <summary>Convert create request to a new entity</summary>
    protected abstract TEntity FromCreateRequest(TCreateRequest request);

    /// <summary>Apply update request to existing entity</summary>
    protected abstract void ApplyUpdate(TEntity entity, TUpdateRequest request);

    /// <summary>
    /// Validate create request. Throw ValidationException if invalid.
    /// </summary>
    protected abstract Task ValidateCreateRequestAsync(TCreateRequest request, CancellationToken ct);

    /// <summary>
    /// Validate update request. Throw ValidationException if invalid.
    /// </summary>
    protected abstract Task ValidateUpdateRequestAsync(TKey id, TUpdateRequest request, TEntity existingEntity, CancellationToken ct);

    // ===== Virtual Methods - Override for custom SignalR notifications =====

    /// <summary>Called after entity is created. Override to send SignalR notifications.</summary>
    protected virtual Task OnCreatedAsync(TEntity entity, TDto dto) => Task.CompletedTask;

    /// <summary>Called after entity is updated. Override to send SignalR notifications.</summary>
    protected virtual Task OnUpdatedAsync(TEntity entity, TDto dto) => Task.CompletedTask;

    /// <summary>Called after entity is deleted. Override to send SignalR notifications.</summary>
    protected virtual Task OnDeletedAsync(TKey id) => Task.CompletedTask;

    /// <summary>
    /// Post-process newly created entity (e.g., add related data).
    /// Override to perform additional operations after creation.
    /// Returns the entity (possibly refreshed from DB).
    /// </summary>
    protected virtual Task<TEntity> PostCreateAsync(TEntity entity, TCreateRequest request, CancellationToken ct)
        => Task.FromResult(entity);

    // ===== Standard CRUD Operations =====

    /// <summary>Get all entities</summary>
    [HttpGet]
    public virtual async Task<IActionResult> GetAll(CancellationToken ct = default)
    {
        var entities = await Repository.GetAllAsync(ct);
        var dtos = entities.Select(ToDto).ToList();
        return Ok(dtos);
    }

    /// <summary>Get entity by ID</summary>
    [HttpGet("{id}")]
    public virtual async Task<IActionResult> GetById(TKey id, CancellationToken ct = default)
    {
        var entity = await Repository.GetByIdOrThrowAsync(id, ResourceName, ct);
        return Ok(ToDto(entity));
    }

    /// <summary>Create new entity</summary>
    [HttpPost]
    public virtual async Task<IActionResult> Create([FromBody] TCreateRequest request, CancellationToken ct = default)
    {
        await ValidateCreateRequestAsync(request, ct);

        var entity = FromCreateRequest(request);
        var created = await Repository.CreateAsync(entity, ct);
        
        // Allow subclasses to perform post-creation operations
        created = await PostCreateAsync(created, request, ct);

        var dto = ToDto(created);
        await OnCreatedAsync(created, dto);

        Logger.LogInformation("Created {Resource}: {Id}", ResourceName, GetEntityId(created));
        return Created(GetLocationUri(created), dto);
    }

    /// <summary>Update existing entity</summary>
    [HttpPut("{id}")]
    public virtual async Task<IActionResult> Update(TKey id, [FromBody] TUpdateRequest request, CancellationToken ct = default)
    {
        var entity = await Repository.GetByIdOrThrowAsync(id, ResourceName, ct);

        await ValidateUpdateRequestAsync(id, request, entity, ct);

        ApplyUpdate(entity, request);
        var updated = await Repository.UpdateAsync(entity, ct);
        var dto = ToDto(updated);

        await OnUpdatedAsync(updated, dto);

        Logger.LogInformation("Updated {Resource}: {Id}", ResourceName, id);
        return Ok(dto);
    }

    /// <summary>Delete entity by ID</summary>
    [HttpDelete("{id}")]
    public virtual async Task<IActionResult> Delete(TKey id, CancellationToken ct = default)
    {
        var entity = await Repository.GetByIdOrThrowAsync(id, ResourceName, ct);

        await Repository.DeleteAsync(entity, ct);
        await OnDeletedAsync(id);

        Logger.LogInformation("Deleted {Resource}: {Id}", ResourceName, id);
        return NoContent();
    }

    // ===== Helper Methods =====

    /// <summary>Get the ID from an entity (override if entity doesn't have standard Id property)</summary>
    protected virtual object GetEntityId(TEntity entity)
    {
        var idProperty = typeof(TEntity).GetProperty("Id");
        return idProperty?.GetValue(entity) ?? "unknown";
    }

    /// <summary>Get the location URI for a newly created entity</summary>
    protected virtual string GetLocationUri(TEntity entity)
    {
        var id = GetEntityId(entity);
        return $"{Request.Path}/{id}";
    }
}
