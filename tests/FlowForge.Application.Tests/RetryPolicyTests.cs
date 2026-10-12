
using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows.Configuration;
using Xunit;

namespace FlowForge.Application.Tests;
public sealed class RetryPolicyTests
{
    private static HttpRequestConfiguration Config(int attempts = 5) => new(new Uri("https://example.com"), HttpRequestMethod.Get, attempts);
    [Theory]
    [InlineData(408, true)] [InlineData(429, true)] [InlineData(500, true)] [InlineData(502, true)] [InlineData(503, true)] [InlineData(504, true)]
    [InlineData(400, false)] [InlineData(401, false)] [InlineData(403, false)] [InlineData(404, false)] [InlineData(409, false)] [InlineData(422, false)] [InlineData(501, false)]
    public void Status_selects_only_transient_failures(int status, bool retry)
    {
        var result = NodeResult.Failure(ExecutionFailureCode.HttpRemoteFailure) with { HttpStatusCode = status };
        Assert.Equal(retry, HttpRetryPolicy.NextDelay(Config(), 1, result, 0.5).HasValue);
    }
    [Theory]
    [InlineData(ExecutionFailureCode.NodeTimeout, true)] [InlineData(ExecutionFailureCode.HttpTransportFailed, true)]
    [InlineData(ExecutionFailureCode.HttpDestinationDenied, false)] [InlineData(ExecutionFailureCode.CredentialUnavailable, false)]
    [InlineData(ExecutionFailureCode.HttpResponseSensitive, false)] [InlineData(ExecutionFailureCode.ExecutionDeadlineExceeded, false)]
    public void Permanent_and_security_failures_are_never_retried(ExecutionFailureCode code, bool retry) =>
        Assert.Equal(retry, HttpRetryPolicy.NextDelay(Config(), 1, NodeResult.Failure(code), 0).HasValue);
    [Fact]
    public void Exponential_jitter_advice_and_budget_are_bounded()
    {
        var result = NodeResult.Failure(ExecutionFailureCode.HttpRemoteFailure) with { HttpStatusCode = 429 };
        Assert.Equal(TimeSpan.FromSeconds(0.5), HttpRetryPolicy.NextDelay(Config(), 1, result, 0));
        Assert.Equal(TimeSpan.FromSeconds(8), HttpRetryPolicy.NextDelay(Config(), 4, result, 1));
        Assert.Equal(TimeSpan.FromSeconds(30), HttpRetryPolicy.NextDelay(Config(), 1, result with { RetryAfter = TimeSpan.FromSeconds(30) }, 0));
        Assert.Null(HttpRetryPolicy.NextDelay(Config(), 1, result with { RetryAfter = TimeSpan.FromHours(1) }, 0));
        Assert.Null(HttpRetryPolicy.NextDelay(Config(), 5, result, 0.5));
        Assert.Null(HttpRetryPolicy.NextDelay(Config(1), 1, result, 0.5));
        Assert.Null(HttpRetryPolicy.NextDelay(Config(), 1, result with { IsCancelled = true }, 0));
    }
    [Fact]
    public void Stable_key_changes_only_for_another_execution_or_node()
    {
        var execution = Guid.NewGuid(); var node = Guid.NewGuid(); var key = HttpRetryPolicy.IdempotencyKey(execution, node);
        Assert.Equal(key, HttpRetryPolicy.IdempotencyKey(execution, node));
        Assert.NotEqual(key, HttpRetryPolicy.IdempotencyKey(Guid.NewGuid(), node));
        Assert.NotEqual(key, HttpRetryPolicy.IdempotencyKey(execution, Guid.NewGuid()));
        Assert.DoesNotContain("secret", key);
    }
}
