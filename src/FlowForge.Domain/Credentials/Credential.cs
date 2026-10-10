namespace FlowForge.Domain.Credentials;

public enum CredentialType { BearerToken = 1, ApiKey = 2 }
public sealed record CredentialSnapshot(Guid Id, Guid OwnerUserId, string Name, CredentialType Type,
    string Origin, string? HeaderName, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt);

// O domínio contém metadados e invariantes. O valor secreto existe somente no adaptador de proteção.
public sealed class Credential
{
    public CredentialSnapshot Snapshot { get; private set; }
    public Credential(Guid id, Guid owner, string name, CredentialType type, HttpsOrigin origin,
        string? headerName, DateTimeOffset now)
    {
        if (id == Guid.Empty || owner == Guid.Empty) throw new ArgumentException("Identidades obrigatórias.");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 120 || name.Any(char.IsControl)) throw new ArgumentException("Nome inválido.");
        ArgumentNullException.ThrowIfNull(origin);
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (type == CredentialType.BearerToken && headerName is not null)
            throw new ArgumentException("Bearer Token usa somente Authorization.");
        if (type == CredentialType.ApiKey && !IsAllowedHeader(headerName))
            throw new ArgumentException("API key exige um header X-* permitido.");
        Snapshot = new(id, owner, name, type, origin.Value, headerName, 1,
            now.ToUniversalTime(), now.ToUniversalTime(), null);
    }
    public static Credential Restore(CredentialSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var credential = new Credential(s.Id, s.OwnerUserId, s.Name, s.Type, HttpsOrigin.Parse(s.Origin), s.HeaderName, s.CreatedAt);
        if (s.Revision < 1 || s.CreatedAt.Offset != TimeSpan.Zero || s.UpdatedAt.Offset != TimeSpan.Zero ||
            s.UpdatedAt < s.CreatedAt || (s.RevokedAt is { } revoked && (revoked.Offset != TimeSpan.Zero || revoked != s.UpdatedAt)))
            throw new ArgumentException("Estado de credencial inconsistente.");
        credential.Snapshot = s; return credential;
    }
    public void Rotate(DateTimeOffset now)
    {
        if (Snapshot.RevokedAt is not null) throw new InvalidOperationException("Credencial revogada é terminal.");
        Change(now, false);
    }
    public void Revoke(DateTimeOffset now)
    {
        if (Snapshot.RevokedAt is null) Change(now, true);
    }
    private void Change(DateTimeOffset now, bool revoke)
    {
        if (now < Snapshot.UpdatedAt) throw new ArgumentOutOfRangeException(nameof(now));
        now = now.ToUniversalTime();
        Snapshot = Snapshot with { Revision = checked(Snapshot.Revision + 1), UpdatedAt = now, RevokedAt = revoke ? now : null };
    }
    private static bool IsAllowedHeader(string? name) =>
        name is not null && (name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-Auth-Token", StringComparison.OrdinalIgnoreCase));
}
