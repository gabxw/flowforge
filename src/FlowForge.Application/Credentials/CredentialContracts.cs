using FlowForge.Domain.Credentials;

namespace FlowForge.Application.Credentials;

public sealed class CredentialSecret
{
    public string Value { get; }
    public CredentialSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 16 or > 4096 || value.Any(c => c < 33 || c > 126))
            throw new ArgumentException("O valor deve ter 16 a 4.096 caracteres ASCII visíveis, sem espaços.");
        Value = value;
    }
    public override string ToString() => "CredentialSecret [redacted]";
}
public sealed class ResolvedCredential(CredentialSnapshot metadata, CredentialSecret secret)
{
    public CredentialSnapshot Metadata { get; } = metadata;
    public CredentialSecret Secret { get; } = secret;
    public override string ToString() => "ResolvedCredential [redacted]";
}
public sealed class CredentialConcurrencyException : Exception;
public sealed class CredentialUnavailableException : Exception;
public interface ICredentialStore
{
    Task CreateAsync(CredentialSnapshot metadata, CredentialSecret secret, CancellationToken ct = default);
    Task<CredentialSnapshot?> GetAsync(Guid id, Guid owner, CancellationToken ct = default);
    Task<IReadOnlyList<CredentialSnapshot>> ListAsync(Guid owner, int offset, int limit, CancellationToken ct = default);
    Task<CredentialSnapshot> RotateAsync(Guid id, Guid owner, int expectedRevision, CredentialSecret secret,
        DateTimeOffset now, CancellationToken ct = default);
    Task<CredentialSnapshot> RevokeAsync(Guid id, Guid owner, int expectedRevision, DateTimeOffset now, CancellationToken ct = default);
    Task<ResolvedCredential?> ResolveAsync(Guid id, Guid owner, HttpsOrigin origin, CancellationToken ct = default);
}
