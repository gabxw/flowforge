using FlowForge.Infrastructure.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FlowForge.Api.Tests;
public sealed class ExecutionProtectionRuntimeTests
{
    [Fact]
    public void Production_or_missing_keyring_cannot_silently_use_ephemeral_or_unwrapped_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), "flowforge-keyring-not-created-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FlowForge:DataProtection:KeyRingPath"] = path }).Build();
        Assert.Throws<RuntimeConfigurationException>(() => ExecutionProtectionRuntime.Create(config, false));
        Assert.False(Directory.Exists(path));
        Assert.Throws<RuntimeConfigurationException>(() => ExecutionProtectionRuntime.Create(new ConfigurationBuilder().Build(), true));
    }
}
