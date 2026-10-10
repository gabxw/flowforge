using FlowForge.Application.Credentials;
using Xunit;
namespace FlowForge.Application.Tests;
public sealed class CredentialSecretTests
{
    [Theory][InlineData(0)][InlineData(15)][InlineData(4097)]
    public void Secret_size_is_bounded(int size) => Assert.Throws<ArgumentException>(() => new CredentialSecret(new string('a', size)));
    [Theory][InlineData(' ')][InlineData('\r')][InlineData('\n')][InlineData('\0')][InlineData('á')]
    public void Secret_cannot_inject_headers_or_have_nonascii_characters(char value) =>
        Assert.Throws<ArgumentException>(() => new CredentialSecret(new string('a', 16) + value));
    [Theory][InlineData(16)][InlineData(4096)]
    public void Secret_accepts_boundaries_and_redacts_object_representation(int size)
    {
        var value = new string('a', size); var secret = new CredentialSecret(value);
        Assert.Equal(value, secret.Value); Assert.DoesNotContain(value, secret.ToString(), StringComparison.Ordinal);
    }
}
