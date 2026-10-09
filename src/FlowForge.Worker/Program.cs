var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

var host = builder.Build();

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("FlowForge.Worker")
    .LogInformation("Worker host initialized. Workflow processing begins in phase {Phase}", 5);

// The host remains alive and handles graceful shutdown. No queue consumer in phase 1.
await host.RunAsync();
