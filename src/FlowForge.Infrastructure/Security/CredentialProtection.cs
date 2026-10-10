using System.Security.Cryptography;
using System.Text;
using FlowForge.Application.Credentials;
using FlowForge.Domain.Credentials;
using Microsoft.AspNetCore.DataProtection;

namespace FlowForge.Infrastructure.Security;
public sealed class CredentialProtection
{
    private readonly Func<IDataProtectionProvider> provider;
    public CredentialProtection(IDataProtectionProvider provider) : this(() => provider) { }
    public CredentialProtection(Func<IDataProtectionProvider> provider) { this.provider = provider; }
    private IDataProtector Protector(CredentialSnapshot metadata) => provider().CreateProtector("FlowForge.Credential.v1",
        metadata.OwnerUserId.ToString("N"), metadata.Id.ToString("N"), metadata.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ((int)metadata.Type).ToString(System.Globalization.CultureInfo.InvariantCulture), metadata.Origin, metadata.HeaderName ?? "Authorization");
    public byte[] Protect(CredentialSecret secret, CredentialSnapshot metadata)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret.Value);
        try { return Protector(metadata).Protect(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public CredentialSecret Unprotect(byte[] ciphertext, CredentialSnapshot metadata)
    {
        if (ciphertext.Length is < 1 or > 16384) throw new CryptographicException();
        var plaintext = Protector(metadata).Unprotect(ciphertext);
        try { return new(new UTF8Encoding(false, true).GetString(plaintext)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
