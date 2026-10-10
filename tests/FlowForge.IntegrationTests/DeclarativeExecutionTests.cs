
using System.Net;
using System.Text;
using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Application.Webhooks;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Http;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;

namespace FlowForge.IntegrationTests;

[Collection("Dispatch")]
public sealed class DeclarativeExecutionTests(DispatchFixture fixture, ControlledHttpsServer server) : IClassFixture<ControlledHttpsServer>
{
    private PostgresExecutionStore Reader => new(fixture.Factory);
    private PostgresExecutionInboxStore Inbox => new(fixture.Factory);
    private PostgresExecutionOutboxStore Outbox => new(fixture.Factory);
    private SequentialExecutionEngine Engine() => fixture.Engine([
        new TriggerNodeExecutor(), new ConditionNodeExecutor(), new TransformJsonNodeExecutor(),
        new DelayNodeExecutor(), new LogNodeExecutor()]);
    private ExecutionMessageHandler Handler() => new(Inbox, Engine());

    private async Task<Workflow> WorkflowAsync()
    {
        var owner = Guid.NewGuid();
        await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner, Start);
        var workflow = new Workflow(Guid.NewGuid(), owner, "Declarative engine", Start);
        workflow.CreateDraft(Guid.NewGuid(), Start);
        return workflow;
    }
    private async Task<ExecutionRequestedMessage> PersistAsync(Workflow workflow)
    {
        workflow.PublishDraft(Start);
        await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workflow.Id, workflow.OwnerUserId);
        await Reader.RequestAsync(request);
        return DispatchStoreTests.Message(request);
    }
    private async Task<(Workflow Workflow, ExecutionRequestedMessage Message)> LinearAsync(params NodeConfiguration[] configs)
    {
        var workflow = await WorkflowAsync(); var version = workflow.DraftVersion!;
        var all = new NodeConfiguration[] { new WebhookTriggerConfiguration() }.Concat(configs).ToArray();
        var nodes = all.Select(c => new WorkflowNode(version.Id, Guid.NewGuid(), c.Type, c)).ToArray();
        var edges = nodes.Zip(nodes.Skip(1), (a, b) => new WorkflowConnection(Guid.NewGuid(), version.Id, a.NodeId, b.NodeId, "next")).ToArray();
        workflow.ReplaceDraftGraph(nodes, edges, Start);
        return (workflow, await PersistAsync(workflow));
    }
    private async Task<ExecutionRequestedMessage> Continuation(Guid execution, int sequence)
    {
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var row = await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.ExecutionId == execution && o.DispatchSequence == sequence);
        return new(row.ContractVersion, row.Id, row.ExecutionId, row.CorrelationId);
    }
    private async Task MarkInitialPublished()
    {
        var claim = (await Outbox.TryClaimAsync(EngineOptions.Default.Lease))!;
        Assert.NotNull(claim); Assert.True(await Outbox.MarkPublishedAsync(claim));
    }
    private async Task MakeDue(Guid execution)
    {
        // Relógio controlado somente no PostgreSQL descartável do teste. Sem aguardar horas reais.
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var row = (await db.WorkflowExecutions.FromSqlInterpolated($"SELECT * FROM workflow_executions WHERE id = {execution} FOR UPDATE").ToArrayAsync()).Single();
        row.ResumeAt = row.StartedAt!.Value.AddTicks(10);
        var dispatch = await db.OutboxMessages.SingleAsync(o => o.ExecutionId == execution && o.DispatchSequence == row.DispatchSequence);
        dispatch.AvailableAt = dispatch.CreatedAt;
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Condition_selects_one_branch_and_preserves_shared_convergence(bool predicate)
    {
        await fixture.ResetAsync(); var workflow = await WorkflowAsync(); var v = workflow.DraftVersion!;
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        NodeConfiguration[] config = [
            new WebhookTriggerConfiguration(),
            new TransformJsonConfiguration([TransformField.FromValue("total", Json(predicate ? "150" : "50"))]),
            new ConditionConfiguration(new JsonPointer("/total"), ConditionOperator.GreaterThan, Json("100")),
            new LogConfiguration("Ramo verdadeiro"), new LogConfiguration("Ramo falso"), new LogConfiguration("Convergência")];
        workflow.ReplaceDraftGraph(config.Select((c, i) => new WorkflowNode(v.Id, ids[i], c.Type, c)).ToArray(), [
            new(Guid.NewGuid(), v.Id, ids[0], ids[1], "next"), new(Guid.NewGuid(), v.Id, ids[1], ids[2], "next"),
            new(Guid.NewGuid(), v.Id, ids[2], ids[3], "true"), new(Guid.NewGuid(), v.Id, ids[2], ids[4], "false"),
            new(Guid.NewGuid(), v.Id, ids[3], ids[5], "next"), new(Guid.NewGuid(), v.Id, ids[4], ids[5], "next")], Start);
        var message = await PersistAsync(workflow);
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(message));
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Succeeded, (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.Status);
        Assert.Equal(NodeExecutionStatus.Succeeded, history.Nodes[predicate ? 3 : 4].Status);
        var skipped = history.Nodes[predicate ? 4 : 3];
        Assert.Equal(NodeExecutionStatus.Skipped, skipped.Status); Assert.Equal(0, skipped.AttemptCount); Assert.Null(skipped.StartedAt);
        Assert.Equal(NodeExecutionStatus.Succeeded, history.Nodes[5].Status); Assert.Equal(1, history.Nodes[5].AttemptCount);
        Assert.Equal(2, history.Logs.Count); Assert.All(history.Nodes.Take(3), n => Assert.Equal(NodeExecutionStatus.Succeeded, n.Status));
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(message));
        Assert.Null(await Reader.HistoryAsync(message.ExecutionId, Guid.NewGuid())); // consulta de outro dono deve ser nula
    }

    [Theory]
    [InlineData(0, ExecutionFailureCode.TransformSourceMissing)]
    [InlineData(1, ExecutionFailureCode.ContextLimitExceeded)]
    [InlineData(2, ExecutionFailureCode.TransformValueInvalid)]
    [InlineData(3, ExecutionFailureCode.ConditionValueNotComparable)]
    public async Task Declarative_failure_has_safe_persisted_code_and_skips_the_tail(int scenario, ExecutionFailureCode expected)
    {
        await fixture.ResetAsync();
        NodeConfiguration config = scenario switch {
            0 => new TransformJsonConfiguration([TransformField.FromPath("missing", new JsonPointer("/absent"))]),
            1 => new TransformJsonConfiguration(Enumerable.Range(0, 50).Select(i => TransformField.FromValue("copy" + i, Json(JsonSerializer.Serialize(new string('x', 4000)))))),
            2 => new TransformJsonConfiguration([TransformField.FromValue("deep", Json(new string('[', 32) + "1" + new string(']', 32)))]),
            _ => new ConditionConfiguration(new JsonPointer(""), ConditionOperator.GreaterThan, Json("1"))
        };
        // Condition necessita ambas as portas; cenário 3 é verificado com um grafo próprio.
        if (scenario == 3) {
            var workflow = await WorkflowAsync(); var v = workflow.DraftVersion!;
            var trigger = Guid.NewGuid(); var condition = Guid.NewGuid(); var log = Guid.NewGuid();
            workflow.ReplaceDraftGraph([new(v.Id, trigger, NodeType.WebhookTrigger, new WebhookTriggerConfiguration()),
                new(v.Id, condition, NodeType.Condition, config), new(v.Id, log, NodeType.Log, new LogConfiguration("Não executar"))],
                [new(Guid.NewGuid(), v.Id, trigger, condition, "next"), new(Guid.NewGuid(), v.Id, condition, log, "true"),
                 new(Guid.NewGuid(), v.Id, condition, log, "false")], Start);
            var message = await PersistAsync(workflow); await Handler().HandleAsync(message);
            await AssertFailure(message, workflow, expected); return;
        }
        var (linear, request) = await LinearAsync(config, new LogConfiguration("Não executar"));
        await Handler().HandleAsync(request); await AssertFailure(request, linear, expected);
    }
    private async Task AssertFailure(ExecutionRequestedMessage message, Workflow workflow, ExecutionFailureCode expected)
    {
        var result = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status); Assert.Equal(expected, result.ErrorCode);
        Assert.Equal(expected, history.Nodes[1].ErrorCode); Assert.Null(history.Nodes[1].Output);
        Assert.Equal(NodeExecutionStatus.Skipped, history.Nodes[2].Status); Assert.Empty(history.Logs);
    }

    [Fact]
    public async Task Waiting_delay_releases_lease_acknowledges_old_dispatch_and_rejects_early_or_stale_wakeups()
    {
        await fixture.ResetAsync();
        var (workflow, message) = await LinearAsync(
            new TransformJsonConfiguration([TransformField.FromValue("confidential", Json("\"ficticio-pausado\""))]),
            new DelayConfiguration(TimeSpan.FromHours(24)), new LogConfiguration("Depois da espera"));
        await MarkInitialPublished();
        var claim = await Inbox.TryClaimAsync(message, EngineOptions.Default.Lease);
        Assert.Equal(MessageDisposition.Completed, await Engine().RunAsync(claim).WaitAsync(TimeSpan.FromSeconds(10)));
        var snapshot = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Running, snapshot.Status); Assert.NotNull(snapshot.ResumeAt); Assert.Null(snapshot.FinishedAt);
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(NodeExecutionStatus.Running, history.Nodes[2].Status); Assert.Equal(1, history.Nodes[2].AttemptCount);
        Assert.Null(history.Nodes[2].FinishedAt); Assert.Equal(NodeExecutionStatus.Pending, history.Nodes[3].Status);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var old = await db.InboxMessages.SingleAsync(); Assert.NotNull(old.CompletedAt); Assert.Null(old.ClaimToken); Assert.Null(old.ClaimUntil);
        var future = await Continuation(message.ExecutionId, 1); Assert.NotEqual(message.MessageId, future.MessageId);
        Assert.Equal(InboxClaimStatus.Completed, (await Inbox.TryClaimAsync(message, EngineOptions.Default.Lease)).Status);
        Assert.Equal(InboxClaimStatus.Completed, (await Inbox.TryClaimAsync(future, EngineOptions.Default.Lease)).Status);
        Assert.Null(await Outbox.TryClaimAsync(EngineOptions.Default.Lease)); Assert.Single(await db.InboxMessages.ToArrayAsync());
        Assert.Equal(LeaseStatus.Lost, await fixture.EngineStore.RenewAsync(claim, EngineOptions.Default.Lease));
        var row = await db.WorkflowExecutions.SingleAsync();
        Assert.DoesNotContain("ficticio-pausado", Encoding.UTF8.GetString(row.ExecutionContextProtected!));
        Assert.Equal(snapshot.ResumeAt, (await db.OutboxMessages.SingleAsync(o => o.DispatchSequence == 1)).AvailableAt);
        // A mesma engine pode processar outra execução durante a espera de 24 horas.
        var (other, otherMessage) = await LinearAsync(new LogConfiguration("Disponível"));
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(otherMessage));
        Assert.Equal(WorkflowExecutionStatus.Succeeded, (await Reader.GetAsync(otherMessage.ExecutionId, other.OwnerUserId))!.Status);
        Assert.Equal(snapshot, await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId));
    }

    [Fact]
    public async Task New_worker_resumes_protected_context_once_without_a_new_delay_attempt_or_mutating_the_pinned_version()
    {
        await fixture.ResetAsync(); var duration = TimeSpan.FromMinutes(1);
        var (workflow, message) = await LinearAsync(new TransformJsonConfiguration([TransformField.FromValue("answer", Json("42"))]),
            new DelayConfiguration(duration), new LogConfiguration("Fim original"));
        await Handler().HandleAsync(message);
        var before = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        var oldVersion = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.WorkflowVersionId;
        var revision = workflow.Revision; var v = workflow.CreateDraft(Guid.NewGuid(), Start.AddSeconds(1));
        workflow.ReplaceDraftGraph(v.Nodes.Select(n => n.Type == NodeType.Log
            ? new WorkflowNode(v.Id, n.NodeId, NodeType.Log, new LogConfiguration("Nova versão")) : n).ToArray(), v.Connections, Start.AddSeconds(2));
        workflow.PublishDraft(Start.AddSeconds(3)); workflow.Archive(Start.AddSeconds(4));
        await new PostgresWorkflowStore(fixture.Factory).SaveAsync(workflow, revision);
        await MakeDue(message.ExecutionId); var continuation = await Continuation(message.ExecutionId, 1);
        var claims = await Task.WhenAll(Inbox.TryClaimAsync(continuation, EngineOptions.Default.Lease), Inbox.TryClaimAsync(continuation, EngineOptions.Default.Lease));
        var current = Assert.Single(claims, c => c.Status == InboxClaimStatus.Acquired);
        Assert.Single(claims, c => c.Status == InboxClaimStatus.Busy);
        Assert.Equal(MessageDisposition.Completed, await Engine().RunAsync(current));
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(continuation));
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(message));
        var after = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(before.Nodes.Take(2), after.Nodes.Take(2)); Assert.Equal(before.Nodes[2].StartedAt, after.Nodes[2].StartedAt);
        Assert.All(after.Nodes, n => { Assert.Equal(1, n.AttemptCount); Assert.Equal(NodeExecutionStatus.Succeeded, n.Status); });
        Assert.Single(after.Logs); Assert.Equal(Encoding.UTF8.GetByteCount("Fim original"), after.Logs[0].MessageByteLength);
        var result = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(oldVersion, result.WorkflowVersionId); Assert.Null(result.ResumeAt); Assert.Equal(WorkflowExecutionStatus.Succeeded, result.Status);
        await using var db = await fixture.Factory.CreateDbContextAsync(); var row = await db.WorkflowExecutions.SingleAsync();
        Assert.Equal(42, fixture.Protection.Unprotect(row.ExecutionContextProtected!, row.Id).GetProperty("answer").GetInt32());
        Assert.Equal(2, await db.InboxMessages.CountAsync()); Assert.All(await db.InboxMessages.ToArrayAsync(), i => Assert.NotNull(i.CompletedAt));
    }

    [Fact]
    public async Task Consecutive_delays_use_distinct_messages_and_do_not_reopen_completed_inbox_entries()
    {
        await fixture.ResetAsync(); var (workflow, message) = await LinearAsync(
            new DelayConfiguration(TimeSpan.FromHours(1)), new DelayConfiguration(TimeSpan.FromHours(2)), new LogConfiguration("Fim"));
        await Handler().HandleAsync(message);
        await MakeDue(message.ExecutionId); var first = await Continuation(message.ExecutionId, 1); await Handler().HandleAsync(first);
        var waiting = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(NodeExecutionStatus.Succeeded, waiting.Nodes[1].Status); Assert.Equal(NodeExecutionStatus.Running, waiting.Nodes[2].Status);
        await Handler().HandleAsync(message); await Handler().HandleAsync(first);
        await MakeDue(message.ExecutionId); var second = await Continuation(message.ExecutionId, 2); await Handler().HandleAsync(second);
        Assert.Equal(WorkflowExecutionStatus.Succeeded, (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.Status);
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Single(history.Logs); Assert.All(history.Nodes, n => Assert.Equal(1, n.AttemptCount));
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(new[] { 0, 1, 2 }, await db.OutboxMessages.OrderBy(o => o.DispatchSequence).Select(o => o.DispatchSequence).ToArrayAsync());
        Assert.Equal(3, await db.InboxMessages.CountAsync()); Assert.Equal(0, await db.InboxMessages.CountAsync(i => i.CompletedAt == null));
    }


    [Fact]
    public async Task Sub_microsecond_duration_is_rounded_up_and_the_scheduler_claims_only_once()
    {
        await fixture.ResetAsync(); var duration = TimeSpan.FromTicks(13);
        var (workflow, message) = await LinearAsync(new DelayConfiguration(duration), new LogConfiguration("Fim"));
        await MarkInitialPublished(); await Handler().HandleAsync(message);
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        var paused = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(history.Nodes[1].StartedAt!.Value.AddTicks(20), paused.ResumeAt);
        Assert.True(paused.ResumeAt >= history.Nodes[1].StartedAt + duration);
        var claims = await Task.WhenAll(Outbox.TryClaimAsync(EngineOptions.Default.Lease), Outbox.TryClaimAsync(EngineOptions.Default.Lease));
        var winner = Assert.Single(claims, c => c is not null)!;
        Assert.Equal((await Continuation(message.ExecutionId, 1)).MessageId, winner.Message.MessageId);
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(winner.Message));
        Assert.Equal(WorkflowExecutionStatus.Succeeded, (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.Status);
    }

    [Fact]
    public async Task Cancellation_accelerates_existing_timer_and_finishes_without_waiting_24_hours()
    {
        await fixture.ResetAsync(); var (workflow, message) = await LinearAsync(new DelayConfiguration(TimeSpan.FromHours(24)), new LogConfiguration("Não executar"));
        await MarkInitialPublished(); await Handler().HandleAsync(message);
        var original = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Null(await Reader.RequestCancellationAsync(message.ExecutionId, Guid.NewGuid()));
        var cancelled = (await Reader.RequestCancellationAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(original.ResumeAt, cancelled.ResumeAt); Assert.Equal(WorkflowExecutionStatus.Running, cancelled.Status);
        Assert.Equal(cancelled, await Reader.RequestCancellationAsync(message.ExecutionId, workflow.OwnerUserId));
        var scheduled = (await Outbox.TryClaimAsync(EngineOptions.Default.Lease))!;
        Assert.Equal((await Continuation(message.ExecutionId, 1)).MessageId, scheduled.Message.MessageId);
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(scheduled.Message));
        var result = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status); Assert.Null(result.ResumeAt); Assert.Null(result.ErrorCode);
        var nodes = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!.Nodes;
        Assert.Equal(NodeExecutionStatus.Cancelled, nodes[1].Status); Assert.Equal(NodeExecutionStatus.Skipped, nodes[2].Status);
        Assert.Equal(1, nodes[1].AttemptCount);
        await using var db = await fixture.Factory.CreateDbContextAsync(); Assert.Equal(2, await db.OutboxMessages.CountAsync()); Assert.Empty(await db.ExecutionLogs.ToArrayAsync());
    }

    [Fact]
    public async Task Cancellation_winning_before_pause_commit_creates_no_future_dispatch()
    {
        await fixture.ResetAsync(); var (workflow, message) = await LinearAsync(new DelayConfiguration(TimeSpan.FromHours(24)), new LogConfiguration("Não executar"));
        var claim = await Inbox.TryClaimAsync(message, EngineOptions.Default.Lease); var store = fixture.EngineStore;
        var cp = (await store.LoadAsync(claim))!; var begin = await store.BeginNodeAsync(claim, cp.Revision, cp.NextNodeId);
        var path = new ExecutionPath(cp.Version, workflow.OwnerUserId); var delayId = path.Next(cp.NextNodeId, "next");
        await store.SaveNodeAsync(claim, begin.Revision, cp.NextNodeId, NodeResult.Success(cp.Input), delayId);
        cp = (await store.LoadAsync(claim))!; begin = await store.BeginNodeAsync(claim, cp.Revision, cp.NextNodeId);
        await Reader.RequestCancellationAsync(message.ExecutionId, workflow.OwnerUserId);
        Assert.Equal(CheckpointWriteStatus.Completed, await store.SaveNodeAsync(claim, begin.Revision, cp.NextNodeId,
            NodeResult.Suspend(cp.Input, TimeSpan.FromHours(24)), path.Next(cp.NextNodeId, "next")));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.Status);
        await using var db = await fixture.Factory.CreateDbContextAsync(); Assert.Single(await db.OutboxMessages.ToArrayAsync()); Assert.Null((await db.WorkflowExecutions.SingleAsync()).ResumeAt);
    }

    [Fact]
    public async Task Pause_commit_failure_rolls_back_future_dispatch_and_replay_keeps_the_original_deadline()
    {
        await fixture.ResetAsync(); var duration = TimeSpan.FromMinutes(1);
        var (workflow, message) = await LinearAsync(new DelayConfiguration(duration), new LogConfiguration("Fim"));
        await using var setup = await fixture.Factory.CreateDbContextAsync();
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_delay_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.completed_at IS NOT NULL THEN RAISE EXCEPTION 'injected failure'; END IF; RETURN NEW; END $$;
            CREATE CONSTRAINT TRIGGER fail_delay_commit AFTER UPDATE ON inbox_messages
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_delay_commit();
            """);
        try {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Handler().HandleAsync(message));
            await using var db = await fixture.Factory.CreateDbContextAsync();
            var row = await db.WorkflowExecutions.SingleAsync();
            Assert.Null(row.ResumeAt); Assert.Equal(0, row.DispatchSequence);
            Assert.Single(await db.OutboxMessages.ToArrayAsync()); Assert.Null((await db.InboxMessages.SingleAsync()).CompletedAt);
            Assert.Equal(NodeExecutionStatus.Running, (await db.NodeExecutions.SingleAsync(n => n.Ordinal == 1)).Status);
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_delay_commit ON inbox_messages; DROP FUNCTION fail_delay_commit();"); }
        var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        var start = history.Nodes[1].StartedAt!.Value;
        await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE message_id = {message.MessageId}");
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(message));
        var paused = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(start + duration, paused.ResumeAt); Assert.Equal(start, (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!.Nodes[1].StartedAt);
        Assert.Equal(2, (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!.Nodes[1].AttemptCount);
    }

    [Fact]
    public async Task All_six_nodes_use_real_broker_and_https_then_resume_after_consumer_recreation_without_repeating_http()
    {
        await fixture.ResetAsync(); var workflow = await WorkflowAsync(); var v = workflow.DraftVersion!;
        var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        NodeConfiguration[] config = [
            new WebhookTriggerConfiguration(),
            new ConditionConfiguration(new JsonPointer("/approved"), ConditionOperator.Equals, Json("true")),
            new TransformJsonConfiguration([TransformField.FromPath("value", new JsonPointer("/amount"))]),
            new HttpRequestConfiguration(new Uri(server.Origin + "/ok"), HttpRequestMethod.Post),
            new DelayConfiguration(TimeSpan.FromMinutes(1)), new LogConfiguration("Fim HTTPS"), new LogConfiguration("Recusado")];
        workflow.ReplaceDraftGraph(config.Select((c, i) => new WorkflowNode(v.Id, ids[i], c.Type, c)).ToArray(), [
            new(Guid.NewGuid(), v.Id, ids[0], ids[1], "next"), new(Guid.NewGuid(), v.Id, ids[1], ids[2], "true"),
            new(Guid.NewGuid(), v.Id, ids[1], ids[6], "false"), new(Guid.NewGuid(), v.Id, ids[2], ids[3], "next"),
            new(Guid.NewGuid(), v.Id, ids[3], ids[4], "next"), new(Guid.NewGuid(), v.Id, ids[4], ids[5], "next")], Start);
        workflow.PublishDraft(Start); await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow);
        var hooks = new PostgresWebhookStore(fixture.Factory, fixture.Protection, WebhookAcceptanceOptions.Default);
        var endpoint = await hooks.CreateAsync(workflow.Id, workflow.OwnerUserId);
        var receipt = await hooks.AcceptAsync(endpoint.Endpoint.Id, endpoint.Secret, WebhookPayload.Parse("""{"approved":true,"amount":42}"""u8.ToArray()), "all-six");
        var options = new HttpNodeOptions([server.Origin]);
        ExecutionMessageHandler NewHandler() => new(Inbox, fixture.Engine([
            new TriggerNodeExecutor(), new ConditionNodeExecutor(), new TransformJsonNodeExecutor(),
            new HttpRequestNodeExecutor(new PostgresCredentialStore(fixture.Factory, fixture.CredentialProtection),
                new HttpDestinationPolicy(options, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })), server.Transport(), options),
            new DelayNodeExecutor(), new LogNodeExecutor()]));
        var before = server.Counts.GetValueOrDefault("/ok");
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        var dispatcher = new OutboxDispatcher(Outbox, publisher);
        Assert.True(await dispatcher.DispatchOneAsync());
        using var stop = new CancellationTokenSource();
        var consume = new RabbitExecutionConsumer(fixture.RabbitOptions, NewHandler(), NullLogger<RabbitExecutionConsumer>.Instance).RunSessionAsync(stop.Token);
        try { await Until(() => Waiting(receipt.ExecutionId, workflow.OwnerUserId)); }
        finally { await Stop(consume, stop); }
        // ACK da mensagem inicial e consumer ausente durante o prazo: nenhuma delivery em aberto.
        await using var connection = await fixture.ConnectAsync(); await using var channel = await connection.CreateChannelAsync();
        var queue = await channel.QueueDeclarePassiveAsync(ExecutionRabbitTopology.Queue); Assert.Equal(0u, queue.MessageCount);
        Assert.False(await dispatcher.DispatchOneAsync());
        await MakeDue(receipt.ExecutionId); Assert.True(await dispatcher.DispatchOneAsync());
        var continuation = await Continuation(receipt.ExecutionId, 1); await publisher.PublishAsync(continuation);
        using var resumedStop = new CancellationTokenSource();
        var resumed = new RabbitExecutionConsumer(fixture.RabbitOptions, NewHandler(), NullLogger<RabbitExecutionConsumer>.Instance).RunSessionAsync(resumedStop.Token);
        try { await Until(async () => (await Reader.GetAsync(receipt.ExecutionId, workflow.OwnerUserId))?.Status == WorkflowExecutionStatus.Succeeded); }
        finally { await Stop(resumed, resumedStop); }
        // Redelivery ainda que o ACK da última duplicata se perca permanece um no-op.
        await NewHandler().HandleAsync(continuation);
        var history = (await Reader.HistoryAsync(receipt.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(before + 1, server.Counts["/ok"]); Assert.Single(history.Logs);
        Assert.All(history.Nodes.Take(6), n => { Assert.Equal(1, n.AttemptCount); Assert.Equal(NodeExecutionStatus.Succeeded, n.Status); });
        Assert.Equal(NodeExecutionStatus.Skipped, history.Nodes[6].Status);
        await using var db = await fixture.Factory.CreateDbContextAsync(); var row = await db.WorkflowExecutions.SingleAsync();
        var context = fixture.Protection.Unprotect(row.ExecutionContextProtected!, row.Id);
        Assert.Equal(42, context.GetProperty("body").GetProperty("input").GetProperty("value").GetInt32());
        Assert.Equal(2, await db.InboxMessages.CountAsync()); Assert.Equal(0, await db.InboxMessages.CountAsync(i => i.CompletedAt == null));
    }
    private async Task<bool> Waiting(Guid id, Guid owner) => (await Reader.GetAsync(id, owner))?.ResumeAt is not null;
    private static async Task Until(Func<Task<bool>> condition) {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(50, limit.Token);
    }
    private static async Task Stop(Task consume, CancellationTokenSource stop) {
        await stop.CancelAsync(); try { await consume.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
    }
}
