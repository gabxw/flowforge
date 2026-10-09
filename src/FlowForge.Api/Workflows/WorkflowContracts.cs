using System.Text.Json;
using System.Text.Json.Serialization;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Api.Workflows;

public sealed record CreateWorkflowRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public sealed record UpdateWorkflowRequest
{
    public required int ExpectedRevision { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public sealed record RevisionRequest
{
    public required int ExpectedRevision { get; init; }
}

public sealed record ReplaceDraftRequest
{
    public required int ExpectedRevision { get; init; }
    public required IReadOnlyList<NodeDto> Nodes { get; init; }
    public required IReadOnlyList<ConnectionDto> Connections { get; init; }
}

public sealed record NodeDto
{
    public required Guid NodeId { get; init; }
    public required ConfigurationDto Configuration { get; init; }
    public PositionDto Position { get; init; } = new(0, 0);
    public Guid? CredentialId { get; init; }
}

public sealed record PositionDto(double X, double Y);

public sealed record ConnectionDto
{
    public required Guid Id { get; init; }
    public required Guid SourceNodeId { get; init; }
    public required Guid TargetNodeId { get; init; }
    public required string SourcePort { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WebhookConfigurationDto), "webhookTrigger")]
[JsonDerivedType(typeof(HttpConfigurationDto), "httpRequest")]
[JsonDerivedType(typeof(DelayConfigurationDto), "delay")]
[JsonDerivedType(typeof(ConditionConfigurationDto), "condition")]
[JsonDerivedType(typeof(TransformConfigurationDto), "transformJson")]
[JsonDerivedType(typeof(LogConfigurationDto), "log")]
public abstract record ConfigurationDto;

public sealed record WebhookConfigurationDto : ConfigurationDto;
public sealed record HttpConfigurationDto : ConfigurationDto
{
    public required string Url { get; init; }
    public required HttpRequestMethod Method { get; init; }
}
public sealed record DelayConfigurationDto : ConfigurationDto
{
    public required long DurationTicks { get; init; }
}
public sealed record ConditionConfigurationDto : ConfigurationDto
{
    public required string SourcePointer { get; init; }
    public required ConditionOperator Operation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ExpectedValue { get; init; }
}
public sealed record TransformConfigurationDto : ConfigurationDto
{
    public required IReadOnlyList<TransformFieldDto> Fields { get; init; }
}
public sealed record TransformFieldDto
{
    public required string TargetProperty { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourcePointer { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Literal { get; init; }
}
public sealed record LogConfigurationDto : ConfigurationDto
{
    public required string Message { get; init; }
}

public sealed record WorkflowDto(Guid Id, Guid OwnerUserId, string Name, string? Description,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt, int Revision,
    Guid? CurrentPublishedVersionId, Guid? DraftVersionId, IReadOnlyList<VersionDto> Versions);

public sealed record VersionDto(Guid Id, int VersionNumber, WorkflowVersionStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, int Revision,
    IReadOnlyList<NodeDto> Nodes, IReadOnlyList<ConnectionDto> Connections);

public sealed record WorkflowPageDto(IReadOnlyList<FlowForge.Application.Workflows.WorkflowSummary> Items, int Offset, int Limit);
