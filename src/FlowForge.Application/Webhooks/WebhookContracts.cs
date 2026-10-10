using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Domain.Webhooks;

namespace FlowForge.Application.Webhooks;

public sealed class WebhookRejectedException : Exception;
public sealed class WebhookIdempotencyConflictException : Exception;
public sealed class WebhookPayloadLimitException : Exception;

public sealed record WebhookAcceptanceOptions
{
    public static WebhookAcceptanceOptions Default { get; } = new(TimeSpan.FromHours(24));
    public TimeSpan IdempotencyLifetime { get; }
    public WebhookAcceptanceOptions(TimeSpan idempotencyLifetime)
    {
        if (idempotencyLifetime < TimeSpan.FromHours(1) || idempotencyLifetime > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(idempotencyLifetime));
        IdempotencyLifetime = idempotencyLifetime;
    }
}

public sealed class IssuedWebhook(WebhookEndpointSnapshot endpoint, string secret)
{
    public WebhookEndpointSnapshot Endpoint { get; } = endpoint;
    public string Secret { get; } = secret;
    public override string ToString() => "IssuedWebhook (segredo omitido)";
}

public sealed class WebhookPayload
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public JsonElement Value { get; }
    public string RequestDigest { get; }
    private WebhookPayload(JsonElement value, string digest) { Value = value; RequestDigest = digest; }
    public static WebhookPayload Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length > ExecutionPayload.MaxBytes) throw new WebhookPayloadLimitException();
        try { _ = StrictUtf8.GetCharCount(body.Span); }
        catch (DecoderFallbackException) { throw new JsonException("O corpo não contém UTF-8 válido."); }
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
        var value = document.RootElement.Clone();
        try { _ = ExecutionPayload.Summarize(value); }
        catch (ExecutionPayloadLimitException) { throw new WebhookPayloadLimitException(); }
        return new(value, Convert.ToHexString(SHA256.HashData(body.Span)));
    }
    // O digest usa bytes exatos. Não deduplicar por JSON equivalente sem chave explícita.
    public override string ToString() => "WebhookPayload (conteúdo omitido)";
}

public sealed record WebhookReceipt(Guid ExecutionId, Guid WorkflowVersionId, Guid CorrelationId,
    DateTimeOffset CreatedAt, bool Replayed);

public interface IWebhookStore
{
    Task<IssuedWebhook> CreateAsync(Guid workflowId, Guid owner, CancellationToken ct = default);
    Task<WebhookEndpointSnapshot?> GetAsync(Guid workflowId, Guid owner, CancellationToken ct = default);
    Task<WebhookEndpointSnapshot> SetEnabledAsync(Guid workflowId, Guid owner, bool enabled, CancellationToken ct = default);
    Task<IssuedWebhook> RotateAsync(Guid workflowId, Guid owner, CancellationToken ct = default);
    Task AuthorizeAsync(Guid endpointId, string secret, CancellationToken ct = default);
    Task<WebhookReceipt> AcceptAsync(Guid endpointId, string secret, WebhookPayload payload,
        string? idempotencyKey, CancellationToken ct = default);
}
