
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Application.Executions;
public static class HttpRetryPolicy
{
    public static TimeSpan? NextDelay(HttpRequestConfiguration configuration, int attempt, NodeResult result, double jitter)
    {
        if (attempt < 1 || jitter is < 0 or > 1 || double.IsNaN(jitter)) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (!configuration.CanRepeat || attempt >= configuration.MaxAttempts || result.IsCancelled) return null;
        var transient = result.ErrorCode is ExecutionFailureCode.NodeTimeout or ExecutionFailureCode.HttpTransportFailed ||
            (result.ErrorCode == ExecutionFailureCode.HttpRemoteFailure && result.HttpStatusCode is 408 or 429 or 500 or 502 or 503 or 504);
        if (!transient) return null;
        // Um destino que exige espera maior que o orçamento não é desrespeitado por um retry antecipado.
        if (result.RetryAfter is { } advice && (advice < TimeSpan.Zero || advice > TimeSpan.FromSeconds(60))) return null;
        var exponential = Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5)));
        var delay = TimeSpan.FromSeconds(exponential * (0.5 + jitter * 0.5));
        return result.RetryAfter > delay ? result.RetryAfter.Value : delay;
    }
    public static string IdempotencyKey(Guid executionId, Guid nodeId)
    {
        if (executionId == Guid.Empty || nodeId == Guid.Empty) throw new ArgumentException("Identidade de efeito inválida.");
        return $"flowforge-{executionId:N}-{nodeId:N}";
    }
}
