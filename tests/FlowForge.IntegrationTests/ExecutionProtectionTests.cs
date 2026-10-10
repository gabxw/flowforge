using System.Security.Cryptography;
using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;

namespace FlowForge.IntegrationTests;
public sealed class ExecutionProtectionTests
{
    [Fact]
    public void Persistent_keyring_survives_provider_restart_and_ciphertext_is_bound_to_execution_identity()
    {
        var path = Path.Combine(Path.GetTempPath(), "flowforge-protection-test-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(path);
        try
        {
            ExecutionContextProtection Create() => new(DataProtectionProvider.Create(directory, b => b.SetApplicationName("FlowForge")));
            var id = Guid.NewGuid(); var payload = Json("{\"secret\":\"ficticio-confidencial\"}");
            var ciphertext = Create().Protect(payload, id);
            Assert.Equal(payload.GetRawText(), Create().Unprotect(ciphertext, id).GetRawText());
            Assert.Throws<CryptographicException>(() => Create().Unprotect(ciphertext, Guid.NewGuid()));
            ciphertext[^1] ^= 1;
            Assert.Throws<CryptographicException>(() => Create().Unprotect(ciphertext, id));
            Assert.NotEmpty(Directory.GetFiles(path, "key-*.xml"));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void Payload_limit_prevents_protection_of_oversized_context()
    {
        var protection = new ExecutionContextProtection(new EphemeralDataProtectionProvider());
        var huge = Json(System.Text.Json.JsonSerializer.Serialize(new string('x', ExecutionPayload.MaxBytes)));
        Assert.Throws<ExecutionPayloadLimitException>(() => protection.Protect(huge, Guid.NewGuid()));
    }

}
