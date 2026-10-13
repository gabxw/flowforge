using System.Diagnostics;
using System.Diagnostics.Metrics;
using FlowForge.Domain.Workflows;
namespace FlowForge.Application.Executions;
public static class ExecutionTelemetry
{
    public const string Name = "FlowForge.Executions";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Metrics = new(Name);
    public static readonly Counter<long> Accepted = Metrics.CreateCounter<long>("flowforge.execution.accepted", unit: "{execution}");
    public static readonly Counter<long> Finished = Metrics.CreateCounter<long>("flowforge.execution.finished", unit: "{execution}");
    public static readonly Counter<long> Retries = Metrics.CreateCounter<long>("flowforge.node.retry", unit: "{retry}");
    public static readonly Counter<long> Published = Metrics.CreateCounter<long>("flowforge.message.published", unit: "{message}");
    public static readonly Counter<long> Deliveries = Metrics.CreateCounter<long>("flowforge.message.received", unit: "{message}");
    public static readonly Counter<long> DeadLetters = Metrics.CreateCounter<long>("flowforge.message.dead_letter", unit: "{message}");
    public static readonly Histogram<double> NodeDuration = Metrics.CreateHistogram<double>("flowforge.node.active.duration", unit: "s");
    public static readonly Histogram<double> WaitDuration = Metrics.CreateHistogram<double>("flowforge.wait.duration", unit: "s");
    public static readonly Histogram<double> ScheduledWait = Metrics.CreateHistogram<double>("flowforge.wait.scheduled", unit: "s");
    public static readonly Histogram<double> OutboxLag = Metrics.CreateHistogram<double>("flowforge.outbox.dispatch.lag", unit: "s");
    public static string? CaptureParent() => ValidParent(Activity.Current?.Id);
    public static string? ValidParent(string? parent) => parent is { Length: 55 } && ActivityContext.TryParse(parent, null, out _) ? parent : null;
    public static Activity? Start(string name, ActivityKind kind, string? parent = null)
    {
        var valid = ValidParent(parent);
        return valid is not null && ActivityContext.TryParse(valid, null, true, out var context)
            ? Activities.StartActivity(name, kind, context) : Activities.StartActivity(name, kind);
    }
    public static Activity? Node(Guid executionId, Guid correlationId, Guid nodeId, NodeType type, int attempt)
    {
        var span = Activities.StartActivity("workflow.node");
        span?.SetTag("flowforge.execution.id", executionId.ToString()); span?.SetTag("flowforge.correlation.id", correlationId.ToString());
        span?.SetTag("flowforge.node.id", nodeId.ToString()); span?.SetTag("flowforge.node.type", type.ToString()); span?.SetTag("flowforge.attempt", attempt);
        return span;
    }
    public static void RecordNode(NodeType type, string outcome, TimeSpan duration) => NodeDuration.Record(duration.TotalSeconds,
        new KeyValuePair<string, object?>("node.type", type.ToString()), new KeyValuePair<string, object?>("outcome", outcome));
}
