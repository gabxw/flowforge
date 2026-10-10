using FlowForge.Domain.Credentials;
namespace FlowForge.Infrastructure.Persistence.Records;
internal sealed class CredentialRecord
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public CredentialType Type { get; set; }
    public string Origin { get; set; } = "";
    public string? HeaderName { get; set; }
    public byte[] ProtectedValue { get; set; } = [];
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
