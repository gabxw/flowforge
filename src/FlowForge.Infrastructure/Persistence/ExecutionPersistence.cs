using FlowForge.Domain.Executions;
using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;

internal static class ExecutionPersistence
{
    public static Task<DateTimeOffset> NowAsync(FlowForgeDbContext db, CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    public static void ValidateLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(lease));
    }

    public static WorkflowExecutionSnapshot Snapshot(WorkflowExecutionRecord r) =>
        WorkflowExecution.Restore(new(r.Id, r.WorkflowId, r.WorkflowVersionId, r.OwnerUserId, r.CorrelationId,
            r.Status, r.CreatedAt, r.StartedAt, r.FinishedAt, r.ErrorCode, r.CancelRequestedAt, r.ResumeAt)).Snapshot;
}
