using FlowForge.Api.Authentication;
using FlowForge.Api.Credentials;
using FlowForge.Api.Webhooks;
using FlowForge.Api.Executions;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowForge.Api;
using FlowForge.Api.Workflows;
using Microsoft.AspNetCore.Routing;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.AllowDuplicateProperties = false;
    options.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});
builder.Services.AddWorkflowRuntime();
builder.Services.AddWebhookRuntime();
builder.Services.AddCredentialRuntime();
builder.Services.AddAuthenticationRuntime();

var app = builder.Build();
app.UseExceptionHandler();
app.UseRouting();
app.Use(async (context, next) => {
    if (context.GetEndpoint() is null) { await Results.Problem(statusCode: 404, title: "Recurso não encontrado.", detail: "Verifique o caminho e o método HTTP.").ExecuteAsync(context); return; }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseStatusCodePages(async status => await Results.Problem(
    statusCode: status.HttpContext.Response.StatusCode,
    title: "Requisição não atendida.",
    detail: "Verifique o caminho e o método HTTP.").ExecuteAsync(status.HttpContext));

// Liveness não depende de configuração, conectividade ou migrations do banco.
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/api/health/live").AllowAnonymous();

app.MapGet("/", () => Results.Ok(new
{
    service = "FlowForge.Api",
    phase = 13,
    status = "private-workflow-engine"
})).WithName("ServiceInfo").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapOpenApi("/api/openapi/{documentName}.json").AllowAnonymous();
}

app.MapAuthentication();
app.MapWorkflows();
app.MapExecutions();
app.MapExecutionRecovery();
app.MapWebhooks();
app.MapCredentials();

app.Run();

// Exposes the entry point to WebApplicationFactory without application logic.
public partial class Program { }
