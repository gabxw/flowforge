using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Runtime;
using FlowForge.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddDbContextFactory<FlowForgeDbContext>((sp, options) =>
    options.UseNpgsql(PostgresRuntimeConfiguration.ConnectionString(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton(sp => RabbitRuntimeOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IExecutionOutboxStore, PostgresExecutionOutboxStore>();
builder.Services.AddSingleton<IExecutionInboxStore, PostgresExecutionInboxStore>();
builder.Services.AddSingleton<IExecutionMessageHandler, ExecutionMessageHandler>();
builder.Services.AddSingleton<IExecutionPublisher, RabbitExecutionPublisher>();
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddSingleton<RabbitExecutionConsumer>();
builder.Services.AddHostedService<OutboxDispatchService>();
builder.Services.AddHostedService<ExecutionConsumerService>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
await builder.Build().RunAsync();
