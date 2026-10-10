using System.Net;
using FlowForge.Infrastructure.Http;
using FlowForge.Infrastructure.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace FlowForge.IntegrationTests;
public sealed class HttpDestinationPolicyTests
{
    [Theory]
    [InlineData("0.0.0.0")][InlineData("10.1.2.3")][InlineData("100.100.100.200")][InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")][InlineData("172.16.0.1")][InlineData("192.168.0.1")][InlineData("192.0.0.192")]
    [InlineData("168.63.129.16")][InlineData("192.0.2.1")][InlineData("192.88.99.2")][InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")][InlineData("203.0.113.1")][InlineData("224.0.0.1")][InlineData("255.255.255.255")]
    [InlineData("::")][InlineData("::1")][InlineData("::ffff:127.0.0.1")][InlineData("::ffff:8.8.8.8")]
    [InlineData("fc00::1")][InlineData("fd00:ec2::254")][InlineData("fe80::1")][InlineData("fe80::1%2")]
    [InlineData("ff02::1")][InlineData("64:ff9b::a00:1")][InlineData("64:ff9b:1::a00:1")]
    [InlineData("2001::1")][InlineData("2001:db8::1")][InlineData("2002:7f00:1::1")][InlineData("3fff::1")][InlineData("5f00::1")]
    public async Task Every_special_private_metadata_or_transition_address_denies_the_entire_dns_result(string value)
    {
        var bad = IPAddress.Parse(value); Assert.False(HttpDestinationPolicy.IsPublic(bad));
        var policy = new HttpDestinationPolicy(new(["https://api.example.com"]), (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), bad }));
        await Assert.ThrowsAsync<HttpDestinationDeniedException>(() => policy.ApproveAsync(new("https://api.example.com/path"), default));
    }
    [Theory][InlineData("1.1.1.1")][InlineData("8.8.8.8")][InlineData("172.15.255.255")][InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")][InlineData("100.128.0.1")][InlineData("2001:4860:4860::8888")][InlineData("2606:4700:4700::1111")]
    public void Normal_public_ipv4_and_ipv6_are_eligible(string value) => Assert.True(HttpDestinationPolicy.IsPublic(IPAddress.Parse(value)));
    [Theory]
    [InlineData("https://api.example.com.evil.test")][InlineData("https://child.api.example.com")]
    [InlineData("https://api.example.com:8443")][InlineData("https://api.example.com.")]
    [InlineData("https://127.0.0.1")][InlineData("https://2130706433")][InlineData("https://0x7f000001")]
    [InlineData("https://[::1]")][InlineData("http://api.example.com")]
    public async Task Origin_is_exact_and_rejected_before_dns(string url)
    {
        var calls = 0;
        var policy = new HttpDestinationPolicy(new(["https://api.example.com"]), (_, _) => { calls++; return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }); });
        await Assert.ThrowsAsync<HttpDestinationDeniedException>(() => policy.ApproveAsync(new(url), default)); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task Empty_allowlist_empty_dns_and_too_many_answers_fail_closed()
    {
        var calls = 0;
        var empty = new HttpDestinationPolicy(new([]), (_, _) => { calls++; return Task.FromResult(Array.Empty<IPAddress>()); });
        await Assert.ThrowsAsync<HttpDestinationDeniedException>(() => empty.ApproveAsync(new("https://api.example.com"), default)); Assert.Equal(0, calls);
        foreach (var answers in new[] { Array.Empty<IPAddress>(), Enumerable.Repeat(IPAddress.Parse("8.8.8.8"), 17).ToArray() })
        {
            var policy = new HttpDestinationPolicy(new(["https://api.example.com"]), (_, _) => Task.FromResult(answers));
            await Assert.ThrowsAsync<HttpDestinationDeniedException>(() => policy.ApproveAsync(new("https://api.example.com"), default));
        }
    }
    [Fact]
    public void Runtime_configuration_is_bounded_and_does_not_echo_invalid_values()
    {
        var marker = "valor-ficticio-nao-registravel";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["FlowForge:Http:AllowedOrigins"] = marker }).Build();
        Assert.DoesNotContain(marker, Assert.Throws<RuntimeConfigurationException>(() => HttpNodeOptions.FromConfiguration(config)).ToString(), StringComparison.Ordinal);
        Assert.Empty(HttpNodeOptions.FromConfiguration(new ConfigurationBuilder().Build()).AllowedOrigins);
        Assert.Throws<ArgumentException>(() => new HttpNodeOptions(["https://*.example.com"]));
        Assert.Throws<ArgumentException>(() => new HttpNodeOptions([], TimeSpan.FromSeconds(9)));
        Assert.Throws<ArgumentException>(() => new HttpNodeOptions([], maxResponseBytes: 32769));
    }
}
