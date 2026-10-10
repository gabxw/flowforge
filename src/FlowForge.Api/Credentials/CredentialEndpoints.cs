using System.Text.Json;
using FlowForge.Application.Credentials;
using FlowForge.Domain.Credentials;
using Microsoft.AspNetCore.Http.Features;

namespace FlowForge.Api.Credentials;
internal sealed class CreateCredentialRequest
{
    public required string Name { get; init; }
    public required CredentialType Type { get; init; }
    public required string Origin { get; init; }
    public string? HeaderName { get; init; }
    public required string Secret { get; init; }
    public override string ToString() => "CreateCredentialRequest [redacted]";
}
internal sealed class RotateCredentialRequest
{
    public required int ExpectedRevision { get; init; }
    public required string Secret { get; init; }
    public override string ToString() => "RotateCredentialRequest [redacted]";
}
internal sealed record RevokeCredentialRequest
{
    public required int ExpectedRevision { get; init; }
}
internal static class CredentialEndpoints
{
    public static void MapCredentials(this WebApplication app)
    {
        var group = app.MapGroup("/api/credentials").WithTags("Credentials");
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapPost("/", async (HttpRequest http, CredentialService service, TechnicalOwner owner, CancellationToken ct) => {
            var request = await ReadAsync<CreateCredentialRequest>(http, ct);
            var metadata = await service.CreateAsync(owner.Id, request.Name, request.Type, request.Origin,
                request.HeaderName, new CredentialSecret(request.Secret), ct);
            return Results.Created($"/api/credentials/{metadata.Id}", metadata);
        }).WithName("CreateCredential").Accepts<CreateCredentialRequest>("application/json").Produces<CredentialSnapshot>(201).Errors(400, 408, 413, 415, 503);
        group.MapGet("/", async (CredentialService service, TechnicalOwner owner, CancellationToken ct, int offset = 0, int limit = 20) =>
            Results.Ok(new { items = await service.ListAsync(owner.Id, offset, limit, ct), offset, limit }))
            .WithName("ListCredentials").Errors(400, 503);
        group.MapGet("/{id:guid}", async (Guid id, CredentialService service, TechnicalOwner owner, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(owner.Id, id, ct))).WithName("GetCredential").Produces<CredentialSnapshot>().Errors(400, 404, 503);
        group.MapPost("/{id:guid}/rotate-secret", async (Guid id, HttpRequest http, CredentialService service, TechnicalOwner owner, CancellationToken ct) => {
            var request = await ReadAsync<RotateCredentialRequest>(http, ct);
            return Results.Ok(await service.RotateAsync(owner.Id, id, request.ExpectedRevision, new CredentialSecret(request.Secret), ct));
        }).WithName("RotateCredential").Accepts<RotateCredentialRequest>("application/json").Produces<CredentialSnapshot>().Errors(400, 404, 408, 409, 413, 415, 503);
        group.MapPost("/{id:guid}/revoke", async (Guid id, HttpRequest http, CredentialService service, TechnicalOwner owner, CancellationToken ct) => {
            var request = await ReadAsync<RevokeCredentialRequest>(http, ct);
            return Results.Ok(await service.RevokeAsync(owner.Id, id, request.ExpectedRevision, ct));
        }).WithName("RevokeCredential").Accepts<RevokeCredentialRequest>("application/json").Produces<CredentialSnapshot>().Errors(400, 404, 408, 409, 413, 415, 503);
    }
    private static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType() || request.Headers.ContentEncoding.Count != 0)
            throw new BadHttpRequestException("JSON sem compressão obrigatório.", 415);
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var media) ||
            (media.Charset.HasValue && !media.Charset.Value!.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new BadHttpRequestException("UTF-8 obrigatório.", 415);
        const int max = 32768;
        if (request.ContentLength > max) throw new BadHttpRequestException("Corpo excedido.", 413);
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = max;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var body = new MemoryStream(); var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, max + 1 - (int)body.Length)), timeout.Token);
                if (count == 0) break;
                body.Write(buffer, 0, count);
                if (body.Length > max) throw new BadHttpRequestException("Corpo excedido.", 413);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new BadHttpRequestException("Tempo de leitura excedido.", 408); }
        var options = request.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var bytes = body.ToArray();
        _ = new System.Text.UTF8Encoding(false, true).GetCharCount(bytes);
        return JsonSerializer.Deserialize<T>(bytes, options) ?? throw new JsonException();
    }
    private static RouteHandlerBuilder Errors(this RouteHandlerBuilder builder, params int[] statuses)
    {
        foreach (var status in statuses) builder.ProducesProblem(status);
        return builder.ProducesProblem(500);
    }
}
