using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Domain.Tests;

internal static class GraphFixtures
{
    internal static readonly Guid VersionId = Id(1000);
    internal static readonly Guid OwnerId = Id(2000);
    internal static readonly DateTimeOffset CreatedAt = new(2026, 10, 9, 10, 0, 0, TimeSpan.FromHours(-3));

    internal static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    internal static WorkflowNode Trigger(int id = 1, Guid? versionId = null) =>
        new(versionId ?? VersionId, Id(id), NodeType.WebhookTrigger, new WebhookTriggerConfiguration());

    internal static WorkflowNode Log(int id = 2, Guid? versionId = null) =>
        new(versionId ?? VersionId, Id(id), NodeType.Log, new LogConfiguration("Logged"));

    internal static WorkflowNode Condition(int id = 2, Guid? versionId = null) =>
        new(versionId ?? VersionId, Id(id), NodeType.Condition,
            new ConditionConfiguration(new JsonPointer("/ready"), ConditionOperator.Exists));

    internal static WorkflowNode Http(int id, Guid credentialOwner, Guid? versionId = null) =>
        new(versionId ?? VersionId, Id(id), NodeType.HttpRequest,
            new HttpRequestConfiguration(new Uri("https://example.com/path"), HttpRequestMethod.Get),
            credential: new CredentialReference(Id(3000 + id), credentialOwner));

    internal static WorkflowConnection Edge(int id, int source, int target, string port = "next", Guid? versionId = null) =>
        new(Id(id), versionId ?? VersionId, Id(source), Id(target), port);

    internal static Workflow NewWorkflow() => new(Id(4000), OwnerId, " Orders ", CreatedAt, "Example");

    internal static WorkflowVersion PublishTrigger(Workflow workflow, Guid? versionId = null)
    {
        var draft = workflow.CreateDraft(versionId ?? VersionId, CreatedAt.AddMinutes(1));
        workflow.ReplaceDraftGraph([Trigger(versionId: draft.Id)], [], CreatedAt.AddMinutes(2));
        return workflow.PublishDraft(CreatedAt.AddMinutes(3));
    }
}
