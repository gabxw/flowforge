namespace FlowForge.Infrastructure.Persistence.Records;
internal sealed class WebhookEndpointRecord
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid OwnerUserId { get; set; }
    public byte[] SecretHash { get; set; } = [];
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
}
internal sealed class WebhookIdempotencyRecord
{
    public Guid EndpointId { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string KeyDigest { get; set; } = "";
    public string RequestDigest { get; set; } = "";
    public Guid ExecutionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
