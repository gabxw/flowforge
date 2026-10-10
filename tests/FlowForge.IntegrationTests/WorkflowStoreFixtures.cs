using System.Text.Json;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Persistence;
namespace FlowForge.IntegrationTests;
internal static class WorkflowStoreFixtures
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    internal static JsonElement Json(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    internal static async Task<Workflow> CreateAsync(PostgreSqlFixture fixture, Guid owner, string name = "Orders", bool draft = true)
    {
        await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner, Start);
        var workflow = Definition(owner, name, draft);
        // Referências agora exigem credencial real do mesmo dono; dados fictícios, somente no container de teste.
        foreach (var reference in workflow.Versions.SelectMany(v => v.Nodes).Where(n => n.Credential is not null).Select(n => n.Credential!).DistinctBy(c => c.Id))
        {
            var credential = new FlowForge.Domain.Credentials.Credential(reference.Id, owner, "Test API",
                FlowForge.Domain.Credentials.CredentialType.BearerToken, FlowForge.Domain.Credentials.HttpsOrigin.Parse("https://example.com"), null, Start);
            await new PostgresCredentialStore(fixture.Factory, new FlowForge.Infrastructure.Security.CredentialProtection(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()))
                .CreateAsync(credential.Snapshot, new FlowForge.Application.Credentials.CredentialSecret(Guid.NewGuid().ToString("N")));
        }
        await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow);
        return workflow;
    }
    internal static Workflow Definition(Guid owner, string name = "Orders", bool draft = true)
    {
        var workflow = new Workflow(Guid.NewGuid(), owner, name, Start.AddTicks(3), "History");
        var version = workflow.CreateDraft(Guid.NewGuid(), Start.AddTicks(11));
        var trigger = Guid.NewGuid(); var condition = Guid.NewGuid(); var http = Guid.NewGuid();
        var delay = Guid.NewGuid(); var transform = Guid.NewGuid(); var log = Guid.NewGuid();
        workflow.ReplaceDraftGraph([
            new(version.Id, log, NodeType.Log, new LogConfiguration("Received")),
            new(version.Id, condition, NodeType.Condition, new ConditionConfiguration(new JsonPointer("/total"), ConditionOperator.GreaterThan, Json("123456.7890123456789"))),
            new(version.Id, trigger, NodeType.WebhookTrigger, new WebhookTriggerConfiguration()),
            new(version.Id, transform, NodeType.TransformJson, new TransformJsonConfiguration([
                TransformField.FromPath("total", new JsonPointer("/total")),
                TransformField.FromValue("meta", Json("""{"b":1.00,"a":[null,true,100]}"""))])),
            new(version.Id, http, NodeType.HttpRequest, new HttpRequestConfiguration(new Uri("https://example.com/api"), HttpRequestMethod.Post),
                new NodePosition(12.5, -42), new CredentialReference(Guid.NewGuid(), owner)),
            new(version.Id, delay, NodeType.Delay, new DelayConfiguration(TimeSpan.FromTicks(123456789)))
        ], [Edge(http, transform), Edge(trigger, condition), Edge(condition, http, "true"), Edge(condition, delay, "false"),
            Edge(delay, transform), Edge(transform, log)], Start.AddTicks(23));
        workflow.PublishDraft(Start.AddTicks(31));
        if (draft) workflow.CreateDraft(Guid.NewGuid(), Start.AddTicks(42));
        return workflow;
        WorkflowConnection Edge(Guid from, Guid to, string port = "next") => new(Guid.NewGuid(), version.Id, from, to, port);
    }
}
