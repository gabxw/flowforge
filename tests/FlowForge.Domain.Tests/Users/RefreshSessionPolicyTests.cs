using FlowForge.Domain.Users;
using Xunit;
namespace FlowForge.Domain.Tests.Users;
public sealed class RefreshSessionPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T12:00:00Z");
    [Theory]
    [InlineData(false, false, false, RefreshDecision.Rotate)]
    [InlineData(true, false, false, RefreshDecision.RevokeFamily)]
    [InlineData(true, true, false, RefreshDecision.Reject)]
    [InlineData(false, false, true, RefreshDecision.Reject)]
    public void Rotation_has_explicit_replay_expiry_and_revocation_policy(bool used, bool revoked, bool expired, RefreshDecision expected) =>
        Assert.Equal(expected, RefreshSessionPolicy.Decide(expired ? Now : Now.AddDays(1), revoked ? Now : null, used, Now));
    [Theory]
    [InlineData("not-email")]
    [InlineData("owner@example.com\n")]
    [InlineData("Owner Name <owner@example.com>")]
    public void Display_names_and_ambiguous_identifiers_are_refused(string email) => Assert.Throws<ArgumentException>(() => new UserIdentity(Guid.NewGuid(), email, UserRole.Owner));
}
