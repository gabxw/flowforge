using FlowForge.Domain.Credentials;
using FlowForge.Domain.Executions;
using Xunit;
namespace FlowForge.Domain.Tests;
public sealed class CredentialTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static Credential Create() => new(Guid.NewGuid(), Guid.NewGuid(), "Integration", CredentialType.BearerToken,
        HttpsOrigin.Parse("https://API.Example.com:443/"), null, Now);
    [Theory]
    [InlineData("https://API.Example.com:443/", "https://api.example.com")]
    [InlineData("https://api.example.com:8443", "https://api.example.com:8443")]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example")]
    public void Origin_has_canonical_hostname_and_explicit_port_identity(string input, string expected) =>
        Assert.Equal(expected, HttpsOrigin.Parse(input).Value);
    [Theory]
    [InlineData("http://api.example.com")][InlineData("file:///tmp/data")][InlineData("https://api.example.com/path")]
    [InlineData("https://api.example.com?key=value")][InlineData("https://api.example.com#fragment")]
    [InlineData("https://user@api.example.com")][InlineData("https://@api.example.com")]
    [InlineData("https://*.example.com")][InlineData("https://api.example.com.")][InlineData("https://localhost")]
    [InlineData("https://127.0.0.1")][InlineData("https://[::1]")]
    public void Origin_rejects_noncanonical_or_unsafe_authorities(string value) => Assert.Throws<ArgumentException>(() => HttpsOrigin.Parse(value));
    [Fact]
    public void Rotation_advances_revision_and_revocation_is_terminal_idempotent_and_preserves_identity()
    {
        var credential = Create(); var original = credential.Snapshot;
        credential.Rotate(Now.AddSeconds(1)); Assert.Equal(2, credential.Snapshot.Revision);
        credential.Revoke(Now.AddSeconds(2)); var revoked = credential.Snapshot;
        Assert.Equal(3, revoked.Revision); Assert.Equal(Now.AddSeconds(2), revoked.RevokedAt);
        credential.Revoke(Now.AddSeconds(3)); Assert.Equal(revoked, credential.Snapshot);
        Assert.Throws<InvalidOperationException>(() => credential.Rotate(Now.AddSeconds(3)));
        Assert.Equal(original.Id, credential.Snapshot.Id); Assert.Equal(original.Origin, credential.Snapshot.Origin);
        Assert.Equal(revoked, Credential.Restore(revoked).Snapshot);
    }
    [Theory]
    [InlineData("Authorization")][InlineData("Host")][InlineData("Cookie")][InlineData("Proxy-Authorization")]
    [InlineData("Content-Type")][InlineData("Content-Length")][InlineData("X-Forwarded-Host")]
    [InlineData("X-Http-Method-Override")][InlineData("X-Original-URL")][InlineData("X-Any-Header")]
    [InlineData("X-Api-Key\r\nHost")]
    public void Api_key_cannot_select_protected_or_arbitrary_headers(string header) => Assert.Throws<ArgumentException>(() =>
        new Credential(Guid.NewGuid(), Guid.NewGuid(), "API", CredentialType.ApiKey, HttpsOrigin.Parse("https://api.example.com"), header, Now));
    [Theory][InlineData("X-Api-Key")][InlineData("x-auth-token")]
    public void Api_key_accepts_only_supported_authentication_headers(string header) => Assert.Equal(header,
        new Credential(Guid.NewGuid(), Guid.NewGuid(), "API", CredentialType.ApiKey, HttpsOrigin.Parse("https://api.example.com"), header, Now).Snapshot.HeaderName);
    [Fact]
    public void Metadata_rejects_inconsistent_identity_enum_time_revision_and_secret_header()
    {
        var s = Create().Snapshot;
        Assert.Throws<ArgumentException>(() => Credential.Restore(s with { Id = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => Credential.Restore(s with { Name = new string('x', 121) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Credential.Restore(s with { Type = (CredentialType)99 }));
        Assert.Throws<ArgumentException>(() => Credential.Restore(s with { HeaderName = "X-Api-Key" }));
        Assert.Throws<ArgumentException>(() => Credential.Restore(s with { Revision = 0 }));
        Assert.Throws<ArgumentException>(() => Credential.Restore(s with { UpdatedAt = Now.AddTicks(-1) }));
        Assert.Throws<ArgumentException>(() => Credential.Restore(s with { RevokedAt = Now.AddSeconds(1) }));
        var c = Create(); c.Rotate(Now.AddSeconds(1)); var before = c.Snapshot;
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Rotate(Now)); Assert.Equal(before, c.Snapshot);
    }
    [Fact]
    public void Node_records_only_a_known_positive_credential_revision_when_running()
    {
        var n = new NodeExecution(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => n.RecordCredentialRevision(1));
        n.Start(new(2, "Object"), Now); Assert.Throws<ArgumentOutOfRangeException>(() => n.RecordCredentialRevision(0));
        n.RecordCredentialRevision(2); n.Fail(ExecutionFailureCode.HttpRemoteFailure, Now);
        Assert.Equal(2, NodeExecution.Restore(n.Snapshot).Snapshot.CredentialRevisionUsed);
        Assert.Throws<InvalidOperationException>(() => n.RecordCredentialRevision(3));
    }
}
