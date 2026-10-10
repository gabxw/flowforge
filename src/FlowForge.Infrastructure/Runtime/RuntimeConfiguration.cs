using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FlowForge.Infrastructure.Runtime;

public sealed class RuntimeConfigurationException : Exception
{
    public RuntimeConfigurationException() : base("Configuração privada de infraestrutura ausente ou inválida.") { }
}

public static class PostgresRuntimeConfiguration
{
    public static string ConnectionString(IConfiguration configuration)
    {
        try
        {
            var direct = configuration["FLOWFORGE_POSTGRES_CONNECTION_STRING"];
            if (!string.IsNullOrWhiteSpace(direct))
            {
                var parsed = new NpgsqlConnectionStringBuilder(direct);
                if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database))
                    throw new RuntimeConfigurationException();
                parsed.IncludeErrorDetail = false;
                return parsed.ConnectionString;
            }
            var section = configuration.GetSection("FlowForge:Postgres");
            var host = section["Host"]; var database = section["Database"]; var username = section["Username"];
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(username))
                throw new RuntimeConfigurationException();
            return new NpgsqlConnectionStringBuilder
            {
                Host = host, Database = database, Username = username,
                Password = RuntimeSecrets.Read(section["PasswordFile"]), Timeout = 5, CommandTimeout = 15, IncludeErrorDetail = false
            }.ConnectionString;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new RuntimeConfigurationException();
        }
    }
}

internal static class RuntimeSecrets
{
    internal static string Read(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new RuntimeConfigurationException();
        var value = File.ReadAllText(file).TrimEnd('\r', '\n');
        if (value.Length == 0) throw new RuntimeConfigurationException();
        return value;
    }
}
