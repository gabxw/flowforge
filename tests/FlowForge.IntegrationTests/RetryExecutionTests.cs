
using System.Collections.Concurrent;
using System.Net;
using FlowForge.Application.Credentials;
using FlowForge.Application.Executions;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Credentials;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Http;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowForge.IntegrationTests;
[Collection("Dispatch")]
public sealed class RetryExecutionTests(DispatchFixture fixture, ControlledHttpsServer server) : IClassFixture<ControlledHttpsServer>
{
    private PostgresExecutionStore Reader => new(fixture.Factory);
    private PostgresExecutionInboxStore Inbox => new(fixture.Factory);
    private PostgresCredentialStore Credentials => new(fixture.Factory, fixture.CredentialProtection);
    private HttpRequestNodeExecutor Http(TimeSpan? timeout = null) {
        var options = new HttpNodeOptions([server.Origin], timeout);
        return new(Credentials, new HttpDestinationPolicy(options, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })), server.Transport(), options);
    }
    private ExecutionMessageHandler Handler(EngineOptions? limits = null) => new(Inbox, fixture.Engine([
        new TriggerNodeExecutor(), Http(), new LogNodeExecutor()], limits));
    private async Task<(Workflow Workflow, ExecutionRequestedMessage Message, CredentialSnapshot? Credential)> Request(string path,
        HttpRequestMethod method = HttpRequestMethod.Get, int max = 3, bool idempotent = false, bool credential = false)
    {
        var owner = Guid.NewGuid(); var now = WorkflowStoreFixtures.Start;
        await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner, now);
        CredentialSnapshot? metadata = null;
        if (credential) {
            metadata = new Credential(Guid.NewGuid(), owner, "Fictícia", CredentialType.BearerToken, HttpsOrigin.Parse(server.Origin), null, now).Snapshot;
            await Credentials.CreateAsync(metadata, new CredentialSecret("ficticio-" + Guid.NewGuid().ToString("N")));
        }
        var workflow = new Workflow(Guid.NewGuid(), owner, "Retry", now); var version = workflow.CreateDraft(Guid.NewGuid(), now);
        var trigger = Guid.NewGuid(); var http = Guid.NewGuid(); var log = Guid.NewGuid();
        workflow.ReplaceDraftGraph([
            new(version.Id, trigger, NodeType.WebhookTrigger, new WebhookTriggerConfiguration()),
            new(version.Id, http, NodeType.HttpRequest, new HttpRequestConfiguration(new(server.Origin + path), method, max, idempotent),
                credential: metadata is null ? null : new(metadata.Id, owner)),
            new(version.Id, log, NodeType.Log, new LogConfiguration("Fim"))
        ], [new(Guid.NewGuid(), version.Id, trigger, http, "next"), new(Guid.NewGuid(), version.Id, http, log, "next")], now);
        workflow.PublishDraft(now); await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workflow.Id, owner);
        await Reader.RequestAsync(request); return (workflow, DispatchStoreTests.Message(request), metadata);
    }
    private void Plan(string path, params (int Status, string? Advice)[] replies) => server.Plans[path] = new(replies);
    private async Task<ExecutionRequestedMessage> Future(Guid id)
    {
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var row = await db.OutboxMessages.Where(o => o.ExecutionId == id).OrderByDescending(o => o.DispatchSequence).FirstAsync();
        return new(1, row.Id, id, row.CorrelationId);
    }
    private async Task Due(Guid id)
    {
        // Simula o relógio vencido apenas no banco descartável; preserva a ordem de todos os timestamps.
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var row = (await db.WorkflowExecutions.FromSqlInterpolated($"SELECT * FROM workflow_executions WHERE id = {id} FOR UPDATE").ToArrayAsync()).Single();
        var node = await db.NodeExecutions.SingleAsync(n => n.ExecutionId == id && n.Status == NodeExecutionStatus.Retrying);
        var prior = await db.NodeAttempts.SingleAsync(a => a.NodeExecutionId == node.Id && a.Number == node.AttemptCount);
        var due = prior.FinishedAt!.Value.AddTicks(10);
        row.ResumeAt = due; node.RetryAt = due; prior.RetryAt = due;
        (await db.OutboxMessages.SingleAsync(o => o.ExecutionId == id && o.DispatchSequence == row.DispatchSequence)).AvailableAt = due;
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    [Fact]
    public async Task Get_503_resumes_once_with_unchanged_input_and_separate_attempt_history()
    {
        await fixture.ResetAsync(); var path = "/retry/" + Guid.NewGuid(); Plan(path, (503, "30"), (200, null));
        var (workflow, message, _) = await Request(path); await Handler().HandleAsync(message);
        var waiting = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Running, waiting.Status); Assert.NotNull(waiting.ResumeAt);
        Assert.Equal(NodeExecutionStatus.Retrying, history.Nodes[1].Status); Assert.Equal(1, history.Nodes[1].AttemptCount);
        var first = Assert.Single(history.Attempts!, a => a.NodeExecutionId == history.Nodes[1].Id);
        Assert.Equal(NodeAttemptStatus.Failed, first.Status); Assert.Equal(503, first.HttpStatusCode);
        Assert.Equal(ExternalOutcome.KnownFailure, first.ExternalOutcome); Assert.NotNull(first.DispatchedAt);
        Assert.True(first.RetryAt >= first.FinishedAt + TimeSpan.FromSeconds(30));
        var future = await Future(message.ExecutionId);
        Assert.Equal(InboxClaimStatus.Completed, (await Inbox.TryClaimAsync(future, EngineOptions.Default.Lease)).Status);
        await Due(message.ExecutionId); await Handler().HandleAsync(future); await Handler().HandleAsync(message); await Handler().HandleAsync(future);
        var final = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(2, server.Counts[path]); Assert.Single(final.Logs); Assert.Equal(2, final.Nodes[1].AttemptCount);
        var attempts = final.Attempts!.Where(a => a.NodeExecutionId == final.Nodes[1].Id).OrderBy(a => a.Number).ToArray();
        Assert.Equal(new[] { NodeAttemptStatus.Failed, NodeAttemptStatus.Succeeded }, attempts.Select(a => a.Status));
        Assert.Equal(ExternalOutcome.KnownSuccess, attempts[1].ExternalOutcome);
        Assert.Equal(WorkflowExecutionStatus.Succeeded, (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.Status);
    }
    [Fact]
    public async Task Unsafe_post_is_not_repeated_and_idempotent_post_reuses_the_same_effect_key()
    {
        await fixture.ResetAsync(); var unsafePath = "/unsafe/" + Guid.NewGuid(); Plan(unsafePath, (503, null), (200, null));
        var (unsafeWorkflow, unsafeMessage, _) = await Request(unsafePath, HttpRequestMethod.Post, 1);
        await Handler().HandleAsync(unsafeMessage); Assert.Equal(1, server.Counts[unsafePath]);
        Assert.Equal(WorkflowExecutionStatus.Failed, (await Reader.GetAsync(unsafeMessage.ExecutionId, unsafeWorkflow.OwnerUserId))!.Status);
        var path = "/idempotent/" + Guid.NewGuid(); server.IdempotentPaths[path] = true; Plan(path, (503, "30"), (200, null));
        var (workflow, message, _) = await Request(path, HttpRequestMethod.Post, 3, true); await Handler().HandleAsync(message);
        var future = await Future(message.ExecutionId); await Due(message.ExecutionId); await Handler().HandleAsync(future);
        Assert.Equal(2, server.Counts[path]); Assert.Equal(1, server.Effects[path]);
        var keys = server.IdempotencyKeys.ToArray().TakeLast(2).ToArray(); Assert.NotNull(keys[0]); Assert.Equal(keys[0], keys[1]);
        Assert.Equal(HttpRetryPolicy.IdempotencyKey(message.ExecutionId, workflow.Versions[0].Nodes[1].NodeId), keys[0]);
        Assert.Equal(WorkflowExecutionStatus.Succeeded, (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.Status);
    }
    [Fact]
    public async Task Maximum_attempts_and_retry_after_above_budget_finish_without_another_dispatch()
    {
        await fixture.ResetAsync(); var path = "/budget/" + Guid.NewGuid(); Plan(path, (503, "30"), (503, null), (200, null));
        var (workflow, message, _) = await Request(path, max: 2); await Handler().HandleAsync(message);
        await Due(message.ExecutionId); await Handler().HandleAsync(await Future(message.ExecutionId));
        Assert.Equal(2, server.Counts[path]); var result = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status); Assert.Null(result.ResumeAt);
        Assert.Equal(2, (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!.Nodes[1].AttemptCount);
        var longPath = "/advice/" + Guid.NewGuid(); Plan(longPath, (429, "3600"));
        var (other, otherMessage, _) = await Request(longPath); await Handler().HandleAsync(otherMessage);
        Assert.Equal(WorkflowExecutionStatus.Failed, (await Reader.GetAsync(otherMessage.ExecutionId, other.OwnerUserId))!.Status);
        Assert.Equal(otherMessage.MessageId, (await Future(otherMessage.ExecutionId)).MessageId);
    }
    [Fact]
    public async Task Cancellation_during_retry_preserves_failed_attempt_and_does_not_make_a_new_http_call()
    {
        await fixture.ResetAsync(); var path = "/cancel/" + Guid.NewGuid(); Plan(path, (429, "60"), (200, null));
        var (workflow, message, _) = await Request(path); await Handler().HandleAsync(message);
        await Reader.RequestCancellationAsync(message.ExecutionId, workflow.OwnerUserId); await Handler().HandleAsync(await Future(message.ExecutionId));
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.Status);
        Assert.Equal(NodeExecutionStatus.Cancelled, history.Nodes[1].Status); Assert.Null(history.Nodes[1].RetryAt);
        Assert.Single(history.Attempts!, a => a.NodeExecutionId == history.Nodes[1].Id); Assert.Equal(1, server.Counts[path]); Assert.Empty(history.Logs);
    }
    [Fact]
    public async Task Deadline_interrupts_http_records_unknown_effect_and_does_not_retry()
    {
        await fixture.ResetAsync(); var path = "/gate/" + Guid.NewGuid(); var gate = new ControlledHttpsServer.Gate(); server.Gates[path] = gate;
        var (workflow, message, _) = await Request(path);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_executions SET deadline_at = clock_timestamp() + interval '1 second' WHERE id = {message.ExecutionId}");
        var processing = Handler(new(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10))).HandleAsync(message);
        try { await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await processing.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { gate.Release.TrySetResult(); }
        var result = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(ExecutionFailureCode.ExecutionDeadlineExceeded, result.ErrorCode); Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        var attempt = Assert.Single(history.Attempts!, a => a.NodeExecutionId == history.Nodes[1].Id);
        Assert.Equal(ExternalOutcome.Unknown, attempt.ExternalOutcome); Assert.NotNull(attempt.DispatchedAt);
        Assert.Equal(1, server.Counts[path]); Assert.Equal(message.MessageId, (await Future(message.ExecutionId)).MessageId);
    }
    [Fact]
    public async Task Credential_rotation_between_attempts_fails_closed_without_crossing_idempotency_scope()
    {
        await fixture.ResetAsync(); var path = "/rotate/" + Guid.NewGuid(); Plan(path, (503, "30"), (200, null));
        var (workflow, message, credential) = await Request(path, HttpRequestMethod.Post, 3, true, true);
        await Handler().HandleAsync(message);
        await Credentials.RotateAsync(credential!.Id, workflow.OwnerUserId, 1, new("ficticio-novo-segredo-teste"), credential.UpdatedAt.AddSeconds(1));
        await Due(message.ExecutionId); await Handler().HandleAsync(await Future(message.ExecutionId));
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(ExecutionFailureCode.CredentialUnavailable, history.Nodes[1].ErrorCode); Assert.Equal(1, server.Counts[path]);
        var attempts = history.Attempts!.Where(a => a.NodeExecutionId == history.Nodes[1].Id).OrderBy(a => a.Number).ToArray();
        Assert.Equal(1, attempts[0].CredentialRevisionUsed); Assert.Null(attempts[1].CredentialRevisionUsed);
        Assert.Equal(ExternalOutcome.NotStarted, attempts[1].ExternalOutcome);
    }
    [Fact]
    public async Task Replay_is_explicit_idempotent_pinned_and_does_not_reopen_the_failed_execution()
    {
        await fixture.ResetAsync(); var path = "/replay/" + Guid.NewGuid(); Plan(path, (503, null));
        var (workflow, message, _) = await Request(path, HttpRequestMethod.Post, 1); await Handler().HandleAsync(message);
        var original = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        var replay = new PostgresExecutionReplayStore(fixture.Factory, fixture.Protection);
        await Assert.ThrowsAsync<WorkflowStateConflictException>(() => replay.ReplayAsync(message.ExecutionId, workflow.OwnerUserId, new("same-key", false)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => replay.ReplayAsync(message.ExecutionId, Guid.NewGuid(), new("same-key", true)));
        var revision = workflow.Revision; workflow.CreateDraft(Guid.NewGuid(), WorkflowStoreFixtures.Start.AddHours(1));
        workflow.PublishDraft(WorkflowStoreFixtures.Start.AddHours(2)); workflow.Archive(WorkflowStoreFixtures.Start.AddHours(3));
        await new PostgresWorkflowStore(fixture.Factory).SaveAsync(workflow, revision);
        var results = await Task.WhenAll(replay.ReplayAsync(message.ExecutionId, workflow.OwnerUserId, new("same-key", true)),
            replay.ReplayAsync(message.ExecutionId, workflow.OwnerUserId, new("same-key", true)));
        Assert.Equal(results[0], results[1]); Assert.Equal(original.WorkflowVersionId, results[0].WorkflowVersionId);
        Assert.NotEqual(original.Id, results[0].Id); Assert.Equal(original, await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId));
        Assert.Single((await replay.ReplaysAsync(original.Id, workflow.OwnerUserId))!);
        await using var db = await fixture.Factory.CreateDbContextAsync(); Assert.Equal(2, await db.WorkflowExecutions.CountAsync());
        Assert.DoesNotContain("same-key", (await db.ExecutionReplays.SingleAsync()).KeyDigest);
    }

    [Fact]
    public async Task Worker_that_lost_lease_cannot_send_an_http_request()
    {
        await fixture.ResetAsync(); var path = "/stale/" + Guid.NewGuid(); var (workflow, message, _) = await Request(path, HttpRequestMethod.Post, 1);
        var claim = await Inbox.TryClaimAsync(message, EngineOptions.Default.Lease); var store = fixture.EngineStore;
        var cp = (await store.LoadAsync(claim))!; var start = await store.BeginNodeAsync(claim, cp.Revision, cp.NextNodeId);
        var next = new ExecutionPath(cp.Version, workflow.OwnerUserId).Next(cp.NextNodeId, "next");
        await store.SaveNodeAsync(claim, start.Revision, cp.NextNodeId, NodeResult.Success(cp.Input), next);
        cp = (await store.LoadAsync(claim))!; start = await store.BeginNodeAsync(claim, cp.Revision, cp.NextNodeId);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE message_id = {message.MessageId}");
        Assert.Equal(InboxClaimStatus.Acquired, (await Inbox.TryClaimAsync(message, EngineOptions.Default.Lease)).Status);
        var definition = cp.Version.Nodes.Single(n => n.NodeId == cp.NextNodeId);
        var old = new NodeRunContext(message.ExecutionId, message.CorrelationId, definition, cp.Input, workflow.OwnerUserId, start.AttemptNumber,
            (revision, token) => store.BeforeExternalCallAsync(claim, start.Revision, cp.NextNodeId, revision, token));
        await Assert.ThrowsAsync<ExecutionAuthorityLostException>(() => Http().ExecuteAsync(old, default));
        Assert.Equal(0, server.Counts.GetValueOrDefault(path));
    }
}
