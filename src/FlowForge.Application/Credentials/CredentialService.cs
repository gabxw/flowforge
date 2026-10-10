using FlowForge.Application.Users;
using FlowForge.Domain.Credentials;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Application.Credentials;
public sealed class CredentialService(ICredentialStore store, ITechnicalUserStore users, TimeProvider clock)
{
    public async Task<CredentialSnapshot> CreateAsync(Guid owner, string name, CredentialType type,
        string origin, string? headerName, CredentialSecret secret, CancellationToken ct = default)
    {
        var now = Now();
        var metadata = new Credential(Guid.NewGuid(), owner, name, type, HttpsOrigin.Parse(origin), headerName, now).Snapshot;
        ArgumentNullException.ThrowIfNull(secret);
        await users.EnsureExistsAsync(owner, now, ct);
        await store.CreateAsync(metadata, secret, ct); return metadata;
    }
    public async Task<CredentialSnapshot> GetAsync(Guid owner, Guid id, CancellationToken ct = default) =>
        await store.GetAsync(id, owner, ct) ?? throw new KeyNotFoundException();
    public Task<IReadOnlyList<CredentialSnapshot>> ListAsync(Guid owner, int offset, int limit, CancellationToken ct = default) =>
        store.ListAsync(owner, offset, limit, ct);
    public Task<CredentialSnapshot> RotateAsync(Guid owner, Guid id, int revision, CredentialSecret secret, CancellationToken ct = default) =>
        store.RotateAsync(id, owner, revision, secret, Now(), ct);
    public Task<CredentialSnapshot> RevokeAsync(Guid owner, Guid id, int revision, CancellationToken ct = default) =>
        store.RevokeAsync(id, owner, revision, Now(), ct);
    public async Task ValidateReferencesAsync(Guid owner, IEnumerable<WorkflowNode> nodes, CancellationToken ct = default)
    {
        foreach (var node in nodes.Where(n => n.Credential is not null))
        {
            var metadata = await store.GetAsync(node.Credential!.Id, owner, ct);
            if (metadata is null || metadata.RevokedAt is not null || metadata.Origin != HttpsOrigin.FromUrl(((HttpRequestConfiguration)node.Configuration).Url).Value)
                throw new CredentialUnavailableException();
        }
    }
    private DateTimeOffset Now() => new(clock.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
}
