using FlowForge.Application.Users;
using FlowForge.Domain.Users;
using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
namespace FlowForge.Infrastructure.Persistence;
public sealed class PostgresAuthenticationStore(IDbContextFactory<FlowForgeDbContext> factory) : IAuthenticationStore
{
    public async Task<LoginAccount?> FindAsync(string email, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var u = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedEmail == email, ct);
        return u?.PasswordHash is null ? null : new(User(u), u.PasswordHash, u.Disabled, u.LockedUntil);
    }
    public async Task<UserIdentity> RegisterAsync(string email, string normalized, string hash, Guid? bootstrapOwnerId, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serializa somente criação de contas e a atribuição do primeiro Admin.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(130013)", ct);
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, ct) || await db.Users.CountAsync(x => x.PasswordHash != null, ct) >= 1000)
            throw new AccountConflictException();
        var first = !await db.Users.AnyAsync(x => x.PasswordHash != null, ct);
        var bootstrap = first && bootstrapOwnerId.HasValue;
        var id = bootstrap ? bootstrapOwnerId!.Value : Guid.NewGuid();
        var u = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (u is null) { u = new() { Id = id, CreatedAt = now }; db.Users.Add(u); }
        if (u.PasswordHash != null) throw new AccountConflictException();
        u.Email = email; u.NormalizedEmail = normalized; u.PasswordHash = hash;
        u.Role = bootstrap ? UserRole.Admin : UserRole.Owner;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return User(u);
    }
    public async Task RecordLoginAsync(Guid id, bool success, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (success) await db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedSignIns, 0).SetProperty(x => x.LockedUntil, (DateTimeOffset?)null), ct);
        else await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET failed_sign_ins = CASE WHEN locked_until <= {now} THEN 1 ELSE LEAST(failed_sign_ins + 1, 5) END, locked_until = CASE WHEN locked_until <= {now} THEN NULL WHEN failed_sign_ins >= 4 THEN {now.AddMinutes(15)} ELSE locked_until END WHERE id = {id}", ct);
    }
    public async Task<SessionIdentity> CreateSessionAsync(Guid id, byte[] hash, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var users = await db.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {id} FOR UPDATE").ToListAsync(ct);
        var u = users.SingleOrDefault();
        if (u?.PasswordHash is null || u.Disabled) throw new AuthenticationRejectedException();
        var active = await db.RefreshSessions.Where(x => x.UserId == id && x.RevokedAt == null && x.ExpiresAt > now).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        foreach (var old in active.Take(Math.Max(0, active.Count - 9))) old.RevokedAt = now;
        var s = new RefreshSessionRecord { Id = Guid.NewGuid(), UserId = id, CreatedAt = now, ExpiresAt = expires };
        db.RefreshSessions.Add(s); db.RefreshTokens.Add(Token(s.Id, hash, now));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(s.Id, User(u), s.ExpiresAt);
    }
    public async Task<SessionIdentity?> RotateAsync(byte[] hash, byte[] replacementHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sessions = await db.RefreshSessions.FromSqlInterpolated($"SELECT s.* FROM refresh_sessions s JOIN refresh_tokens t ON t.session_id = s.id WHERE t.hash = {hash} FOR UPDATE OF s").ToListAsync(ct);
        var s = sessions.SingleOrDefault(); if (s is null) return null;
        var t = await db.RefreshTokens.SingleAsync(x => x.Hash == hash, ct);
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == s.UserId, ct);
        var decision = RefreshSessionPolicy.Decide(s.ExpiresAt, s.RevokedAt, t.ConsumedAt != null, now);
        if (decision != RefreshDecision.Rotate || u.Disabled || u.PasswordHash is null)
        {
            if (decision == RefreshDecision.RevokeFamily || u.Disabled) s.RevokedAt ??= now;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        t.ConsumedAt = now; db.RefreshTokens.Add(Token(s.Id, replacementHash, now));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(s.Id, User(u), s.ExpiresAt);
    }
    public async Task RevokeAsync(byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = db.RefreshTokens.Where(x => x.Hash == hash).Select(x => x.SessionId);
        await db.RefreshSessions.Where(x => ids.Contains(x.Id) && x.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }
    public async Task RevokeAllAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.RefreshSessions.Where(x => x.UserId == id && x.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }
    public async Task<bool> IsActiveAsync(Guid id, Guid session, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await (from s in db.RefreshSessions.AsNoTracking() join u in db.Users on s.UserId equals u.Id
            where s.Id == session && s.UserId == id && s.RevokedAt == null && s.ExpiresAt > now && !u.Disabled && u.PasswordHash != null select s.Id).AnyAsync(ct);
    }
    public async Task<IReadOnlyList<UserIdentity>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Users.AsNoTracking().Where(x => x.PasswordHash != null).OrderBy(x => x.CreatedAt).Take(1000).ToListAsync(ct)).Select(User).ToArray();
    }
    public async Task<bool> HasAccountsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); return await db.Users.AnyAsync(x => x.PasswordHash != null, ct);
    }
    private static RefreshTokenRecord Token(Guid id, byte[] hash, DateTimeOffset now) => new() { Id = Guid.NewGuid(), SessionId = id, Hash = hash, CreatedAt = now };
    private static UserIdentity User(TechnicalUserRecord u) => new(u.Id, u.Email!, u.Role);
}
