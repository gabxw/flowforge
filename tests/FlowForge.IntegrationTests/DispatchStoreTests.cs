using FlowForge.Application.Executions;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;

namespace FlowForge.IntegrationTests;

[Collection("Dispatch")]
public sealed class DispatchStoreTests(DispatchFixture fixture)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    internal async Task<(Workflow Workflow, ExecutionRequest Request, WorkflowExecutionSnapshot Execution)> RequestAsync()
    {
        var workflow = await CreateAsync(fixture.Postgres, Guid.NewGuid());
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workflow.Id, workflow.OwnerUserId);
        var execution = await new PostgresExecutionStore(fixture.Factory).RequestAsync(request);
        return (workflow, request, execution);
    }

    internal static ExecutionRequestedMessage Message(ExecutionRequest r) => new(1, r.MessageId, r.ExecutionId, r.CorrelationId);

    [Fact]
    public async Task Request_atomically_records_pending_execution_and_outbox_without_broker()
    {
        await fixture.ResetAsync();
        var (workflow, request, execution) = await RequestAsync();
        Assert.Equal(workflow.CurrentPublishedVersionId, execution.WorkflowVersionId);
        Assert.Equal(WorkflowExecutionStatus.Pending, execution.Status);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var outbox = Assert.Single(await db.OutboxMessages.ToArrayAsync());
        Assert.Equal(execution.Id, outbox.ExecutionId); Assert.Equal(execution.CreatedAt, outbox.CreatedAt);
        Assert.Equal(request.MessageId, outbox.Id); Assert.Null(outbox.PublishedAt);
        Assert.Empty(await db.InboxMessages.ToArrayAsync());
        Assert.Null(await new PostgresExecutionStore(fixture.Factory).GetAsync(execution.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Outbox_insert_failure_rolls_back_execution()
    {
        await fixture.ResetAsync();
        var (_, request, _) = await RequestAsync();
        var second = request with { ExecutionId = Guid.NewGuid() }; // ID da mensagem colide após inserir a nova execução.
        await Assert.ThrowsAsync<DbUpdateException>(() => new PostgresExecutionStore(fixture.Factory).RequestAsync(second));
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.WorkflowExecutions.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.False(await db.WorkflowExecutions.AnyAsync(e => e.Id == second.ExecutionId));
    }

    [Fact]
    public async Task Version_is_pinned_and_archived_or_unpublished_workflow_cannot_be_requested()
    {
        await fixture.ResetAsync();
        var (workflow, request, execution) = await RequestAsync();
        var store = new PostgresWorkflowStore(fixture.Factory);
        var revision = workflow.Revision;
        workflow.PublishDraft(Start.AddSeconds(1));
        await store.SaveAsync(workflow, revision);
        Assert.NotEqual(execution.WorkflowVersionId, workflow.CurrentPublishedVersionId);
        Assert.Equal(execution.WorkflowVersionId, (await new PostgresExecutionStore(fixture.Factory).GetAsync(execution.Id, request.OwnerUserId))!.WorkflowVersionId);
        revision = workflow.Revision;
        workflow.Archive(Start.AddSeconds(2));
        await store.SaveAsync(workflow, revision);
        await Assert.ThrowsAsync<WorkflowStateConflictException>(() => new PostgresExecutionStore(fixture.Factory).RequestAsync(request with { ExecutionId = Guid.NewGuid(), MessageId = Guid.NewGuid() }));
        var unpublished = new Workflow(Guid.NewGuid(), request.OwnerUserId, "Rascunho", Start);
        unpublished.CreateDraft(Guid.NewGuid(), Start);
        await store.AddAsync(unpublished);
        await Assert.ThrowsAsync<WorkflowStateConflictException>(() => new PostgresExecutionStore(fixture.Factory).RequestAsync(request with { WorkflowId = unpublished.Id, ExecutionId = Guid.NewGuid(), MessageId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Concurrent_publishers_claim_a_pending_message_only_once()
    {
        await fixture.ResetAsync();
        await RequestAsync();
        var store = new PostgresExecutionOutboxStore(fixture.Factory);
        var claims = await Task.WhenAll(store.TryClaimAsync(Lease), store.TryClaimAsync(Lease));
        var winner = Assert.Single(claims, c => c is not null)!;
        Assert.Single(claims, c => c is null);
        Assert.True(await store.MarkPublishedAsync(winner));
        Assert.False(await store.MarkPublishedAsync(winner));
        Assert.Null(await store.TryClaimAsync(Lease));
    }

    [Fact]
    public async Task Expired_outbox_claim_is_recoverable_and_old_token_cannot_mark_published()
    {
        await fixture.ResetAsync();
        await RequestAsync();
        var store = new PostgresExecutionOutboxStore(fixture.Factory);
        var first = (await store.TryClaimAsync(Lease))!;
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE id = {first.Message.MessageId}");
        Assert.False(await store.MarkPublishedAsync(first));
        var second = (await store.TryClaimAsync(Lease))!;
        Assert.NotEqual(first.Token, second.Token);
        await store.ReleaseAsync(first, TimeSpan.Zero);
        Assert.False(await store.MarkPublishedAsync(first));
        Assert.True(await store.MarkPublishedAsync(second));
    }

    [Fact]
    public async Task Two_workers_cannot_acquire_same_execution_and_done_requires_result_transaction()
    {
        await fixture.ResetAsync();
        var (_, request, _) = await RequestAsync();
        var inbox = new PostgresExecutionInboxStore(fixture.Factory);
        var message = Message(request);
        var claims = await Task.WhenAll(inbox.TryClaimAsync(message, Lease), inbox.TryClaimAsync(message, Lease));
        var winner = Assert.Single(claims, c => c.Status == InboxClaimStatus.Acquired);
        Assert.Single(claims, c => c.Status == InboxClaimStatus.Busy);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            Assert.Null((await db.InboxMessages.SingleAsync()).CompletedAt);
            Assert.Equal(WorkflowExecutionStatus.Running, (await db.WorkflowExecutions.SingleAsync()).Status);
        }
        Assert.True(await inbox.CompleteEngineUnavailableAsync(winner));
        Assert.False(await inbox.CompleteEngineUnavailableAsync(winner));
        Assert.Equal(InboxClaimStatus.Completed, (await inbox.TryClaimAsync(message, Lease)).Status);
        await using var final = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, (await final.InboxMessages.SingleAsync()).ClaimAttempts);
        Assert.Equal(ExecutionFailureCode.EngineUnavailable, (await final.WorkflowExecutions.SingleAsync()).ErrorCode);
    }

    [Fact]
    public async Task Interrupted_received_message_is_reclaimed_without_resetting_start_time()
    {
        await fixture.ResetAsync();
        var (_, request, _) = await RequestAsync();
        var inbox = new PostgresExecutionInboxStore(fixture.Factory);
        var first = await inbox.TryClaimAsync(Message(request), Lease);
        var running = await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE message_id = {request.MessageId}");
        Assert.False(await inbox.CompleteEngineUnavailableAsync(first));
        var second = await inbox.TryClaimAsync(Message(request), Lease);
        Assert.Equal(InboxClaimStatus.Acquired, second.Status); Assert.NotEqual(first.Token, second.Token);
        Assert.False(await inbox.CompleteEngineUnavailableAsync(first));
        Assert.True(await inbox.CompleteEngineUnavailableAsync(second));
        var failed = await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId);
        Assert.Equal(running!.StartedAt, failed!.StartedAt);
        Assert.Equal(2, (await db.InboxMessages.AsNoTracking().SingleAsync()).ClaimAttempts);
    }

    [Fact]
    public async Task Identity_mismatch_and_unknown_version_do_not_claim_existing_execution()
    {
        await fixture.ResetAsync();
        var (_, request, _) = await RequestAsync();
        var inbox = new PostgresExecutionInboxStore(fixture.Factory);
        foreach (var invalid in new[] { Message(request) with { ContractVersion = 2 },
            Message(request) with { MessageId = Guid.NewGuid() }, Message(request) with { CorrelationId = Guid.NewGuid() } })
            Assert.Equal(InboxClaimStatus.Invalid, (await inbox.TryClaimAsync(invalid, Lease)).Status);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Empty(await db.InboxMessages.ToArrayAsync());
        Assert.Equal(WorkflowExecutionStatus.Pending, (await db.WorkflowExecutions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Completion_commit_failure_rolls_back_both_result_and_inbox()
    {
        await fixture.ResetAsync();
        var (_, request, _) = await RequestAsync();
        var inbox = new PostgresExecutionInboxStore(fixture.Factory);
        var claim = await inbox.TryClaimAsync(Message(request), Lease);
        await using var setup = await fixture.Factory.CreateDbContextAsync();
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_dispatch_completion() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.completed_at IS NOT NULL THEN RAISE EXCEPTION 'injected commit failure'; END IF; RETURN NEW; END $$;
            CREATE CONSTRAINT TRIGGER fail_dispatch_completion AFTER UPDATE ON inbox_messages
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_dispatch_completion();
            """);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => inbox.CompleteEngineUnavailableAsync(claim));
            await using var db = await fixture.Factory.CreateDbContextAsync();
            Assert.Equal(WorkflowExecutionStatus.Running, (await db.WorkflowExecutions.SingleAsync()).Status);
            Assert.Null((await db.InboxMessages.SingleAsync()).CompletedAt);
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_dispatch_completion ON inbox_messages; DROP FUNCTION fail_dispatch_completion();");
        }
        Assert.True(await inbox.CompleteEngineUnavailableAsync(claim));
    }

    [Fact]
    public async Task Database_rejects_execution_from_another_owner_and_failed_state_without_error()
    {
        await fixture.ResetAsync();
        var (_, request, _) = await RequestAsync();
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var wrongOwner = Guid.NewGuid();
        await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(wrongOwner, Start);
        var invalidId = Guid.NewGuid();
        var foreignKey = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_executions (id, workflow_id, workflow_version_id, owner_user_id, correlation_id, status, created_at)
            SELECT {invalidId}, workflow_id, workflow_version_id, {wrongOwner}, correlation_id, 1, created_at
            FROM workflow_executions WHERE id = {request.ExecutionId}
            """));
        Assert.Equal("23503", foreignKey.SqlState);
        var state = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_executions SET status = 4, started_at = created_at, finished_at = created_at
            WHERE id = {request.ExecutionId}
            """));
        Assert.Equal("23514", state.SqlState);
    }
}
