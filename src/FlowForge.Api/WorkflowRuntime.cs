using FlowForge.Application.Users;
using FlowForge.Application.Workflows;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using FlowForge.Infrastructure.Runtime;
using FlowForge.Application.Executions;

namespace FlowForge.Api;

internal sealed record TechnicalOwner(Guid Id);
internal sealed class WorkflowUnavailableException : Exception;

internal static class WorkflowRuntime
{
    public static void AddWorkflowRuntime(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var value = sp.GetRequiredService<IConfiguration>()["FlowForge:TechnicalOwnerId"];
            return Guid.TryParse(value, out var id) && id != Guid.Empty
                ? new TechnicalOwner(id) : throw new WorkflowUnavailableException();
        });
        // Opções são resolvidas somente quando um caso de uso pede o store.
        services.AddDbContextFactory<FlowForgeDbContext>((sp, options) =>
            options.UseNpgsql(ConnectionString(sp.GetRequiredService<IConfiguration>())));
        services.AddScoped<IWorkflowStore, PostgresWorkflowStore>();
        services.AddScoped<ITechnicalUserStore, PostgresTechnicalUserStore>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<IExecutionStore, PostgresExecutionStore>();
        services.AddScoped<ExecutionService>();
    }

    private static string ConnectionString(IConfiguration configuration)
    {
        try { return PostgresRuntimeConfiguration.ConnectionString(configuration); }
        catch (RuntimeConfigurationException) { throw new WorkflowUnavailableException(); }
    }
}
