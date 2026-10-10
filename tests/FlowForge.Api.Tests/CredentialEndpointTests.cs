using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;
namespace FlowForge.Api.Tests;
[Collection("Workflow API")]
public sealed class CredentialEndpointTests(WorkflowApiFixture fixture)
{
    private static string Secret() => "ficticio-" + Guid.NewGuid().ToString("N");
    private static async Task<JsonObject> CreateCredential(HttpClient client, string secret) {
        using var response = await client.PostAsJsonAsync("/api/credentials", new { name = "Test API", type = "bearerToken", origin = "https://api.example.test", secret });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal); return await Body(response);
    }
    [Fact]
    public async Task Create_list_get_rotate_and_revoke_return_only_metadata_with_revision_control()
    {
        using var api = fixture.CreateApi(); using var client = api.CreateClient(); var secret = Secret(); var next = Secret();
        var credential = await CreateCredential(client, secret); var path = "/api/credentials/" + credential["id"]!.GetValue<string>();
        using var get = await client.GetAsync(path); Assert.Equal(credential.ToJsonString(), (await Body(get)).ToJsonString());
        using var list = await client.GetAsync("/api/credentials"); Assert.Single((await Body(list))["items"]!.AsArray());
        using var rotate = await client.PostAsJsonAsync(path + "/rotate-secret", new { expectedRevision = 1, secret = next });
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode); Assert.Equal(2, (await Body(rotate))["revision"]!.GetValue<int>());
        using var stale = await client.PostAsJsonAsync(path + "/rotate-secret", new { expectedRevision = 1, secret }); await Problem(stale, HttpStatusCode.Conflict);
        using var revoke = await client.PostAsJsonAsync(path + "/revoke", new { expectedRevision = 2 }); var revoked = await Body(revoke); Assert.NotNull(revoked["revokedAt"]);
        using var repeat = await client.PostAsJsonAsync(path + "/revoke", new { expectedRevision = 3 }); Assert.Equal(revoked.ToJsonString(), (await Body(repeat)).ToJsonString());
        using var rotateRevoked = await client.PostAsJsonAsync(path + "/rotate-secret", new { expectedRevision = 3, secret = next }); await Problem(rotateRevoked, HttpStatusCode.NotFound);
        foreach (var body in new[] { await get.Content.ReadAsStringAsync(), await list.Content.ReadAsStringAsync(), await rotate.Content.ReadAsStringAsync(), await revoke.Content.ReadAsStringAsync() })
        { Assert.DoesNotContain(secret, body, StringComparison.Ordinal); Assert.DoesNotContain(next, body, StringComparison.Ordinal); Assert.DoesNotContain("protectedValue", body, StringComparison.Ordinal); Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase); }
    }
    [Fact]
    public async Task Another_owner_cannot_read_rotate_revoke_or_attach_credentials()
    {
        using var first = fixture.CreateApi(); using var client = first.CreateClient(); var credential = await CreateCredential(client, Secret());
        using var other = fixture.CreateApi(); using var otherClient = other.CreateClient(); var path = "/api/credentials/" + credential["id"]!.GetValue<string>();
        using var get = await otherClient.GetAsync(path); await Problem(get, HttpStatusCode.NotFound);
        using var rotate = await otherClient.PostAsJsonAsync(path + "/rotate-secret", new { expectedRevision = 1, secret = Secret() }); await Problem(rotate, HttpStatusCode.NotFound);
        using var revoke = await otherClient.PostAsJsonAsync(path + "/revoke", new { expectedRevision = 1 }); await Problem(revoke, HttpStatusCode.NotFound);
        var (workflowPath, workflow) = await Create(otherClient);
        using var save = await otherClient.PutAsJsonAsync(workflowPath + "/draft", Draft(Revision(workflow), credential["id"]!.GetValue<string>())); await Problem(save, HttpStatusCode.NotFound);
        using var unchanged = await otherClient.GetAsync(workflowPath); Assert.Equal(workflow.ToJsonString(), (await Body(unchanged)).ToJsonString());
    }
    private static object Draft(int revision, string id, string url = "https://api.example.test/ok") {
        var trigger = Guid.NewGuid(); var http = Guid.NewGuid();
        return new { expectedRevision = revision, nodes = new object[] {
            new { nodeId = trigger, configuration = new { type = "webhookTrigger" } },
            new { nodeId = http, credentialId = id, configuration = new { type = "httpRequest", url, method = "post" } }
        }, connections = new[] { new { id = Guid.NewGuid(), sourceNodeId = trigger, targetNodeId = http, sourcePort = "next" } } };
    }
    [Fact]
    public async Task Origin_mismatch_missing_and_revoked_credentials_are_rejected_before_draft_or_publication_change()
    {
        using var api = fixture.CreateApi(); using var client = api.CreateClient(); var credential = await CreateCredential(client, Secret());
        var id = credential["id"]!.GetValue<string>(); var (path, workflow) = await Create(client);
        using var mismatch = await client.PutAsJsonAsync(path + "/draft", Draft(Revision(workflow), id, "https://other.example.test")); await Problem(mismatch, HttpStatusCode.NotFound);
        using var missing = await client.PutAsJsonAsync(path + "/draft", Draft(Revision(workflow), Guid.NewGuid().ToString())); await Problem(missing, HttpStatusCode.NotFound);
        using var save = await client.PutAsJsonAsync(path + "/draft", Draft(Revision(workflow), id)); Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var revoke = await client.PostAsJsonAsync("/api/credentials/" + id + "/revoke", new { expectedRevision = 1 }); Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(save)) }); await Problem(publish, HttpStatusCode.NotFound);
    }
    [Theory]
    [InlineData("{\"name\":\"API\",\"type\":1,\"origin\":\"https://api.example.test\",\"secret\":\"ficticio-1234567890\"}")]
    [InlineData("{\"name\":\"API\",\"type\":\"bearerToken\",\"origin\":\"https://api.example.test\",\"secret\":\"short\"}")]
    [InlineData("{\"name\":\"API\",\"type\":\"bearerToken\",\"origin\":\"https://api.example.test\",\"secret\":\"ficticio-1234567890\",\"ownerUserId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"name\":\"API\",\"type\":\"apiKey\",\"origin\":\"https://api.example.test\",\"headerName\":\"Host\",\"secret\":\"ficticio-1234567890\"}")]
    [InlineData("{\"name\":\"API\",\"type\":\"bearerToken\",\"origin\":\"https://api.example.test/path\",\"secret\":\"ficticio-1234567890\"}")]
    [InlineData("{\"name\":\"API\",\"type\":\"bearerToken\",\"origin\":\"https://api.example.test\",\"secret\":null}")]
    public async Task Invalid_secret_owner_fields_headers_type_and_origin_do_not_create_a_credential(string body)
    {
        using var api = fixture.CreateApi(); using var client = api.CreateClient();
        using var response = await client.PostAsync("/api/credentials", new StringContent(body, Encoding.UTF8, "application/json")); await Problem(response, HttpStatusCode.BadRequest);
        using var list = await client.GetAsync("/api/credentials"); Assert.Empty((await Body(list))["items"]!.AsArray());
    }
    [Theory][InlineData("Authorization")][InlineData("Host")][InlineData("Cookie")][InlineData("Proxy-Authorization")]
    public async Task Http_configuration_does_not_accept_arbitrary_or_secret_headers(string header)
    {
        using var api = fixture.CreateApi(); using var client = api.CreateClient(); var credential = await CreateCredential(client, Secret());
        var (path, workflow) = await Create(client);
        var draft = System.Text.Json.JsonSerializer.SerializeToNode(Draft(Revision(workflow), credential["id"]!.GetValue<string>()))!;
        draft["nodes"]![1]!["configuration"]!["headers"] = new JsonObject { [header] = "ficticio-1234567890" };
        using var response = await client.PutAsJsonAsync(path + "/draft", draft); await Problem(response, HttpStatusCode.BadRequest);
    }
    [Fact]
    public async Task Credential_request_limits_utf8_and_missing_revoke_revision_are_enforced()
    {
        using var api = fixture.CreateApi(); using var client = api.CreateClient();
        using var tooLarge = await client.PostAsync("/api/credentials", new StringContent(new string('x', 32769), Encoding.UTF8, "application/json")); await Problem(tooLarge, HttpStatusCode.RequestEntityTooLarge);
        using var utf16 = await client.PostAsync("/api/credentials", new StringContent("{}", Encoding.Unicode, "application/json")); await Problem(utf16, HttpStatusCode.UnsupportedMediaType);
        var credential = await CreateCredential(client, Secret());
        using var missing = await client.PostAsJsonAsync("/api/credentials/" + credential["id"]!.GetValue<string>() + "/revoke", new { }); await Problem(missing, HttpStatusCode.BadRequest);
    }
    [Fact]
    public async Task Unconfigured_protection_fails_closed_and_health_stays_available()
    {
        using var api = fixture.CreateApi(protectWebhookInput: false); using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/credentials", new { name = "API", type = "bearerToken", origin = "https://api.example.test", secret = Secret() });
        await Problem(response, HttpStatusCode.ServiceUnavailable);
        using var health = await client.GetAsync("/health/live"); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
    [Fact]
    public async Task Credential_values_are_absent_from_operational_logs_and_trace_tags()
    {
        var logs = new CaptureLogs(); var tags = new ConcurrentQueue<string>();
        using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { foreach (var t in a.TagObjects) tags.Enqueue(t.Value?.ToString() ?? ""); } };
        ActivitySource.AddActivityListener(listener);
        using var api = fixture.CreateApi().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ILoggerProvider>(logs)));
        using var client = api.CreateClient(); var secret = Secret(); var next = Secret(); var credential = await CreateCredential(client, secret);
        using var rotate = await client.PostAsJsonAsync("/api/credentials/" + credential["id"]!.GetValue<string>() + "/rotate-secret", new { expectedRevision = 1, secret = next }); Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        Assert.NotEmpty(logs.Values); Assert.NotEmpty(tags);
        foreach (var value in logs.Values.Concat(tags)) { Assert.DoesNotContain(secret, value, StringComparison.Ordinal); Assert.DoesNotContain(next, value, StringComparison.Ordinal); }
    }
    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Values { get; } = new(); public ILogger CreateLogger(string name) => new Capture(Values); public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> values) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => values.Enqueue(formatter(state, exception));
        }
    }
}
