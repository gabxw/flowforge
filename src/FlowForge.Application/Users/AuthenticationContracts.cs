using FlowForge.Domain.Users;
namespace FlowForge.Application.Users;
public sealed class AuthenticationRejectedException : Exception;
public sealed class AccountConflictException : Exception;
public sealed record LoginAccount(UserIdentity User, string PasswordHash, bool Disabled, DateTimeOffset? LockedUntil)
{
    public override string ToString() => "LoginAccount [redacted]";
}
public sealed record SessionIdentity(Guid Id, UserIdentity User, DateTimeOffset ExpiresAt);
public sealed record SessionGrant(SessionIdentity Session, string RefreshToken)
{
    public override string ToString() => "SessionGrant [redacted]";
}
public interface IPasswordHashing
{
    string Hash(string password);
    bool Verify(string hash, string password);
    void VerifyDummy(string password);
}
public interface IAuthSecrets
{
    string Create();
    byte[] Hash(string token);
}
public interface IAuthenticationStore
{
    Task<LoginAccount?> FindAsync(string normalizedEmail, CancellationToken ct);
    Task<UserIdentity> RegisterAsync(string email, string normalizedEmail, string passwordHash, Guid? bootstrapOwnerId, DateTimeOffset now, CancellationToken ct);
    Task RecordLoginAsync(Guid id, bool success, DateTimeOffset now, CancellationToken ct);
    Task<SessionIdentity> CreateSessionAsync(Guid id, byte[] hash, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct);
    Task<SessionIdentity?> RotateAsync(byte[] hash, byte[] replacementHash, DateTimeOffset now, CancellationToken ct);
    Task RevokeAsync(byte[] hash, DateTimeOffset now, CancellationToken ct);
    Task RevokeAllAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
    Task<bool> IsActiveAsync(Guid userId, Guid sessionId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<UserIdentity>> ListAsync(CancellationToken ct);
    Task<bool> HasAccountsAsync(CancellationToken ct);
}
