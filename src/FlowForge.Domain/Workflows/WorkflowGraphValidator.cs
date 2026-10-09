namespace FlowForge.Domain.Workflows;

public static class WorkflowGraphValidator
{
    public static IReadOnlyList<GraphValidationError> Validate(Guid workflowVersionId, Guid ownerUserId,
        IReadOnlyList<WorkflowNode> nodes, IReadOnlyList<WorkflowConnection> connections)
    {
        if (workflowVersionId == Guid.Empty)
            throw new ArgumentException("A versão deve ter uma identidade.", nameof(workflowVersionId));
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException("O proprietário deve ter uma identidade.", nameof(ownerUserId));
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(connections);

        var errors = new List<GraphValidationError>();
        if (nodes.Count > 50)
            errors.Add(new(GraphErrorCode.NodeLimitExceeded, "O grafo deve ter até 50 nodes."));
        if (connections.Count > 100)
            errors.Add(new(GraphErrorCode.ConnectionLimitExceeded, "O grafo deve ter até 100 conexões."));
        if (errors.Count != 0)
            return errors.AsReadOnly();

        var byId = new Dictionary<Guid, WorkflowNode>();
        var triggers = new List<WorkflowNode>();
        foreach (var node in nodes)
        {
            if (node is null)
                throw new ArgumentException("Os nodes não podem ser nulos.", nameof(nodes));
            if (!byId.TryAdd(node.NodeId, node))
                errors.Add(new(GraphErrorCode.DuplicateNodeId, "A identidade do node está duplicada.", node.NodeId));
            if (node.WorkflowVersionId != workflowVersionId)
                errors.Add(new(GraphErrorCode.NodeVersionMismatch, "O node pertence a outra versão.", node.NodeId));
            if (node.Credential is not null && node.Credential.OwnerUserId != ownerUserId)
                errors.Add(new(GraphErrorCode.CredentialOwnerMismatch, "A credencial pertence a outro proprietário.", node.NodeId));
            if (node.Type == NodeType.WebhookTrigger)
                triggers.Add(node);
        }

        if (nodes.Count == 0)
            errors.Add(new(GraphErrorCode.EmptyGraph, "O grafo deve conter ao menos um node."));
        if (triggers.Count != 1)
            errors.Add(new(GraphErrorCode.TriggerCount, "O grafo deve conter exatamente um trigger."));

        var connectionIds = new HashSet<Guid>();
        var ports = new Dictionary<(Guid NodeId, string Port), int>();
        var outgoing = byId.Keys.ToDictionary(id => id, _ => new List<Guid>());
        var indegree = byId.Keys.ToDictionary(id => id, _ => 0);
        foreach (var connection in connections)
        {
            if (connection is null)
                throw new ArgumentException("As conexões não podem ser nulas.", nameof(connections));
            if (!connectionIds.Add(connection.Id))
                errors.Add(new(GraphErrorCode.DuplicateConnectionId, "A identidade da conexão está duplicada.", ConnectionId: connection.Id));
            if (connection.WorkflowVersionId != workflowVersionId)
                errors.Add(new(GraphErrorCode.ConnectionVersionMismatch, "A conexão pertence a outra versão.", ConnectionId: connection.Id));

            var hasSource = byId.TryGetValue(connection.SourceNodeId, out var source);
            var hasTarget = byId.TryGetValue(connection.TargetNodeId, out var target);
            if (!hasSource)
                errors.Add(new(GraphErrorCode.MissingSourceNode, "O node de origem não existe.", connection.SourceNodeId, connection.Id));
            if (!hasTarget)
                errors.Add(new(GraphErrorCode.MissingTargetNode, "O node de destino não existe.", connection.TargetNodeId, connection.Id));
            if (hasTarget && target!.Type == NodeType.WebhookTrigger)
                errors.Add(new(GraphErrorCode.TriggerHasIncomingConnection, "O trigger não aceita conexões de entrada.", target.NodeId, connection.Id));
            if (hasSource)
            {
                var validPort = source!.Type == NodeType.Condition
                    ? connection.SourcePort is "true" or "false"
                    : connection.SourcePort == "next";
                if (!validPort)
                    errors.Add(new(GraphErrorCode.InvalidSourcePort, "A porta não é válida para o tipo do node.", source.NodeId, connection.Id));

                var key = (source.NodeId, connection.SourcePort);
                var count = ports.GetValueOrDefault(key) + 1;
                ports[key] = count;
                if (count > 1)
                    errors.Add(new(GraphErrorCode.DuplicateSourcePort, "A porta de origem possui mais de uma conexão.", source.NodeId, connection.Id));
            }
            if (hasSource && hasTarget)
            {
                // As duas portas de Condition são duas arestas, mesmo com o mesmo destino.
                outgoing[connection.SourceNodeId].Add(connection.TargetNodeId);
                indegree[connection.TargetNodeId]++;
            }
        }

        foreach (var node in byId.Values)
        {
            if (node.Type == NodeType.Condition &&
                (ports.GetValueOrDefault((node.NodeId, "true")) != 1 ||
                 ports.GetValueOrDefault((node.NodeId, "false")) != 1))
                errors.Add(new(GraphErrorCode.ConditionBranchesIncomplete,
                    "Condition exige exatamente uma conexão true e uma false.", node.NodeId));
        }

        if (HasCycle(outgoing, indegree))
            errors.Add(new(GraphErrorCode.Cycle, "O grafo não pode conter ciclos."));
        if (triggers.Count == 1)
        {
            var reachable = FindReachable(triggers[0].NodeId, outgoing);
            foreach (var nodeId in byId.Keys)
                if (!reachable.Contains(nodeId))
                    errors.Add(new(GraphErrorCode.UnreachableNode, "O node não é alcançável a partir do trigger.", nodeId));
        }

        return errors.AsReadOnly();
    }

    private static bool HasCycle(Dictionary<Guid, List<Guid>> outgoing, Dictionary<Guid, int> indegree)
    {
        var ready = new Queue<Guid>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var nodeId))
        {
            visited++;
            foreach (var targetId in outgoing[nodeId])
                if (--indegree[targetId] == 0)
                    ready.Enqueue(targetId);
        }
        return visited != indegree.Count;
    }

    private static HashSet<Guid> FindReachable(Guid triggerId, Dictionary<Guid, List<Guid>> outgoing)
    {
        var reachable = new HashSet<Guid> { triggerId };
        var pending = new Queue<Guid>();
        pending.Enqueue(triggerId);
        while (pending.TryDequeue(out var nodeId))
            foreach (var targetId in outgoing[nodeId])
                if (reachable.Add(targetId))
                    pending.Enqueue(targetId);
        return reachable;
    }
}
