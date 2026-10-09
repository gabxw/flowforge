namespace FlowForge.Domain.Workflows;

public sealed partial class Workflow
{
    public static Workflow Restore(WorkflowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var workflow = new Workflow(snapshot.Id, snapshot.OwnerUserId, snapshot.Name,
            snapshot.CreatedAt, snapshot.Description);
        if (workflow.Name != snapshot.Name || snapshot.Revision < 0)
            throw new ArgumentException("Metadados inválidos no snapshot.", nameof(snapshot));
        var updatedAt = snapshot.UpdatedAt.ToUniversalTime();
        if (updatedAt < workflow.CreatedAt ||
            (snapshot.ArchivedAt is { } archivedAt && archivedAt.ToUniversalTime() != updatedAt))
            throw new ArgumentException("Datas inconsistentes no snapshot.", nameof(snapshot));
        ArgumentNullException.ThrowIfNull(snapshot.Versions);
        var versionIds = new HashSet<Guid>();
        var connectionIds = new HashSet<Guid>();
        Guid? lastPublishedId = null;
        DateTimeOffset previousEnd = workflow.CreatedAt;
        for (var index = 0; index < snapshot.Versions.Count; index++)
        {
            var version = snapshot.Versions[index];
            if (version is null || version.Id == Guid.Empty || !versionIds.Add(version.Id) ||
                version.VersionNumber != index + 1 || version.Revision < 0 || !Enum.IsDefined(version.Status))
                throw new ArgumentException("Identidade, número ou estado de versão inválido.", nameof(snapshot));
            var createdAt = version.CreatedAt.ToUniversalTime();
            if (createdAt < previousEnd || createdAt > updatedAt)
                throw new ArgumentException("Data de criação da versão inconsistente.", nameof(snapshot));
            var publishedAt = version.PublishedAt?.ToUniversalTime();
            if (version.Status == WorkflowVersionStatus.Published)
            {
                if (publishedAt is null || publishedAt < createdAt || publishedAt > updatedAt)
                    throw new ArgumentException("Data de publicação inconsistente.", nameof(snapshot));
                lastPublishedId = version.Id;
            }
            else if (publishedAt is not null || index != snapshot.Versions.Count - 1)
                throw new ArgumentException("O único rascunho deve ser a última versão.", nameof(snapshot));
            ArgumentNullException.ThrowIfNull(version.Nodes);
            ArgumentNullException.ThrowIfNull(version.Connections);
            var nodes = version.Nodes.ToArray();
            var connections = version.Connections.ToArray();
            var nodeIds = new HashSet<Guid>();
            foreach (var node in nodes)
                if (node is null || node.WorkflowVersionId != version.Id || !nodeIds.Add(node.NodeId) ||
                    (node.Credential is { } credential && credential.OwnerUserId != workflow.OwnerUserId))
                    throw new ArgumentException("Node duplicado ou vinculado a outra versão/proprietário.", nameof(snapshot));
            foreach (var connection in connections)
                if (connection is null || connection.WorkflowVersionId != version.Id || !connectionIds.Add(connection.Id))
                    throw new ArgumentException("Conexão duplicada ou vinculada a outra versão.", nameof(snapshot));
            if (version.Status == WorkflowVersionStatus.Published &&
                WorkflowGraphValidator.Validate(version.Id, workflow.OwnerUserId, nodes, connections).Count != 0)
                throw new ArgumentException("A versão publicada contém um grafo inválido.", nameof(snapshot));
            var restored = WorkflowVersion.Restore(version.Id, workflow.Id, workflow.OwnerUserId,
                version.VersionNumber, createdAt, publishedAt, version.Status, version.Revision,
                Array.AsReadOnly(nodes), Array.AsReadOnly(connections));
            workflow.versions.Add(restored);
            if (restored.Status == WorkflowVersionStatus.Draft) workflow.DraftVersion = restored;
            previousEnd = publishedAt ?? createdAt;
        }
        if (snapshot.CurrentPublishedVersionId != lastPublishedId)
            throw new ArgumentException("O ponteiro deve identificar a última publicação.", nameof(snapshot));
        workflow.UpdatedAt = updatedAt;
        workflow.ArchivedAt = snapshot.ArchivedAt?.ToUniversalTime();
        workflow.Revision = snapshot.Revision;
        workflow.CurrentPublishedVersionId = lastPublishedId;
        return workflow;
    }
}
