var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

var app = builder.Build();

// Liveness only: database and broker readiness will be added with their integrations.
app.MapHealthChecks("/health/live");

app.MapGet("/", () => Results.Ok(new
{
    service = "FlowForge.Api",
    phase = 1,
    status = "bootstrap"
})).WithName("ServiceInfo");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

// Exposes the entry point to WebApplicationFactory without application logic.
public partial class Program { }
