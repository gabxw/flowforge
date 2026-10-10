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

var app = builder.Build();
app.UseExceptionHandler();
app.UseRouting();
app.UseRateLimiter();
app.UseStatusCodePages(async status => await Results.Problem(
    statusCode: status.HttpContext.Response.StatusCode,
    title: "Requisição não atendida.",
    detail: "Verifique o caminho e o método HTTP.").ExecuteAsync(status.HttpContext));

// Liveness não depende de configuração, conectividade ou migrations do banco.
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/api/health/live");

app.MapGet("/", () => Results.Ok(new
{
    service = "FlowForge.Api",
    phase = 9,
    status = "private-workflow-engine"
})).WithName("ServiceInfo");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapOpenApi("/api/openapi/{documentName}.json");
}

app.MapWorkflows();
app.MapExecutions();
app.MapWebhooks();
app.MapCredentials();

app.Run();

// Exposes the entry point to WebApplicationFactory without application logic.
public partial class Program { }
