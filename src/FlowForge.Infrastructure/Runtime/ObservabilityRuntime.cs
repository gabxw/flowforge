using System.Diagnostics;
using FlowForge.Application.Executions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
namespace FlowForge.Infrastructure.Runtime;
public sealed class SafeActivityProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
        "http.request.method", "http.response.status_code", "http.route", "server.address", "server.port", "url.scheme", "network.protocol.version", "error.type",
        "messaging.system", "messaging.destination.name", "flowforge.execution.id", "flowforge.correlation.id", "flowforge.node.id", "flowforge.node.type",
        "flowforge.attempt", "flowforge.outcome", "flowforge.message.id", "flowforge.wait.seconds"
    };
    public override void OnEnd(Activity activity)
    {
        foreach (var tag in activity.TagObjects.ToArray()) if (!Allowed.Contains(tag.Key)) activity.SetTag(tag.Key, null);
        if (activity.Status == ActivityStatusCode.Error) activity.SetStatus(ActivityStatusCode.Error, "operation_failed");
    }
}
public static class ObservabilityRuntime
{
    public static OpenTelemetryBuilder AddFlowForgeTelemetry(this IServiceCollection services, IConfiguration configuration, string service)
    {
        // Apenas W3C TraceContext: não extrai/propaga baggage fornecido por clientes.
        // HttpClient usa seu propagador sem saída; o SDK não reinjeta contexto composto.
        Sdk.SetDefaultTextMapPropagator(new OpenTelemetry.Context.Propagation.TraceContextPropagator());
        var endpoint = Endpoint(configuration);
        return services.AddOpenTelemetry().ConfigureResource(r => r.AddService(service))
            .WithTracing(t => {
                t.AddSource(ExecutionTelemetry.Name).AddHttpClientInstrumentation(o => o.RecordException = false).AddProcessor(new SafeActivityProcessor());
                if (endpoint is not null) t.AddOtlpExporter(o => o.Endpoint = endpoint);
            })
            .WithMetrics(m => {
                m.AddMeter(ExecutionTelemetry.Name).AddHttpClientInstrumentation();
                if (endpoint is not null) m.AddOtlpExporter(o => o.Endpoint = endpoint);
            });
    }
    public static void AddFlowForgeTelemetryLogs(this ILoggingBuilder logging, IConfiguration configuration, string service)
    {
        logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        // Exporta só eventos próprios; não exporta corpo/query/exceções de frameworks.
        logging.AddFilter<OpenTelemetryLoggerProvider>("", LogLevel.None);
        logging.AddFilter<OpenTelemetryLoggerProvider>("FlowForge", LogLevel.Information);
        var endpoint = Endpoint(configuration);
        if (endpoint is not null) logging.AddOpenTelemetry(o => {
            o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(service));
            o.IncludeScopes = true; o.IncludeFormattedMessage = true;
            o.AddOtlpExporter(e => e.Endpoint = endpoint);
        });
    }
    private static Uri? Endpoint(IConfiguration configuration)
    {
        var value = configuration["FlowForge:Telemetry:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new RuntimeConfigurationException();
        return uri;
    }
}
