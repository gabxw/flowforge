using System.Text.Json;
using FlowForge.Domain.Workflows;
using FlowForge.Infrastructure.Persistence.Records;
using FlowForge.Infrastructure.Persistence.Serialization;
using Microsoft.EntityFrameworkCore;
namespace FlowForge.Infrastructure.Persistence;
internal static class WorkflowPersistenceMapper
{
    internal static DateTimeOffset Normalize(DateTimeOffset value) => new(value.UtcTicks / 10 * 10, TimeSpan.Zero);
    internal static DateTimeOffset? Normalize(DateTimeOffset? value) => value is { } timestamp ? Normalize(timestamp) : null;
    internal static WorkflowSnapshot Capture(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var snapshot = new WorkflowSnapshot(workflow.Id, workflow.OwnerUserId, workflow.Name, workflow.Description,
            Normalize(workflow.CreatedAt), Normalize(workflow.UpdatedAt), Normalize(workflow.ArchivedAt), workflow.Revision,
            workflow.CurrentPublishedVersionId, workflow.Versions.Select(version => new WorkflowVersionSnapshot(version.Id,
                version.VersionNumber, version.Status, Normalize(version.CreatedAt), Normalize(version.PublishedAt),
                version.Revision, version.Nodes.ToArray(), version.Connections.ToArray())).ToArray());
        _ = Workflow.Restore(snapshot);
        return snapshot;
    }
    internal static WorkflowRecord Root(WorkflowSnapshot snapshot) => new()
    {
        Id = snapshot.Id, OwnerUserId = snapshot.OwnerUserId, Name = snapshot.Name, Description = snapshot.Description,
        CreatedAt = snapshot.CreatedAt, UpdatedAt = snapshot.UpdatedAt, ArchivedAt = snapshot.ArchivedAt, Revision = snapshot.Revision
    };
    internal static WorkflowVersionRecord Version(WorkflowSnapshot root, WorkflowVersionSnapshot version) => new()
    {
        Id = version.Id, WorkflowId = root.Id, OwnerUserId = root.OwnerUserId, VersionNumber = version.VersionNumber,
        Status = version.Status, CreatedAt = version.CreatedAt, PublishedAt = version.PublishedAt, Revision = version.Revision
    };
    internal static WorkflowNodeRecord[] Nodes(WorkflowSnapshot root, WorkflowVersionSnapshot version) => version.Nodes.Select((node, ordinal) => new WorkflowNodeRecord
    {
        WorkflowVersionId = version.Id, WorkflowId = root.Id, OwnerUserId = root.OwnerUserId, NodeId = node.NodeId,
        Type = node.Type, Configuration = NodeConfigurationJson.Serialize(node.Configuration), CredentialId = node.Credential?.Id,
        PositionX = node.Position.X, PositionY = node.Position.Y, Ordinal = ordinal
    }).ToArray();
    internal static WorkflowConnectionRecord[] Connections(WorkflowVersionSnapshot version) => version.Connections.Select((connection, ordinal) => new WorkflowConnectionRecord
    {
        Id = connection.Id, WorkflowVersionId = version.Id, SourceNodeId = connection.SourceNodeId,
        TargetNodeId = connection.TargetNodeId, SourcePort = connection.SourcePort, Ordinal = ordinal
    }).ToArray();
    internal static async Task<WorkflowSnapshot> ReadAsync(FlowForgeDbContext context, WorkflowRecord root, CancellationToken cancellationToken)
    {
        var versions = await context.WorkflowVersions.AsNoTracking().Where(v => v.WorkflowId == root.Id && v.OwnerUserId == root.OwnerUserId)
            .OrderBy(v => v.VersionNumber).ToArrayAsync(cancellationToken);
        var nodes = await context.WorkflowNodes.AsNoTracking().Where(n => n.WorkflowId == root.Id && n.OwnerUserId == root.OwnerUserId)
            .OrderBy(n => n.Ordinal).ToArrayAsync(cancellationToken);
        var versionIds = versions.Select(v => v.Id).ToArray();
        var connections = await context.WorkflowConnections.AsNoTracking().Where(c => versionIds.Contains(c.WorkflowVersionId))
            .OrderBy(c => c.Ordinal).ToArrayAsync(cancellationToken);
        return new WorkflowSnapshot(root.Id, root.OwnerUserId, root.Name, root.Description, root.CreatedAt, root.UpdatedAt,
            root.ArchivedAt, root.Revision, root.CurrentPublishedVersionId, versions.Select(version => new WorkflowVersionSnapshot(
                version.Id, version.VersionNumber, version.Status, version.CreatedAt, version.PublishedAt, version.Revision,
                nodes.Where(n => n.WorkflowVersionId == version.Id).Select(node => new WorkflowNode(node.WorkflowVersionId, node.NodeId,
                    node.Type, NodeConfigurationJson.Deserialize(node.Type, node.Configuration), new NodePosition(node.PositionX, node.PositionY),
                    node.CredentialId is { } credentialId ? new CredentialReference(credentialId, node.OwnerUserId) : null)).ToArray(),
                connections.Where(c => c.WorkflowVersionId == version.Id).Select(c => new WorkflowConnection(c.Id, c.WorkflowVersionId,
                    c.SourceNodeId, c.TargetNodeId, c.SourcePort)).ToArray())).ToArray());
    }
    internal static bool SameGraph(WorkflowVersionSnapshot left, WorkflowVersionSnapshot right)
    {
        if (left.Nodes.Count != right.Nodes.Count || left.Connections.Count != right.Connections.Count) return false;
        for (var index = 0; index < left.Nodes.Count; index++)
        {
            var a = left.Nodes[index]; var b = right.Nodes[index];
            if (a.NodeId != b.NodeId || a.WorkflowVersionId != b.WorkflowVersionId || a.Type != b.Type ||
                a.Position.X != b.Position.X || a.Position.Y != b.Position.Y || a.Credential?.Id != b.Credential?.Id ||
                a.Credential?.OwnerUserId != b.Credential?.OwnerUserId) return false;
            using var aJson = JsonDocument.Parse(NodeConfigurationJson.Serialize(a.Configuration));
            using var bJson = JsonDocument.Parse(NodeConfigurationJson.Serialize(b.Configuration));
            if (!JsonElement.DeepEquals(aJson.RootElement, bJson.RootElement)) return false;
        }
        for (var index = 0; index < left.Connections.Count; index++)
        {
            var a = left.Connections[index]; var b = right.Connections[index];
            if (a.Id != b.Id || a.WorkflowVersionId != b.WorkflowVersionId || a.SourceNodeId != b.SourceNodeId ||
                a.TargetNodeId != b.TargetNodeId || a.SourcePort != b.SourcePort) return false;
        }
        return true;
    }
}
