using FlowForge.Domain.Users;
namespace FlowForge.Application.Users;
public sealed class AuthenticationService(IAuthenticationStore store, IPasswordHashing passwords, IAuthSecrets secrets, TimeProvider clock)
{
    public async Task<SessionGrant> RegisterAsync(string email, string password, Guid? bootstrapOwnerId, CancellationToken ct = default)
    {
        ValidatePassword(password); email = email.Trim(); var normalized = UserIdentity.NormalizeEmail(email);
        var user = await store.RegisterAsync(email, normalized, passwords.Hash(password), bootstrapOwnerId, Now(), ct);
        return await GrantAsync(user.Id, ct);
    }
    public Task<UserIdentity> CreateOwnerAsync(string email, string password, CancellationToken ct = default)
    {
        ValidatePassword(password); email = email.Trim();
        return store.RegisterAsync(email, UserIdentity.NormalizeEmail(email), passwords.Hash(password), null, Now(), ct);
    }
    public async Task<SessionGrant> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (password is null || password.Length is < 1 or > 128) throw new AuthenticationRejectedException();
        string normalized;
        try { normalized = UserIdentity.NormalizeEmail(email); }
        catch (ArgumentException) { passwords.VerifyDummy(password); throw new AuthenticationRejectedException(); }
        var account = await store.FindAsync(normalized, ct);
        if (account is null) { passwords.VerifyDummy(password); throw new AuthenticationRejectedException(); }
        var matches = passwords.Verify(account.PasswordHash, password);
        if (account.Disabled || account.LockedUntil > Now()) throw new AuthenticationRejectedException();
        await store.RecordLoginAsync(account.User.Id, matches, Now(), ct);
        if (!matches) throw new AuthenticationRejectedException();
        return await GrantAsync(account.User.Id, ct);
    }
    public async Task<SessionGrant> RefreshAsync(string token, CancellationToken ct = default)
    {
        var replacement = secrets.Create();
        var session = await store.RotateAsync(secrets.Hash(token), secrets.Hash(replacement), Now(), ct) ?? throw new AuthenticationRejectedException();
        return new(session, replacement);
    }
    public Task LogoutAsync(string token, CancellationToken ct = default) => store.RevokeAsync(secrets.Hash(token), Now(), ct);
    public Task RevokeAllAsync(Guid id, CancellationToken ct = default) => store.RevokeAllAsync(id, Now(), ct);
    private async Task<SessionGrant> GrantAsync(Guid id, CancellationToken ct)
    {
        var token = secrets.Create(); var now = Now();
        return new(await store.CreateSessionAsync(id, secrets.Hash(token), now, now.AddDays(7), ct), token);
    }
    private DateTimeOffset Now() => clock.GetUtcNow();
    public static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < 15 or > 128) throw new ArgumentException("A senha deve ter de 15 a 128 caracteres.");
    }
}
