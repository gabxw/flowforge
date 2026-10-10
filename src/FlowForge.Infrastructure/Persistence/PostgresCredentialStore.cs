using System.Security.Cryptography;
using FlowForge.Application.Credentials;
using FlowForge.Domain.Credentials;
using FlowForge.Infrastructure.Persistence.Records;
using FlowForge.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;
public sealed class PostgresCredentialStore(IDbContextFactory<FlowForgeDbContext> factory,
    CredentialProtection protection) : ICredentialStore
{
    public async Task CreateAsync(CredentialSnapshot metadata, CredentialSecret secret, CancellationToken ct = default)
    {
        _ = Credential.Restore(metadata);
        if (metadata.Revision != 1 || metadata.RevokedAt is not null) throw new ArgumentException("Estado inicial inválido.");
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Credentials.Add(new() { Id = metadata.Id, OwnerUserId = metadata.OwnerUserId, Name = metadata.Name,
            Type = metadata.Type, Origin = metadata.Origin, HeaderName = metadata.HeaderName,
            Revision = metadata.Revision, CreatedAt = metadata.CreatedAt, UpdatedAt = metadata.UpdatedAt,
            ProtectedValue = protection.Protect(secret, metadata) });
        await db.SaveChangesAsync(ct);
    }
    public async Task<CredentialSnapshot?> GetAsync(Guid id, Guid owner, CancellationToken ct = default)
    {
        Validate(id, owner);
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == owner, ct);
        return row is null ? null : Snapshot(row);
    }
    public async Task<IReadOnlyList<CredentialSnapshot>> ListAsync(Guid owner, int offset, int limit, CancellationToken ct = default)
    {
        if (owner == Guid.Empty || offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Paginação ou proprietário inválido.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Credentials.AsNoTracking().Where(c => c.OwnerUserId == owner)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Skip(offset).Take(limit).ToArrayAsync(ct);
        return rows.Select(Snapshot).ToArray();
    }
    public Task<CredentialSnapshot> RotateAsync(Guid id, Guid owner, int expectedRevision, CredentialSecret secret,
        DateTimeOffset now, CancellationToken ct = default) => ChangeAsync(id, owner, expectedRevision, now, secret, ct);
    public Task<CredentialSnapshot> RevokeAsync(Guid id, Guid owner, int expectedRevision, DateTimeOffset now,
        CancellationToken ct = default) => ChangeAsync(id, owner, expectedRevision, now, null, ct);
    private async Task<CredentialSnapshot> ChangeAsync(Guid id, Guid owner, int revision, DateTimeOffset now,
        CredentialSecret? secret, CancellationToken ct)
    {
        Validate(id, owner);
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Credentials.FromSqlInterpolated($"SELECT * FROM public.credentials WHERE id = {id} AND owner_user_id = {owner} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
        if (row.Revision != revision) throw new CredentialConcurrencyException();
        var credential = Credential.Restore(Snapshot(row));
        now = now < row.UpdatedAt ? row.UpdatedAt : now;
        if (secret is not null)
        {
            if (row.RevokedAt is not null) throw new CredentialUnavailableException();
            credential.Rotate(now);
            row.ProtectedValue = protection.Protect(secret, credential.Snapshot);
        }
        else credential.Revoke(now);
        row.Revision = credential.Snapshot.Revision; row.UpdatedAt = credential.Snapshot.UpdatedAt;
        row.RevokedAt = credential.Snapshot.RevokedAt;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return credential.Snapshot;
    }
    public async Task<ResolvedCredential?> ResolveAsync(Guid id, Guid owner, HttpsOrigin origin, CancellationToken ct = default)
    {
        Validate(id, owner); ArgumentNullException.ThrowIfNull(origin);
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == owner &&
            c.Origin == origin.Value && c.RevokedAt == null, ct);
        if (row is null) return null;
        var metadata = Snapshot(row);
        try { return new(metadata, protection.Unprotect(row.ProtectedValue, metadata)); }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { throw new CredentialUnavailableException(); }
    }
    internal static CredentialSnapshot Snapshot(CredentialRecord r) => Credential.Restore(new(r.Id, r.OwnerUserId, r.Name,
        r.Type, r.Origin, r.HeaderName, r.Revision, r.CreatedAt, r.UpdatedAt, r.RevokedAt)).Snapshot;
    private static void Validate(Guid id, Guid owner)
    {
        if (id == Guid.Empty || owner == Guid.Empty) throw new ArgumentException("Identidades obrigatórias.");
    }
}
