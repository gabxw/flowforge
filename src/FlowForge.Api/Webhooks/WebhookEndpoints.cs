using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Application.Webhooks;
using FlowForge.Domain.Webhooks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace FlowForge.Api.Webhooks;
internal sealed record WebhookDto(Guid Id, Guid WorkflowId, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset? RotatedAt)
{
    public static WebhookDto From(WebhookEndpointSnapshot e) => new(e.Id, e.WorkflowId, e.Enabled, e.CreatedAt, e.RotatedAt);
}
internal sealed class IssuedWebhookDto(IssuedWebhook value)
{
    public WebhookDto Endpoint { get; } = WebhookDto.From(value.Endpoint);
    public string Secret { get; } = value.Secret;
    public override string ToString() => "IssuedWebhookDto (segredo omitido)";
}
internal sealed record SetWebhookEnabledRequest(bool? Enabled);

internal static class WebhookEndpoints
{
    private const string SecretHeader = "X-FlowForge-Webhook-Secret";
    public static void MapWebhooks(this WebApplication app)
    {
        var group = app.MapGroup("/api/workflows/{id:guid}/webhook").WithTags("Webhooks");
        group.MapPost("", async (Guid id, HttpContext context, WebhookService service, TechnicalOwner owner, CancellationToken ct) => {
            NoBody(context.Request); context.Response.Headers.CacheControl = "no-store";
            var issued = await service.CreateAsync(owner.Id, id, ct);
            return Results.Created($"/api/workflows/{id}/webhook", new IssuedWebhookDto(issued));
        }).WithName("CreateWebhook").Produces<IssuedWebhookDto>(201).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        group.MapGet("", async (Guid id, HttpContext context, WebhookService service, TechnicalOwner owner, CancellationToken ct) => {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(WebhookDto.From(await service.GetAsync(owner.Id, id, ct)));
        }).WithName("GetWebhook").Produces<WebhookDto>().ProducesProblem(404).ProducesProblem(503);
        group.MapPut("", async (Guid id, SetWebhookEnabledRequest body, WebhookService service, TechnicalOwner owner, CancellationToken ct) =>
            Results.Ok(WebhookDto.From(await service.SetEnabledAsync(owner.Id, id, body.Enabled ?? throw new ArgumentException("Enabled é obrigatório."), ct))))
            .WithName("SetWebhookEnabled").Produces<WebhookDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        group.MapPost("/rotate-secret", async (Guid id, HttpContext context, WebhookService service, TechnicalOwner owner, CancellationToken ct) => {
            NoBody(context.Request); context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new IssuedWebhookDto(await service.RotateAsync(owner.Id, id, ct)));
        }).WithName("RotateWebhookSecret").Produces<IssuedWebhookDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(503);

        app.MapPost("/hooks/{id:guid}", Accept).WithTags("Webhooks").WithName("AcceptWebhook")
            .RequireRateLimiting("webhooks").Accepts<JsonElement>("application/json").Produces<WebhookReceipt>(202)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(408).ProducesProblem(409)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(503);
    }
    private static async Task<IResult> Accept(Guid id, HttpContext context, WebhookService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        var request = context.Request;
        if (!request.Headers.TryGetValue(SecretHeader, out var secrets) || secrets.Count != 1)
            throw new WebhookRejectedException();
        var secret = secrets[0] ?? "";
        await service.AuthorizeAsync(id, secret, context.RequestAborted);
        if (request.QueryString.HasValue) throw new BadHttpRequestException("A rota não aceita query string.");
        string? key = null;
        if (request.Headers.TryGetValue("Idempotency-Key", out var keys))
        {
            if (keys.Count != 1) throw new BadHttpRequestException("Idempotency-Key deve ser único.");
            key = keys[0]; WebhookService.ValidateKey(key);
        }
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || (contentType.Charset.HasValue && !contentType.Charset.Value.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            || request.Headers.ContainsKey("Content-Encoding"))
            throw new BadHttpRequestException("O webhook exige JSON UTF-8 sem compressão.", 415);
        if (request.ContentLength > ExecutionPayload.MaxBytes) throw new WebhookPayloadLimitException();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = ExecutionPayload.MaxBytes;
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        reading.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[ExecutionPayload.MaxBytes + 1]; var count = 0;
        try
        {
            while (count < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(count), reading.Token);
                if (read == 0) break;
                count += read;
            }
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { throw new BadHttpRequestException("Tempo de leitura do webhook excedido.", 408); }
        var payload = WebhookPayload.Parse(buffer.AsMemory(0, count));
        // Revalidação transacional fecha a janela de rotação/desativação durante a leitura.
        var receipt = await service.AcceptAsync(id, secret, payload, key, context.RequestAborted);
        return Results.Accepted($"/api/executions/{receipt.ExecutionId}", receipt);
    }
    private static void NoBody(HttpRequest request)
    {
        if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
            throw new BadHttpRequestException("Este comando não aceita corpo.");
    }
}
