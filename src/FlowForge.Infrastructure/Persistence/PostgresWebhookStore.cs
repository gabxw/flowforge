using FlowForge.Application.Executions;
using FlowForge.Application.Webhooks;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Webhooks;
using FlowForge.Domain.Workflows;
using FlowForge.Infrastructure.Persistence.Records;
using FlowForge.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;

public sealed class PostgresWebhookStore(IDbContextFactory<FlowForgeDbContext> factory,
    ExecutionContextProtection protection, WebhookAcceptanceOptions options) : IWebhookStore
{
    public async Task<IssuedWebhook> CreateAsync(Guid workflowId, Guid owner, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var root = await LockWorkflow(db, workflowId, owner, ct);
        RequirePublished(root);
        if (await db.WebhookEndpoints.AnyAsync(e => e.WorkflowId == workflowId, ct))
            throw new WorkflowStateConflictException("Este workflow já possui um endpoint. Consulte ou rotacione seu segredo.");
        var now = await ExecutionPersistence.NowAsync(db, ct);
        var endpoint = new WebhookEndpoint(Guid.NewGuid(), workflowId, owner, now).Snapshot;
        var secret = WebhookSecrets.Generate();
        db.WebhookEndpoints.Add(new() { Id = endpoint.Id, WorkflowId = workflowId, OwnerUserId = owner,
            Enabled = true, CreatedAt = now, SecretHash = WebhookSecrets.Hash(secret) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(endpoint, secret);
    }
    public async Task<WebhookEndpointSnapshot?> GetAsync(Guid workflowId, Guid owner, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.WorkflowId == workflowId && e.OwnerUserId == owner, ct);
        return row is null ? null : Snapshot(row);
    }
    public async Task<WebhookEndpointSnapshot> SetEnabledAsync(Guid workflowId, Guid owner, bool enabled, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var root = await LockWorkflow(db, workflowId, owner, ct);
        var row = await LockEndpoint(db, workflowId, owner, ct);
        if (enabled) RequirePublished(root);
        var endpoint = WebhookEndpoint.Restore(Snapshot(row)); endpoint.SetEnabled(enabled);
        row.Enabled = endpoint.Snapshot.Enabled;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return endpoint.Snapshot;
    }
    public async Task<IssuedWebhook> RotateAsync(Guid workflowId, Guid owner, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        _ = await LockWorkflow(db, workflowId, owner, ct);
        var row = await LockEndpoint(db, workflowId, owner, ct);
        var now = await ExecutionPersistence.NowAsync(db, ct);
        var previous = row.RotatedAt ?? row.CreatedAt;
        var endpoint = WebhookEndpoint.Restore(Snapshot(row)); endpoint.Rotate(now < previous ? previous : now);
        var secret = WebhookSecrets.Generate();
        row.SecretHash = WebhookSecrets.Hash(secret); row.RotatedAt = endpoint.Snapshot.RotatedAt;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(endpoint.Snapshot, secret);
    }
    public async Task AuthorizeAsync(Guid endpointId, string secret, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await (from e in db.WebhookEndpoints.AsNoTracking()
            join w in db.Workflows.AsNoTracking() on e.WorkflowId equals w.Id
            where e.Id == endpointId && e.Enabled && w.ArchivedAt == null && w.CurrentPublishedVersionId != null
            select e).ToArrayAsync(ct);
        if (rows.SingleOrDefault() is not { } row || !WebhookSecrets.Matches(secret, row.SecretHash))
            throw new WebhookRejectedException();
    }
    public async Task<WebhookReceipt> AcceptAsync(Guid endpointId, string secret, WebhookPayload payload,
        string? idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload); WebhookService.ValidateKey(idempotencyKey);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var identity = await db.WebhookEndpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.WorkflowId, e.OwnerUserId }).SingleOrDefaultAsync(ct) ?? throw new WebhookRejectedException();
        // Toda mutação segue workflow -> endpoint. O mesmo root lock ordena publicação e arquivamento.
        var root = await LockWorkflow(db, identity.WorkflowId, identity.OwnerUserId, ct);
        var endpoint = await LockEndpoint(db, root.Id, root.OwnerUserId, ct);
        if (endpoint.Id != endpointId || !endpoint.Enabled || !WebhookSecrets.Matches(secret, endpoint.SecretHash)
            || root.ArchivedAt is not null || root.CurrentPublishedVersionId is null) throw new WebhookRejectedException();
        var now = await ExecutionPersistence.NowAsync(db, ct);
        var digest = idempotencyKey is null ? null : WebhookSecrets.KeyDigest(idempotencyKey);
        WebhookIdempotencyRecord? reservation = null;
        if (digest is not null)
        {
            reservation = await db.WebhookIdempotency.SingleOrDefaultAsync(e => e.EndpointId == endpointId && e.KeyDigest == digest, ct);
            if (reservation is not null && reservation.ExpiresAt > now)
            {
                if (reservation.RequestDigest != payload.RequestDigest) throw new WebhookIdempotencyConflictException();
                var original = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == reservation.ExecutionId, ct);
                await tx.CommitAsync(ct);
                return new(original.Id, original.WorkflowVersionId, original.CorrelationId, original.CreatedAt, true);
            }
        }
        var version = await db.WorkflowVersions.AsNoTracking().SingleAsync(v => v.Id == root.CurrentPublishedVersionId
            && v.WorkflowId == root.Id && v.OwnerUserId == root.OwnerUserId, ct);
        if (version.Status != WorkflowVersionStatus.Published) throw new WebhookRejectedException();
        var execution = new WorkflowExecution(Guid.NewGuid(), root.Id, version.Id, root.OwnerUserId, Guid.NewGuid(), now).Snapshot;
        db.WorkflowExecutions.Add(new() { Id = execution.Id, WorkflowId = root.Id, WorkflowVersionId = version.Id,
            OwnerUserId = root.OwnerUserId, CorrelationId = execution.CorrelationId, Status = execution.Status, CreatedAt = now,
            WebhookEndpointId = endpointId, TriggerInputProtected = protection.ProtectTrigger(payload.Value, execution.Id) });
        db.OutboxMessages.Add(new() { Id = Guid.NewGuid(), ExecutionId = execution.Id, CorrelationId = execution.CorrelationId,
            ContractVersion = ExecutionRequestedMessage.CurrentVersion, CreatedAt = now, AvailableAt = now });
        if (digest is not null)
        {
            if (reservation is null) { reservation = new() { EndpointId = endpointId, WorkflowId = root.Id, OwnerUserId = root.OwnerUserId, KeyDigest = digest }; db.WebhookIdempotency.Add(reservation); }
            reservation.RequestDigest = payload.RequestDigest; reservation.ExecutionId = execution.Id;
            reservation.CreatedAt = now; reservation.ExpiresAt = now + options.IdempotencyLifetime;
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(execution.Id, execution.WorkflowVersionId, execution.CorrelationId, execution.CreatedAt, false);
    }
    private static async Task<WorkflowRecord> LockWorkflow(FlowForgeDbContext db, Guid id, Guid owner, CancellationToken ct) =>
        (await db.Workflows.FromSqlInterpolated($"SELECT * FROM workflows WHERE id = {id} AND owner_user_id = {owner} FOR UPDATE")
            .AsNoTracking().ToArrayAsync(ct)).SingleOrDefault() ?? throw new KeyNotFoundException("Workflow não encontrado.");
    private static async Task<WebhookEndpointRecord> LockEndpoint(FlowForgeDbContext db, Guid workflowId, Guid owner, CancellationToken ct) =>
        (await db.WebhookEndpoints.FromSqlInterpolated($"SELECT * FROM webhook_endpoints WHERE workflow_id = {workflowId} AND owner_user_id = {owner} FOR UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault() ?? throw new KeyNotFoundException("Endpoint não encontrado.");
    private static void RequirePublished(WorkflowRecord root)
    {
        if (root.ArchivedAt is not null || root.CurrentPublishedVersionId is null)
            throw new WorkflowStateConflictException("O endpoint exige um workflow ativo e publicado.");
    }
    private static WebhookEndpointSnapshot Snapshot(WebhookEndpointRecord row) =>
        WebhookEndpoint.Restore(new(row.Id, row.WorkflowId, row.OwnerUserId, row.Enabled, row.CreatedAt, row.RotatedAt)).Snapshot;
}
