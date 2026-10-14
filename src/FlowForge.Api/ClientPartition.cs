using FlowForge.Infrastructure.Runtime;
using Microsoft.AspNetCore.HttpOverrides;

namespace FlowForge.Api;

// Limites anônimos são contados por cliente, nunca num balde único: um cliente
// abusivo esgota só a própria cota. Atrás do proxy, o endereço vem do
// X-Forwarded-For, aceito apenas quando a conexão parte de uma rede configurada.
internal static class ClientPartition
{
    public static string Key(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        var networks = configuration.GetSection("FlowForge:Network:TrustedProxies").Get<string[]>() ?? [];
        return services.Configure<ForwardedHeadersOptions>(options => {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            // Só o salto adicionado pelo proxy confiável conta; valores anteriores vêm do cliente.
            options.ForwardLimit = 1;
            foreach (var network in networks)
            {
                if (!System.Net.IPNetwork.TryParse(network, out var parsed)) throw new RuntimeConfigurationException();
                options.KnownIPNetworks.Add(parsed);
            }
        });
    }
}
