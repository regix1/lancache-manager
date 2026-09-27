using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LancacheManager.Core.Services;

/// <summary>
/// Creates and stores the append-only history of completed schedule runs.
/// </summary>
public class ScheduleExecutionService
{
    private const int DetailLimit = 4096;
    private const string OperationIdIndex = "IX_ScheduleExecutions_OperationId";

    private readonly IDbContextFactory<AppDbContext> _dbContextSource;
    private readonly ILogger<ScheduleExecutionService> _logger;

    public ScheduleExecutionService(
        IDbContextFactory<AppDbContext> dbContextSource,
        ILogger<ScheduleExecutionService> logger)
    {
        _dbContextSource = dbContextSource;
        _logger = logger;
    }

    public virtual async Task<ScheduleActor> ResolveActorAsync(
        Guid? accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId is null)
        {
            return new ScheduleActor(ScheduleActorKind.Unknown, null, null);
        }

        await using var context = await _dbContextSource.CreateDbContextAsync(cancellationToken);
        var account = await context.UserAccounts
            .AsNoTracking()
            .Where(candidate => candidate.Id == accountId.Value)
            .Select(candidate => new { candidate.Id, candidate.Username })
            .SingleOrDefaultAsync(cancellationToken);

        return account is null
            ? new ScheduleActor(ScheduleActorKind.Unknown, null, null)
            : new ScheduleActor(ScheduleActorKind.Account, account.Id, account.Username);
    }

    public ScheduleExecution Capture(OperationInfo operation, string serviceKey)
    {
        var notice = operation.Notice;
        var restored = notice?.RestoredOrigin == true;
        var actor = restored ? null : notice?.Actor;
        if (actor is null)
        {
            actor = !restored && notice?.Trigger is RunTrigger.Scheduled or RunTrigger.Startup
                ? new ScheduleActor(ScheduleActorKind.Server, null, null)
                : new ScheduleActor(ScheduleActorKind.Unknown, null, null);
        }

        var serviceRun = operation.Metadata as ScheduledPrefillServiceRunState;
        var detail = operation.Status == OperationStatus.Completed || string.IsNullOrWhiteSpace(operation.Message)
            ? null
            : operation.Message[..Math.Min(operation.Message.Length, DetailLimit)];

        return new ScheduleExecution
        {
            OperationId = operation.Id,
            ServiceKey = serviceKey,
            Status = operation.Status,
            Trigger = restored ? null : notice?.Trigger,
            ActorKind = actor.Kind,
            AccountId = actor.AccountId,
            Username = actor.Username,
            StartedAt = DateTime.SpecifyKind(operation.StartedAt, DateTimeKind.Utc),
            CompletedAt = DateTime.SpecifyKind(operation.CompletedAt!.Value, DateTimeKind.Utc),
            Detail = detail,
            ScheduleId = serviceRun?.ScheduleId,
            ScheduleName = serviceRun?.Name,
            Platform = serviceRun?.ServiceId,
            WorkerStarted = operation.WorkerStarted
        };
    }

    public virtual async Task<bool> InsertAsync(
        ScheduleExecution execution,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _dbContextSource.CreateDbContextAsync(cancellationToken);
            context.ScheduleExecutions.Add(execution);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: OperationIdIndex
        })
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not store schedule execution {OperationId}", execution.OperationId);
            return false;
        }
    }

    public virtual async Task<ScheduleExecutionResponse> GetPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextSource.CreateDbContextAsync(cancellationToken);
        var query = context.ScheduleExecutions.AsNoTracking();
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(execution => execution.StartedAt)
            .ThenByDescending(execution => execution.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new ScheduleExecutionResponse
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize)
        };
    }
}
