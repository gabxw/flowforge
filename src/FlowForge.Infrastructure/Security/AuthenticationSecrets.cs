using System.Security.Cryptography;
using FlowForge.Application.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
namespace FlowForge.Infrastructure.Security;
public sealed class AuthenticationSecrets : IAuthSecrets
{
    public string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public byte[] Hash(string token)
    {
        if (token is null || token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new AuthenticationRejectedException();
        return SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
    }
}
public sealed class IdentityPasswordHashing : IPasswordHashing
{
    private readonly PasswordHasher<object> hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));
    private readonly object subject = new();
    private readonly string dummy;
    public IdentityPasswordHashing() => dummy = hasher.HashPassword(subject, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    public string Hash(string password) => hasher.HashPassword(subject, password);
    public bool Verify(string hash, string password) => hasher.VerifyHashedPassword(subject, hash, password) != PasswordVerificationResult.Failed;
    public void VerifyDummy(string password) => _ = hasher.VerifyHashedPassword(subject, dummy, password);
}
