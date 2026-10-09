using System.Text.Json;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Api.Workflows;

internal static class WorkflowTransport
{
    public static async Task<ReplaceDraftRequest> ReadDraftAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType())
            throw new BadHttpRequestException("O rascunho exige application/json.", StatusCodes.Status415UnsupportedMediaType);
        try
        {
            return await request.ReadFromJsonAsync<ReplaceDraftRequest>(cancellationToken: ct) ??
                throw new ArgumentException("O corpo do rascunho é obrigatório.");
        }
        catch (NotSupportedException)
        {
            // O serializer usa NotSupportedException para configuração abstrata sem discriminador.
            throw new BadHttpRequestException("Cada configuração exige um discriminador type conhecido.");
        }
    }

    public static WorkflowDto ToDto(Workflow workflow) => new(workflow.Id, workflow.OwnerUserId, workflow.Name,
        workflow.Description, workflow.CreatedAt, workflow.UpdatedAt, workflow.ArchivedAt, workflow.Revision,
        workflow.CurrentPublishedVersionId, workflow.DraftVersion?.Id, workflow.Versions.Select(ToDto).ToArray());

    public static VersionDto ToDto(WorkflowVersion version) => new(version.Id, version.VersionNumber, version.Status,
        version.CreatedAt, version.PublishedAt, version.Revision, version.Nodes.Select(n => new NodeDto
        {
            NodeId = n.NodeId, Configuration = ToDto(n.Configuration), Position = new(n.Position.X, n.Position.Y),
            CredentialId = n.Credential?.Id
        }).ToArray(), version.Connections.Select(c => new ConnectionDto
        {
            Id = c.Id, SourceNodeId = c.SourceNodeId, TargetNodeId = c.TargetNodeId, SourcePort = c.SourcePort
        }).ToArray());

    public static NodeDefinition ToDefinition(NodeDto node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(node.Position);
        return new(node.NodeId, ToDomain(node.Configuration),
            new NodePosition(node.Position.X, node.Position.Y), node.CredentialId);
    }

    public static ConnectionDefinition ToDefinition(ConnectionDto connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new(connection.Id, connection.SourceNodeId, connection.TargetNodeId, connection.SourcePort);
    }

    private static NodeConfiguration ToDomain(ConfigurationDto configuration) => configuration switch
    {
        WebhookConfigurationDto => new WebhookTriggerConfiguration(),
        HttpConfigurationDto http => new HttpRequestConfiguration(
            Uri.TryCreate(http.Url, UriKind.Absolute, out var uri) ? uri : throw new ArgumentException("URL inválida."),
            http.Method),
        DelayConfigurationDto delay => new DelayConfiguration(TimeSpan.FromTicks(delay.DurationTicks)),
        LogConfigurationDto log => new LogConfiguration(log.Message),
        ConditionConfigurationDto condition => new ConditionConfiguration(new JsonPointer(condition.SourcePointer),
            condition.Operation, condition.ExpectedValue.ValueKind == JsonValueKind.Undefined ? null : condition.ExpectedValue),
        TransformConfigurationDto transform => new TransformJsonConfiguration(Fields(transform.Fields)),
        _ => throw new ArgumentException("Configuração de node ausente ou desconhecida.")
    };

    private static IEnumerable<TransformField> Fields(IReadOnlyList<TransformFieldDto> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            var hasPath = field.SourcePointer is not null;
            var hasLiteral = field.Literal.ValueKind != JsonValueKind.Undefined;
            if (hasPath == hasLiteral)
                throw new ArgumentException("Cada campo exige exatamente um sourcePointer ou literal.");
            yield return hasPath
                ? TransformField.FromPath(field.TargetProperty, new JsonPointer(field.SourcePointer!))
                : TransformField.FromValue(field.TargetProperty, field.Literal);
        }
    }

    private static ConfigurationDto ToDto(NodeConfiguration configuration) => configuration switch
    {
        WebhookTriggerConfiguration => new WebhookConfigurationDto(),
        HttpRequestConfiguration http => new HttpConfigurationDto { Url = http.Url.AbsoluteUri, Method = http.Method },
        DelayConfiguration delay => new DelayConfigurationDto { DurationTicks = delay.Duration.Ticks },
        LogConfiguration log => new LogConfigurationDto { Message = log.Message },
        ConditionConfiguration condition => new ConditionConfigurationDto
        {
            SourcePointer = condition.SourcePointer.Value, Operation = condition.Operation,
            ExpectedValue = condition.ExpectedValue ?? default
        },
        TransformJsonConfiguration transform => new TransformConfigurationDto
        {
            Fields = transform.Fields.Select(f => new TransformFieldDto
            {
                TargetProperty = f.TargetProperty, SourcePointer = f.SourcePointer?.Value, Literal = f.Literal ?? default
            }).ToArray()
        },
        _ => throw new InvalidOperationException("Configuração de domínio desconhecida.")
    };
}
