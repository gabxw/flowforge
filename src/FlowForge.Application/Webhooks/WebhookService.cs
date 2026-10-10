using FlowForge.Domain.Webhooks;

namespace FlowForge.Application.Webhooks;

public sealed class WebhookService(IWebhookStore store)
{
    public Task<IssuedWebhook> CreateAsync(Guid owner, Guid workflowId, CancellationToken ct = default)
    { ValidateIds(owner, workflowId); return store.CreateAsync(workflowId, owner, ct); }
    public async Task<WebhookEndpointSnapshot> GetAsync(Guid owner, Guid workflowId, CancellationToken ct = default)
    { ValidateIds(owner, workflowId); return await store.GetAsync(workflowId, owner, ct) ?? throw new KeyNotFoundException("Endpoint não encontrado."); }
    public Task<WebhookEndpointSnapshot> SetEnabledAsync(Guid owner, Guid workflowId, bool enabled, CancellationToken ct = default)
    { ValidateIds(owner, workflowId); return store.SetEnabledAsync(workflowId, owner, enabled, ct); }
    public Task<IssuedWebhook> RotateAsync(Guid owner, Guid workflowId, CancellationToken ct = default)
    { ValidateIds(owner, workflowId); return store.RotateAsync(workflowId, owner, ct); }
    public Task AuthorizeAsync(Guid endpointId, string secret, CancellationToken ct = default)
    {
        if (endpointId == Guid.Empty || !ValidSecret(secret)) throw new WebhookRejectedException();
        return store.AuthorizeAsync(endpointId, secret, ct);
    }
    public Task<WebhookReceipt> AcceptAsync(Guid endpointId, string secret, WebhookPayload payload,
        string? idempotencyKey, CancellationToken ct = default)
    {
        if (endpointId == Guid.Empty || !ValidSecret(secret)) throw new WebhookRejectedException();
        ArgumentNullException.ThrowIfNull(payload);
        ValidateKey(idempotencyKey);
        return store.AcceptAsync(endpointId, secret, payload, idempotencyKey, ct);
    }
    public static void ValidateKey(string? key)
    {
        if (key is not null && (key.Length is < 1 or > 128 || key.Any(c => c is < '!' or > '~')))
            throw new ArgumentException("Idempotency-Key exige de 1 a 128 caracteres ASCII visíveis, sem espaços.");
    }
    public static bool ValidSecret(string? secret) => secret is { Length: 64 } && secret.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static void ValidateIds(Guid owner, Guid workflowId)
    {
        if (owner == Guid.Empty || workflowId == Guid.Empty) throw new ArgumentException("Identidades não podem ser vazias.");
    }
}
