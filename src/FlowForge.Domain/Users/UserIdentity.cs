using System.Net.Mail;
namespace FlowForge.Domain.Users;
public enum UserRole { Owner, Admin }
public sealed record UserIdentity
{
    public Guid Id { get; }
    public string Email { get; }
    public UserRole Role { get; }
    public UserIdentity(Guid id, string email, UserRole role)
    {
        if (id == Guid.Empty || !Enum.IsDefined(role)) throw new ArgumentException("Identidade inválida.");
        Email = ValidateEmail(email); Id = id; Role = role;
    }
    public static string ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Any(c => c > 127 || char.IsControl(c)) ||
            !MailAddress.TryCreate(email, out var address) || address.Address != email || !email.Contains('@'))
            throw new ArgumentException("E-mail inválido.");
        return email;
    }
    public static string NormalizeEmail(string email) => ValidateEmail(email.Trim()).ToUpperInvariant();
}
public enum RefreshDecision { Rotate, Reject, RevokeFamily }
public static class RefreshSessionPolicy
{
    public static RefreshDecision Decide(DateTimeOffset expiresAt, DateTimeOffset? revokedAt, bool used, DateTimeOffset now) =>
        revokedAt is not null || now >= expiresAt ? RefreshDecision.Reject : used ? RefreshDecision.RevokeFamily : RefreshDecision.Rotate;
}
