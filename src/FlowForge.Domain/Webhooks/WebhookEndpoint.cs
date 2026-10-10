namespace FlowForge.Domain.Webhooks;

public sealed record WebhookEndpointSnapshot(Guid Id, Guid WorkflowId, Guid OwnerUserId,
    bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset? RotatedAt);

// O domínio controla o ciclo de vida; geração/hash do segredo pertencem ao adaptador.
public sealed class WebhookEndpoint
{
    public WebhookEndpointSnapshot Snapshot { get; private set; }
    public WebhookEndpoint(Guid id, Guid workflowId, Guid ownerUserId, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || workflowId == Guid.Empty || ownerUserId == Guid.Empty)
            throw new ArgumentException("Identidades do endpoint não podem ser vazias.");
        RequireUtc(createdAt);
        Snapshot = new(id, workflowId, ownerUserId, true, createdAt, null);
    }
    public static WebhookEndpoint Restore(WebhookEndpointSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var endpoint = new WebhookEndpoint(snapshot.Id, snapshot.WorkflowId, snapshot.OwnerUserId, snapshot.CreatedAt);
        if (snapshot.RotatedAt is { } rotated) { RequireUtc(rotated); if (rotated < snapshot.CreatedAt) throw new ArgumentException("Rotação anterior à criação."); }
        endpoint.Snapshot = snapshot;
        return endpoint;
    }
    public void SetEnabled(bool enabled) => Snapshot = Snapshot with { Enabled = enabled };
    public void Rotate(DateTimeOffset now)
    {
        RequireUtc(now);
        if (now < (Snapshot.RotatedAt ?? Snapshot.CreatedAt)) throw new ArgumentException("Rotação regressiva.");
        Snapshot = Snapshot with { RotatedAt = now };
    }
    private static void RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Horários devem ser UTC.");
    }
}
