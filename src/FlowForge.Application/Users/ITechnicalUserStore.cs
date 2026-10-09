namespace FlowForge.Application.Users;
public interface ITechnicalUserStore
{
    Task EnsureExistsAsync(Guid id, DateTimeOffset createdAt, CancellationToken cancellationToken = default);
}
