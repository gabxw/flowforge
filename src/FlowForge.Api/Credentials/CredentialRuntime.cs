using FlowForge.Application.Credentials;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Security;

namespace FlowForge.Api.Credentials;
internal static class CredentialRuntime
{
    public static void AddCredentialRuntime(this IServiceCollection services)
    {
        // O mesmo provider configurado protege contexts, logs e credenciais com purposes distintos.
        services.AddSingleton(sp => new CredentialProtection(() => sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        services.AddScoped<ICredentialStore, PostgresCredentialStore>();
        services.AddScoped<CredentialService>();
    }
}
