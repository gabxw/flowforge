using FlowForge.Infrastructure.Runtime;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace FlowForge.Infrastructure.Messaging;

// Classe deliberadamente sem ToString gerado: a senha nunca deve ir para logs.
public sealed class RabbitRuntimeOptions(string host, int port, string username, string password)
{
    internal ConnectionFactory CreateFactory(string name)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) throw new RuntimeConfigurationException();
        return new ConnectionFactory
        {
            HostName = host, Port = port, UserName = username, Password = password,
            AutomaticRecoveryEnabled = false, ConsumerDispatchConcurrency = 1,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5), RequestedHeartbeat = TimeSpan.FromSeconds(10),
            ClientProvidedName = name
        };
    }

    public static RabbitRuntimeOptions FromConfiguration(IConfiguration configuration)
    {
        try
        {
            var section = configuration.GetSection("FlowForge:RabbitMq");
            var host = section["Host"]; var username = section["Username"];
            var port = section["Port"] is null ? 5672 : int.Parse(section["Port"]!, System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || port is < 1 or > 65535)
                throw new RuntimeConfigurationException();
            return new(host, port, username, RuntimeSecrets.Read(section["PasswordFile"]));
        }
        catch (Exception e) when (e is FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            throw new RuntimeConfigurationException();
        }
    }
}
