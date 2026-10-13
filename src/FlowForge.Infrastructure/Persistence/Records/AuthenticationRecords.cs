namespace FlowForge.Infrastructure.Persistence.Records;
internal sealed class RefreshSessionRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
internal sealed class RefreshTokenRecord
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public byte[] Hash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
