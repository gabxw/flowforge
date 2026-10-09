using FlowForge.Application.Users;
using FlowForge.Application.Workflows;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
    }

    private static string ConnectionString(IConfiguration configuration)
    {
        try
        {
            var direct = configuration["FLOWFORGE_POSTGRES_CONNECTION_STRING"];
            if (!string.IsNullOrWhiteSpace(direct))
                return new NpgsqlConnectionStringBuilder(direct).ConnectionString;
            var section = configuration.GetSection("FlowForge:Postgres");
            var host = section["Host"];
            var database = section["Database"];
            var username = section["Username"];
            var passwordFile = section["PasswordFile"];
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database) ||
                string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordFile))
                throw new WorkflowUnavailableException();
            var password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
            if (password.Length == 0) throw new WorkflowUnavailableException();
            return new NpgsqlConnectionStringBuilder
            {
                Host = host, Database = database, Username = username, Password = password,
                Timeout = 5, CommandTimeout = 15, IncludeErrorDetail = false
            }.ConnectionString;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new WorkflowUnavailableException();
        }
    }
}
