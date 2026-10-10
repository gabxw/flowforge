using System.Security.Cryptography;
using System.Text;
using FlowForge.Application.Credentials;
using FlowForge.Domain.Credentials;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
namespace FlowForge.IntegrationTests;
[Collection("PostgreSQL")]
public sealed class CredentialStoreTests(PostgreSqlFixture fixture)
{
    private readonly CredentialProtection protection = new(new EphemeralDataProtectionProvider());
    private PostgresCredentialStore Store => new(fixture.Factory, protection);
    private async Task<(CredentialSnapshot Metadata, CredentialSecret Secret)> CreateAsync(Guid? owner = null)
    {
        var id = owner ?? Guid.NewGuid(); var now = WorkflowStoreFixtures.Start;
        await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(id, now);
        var metadata = new Credential(Guid.NewGuid(), id, "Test integration", CredentialType.ApiKey,
            HttpsOrigin.Parse("https://api.example.test"), "X-Api-Key", now).Snapshot;
        var secret = new CredentialSecret("ficticio-" + Guid.NewGuid().ToString("N"));
        await Store.CreateAsync(metadata, secret); return (metadata, secret);
    }
    [Fact]
    public async Task Value_is_authenticated_ciphertext_metadata_is_safe_and_resolution_is_owner_and_origin_scoped()
    {
        var (metadata, secret) = await CreateAsync();
        await using var db = await fixture.Factory.CreateDbContextAsync(); var row = await db.Credentials.AsNoTracking().SingleAsync(c => c.Id == metadata.Id);
        Assert.DoesNotContain(secret.Value, Encoding.UTF8.GetString(row.ProtectedValue), StringComparison.Ordinal);
        Assert.Equal(metadata, await Store.GetAsync(metadata.Id, metadata.OwnerUserId));
        Assert.Null(await Store.GetAsync(metadata.Id, Guid.NewGuid()));
        Assert.Empty(await Store.ListAsync(Guid.NewGuid(), 0, 20));
        Assert.Single(await Store.ListAsync(metadata.OwnerUserId, 0, 20));
        Assert.Null(await Store.ResolveAsync(metadata.Id, Guid.NewGuid(), HttpsOrigin.Parse(metadata.Origin)));
        Assert.Null(await Store.ResolveAsync(metadata.Id, metadata.OwnerUserId, HttpsOrigin.Parse("https://api.example.test:8443")));
        var resolved = await Store.ResolveAsync(metadata.Id, metadata.OwnerUserId, HttpsOrigin.Parse(metadata.Origin));
        Assert.Equal(secret.Value, resolved!.Secret.Value); Assert.DoesNotContain(secret.Value, resolved.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task Purpose_binds_id_owner_revision_type_origin_and_header_and_does_not_share_execution_purpose()
    {
        var (metadata, secret) = await CreateAsync();
        var ciphertext = protection.Protect(secret, metadata);
        foreach (var changed in new[] { metadata with { Id = Guid.NewGuid() }, metadata with { OwnerUserId = Guid.NewGuid() },
            metadata with { Revision = 2 }, metadata with { Type = CredentialType.BearerToken },
            metadata with { Origin = "https://other.example.test" }, metadata with { HeaderName = "X-Auth-Token" } })
            Assert.Throws<CryptographicException>(() => protection.Unprotect(ciphertext, changed));
        var tampered = ciphertext.ToArray(); tampered[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => protection.Unprotect(tampered, metadata));
        var provider = new EphemeralDataProtectionProvider(); var checkpoint = new ExecutionContextProtection(provider);
        var protectedSecret = new CredentialProtection(provider).Protect(secret, metadata);
        Assert.Throws<CryptographicException>(() => checkpoint.Unprotect(protectedSecret, metadata.Id));
    }
    [Fact]
    public async Task Only_one_concurrent_rotation_wins_and_latest_revision_is_resolved_for_the_next_operation()
    {
        var (metadata, original) = await CreateAsync();
        var next = Enumerable.Range(0, 8).Select(_ => new CredentialSecret("ficticio-" + Guid.NewGuid().ToString("N"))).ToArray();
        var outcomes = await Task.WhenAll(next.Select(async secret => {
            try { return (Metadata: await Store.RotateAsync(metadata.Id, metadata.OwnerUserId, 1, secret, metadata.UpdatedAt.AddSeconds(1)), Secret: secret); }
            catch (CredentialConcurrencyException) { return (Metadata: (CredentialSnapshot?)null, Secret: secret); }
        }));
        var winner = Assert.Single(outcomes, o => o.Metadata is not null); Assert.Equal(2, winner.Metadata!.Revision);
        var resolved = await Store.ResolveAsync(metadata.Id, metadata.OwnerUserId, HttpsOrigin.Parse(metadata.Origin));
        Assert.Equal(winner.Secret.Value, resolved!.Secret.Value); Assert.NotEqual(original.Value, resolved.Secret.Value);
        // O valor já resolvido permanece estável na chamada atual; uma nova resolução observa a rotação.
        var captured = resolved;
        await Store.RotateAsync(metadata.Id, metadata.OwnerUserId, 2, original, metadata.UpdatedAt.AddSeconds(2));
        Assert.Equal(winner.Secret.Value, captured.Secret.Value); Assert.Equal(2, captured.Metadata.Revision);
        Assert.Equal(3, (await Store.ResolveAsync(metadata.Id, metadata.OwnerUserId, HttpsOrigin.Parse(metadata.Origin)))!.Metadata.Revision);
    }
    [Fact]
    public async Task Revocation_is_terminal_preserves_references_and_blocks_resolution_and_rotation()
    {
        var (metadata, secret) = await CreateAsync(); var revoked = await Store.RevokeAsync(metadata.Id, metadata.OwnerUserId, 1, metadata.UpdatedAt.AddSeconds(1));
        Assert.NotNull(revoked.RevokedAt); Assert.Equal(2, revoked.Revision);
        Assert.Equal(revoked, await Store.RevokeAsync(metadata.Id, metadata.OwnerUserId, 2, metadata.UpdatedAt.AddSeconds(2)));
        Assert.Null(await Store.ResolveAsync(metadata.Id, metadata.OwnerUserId, HttpsOrigin.Parse(metadata.Origin)));
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => Store.RotateAsync(metadata.Id, metadata.OwnerUserId, 2, secret, metadata.UpdatedAt.AddSeconds(2)));
        await Assert.ThrowsAsync<CredentialConcurrencyException>(() => Store.RevokeAsync(metadata.Id, metadata.OwnerUserId, 1, metadata.UpdatedAt.AddSeconds(2)));
    }
    [Fact]
    public async Task Composite_foreign_key_rejects_a_workflow_node_linked_to_another_owner()
    {
        var workflow = await WorkflowStoreFixtures.CreateAsync(fixture, Guid.NewGuid()); var (foreign, _) = await CreateAsync();
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var http = workflow.Versions[0].Nodes.Single(n => n.Type == FlowForge.Domain.Workflows.NodeType.HttpRequest);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_nodes SET credential_id = {foreign.Id} WHERE workflow_version_id = {http.WorkflowVersionId} AND node_id = {http.NodeId}"));
        Assert.Equal("23503", exception.SqlState);
    }
    [Fact]
    public async Task Ciphertext_cannot_be_swapped_to_another_owner_or_origin_and_corruption_has_a_safe_error()
    {
        var (a, _) = await CreateAsync(); var (b, _) = await CreateAsync();
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var ciphertext = (await db.Credentials.AsNoTracking().SingleAsync(c => c.Id == a.Id)).ProtectedValue;
        await db.Credentials.Where(c => c.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ProtectedValue, ciphertext));
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => Store.ResolveAsync(b.Id, b.OwnerUserId, HttpsOrigin.Parse(b.Origin)));
    }
}
