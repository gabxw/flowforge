using System.Diagnostics.Metrics;
using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace FlowForge.Infrastructure.Runtime;
public sealed class ExecutionMetricsSampler(IDbContextFactory<FlowForgeDbContext> factory, ILogger<ExecutionMetricsSampler> logger) : BackgroundService
{
    private sealed record Sample(long OutboxReady, long Waiting, double OldestReadyAge);
    private Sample? sample;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var meter = new Meter(ExecutionTelemetry.Name);
        _ = meter.CreateObservableGauge<long>("flowforge.outbox.ready", () => ReadLong(x => x.OutboxReady), "{message}");
        _ = meter.CreateObservableGauge<long>("flowforge.execution.waiting", () => ReadLong(x => x.Waiting), "{execution}");
        _ = meter.CreateObservableGauge<double>("flowforge.outbox.oldest.age", () => Volatile.Read(ref sample) is { } s ? [new Measurement<double>(s.OldestReadyAge)] : Array.Empty<Measurement<double>>(), "s");
        while (!stoppingToken.IsCancellationRequested)
        {
            try {
                await using var db = await factory.CreateDbContextAsync(stoppingToken); var now = DateTimeOffset.UtcNow;
                var pending = db.OutboxMessages.AsNoTracking().Where(x => x.PublishedAt == null && x.AvailableAt <= now);
                var count = await pending.LongCountAsync(stoppingToken);
                var oldest = await pending.MinAsync(x => (DateTimeOffset?)x.AvailableAt, stoppingToken);
                var waitingCount = await db.WorkflowExecutions.LongCountAsync(x => x.ResumeAt != null, stoppingToken);
                Volatile.Write(ref sample, new(count, waitingCount, oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds)));
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { Volatile.Write(ref sample, null); logger.LogWarning("Métricas indisponíveis. EventCode={EventCode} ErrorType={ErrorType}", "metrics_unavailable", e.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private IEnumerable<Measurement<long>> ReadLong(Func<Sample, long> read) => Volatile.Read(ref sample) is { } s ? [new Measurement<long>(read(s))] : Array.Empty<Measurement<long>>();
}
