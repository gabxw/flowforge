using System.Security.Cryptography;
using System.Text;
using FlowForge.Application.Webhooks;

namespace FlowForge.Infrastructure.Security;
internal static class WebhookSecrets
{
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));
    public static bool Matches(string secret, byte[] hash) => WebhookService.ValidSecret(secret)
        && hash.Length == 32 && CryptographicOperations.FixedTimeEquals(Hash(secret), hash);
    public static string KeyDigest(string key) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key)));
}
