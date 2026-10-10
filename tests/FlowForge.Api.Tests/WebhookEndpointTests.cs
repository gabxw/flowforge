using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;

namespace FlowForge.Api.Tests;
[Collection("Workflow API")]
public sealed class WebhookEndpointTests(WorkflowApiFixture fixture)
{
    private static async Task<(string Path, string Hook, string Secret)> CreateHook(HttpClient client)
    {
        var (path, workflow) = await Create(client);
        var trigger = Guid.NewGuid(); var log = Guid.NewGuid();
        using var saved = await client.PutAsJsonAsync(path + "/draft", new { expectedRevision = Revision(workflow),
            nodes = new object[] { new { nodeId = trigger, configuration = new { type = "webhookTrigger" } },
                new { nodeId = log, configuration = new { type = "log", message = "Evento fictício" } } },
            connections = new[] { new { id = Guid.NewGuid(), sourceNodeId = trigger, targetNodeId = log, sourcePort = "next" } } });
        using var published = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(saved)) });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        using var issued = await client.PostAsync(path + "/webhook", null);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode); Assert.Equal("no-store", issued.Headers.CacheControl!.ToString());
        var body = await Body(issued);
        return (path, "/hooks/" + body["endpoint"]!["id"], body["secret"]!.GetValue<string>());
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, string hook, string? secret, string body = "{}", string? key = null,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, hook) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (secret is not null) request.Headers.TryAddWithoutValidation("X-FlowForge-Webhook-Secret", secret);
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
    [Fact]
    public async Task Webhook_is_durable_without_broker_and_engine_receives_original_protected_payload()
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient();
        var (path, hook, secret) = await CreateHook(client);
        const string input = "{\"event\":42,\"privateToken\":\"ficticio-nao-expor\"}";
        using var accepted = await Send(client, hook, secret, input, "event-42");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var receipt = await Body(accepted); Assert.False(receipt["replayed"]!.GetValue<bool>());
        Assert.DoesNotContain("ficticio-nao-expor", receipt.ToJsonString());
        using var pending = await client.GetAsync(accepted.Headers.Location);
        Assert.Equal("pending", (await Body(pending))["status"]!.GetValue<string>());
        var factory = api.Services.GetRequiredService<IDbContextFactory<FlowForgeDbContext>>();
        var protection = api.Services.GetRequiredService<ExecutionContextProtection>();
        await using var db = await factory.CreateDbContextAsync();
        var id = Guid.Parse(receipt["executionId"]!.ToString());
        var originalInput = await db.Database.SqlQuery<byte[]>($"SELECT trigger_input_protected AS \"Value\" FROM workflow_executions WHERE id = {id}").SingleAsync();
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT execution_context_protected IS NULL AS \"Value\" FROM workflow_executions WHERE id = {id}").SingleAsync());
        Assert.DoesNotContain("ficticio-nao-expor", Encoding.UTF8.GetString(originalInput));
        var outgoing = await db.Database.SqlQuery<Outgoing>($"SELECT id AS \"Id\", correlation_id AS \"CorrelationId\", contract_version AS \"ContractVersion\" FROM outbox_messages WHERE execution_id = {id}").SingleAsync();
        var message = new ExecutionRequestedMessage(outgoing.ContractVersion, outgoing.Id, id, outgoing.CorrelationId);
        var handler = new ExecutionMessageHandler(new PostgresExecutionInboxStore(factory),
            new SequentialExecutionEngine(new PostgresExecutionEngineStore(factory, protection), [new TriggerNodeExecutor(), new LogNodeExecutor()], EngineOptions.Default));
        Assert.Equal(MessageDisposition.Completed, await handler.HandleAsync(message));
        using var result = await client.GetAsync(accepted.Headers.Location); Assert.Equal("succeeded", (await Body(result))["status"]!.GetValue<string>());
        using var nodesResponse = await client.GetAsync(accepted.Headers.Location + "/nodes");
        var nodes = await nodesResponse.Content.ReadAsStringAsync(); Assert.DoesNotContain("ficticio-nao-expor", nodes);
        var finalContext = await db.Database.SqlQuery<byte[]>($"SELECT execution_context_protected AS \"Value\" FROM workflow_executions WHERE id = {id}").SingleAsync();
        Assert.Equal(42, protection.Unprotect(finalContext, id).GetProperty("event").GetInt32());
        using var metadata = await client.GetAsync(path + "/webhook"); var publicMetadata = await metadata.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, publicMetadata); Assert.DoesNotContain("secretHash", publicMetadata); Assert.DoesNotContain("ownerUserId", publicMetadata);
        using var again = await Send(client, hook, secret, input, "event-42");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode); var repeated = await Body(again);
        Assert.Equal(id.ToString(), repeated["executionId"]!.ToString()); Assert.True(repeated["replayed"]!.GetValue<bool>());
    }
    [Fact]
    public async Task Concurrent_duplicate_keys_return_one_execution_and_changed_body_conflicts()
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient(); var (_, hook, secret) = await CreateHook(client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Send(client, hook, secret, "{\"n\":1}", "same-event")));
        var ids = new List<string>();
        foreach (var response in responses) { using (response) { Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); ids.Add((await Body(response))["executionId"]!.ToString()); } }
        Assert.Single(ids.Distinct());
        using var conflict = await Send(client, hook, secret, "{\"n\":2}", "same-event"); await Problem(conflict, HttpStatusCode.Conflict);
        using var whitespace = await Send(client, hook, secret, "{ \"n\":1}", "same-event"); await Problem(whitespace, HttpStatusCode.Conflict);
        using var noKey = await Send(client, hook, secret, "{\"n\":1}");
        using var otherKey = await Send(client, hook, secret, "{\"n\":1}", "different-event");
        Assert.NotEqual(ids[0], (await Body(noKey))["executionId"]!.ToString()); Assert.NotEqual(ids[0], (await Body(otherKey))["executionId"]!.ToString());
    }
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("bad-secret")][InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Missing_or_wrong_secret_does_not_create_an_execution(string? wrong)
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient(); var (_, hook, secret) = await CreateHook(client);
        using var denied = await Send(client, hook, wrong); var problem = await Problem(denied, HttpStatusCode.NotFound);
        Assert.DoesNotContain(secret, problem.ToJsonString());
        var factory = api.Services.GetRequiredService<IDbContextFactory<FlowForgeDbContext>>(); await using var db = await factory.CreateDbContextAsync();
        var endpoint = Guid.Parse(hook.Split('/').Last()); Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM workflow_executions WHERE webhook_endpoint_id = {endpoint}").SingleAsync());
        using var missing = await Send(client, "/hooks/" + Guid.NewGuid(), secret); await Problem(missing, HttpStatusCode.NotFound);
    }
    [Fact]
    public async Task Disable_rotation_and_archive_revoke_ingress_including_idempotent_replay()
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient(); var (path, hook, secret) = await CreateHook(client);
        using var accepted = await Send(client, hook, secret, "{}", "same"); var id = (await Body(accepted))["executionId"]!.ToString();
        using var disabled = await client.PutAsJsonAsync(path + "/webhook", new { enabled = false }); Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using var rejected = await Send(client, hook, secret, "{}", "same"); await Problem(rejected, HttpStatusCode.NotFound);
        using var rotation = await client.PostAsync(path + "/webhook/rotate-secret", null); var rotated = await Body(rotation);
        var newSecret = rotated["secret"]!.GetValue<string>(); Assert.NotEqual(secret, newSecret);
        Assert.False(rotated["endpoint"]!["enabled"]!.GetValue<bool>());
        using var enabled = await client.PutAsJsonAsync(path + "/webhook", new { enabled = true }); Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var old = await Send(client, hook, secret); await Problem(old, HttpStatusCode.NotFound);
        using var replay = await Send(client, hook, newSecret, "{}", "same"); Assert.Equal(id, (await Body(replay))["executionId"]!.ToString());
        using var workflow = await client.GetAsync(path);
        using var archived = await client.PostAsJsonAsync(path + "/archive", new { expectedRevision = Revision(await Body(workflow)) }); Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        using var afterArchive = await Send(client, hook, newSecret); await Problem(afterArchive, HttpStatusCode.NotFound);
    }
    [Fact]
    public async Task Management_requires_owner_published_workflow_single_endpoint_and_explicit_boolean()
    {
        var owner = Guid.NewGuid(); await using var api = fixture.CreateApi(owner); using var client = api.CreateClient();
        var (draftPath, _) = await Create(client); using var draft = await client.PostAsync(draftPath + "/webhook", null); await Problem(draft, HttpStatusCode.Conflict);
        var (path, _, secret) = await CreateHook(client); using var duplicate = await client.PostAsync(path + "/webhook", null); await Problem(duplicate, HttpStatusCode.Conflict);
        using var absent = await client.PutAsJsonAsync(path + "/webhook", new { }); await Problem(absent, HttpStatusCode.BadRequest);
        using var injected = await client.PutAsJsonAsync(path + "/webhook", new { enabled = false, ownerUserId = Guid.NewGuid() }); await Problem(injected, HttpStatusCode.BadRequest);
        await using var other = fixture.CreateApi(); using var outsider = other.CreateClient();
        using var read = await outsider.GetAsync(path + "/webhook"); await Problem(read, HttpStatusCode.NotFound);
        using var rotate = await outsider.PostAsync(path + "/webhook/rotate-secret", null); await Problem(rotate, HttpStatusCode.NotFound);
        using var disable = await outsider.PutAsJsonAsync(path + "/webhook", new { enabled = false }); await Problem(disable, HttpStatusCode.NotFound);
        using var create = await outsider.PostAsync(path + "/webhook", null); await Problem(create, HttpStatusCode.NotFound);
        using var body = await client.PostAsJsonAsync(path + "/webhook/rotate-secret", new { secret }); await Problem(body, HttpStatusCode.BadRequest);
    }
    [Theory]
    [InlineData("")][InlineData("{invalid}")][InlineData("{\"a\":1,\"a\":2}")][InlineData("{} {}")]
    public async Task Invalid_json_is_rejected_without_private_content_in_problem(string input)
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient(); var (_, hook, secret) = await CreateHook(client);
        using var rejected = await Send(client, hook, secret, input); var problem = await Problem(rejected, HttpStatusCode.BadRequest); Assert.DoesNotContain(secret, problem.ToJsonString());
    }
    [Fact]
    public async Task Content_type_compression_body_size_query_and_key_limits_are_enforced()
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient(); var (_, hook, secret) = await CreateHook(client);
        using var text = await Send(client, hook, secret, "{}", contentType: "text/plain"); await Problem(text, HttpStatusCode.UnsupportedMediaType);
        using var query = await Send(client, hook + "?secret=sentinel-ficticio", secret); var queryProblem = await Problem(query, HttpStatusCode.BadRequest); Assert.DoesNotContain("sentinel", queryProblem.ToJsonString());
        using var big = await Send(client, hook, secret, "\"" + new string('a', 65536) + "\""); await Problem(big, HttpStatusCode.RequestEntityTooLarge);
        using var key = await Send(client, hook, secret, "{}", new string('a', 129)); await Problem(key, HttpStatusCode.BadRequest);
        using var compressedRequest = new HttpRequestMessage(HttpMethod.Post, hook) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        compressedRequest.Headers.Add("X-FlowForge-Webhook-Secret", secret); compressedRequest.Content.Headers.ContentEncoding.Add("gzip");
        using var compressed = await client.SendAsync(compressedRequest); await Problem(compressed, HttpStatusCode.UnsupportedMediaType);
        using var streamRequest = new HttpRequestMessage(HttpMethod.Post, hook) { Content = new StreamContent(new NonSeekableStream(new byte[65537])) };
        streamRequest.Headers.Add("X-FlowForge-Webhook-Secret", secret); streamRequest.Content.Headers.ContentType = new("application/json");
        using var streamed = await client.SendAsync(streamRequest); await Problem(streamed, HttpStatusCode.RequestEntityTooLarge);
        using var duplicateKey = new HttpRequestMessage(HttpMethod.Post, hook) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        duplicateKey.Headers.Add("X-FlowForge-Webhook-Secret", secret); duplicateKey.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "one", "two" });
        using var ambiguous = await client.SendAsync(duplicateKey); await Problem(ambiguous, HttpStatusCode.BadRequest);
    }
    [Fact]
    public async Task Local_rate_limit_bounds_arbitrary_routes_and_returns_retry_after_without_blocking_liveness()
    {
        await using var api = fixture.CreateApi(permits: 2); using var client = api.CreateClient();
        using var first = await Send(client, "/hooks/" + Guid.NewGuid(), null); Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        using var second = await Send(client, "/hooks/" + Guid.NewGuid(), null); Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        using var limited = await Send(client, "/hooks/" + Guid.NewGuid(), null); await Problem(limited, HttpStatusCode.TooManyRequests); Assert.NotNull(limited.Headers.RetryAfter);
        using var live = await client.GetAsync("/health/live"); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
    [Fact]
    public async Task Operational_logs_and_server_activity_tags_omit_secret_and_payload()
    {
        using var sink = new TestLogSink(); var tags = new ConcurrentQueue<string>();
        using var listener = new ActivityListener { ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { foreach (var tag in activity.TagObjects) tags.Enqueue($"{tag.Key}={tag.Value}"); } };
        ActivitySource.AddActivityListener(listener);
        await using var api = fixture.CreateApi().WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(sink)));
        using var client = api.CreateClient(); var (_, hook, secret) = await CreateHook(client);
        using var accepted = await Send(client, hook, secret, "{\"value\":\"ficticio-sentinel-privacidade\"}", "privacy");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var bad = await Send(client, hook, secret, "{\"ficticio-sentinel-erro\":"); await Problem(bad, HttpStatusCode.BadRequest);
        var diagnosticText = string.Join("\n", sink.Lines.Concat(tags));
        Assert.NotEmpty(sink.Lines); Assert.NotEmpty(tags);
        Assert.DoesNotContain(secret, diagnosticText); Assert.DoesNotContain("ficticio-sentinel-privacidade", diagnosticText);
        Assert.DoesNotContain("ficticio-sentinel-erro", diagnosticText);
    }
    [Fact]
    public async Task Runtime_without_supported_persistent_protection_fails_closed_and_liveness_stays_available()
    {
        await using var api = fixture.CreateApi(protectWebhookInput: false); using var client = api.CreateClient();
        using var request = await Send(client, "/hooks/" + Guid.NewGuid(), new string('A', 64)); await Problem(request, HttpStatusCode.ServiceUnavailable);
        using var live = await client.GetAsync("/health/live"); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
    private sealed class TestLogSink : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Lines);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
                => lines.Enqueue(formatter(state, error));
        }
    }
    private sealed record Outgoing(Guid Id, Guid CorrelationId, int ContractVersion);
    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
