using FlowForge.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace FlowForge.Infrastructure.Runtime;
public static class ExecutionProtectionRuntime
{
    public static ExecutionContextProtection Create(IConfiguration configuration, bool isDevelopment)
        => new(CreateProvider(configuration, isDevelopment));

    public static IDataProtectionProvider CreateProvider(IConfiguration configuration, bool isDevelopment)
    {
        var path = configuration["FlowForge:DataProtection:KeyRingPath"];
        // O MVP privado usa volume protegido pelo SO. Produção precisa de proteção das chaves em repouso.
        if (!isDevelopment || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new RuntimeConfigurationException();
        try
        {
            var directory = Directory.CreateDirectory(path);
            return DataProtectionProvider.Create(directory, b => b.SetApplicationName("FlowForge"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new RuntimeConfigurationException();
        }
    }
}
