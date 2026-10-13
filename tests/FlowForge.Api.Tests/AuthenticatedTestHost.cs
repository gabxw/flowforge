using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
namespace FlowForge.Api.Tests;
// Só nos testes de contratos anteriores. Testes de autenticação usam JwtBearer real.
internal sealed class ContractAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Context.RequestServices.GetRequiredService<ContractIdentity>().Id;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", id.ToString()), new(ClaimTypes.Role, "Owner")], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
internal sealed record ContractIdentity(Guid Id);
internal static class AuthenticatedTestHost
{
    public static IServiceCollection AddContractIdentity(this IServiceCollection services, Guid? id = null)
    {
        services.AddSingleton(new ContractIdentity(id ?? Guid.NewGuid()));
        services.AddAuthentication(options => {
            options.DefaultAuthenticateScheme = "Contract"; options.DefaultChallengeScheme = "Contract";
        }).AddScheme<AuthenticationSchemeOptions, ContractAuthenticationHandler>("Contract", _ => { });
        return services;
    }
}
