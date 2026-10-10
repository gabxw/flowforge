using FlowForge.Application.Executions;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;

public sealed class PostgresExecutionOutboxStore(IDbContextFactory<FlowForgeDbContext> factory) : IExecutionOutboxStore
{
    public async Task<OutboxClaim?> TryClaimAsync(TimeSpan lease, CancellationToken ct = default)
    {
        ExecutionPersistence.ValidateLease(lease);
        await using var db = await factory.CreateDbContextAsync(ct);
        var token = Guid.NewGuid();
        var rows = await db.OutboxMessages.FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT id FROM outbox_messages
                WHERE published_at IS NULL AND available_at <= clock_timestamp()
                  AND (claim_until IS NULL OR claim_until <= clock_timestamp())
                ORDER BY available_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE outbox_messages o SET claim_token = {token},
                claim_until = clock_timestamp() + {lease}, publish_attempts = publish_attempts + 1
            FROM candidate c WHERE o.id = c.id RETURNING o.*
            """).AsNoTracking().ToArrayAsync(ct);
        var row = rows.SingleOrDefault();
        return row is null ? null : new(new(row.ContractVersion, row.Id, row.ExecutionId, row.CorrelationId), token);
    }

    public async Task<bool> MarkPublishedAsync(OutboxClaim claim, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET published_at = clock_timestamp(), claim_token = NULL, claim_until = NULL
            WHERE id = {claim.Message.MessageId} AND claim_token = {claim.Token}
              AND claim_until > clock_timestamp() AND published_at IS NULL
            """, ct) == 1;
    }

    public async Task ReleaseAsync(OutboxClaim claim, TimeSpan delay, CancellationToken ct = default)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(delay));
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET available_at = clock_timestamp() + {delay}, claim_token = NULL, claim_until = NULL
            WHERE id = {claim.Message.MessageId} AND claim_token = {claim.Token} AND published_at IS NULL
            """, ct);
    }
}
