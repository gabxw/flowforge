using System.Text;
using FlowForge.Application.Executions;
using RabbitMQ.Client;
namespace FlowForge.Infrastructure.Messaging;
public static class RabbitTraceContext
{
    public static IDictionary<string, object?> Headers(string? parent = null)
    {
        var valid = ExecutionTelemetry.ValidParent(parent ?? ExecutionTelemetry.CaptureParent());
        return valid is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["traceparent"] = Encoding.ASCII.GetBytes(valid) };
    }
    public static string? Read(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue("traceparent", out var value) != true) return null;
        return value is byte[] { Length: 55 } bytes ? ExecutionTelemetry.ValidParent(Encoding.ASCII.GetString(bytes)) : null;
    }
}
