using System.Security.Cryptography;
using System.Text;
using FlowForge.Application.Executions;
using FlowForge.Application.Webhooks;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;

namespace FlowForge.IntegrationTests;
[Collection("Dispatch")]
public sealed class WebhookStoreTests(DispatchFixture fixture)
{
    private PostgresWebhookStore Store => new(fixture.Factory, fixture.Protection, WebhookAcceptanceOptions.Default);
    private static WebhookPayload Payload(string text = "{\"event\":42,\"token\":\"ficticio-privado\"}") => WebhookPayload.Parse(Encoding.UTF8.GetBytes(text));
    private async Task<Workflow> Workflow(Guid? user = null)
    {
        var owner = user ?? Guid.NewGuid(); await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner, Start);
        var workflow = new Workflow(Guid.NewGuid(), owner, "Webhook", Start); var version = workflow.CreateDraft(Guid.NewGuid(), Start);
        var first = Guid.NewGuid(); var last = Guid.NewGuid();
        workflow.ReplaceDraftGraph([new(version.Id, first, NodeType.WebhookTrigger, new WebhookTriggerConfiguration()),
            new(version.Id, last, NodeType.Log, new LogConfiguration("Evento fictício"))],
            [new(Guid.NewGuid(), version.Id, first, last, "next")], Start);
        workflow.PublishDraft(Start); await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow); return workflow;
    }
    private async Task<ExecutionRequestedMessage> Message(Guid executionId)
    {
        await using var db = await fixture.Factory.CreateDbContextAsync(); var outgoing = await db.OutboxMessages.SingleAsync(o => o.ExecutionId == executionId);
        return new(outgoing.ContractVersion, outgoing.Id, outgoing.ExecutionId, outgoing.CorrelationId);
    }
    [Fact]
    public async Task Protected_initial_input_is_bound_to_execution_and_separate_from_mutable_checkpoint()
    {
        await fixture.ResetAsync(); var workflow = await Workflow(); var issued = await Store.CreateAsync(workflow.Id, workflow.OwnerUserId);
        var payload = Payload(); var receipt = await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, payload, "event-42");
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var endpoint = await db.WebhookEndpoints.SingleAsync(e => e.Id == issued.Endpoint.Id);
        Assert.Equal(32, endpoint.SecretHash.Length); Assert.NotEqual(Encoding.ASCII.GetBytes(issued.Secret), endpoint.SecretHash);
        var row = await db.WorkflowExecutions.SingleAsync(); Assert.Null(row.ExecutionContextProtected);
        Assert.Equal(payload.Value.GetRawText(), fixture.Protection.UnprotectTrigger(row.TriggerInputProtected!, row.Id).GetRawText());
        Assert.DoesNotContain("ficticio-privado", Encoding.UTF8.GetString(row.TriggerInputProtected!));
        Assert.Throws<CryptographicException>(() => fixture.Protection.UnprotectTrigger(row.TriggerInputProtected!, Guid.NewGuid()));
        Assert.Throws<CryptographicException>(() => fixture.Protection.Unprotect(row.TriggerInputProtected!, row.Id));
        var original = row.TriggerInputProtected!.ToArray(); var message = await Message(receipt.ExecutionId);
        var claim = await new PostgresExecutionInboxStore(fixture.Factory).TryClaimAsync(message, EngineOptions.Default.Lease);
        var engineStore = fixture.EngineStore; var checkpoint = (await engineStore.LoadAsync(claim))!;
        Assert.Equal(2, checkpoint.Nodes.Count); Assert.Equal(42, checkpoint.Input.GetProperty("event").GetInt32());
        var start = await engineStore.BeginNodeAsync(claim, checkpoint.Revision, checkpoint.NextNodeId);
        var changed = Json("{\"changed\":true}"); var next = new ExecutionPath(checkpoint.Version, workflow.OwnerUserId).Next(checkpoint.NextNodeId, "next");
        await engineStore.SaveNodeAsync(claim, start.Revision, checkpoint.NextNodeId, NodeResult.Success(changed), next);
        Assert.Equal(MessageDisposition.Completed, await fixture.Engine().RunAsync(claim));
        var final = await db.WorkflowExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(original, final.TriggerInputProtected);
        Assert.True(fixture.Protection.Unprotect(final.ExecutionContextProtected!, final.Id).GetProperty("changed").GetBoolean());
        var nodes = (await new PostgresExecutionStore(fixture.Factory).HistoryAsync(row.Id, workflow.OwnerUserId))!.Nodes;
        Assert.Equal(ExecutionPayload.Summarize(payload.Value), nodes[0].Input);
        Assert.Equal(ExecutionPayload.Summarize(changed), nodes[1].Input);
    }
    [Fact]
    public async Task Reservation_execution_and_outbox_roll_back_together_when_commit_fails()
    {
        await fixture.ResetAsync(); var workflow = await Workflow(); var issued = await Store.CreateAsync(workflow.Id, workflow.OwnerUserId);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_webhook_commit_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test commit failure'; END; $$;
            CREATE CONSTRAINT TRIGGER test_webhook_commit_failure AFTER INSERT ON webhook_idempotency DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION test_webhook_commit_failure();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "rollback"));
            Assert.Empty(await db.WorkflowExecutions.ToArrayAsync()); Assert.Empty(await db.OutboxMessages.ToArrayAsync()); Assert.Empty(await db.WebhookIdempotency.ToArrayAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER test_webhook_commit_failure ON webhook_idempotency; DROP FUNCTION test_webhook_commit_failure()"); }
        Assert.False((await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "rollback")).Replayed);
    }
    [Fact]
    public async Task Same_key_is_scoped_to_endpoint_and_owner_and_requests_without_key_are_distinct()
    {
        await fixture.ResetAsync(); var first = await Workflow(); var second = await Workflow(first.OwnerUserId); var other = await Workflow();
        var ids = new List<Guid>();
        foreach (var workflow in new[] { first, second, other })
        {
            var issued = await Store.CreateAsync(workflow.Id, workflow.OwnerUserId);
            ids.Add((await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "shared-key")).ExecutionId);
            var replay = await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "shared-key"); Assert.True(replay.Replayed); Assert.Equal(ids.Last(), replay.ExecutionId);
            ids.Add((await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), null)).ExecutionId);
            ids.Add((await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), null)).ExecutionId);
        }
        Assert.Equal(9, ids.Distinct().Count());
        await using var db = await fixture.Factory.CreateDbContextAsync(); Assert.Equal(9, await db.OutboxMessages.CountAsync()); Assert.Equal(3, await db.WebhookIdempotency.CountAsync());
    }
    [Fact]
    public async Task Expired_key_can_be_reused_but_previous_execution_remains_immutable()
    {
        await fixture.ResetAsync(); var workflow = await Workflow(); var issued = await Store.CreateAsync(workflow.Id, workflow.OwnerUserId);
        var first = await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "expires");
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE webhook_idempotency SET created_at = clock_timestamp() - interval '2 days', expires_at = clock_timestamp() - interval '1 day'");
        var next = await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload("[]"), "expires"); Assert.False(next.Replayed); Assert.NotEqual(first.ExecutionId, next.ExecutionId);
        Assert.Equal(2, await db.WorkflowExecutions.CountAsync()); Assert.Equal(2, await db.OutboxMessages.CountAsync());
        var reservation = await db.WebhookIdempotency.SingleAsync(); Assert.Equal(next.ExecutionId, reservation.ExecutionId);
        Assert.Equal(TimeSpan.FromHours(24), reservation.ExpiresAt - reservation.CreatedAt);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Accept_revalidates_secret_and_enabled_state_after_preflight(bool rotate)
    {
        await fixture.ResetAsync(); var workflow = await Workflow(); var issued = await Store.CreateAsync(workflow.Id, workflow.OwnerUserId);
        await Store.AuthorizeAsync(issued.Endpoint.Id, issued.Secret);
        if (rotate) _ = await Store.RotateAsync(workflow.Id, workflow.OwnerUserId);
        else _ = await Store.SetEnabledAsync(workflow.Id, workflow.OwnerUserId, false);
        await Assert.ThrowsAsync<WebhookRejectedException>(() => Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "race"));
        await using var db = await fixture.Factory.CreateDbContextAsync(); Assert.Empty(await db.WorkflowExecutions.ToArrayAsync()); Assert.Empty(await db.WebhookIdempotency.ToArrayAsync()); Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }
    [Fact]
    public async Task Republish_moves_new_requests_but_replays_and_accepted_executions_keep_original_version()
    {
        await fixture.ResetAsync(); var workflow = await Workflow(); var issued = await Store.CreateAsync(workflow.Id, workflow.OwnerUserId);
        var first = await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "pinned");
        var revision = workflow.Revision; workflow.CreateDraft(Guid.NewGuid(), Start.AddHours(1)); workflow.PublishDraft(Start.AddHours(2));
        await new PostgresWorkflowStore(fixture.Factory).SaveAsync(workflow, revision);
        var replay = await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "pinned"); Assert.Equal(first.WorkflowVersionId, replay.WorkflowVersionId); Assert.Equal(first.ExecutionId, replay.ExecutionId);
        var newer = await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), null); Assert.Equal(workflow.CurrentPublishedVersionId, newer.WorkflowVersionId); Assert.NotEqual(first.WorkflowVersionId, newer.WorkflowVersionId);
        await Store.SetEnabledAsync(workflow.Id, workflow.OwnerUserId, false);
        Assert.Equal(MessageDisposition.Completed, await fixture.Handler().HandleAsync(await Message(first.ExecutionId)));
        var execution = (await new PostgresExecutionStore(fixture.Factory).GetAsync(first.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Succeeded, execution.Status); Assert.Equal(first.WorkflowVersionId, execution.WorkflowVersionId);
    }
    [Fact]
    public async Task Schema_rejects_cross_owner_endpoint_and_receipt_relations()
    {
        await fixture.ResetAsync(); var first = await Workflow(); var second = await Workflow();
        var issued = await Store.CreateAsync(first.Id, first.OwnerUserId); await Store.AcceptAsync(issued.Endpoint.Id, issued.Secret, Payload(), "constraint");
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var crossOwner = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_endpoints SET owner_user_id = {second.OwnerUserId} WHERE id = {issued.Endpoint.Id}")); Assert.Equal("23503", crossOwner.SqlState);
        var receipt = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_idempotency SET workflow_id = {second.Id}, owner_user_id = {second.OwnerUserId} WHERE endpoint_id = {issued.Endpoint.Id}")); Assert.Equal("23503", receipt.SqlState);
    }
}
