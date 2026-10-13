using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FlowForge.Application.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Domain.Executions;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FlowForge.Infrastructure.Http;
using OpenTelemetry;
using OpenTelemetry.Trace;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace FlowForge.IntegrationTests;
[Collection("Dispatch")]
public sealed class ExecutionTelemetryTests(DispatchFixture fixture, ControlledHttpsServer server) : IClassFixture<ControlledHttpsServer>
{
    private static ActivityListener Listener(ConcurrentQueue<Activity> spans) => new() {
        ShouldListenTo = source => source.Name == ExecutionTelemetry.Name,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = a => spans.Enqueue(a)
    };
    [Fact]
    public async Task Stored_parent_reaches_real_Rabbit_consumer_and_node_after_acceptance_scope_ends()
    {
        await fixture.ResetAsync();var spans=new ConcurrentQueue<Activity>();using var listener=Listener(spans);ActivitySource.AddActivityListener(listener);
        var owner=Guid.NewGuid();var now=DateTimeOffset.UtcNow;await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner,now);
        var workflow=new Workflow(Guid.NewGuid(),owner,"Trace fixture",now);var version=workflow.CreateDraft(Guid.NewGuid(),now);var trigger=Guid.NewGuid();var log=Guid.NewGuid();
        workflow.ReplaceDraftGraph([new(version.Id,trigger,NodeType.WebhookTrigger,new WebhookTriggerConfiguration()),new(version.Id,log,NodeType.Log,new LogConfiguration("Fictício"))],
            [new(Guid.NewGuid(),version.Id,trigger,log,"next")],now);workflow.PublishDraft(now);await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow);
        string parent;ActivityTraceId trace;var request=new ExecutionRequest(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),workflow.Id,owner);
        using(var accepted=ExecutionTelemetry.Start("test.accept",ActivityKind.Server)!) {parent=accepted.Id!;trace=accepted.TraceId;await new PostgresExecutionStore(fixture.Factory).RequestAsync(request);}
        var outbox=new PostgresExecutionOutboxStore(fixture.Factory);var claim=await outbox.TryClaimAsync(TimeSpan.FromSeconds(30));Assert.NotNull(claim);Assert.Equal(parent,claim.TraceParent);
        await outbox.ReleaseAsync(claim,TimeSpan.Zero);
        await using var publisher=new RabbitExecutionPublisher(fixture.RabbitOptions);Assert.True(await new OutboxDispatcher(outbox,publisher).DispatchOneAsync());
        using var stop=new CancellationTokenSource();var consumer=new RabbitExecutionConsumer(fixture.RabbitOptions,fixture.Handler(),NullLogger<RabbitExecutionConsumer>.Instance);
        var running=consumer.RunSessionAsync(stop.Token);
        try {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while((await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId,owner))?.Status!=WorkflowExecutionStatus.Succeeded)await Task.Delay(50,deadline.Token);
        } finally {await stop.CancelAsync();try{await running;}catch(OperationCanceledException){}}
        Assert.Contains(spans,a=>a.DisplayName=="workflow.process"&&a.TraceId==trace);
        Assert.Contains(spans,a=>a.DisplayName=="workflow.node"&&a.TraceId==trace);
        Assert.DoesNotContain(spans.SelectMany(a=>a.TagObjects),t=>t.Key.Contains("payload")||t.Key.Contains("password"));
    }
    [Fact]
    public void Metrics_use_bounded_dimensions_and_traces_remove_unknown_attributes()
    {
        var measurements=new List<string>();using var listener=new MeterListener();
        listener.InstrumentPublished=(instrument,l)=>{if(instrument.Name=="flowforge.node.active.duration")l.EnableMeasurementEvents(instrument);};
        listener.SetMeasurementEventCallback<double>((_,_,tags,_)=>{foreach(var tag in tags)measurements.Add(tag.Key);});listener.Start();
        ExecutionTelemetry.RecordNode(NodeType.Log,"succeeded",TimeSpan.FromMilliseconds(10));
        Assert.Equal(["node.type","outcome"],measurements);
        using var activity=new Activity("safe").Start();activity.SetTag("http.route","/hooks/{id}");activity.SetTag("url.full","https://example.com/private?token=sentinel");activity.SetTag("request.body","sentinel");activity.SetStatus(ActivityStatusCode.Error,"sentinel");
        new SafeActivityProcessor().OnEnd(activity);Assert.Null(activity.GetTagItem("url.full"));Assert.Null(activity.GetTagItem("request.body"));Assert.Equal("/hooks/{id}",activity.GetTagItem("http.route"));Assert.Equal("operation_failed",activity.StatusDescription);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("bad-parent")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-01")]
    public void Invalid_trace_metadata_does_not_become_propagated_context(string? parent) => Assert.Null(ExecutionTelemetry.ValidParent(parent));
    private sealed class Capture(ConcurrentQueue<Activity> spans) : BaseProcessor<Activity> { public override void OnEnd(Activity activity)=>spans.Enqueue(activity); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Webhook_HTTP_spans_failure_and_durable_retry_keep_trace_and_exclude_payload(bool retry)
    {
        await fixture.ResetAsync();var spans=new ConcurrentQueue<Activity>();
        var services = new ServiceCollection();
        services.AddFlowForgeTelemetry(new ConfigurationBuilder().Build(), "flowforge-test")
            .WithTracing(t => t.AddProcessor(new Capture(spans)));
        using var container = services.BuildServiceProvider();
        _ = container.GetRequiredService<TracerProvider>();
        var owner=Guid.NewGuid();var now=DateTimeOffset.UtcNow;await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner,now);
        var path="/telemetry-"+Guid.NewGuid().ToString("N");server.Plans[path]=new([(retry?503:400,null),(200,null)]);
        var workflow=new Workflow(Guid.NewGuid(),owner,"HTTP telemetry",now);var version=workflow.CreateDraft(Guid.NewGuid(),now);var trigger=Guid.NewGuid();var http=Guid.NewGuid();var log=Guid.NewGuid();
        workflow.ReplaceDraftGraph([new(version.Id,trigger,NodeType.WebhookTrigger,new WebhookTriggerConfiguration()),
            new(version.Id,http,NodeType.HttpRequest,new HttpRequestConfiguration(new(server.Origin+path),HttpRequestMethod.Get,retry?2:1)),
            new(version.Id,log,NodeType.Log,new LogConfiguration("Fictício"))],
            [new(Guid.NewGuid(),version.Id,trigger,http,"next"),new(Guid.NewGuid(),version.Id,http,log,"next")],now);
        workflow.PublishDraft(now);await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow);
        var hooks=new PostgresWebhookStore(fixture.Factory,fixture.Protection,FlowForge.Application.Webhooks.WebhookAcceptanceOptions.Default);
        var hook=await hooks.CreateAsync(workflow.Id,owner);FlowForge.Application.Webhooks.WebhookReceipt receipt;ActivityTraceId trace;
        using(var root=ExecutionTelemetry.Start("webhook.accept",ActivityKind.Server)!){trace=root.TraceId;receipt=await hooks.AcceptAsync(hook.Endpoint.Id,hook.Secret,FlowForge.Application.Webhooks.WebhookPayload.Parse("{\"fictitious\":true}"u8.ToArray()),null);}
        var options=new HttpNodeOptions([server.Origin]);var httpExecutor=new HttpRequestNodeExecutor(new PostgresCredentialStore(fixture.Factory,fixture.CredentialProtection),
            new HttpDestinationPolicy(options,(_,_)=>Task.FromResult(new[]{IPAddress.Parse("8.8.8.8")})),server.Transport(),options);
        var handler=new ExecutionMessageHandler(new PostgresExecutionInboxStore(fixture.Factory),fixture.Engine([new TriggerNodeExecutor(),httpExecutor,new LogNodeExecutor()]));
        await using var publisher=new RabbitExecutionPublisher(fixture.RabbitOptions);var dispatcher=new OutboxDispatcher(new PostgresExecutionOutboxStore(fixture.Factory),publisher);
        using var stop=new CancellationTokenSource();var running=new RabbitExecutionConsumer(fixture.RabbitOptions,handler,NullLogger<RabbitExecutionConsumer>.Instance).RunSessionAsync(stop.Token);
        try {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while(true){await dispatcher.DispatchOneAsync(deadline.Token);var execution=await new PostgresExecutionStore(fixture.Factory).GetAsync(receipt.ExecutionId,owner);
                if(execution?.Status is WorkflowExecutionStatus.Succeeded or WorkflowExecutionStatus.Failed){Assert.Equal(retry?WorkflowExecutionStatus.Succeeded:WorkflowExecutionStatus.Failed,execution.Status);break;}await Task.Delay(50,deadline.Token);}
        }finally{await stop.CancelAsync();try{await running;}catch(OperationCanceledException){}}
        Assert.Contains(spans,a=>a.Kind==ActivityKind.Client&&a.TraceId==trace);
        Assert.Contains(server.TraceHeaders,h=>h.Path==path);
        Assert.All(server.TraceHeaders.Where(h=>h.Path==path),h=>{Assert.False(h.HasTrace);Assert.False(h.HasBaggage);});
        Assert.Contains(spans,a=>a.DisplayName=="workflow.node"&&a.TraceId==trace&&a.Status==ActivityStatusCode.Error);
        Assert.DoesNotContain(spans.SelectMany(a=>a.TagObjects),t=>t.Key=="url.full"||t.Key=="request.body"||t.Key=="http.request.header.authorization");
        if(retry){var history=await new PostgresExecutionStore(fixture.Factory).HistoryAsync(receipt.ExecutionId,owner);Assert.Equal(2,history!.Nodes.Single(n=>n.NodeId==http).AttemptCount);}
    }

}
