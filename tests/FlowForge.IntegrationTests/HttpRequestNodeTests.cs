using System.Net;
using System.Text;
using System.Text.Json;
using FlowForge.Application.Credentials;
using FlowForge.Application.Executions;
using FlowForge.Domain.Credentials;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Http;
using Xunit;
namespace FlowForge.IntegrationTests;
public sealed class HttpRequestNodeTests(ControlledHttpsServer server) : IClassFixture<ControlledHttpsServer>
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    private NodeRunContext Context(string path, HttpRequestMethod method = HttpRequestMethod.Get, CredentialReference? reference = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new WorkflowNode(Guid.NewGuid(), Guid.NewGuid(), NodeType.HttpRequest,
            new HttpRequestConfiguration(new Uri(server.Origin + path), method), credential: reference), Json("{\"value\":42}"), Owner);
    private HttpRequestNodeExecutor Executor(CredentialStub? credentials = null, TimeSpan? timeout = null) {
        var options = new HttpNodeOptions([server.Origin], timeout);
        return new(credentials ?? new CredentialStub(null), new HttpDestinationPolicy(options, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })), server.Transport(), options);
    }
    [Fact]
    public async Task Transport_establishes_a_real_tls_connection_without_disabling_certificate_validation()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/ok");
        var result = await server.Transport().SendAsync(request, new ApprovedHttpDestination(HttpsOrigin.Parse(server.Origin), [IPAddress.Parse("8.8.8.8")]), 32768, default);
        Assert.Equal(200, result.StatusCode);
    }

    [Theory][InlineData(HttpRequestMethod.Post)][InlineData(HttpRequestMethod.Get)][InlineData(HttpRequestMethod.Delete)]
    public async Task Request_received_before_connection_abort_is_not_repeated_by_the_http_transport(HttpRequestMethod method)
    {
        var before = server.Counts.GetValueOrDefault("/disconnect");
        var result = await Executor().ExecuteAsync(Context("/disconnect", method), default);
        Assert.Equal(ExecutionFailureCode.HttpTransportFailed, result.ErrorCode);
        Assert.Equal(before + 1, server.Counts["/disconnect"]);
    }

    [Fact]
    public async Task Graceful_tls_eof_cannot_start_a_second_http_session_or_repeat_a_bodyless_request()
    {
        // EOF ordenado difere de RST: o handler padrão pode considerá-lo retryable para requests sem body.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var received = 0;
        var serving = Task.Run(async () => {
            try {
                while (!stop.IsCancellationRequested) {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var tls = new System.Net.Security.SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions { ServerCertificate = server.CertificateForRawServer }, stop.Token);
                    var bytes = new byte[4096]; var header = "";
                    while (!header.Contains("\r\n\r\n", StringComparison.Ordinal)) {
                        var count = await tls.ReadAsync(bytes, stop.Token); if (count == 0) break;
                        header += Encoding.ASCII.GetString(bytes, 0, count);
                    }
                    Interlocked.Increment(ref received); await tls.ShutdownAsync();
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var origin = "https://flowforge-http.test:" + port;
        var options = new HttpNodeOptions([origin]); var sockets = 0;
        var transport = new PinnedHttpTransport(async (_, ct) => {
            Interlocked.Increment(ref sockets); var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct); return new System.Net.Sockets.NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }, server.Trust());
        try {
            var context = new NodeRunContext(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), NodeType.HttpRequest,
                new HttpRequestConfiguration(new(origin), HttpRequestMethod.Delete)), Json("{}"), Owner);
            var result = await new HttpRequestNodeExecutor(new CredentialStub(null), new(options, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })), transport, options).ExecuteAsync(context, default);
            Assert.Equal(ExecutionFailureCode.HttpTransportFailed, result.ErrorCode); Assert.Equal(1, sockets); Assert.Equal(1, received);
        } finally { await stop.CancelAsync(); listener.Stop(); await serving; }
    }

    [Fact]
    public async Task Default_connector_uses_the_supplied_ip_even_when_the_tls_hostname_is_not_resolved()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/ok");
        // Aprovação fabricada apenas no teste; a política real rejeita loopback antes de chegar a este transporte.
        var result = await new PinnedHttpTransport(server.Trust()).SendAsync(request,
            new ApprovedHttpDestination(HttpsOrigin.Parse(server.Origin), [IPAddress.Loopback]), 32768, default);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Protected_host_override_is_rejected_before_connecting()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/ok"); request.Headers.Host = "localhost";
        var before = server.ApprovedConnections.Count;
        await Assert.ThrowsAsync<HttpDestinationDeniedException>(() => server.Transport().SendAsync(request,
            new ApprovedHttpDestination(HttpsOrigin.Parse(server.Origin), [IPAddress.Parse("8.8.8.8")]), 32768, default));
        Assert.Equal(before, server.ApprovedConnections.Count);
    }

    [Fact]
    public async Task Post_sends_context_as_json_with_original_host_tls_name_and_no_arbitrary_headers()
    {
        var secret = new CredentialSecret("ficticio-" + Guid.NewGuid().ToString("N"));
        var metadata = new Credential(Guid.NewGuid(), Owner, "Test", CredentialType.ApiKey, HttpsOrigin.Parse(server.Origin), "X-Api-Key", DateTimeOffset.UnixEpoch).Snapshot;
        var context = Context("/ok", HttpRequestMethod.Post, new(metadata.Id, Owner));
        var result = await Executor(new(new(metadata, secret))).ExecuteAsync(context, default);
        Assert.Null(result.ErrorCode); Assert.Equal(1, result.CredentialRevisionUsed);
        Assert.Equal(200, result.Output!.Value.GetProperty("statusCode").GetInt32());
        Assert.Equal(42, result.Output.Value.GetProperty("body").GetProperty("input").GetProperty("value").GetInt32());
        Assert.DoesNotContain(secret.Value, result.Output.Value.GetRawText(), StringComparison.Ordinal);
        var observed = server.Observations.Last(); Assert.Equal(secret.Value, observed.ApiKey); Assert.Null(observed.Authorization);
        Assert.Equal("flowforge-http.test:" + server.Port, observed.Host); Assert.Equal("flowforge-http.test", observed.Sni);
        Assert.Equal(context.Input.GetRawText(), Encoding.UTF8.GetString(observed.Body));
        Assert.Equal(IPAddress.Parse("8.8.8.8"), server.ApprovedConnections.Last().Address);
        Assert.False(Executor().CanReplayAfterInterruption);
    }
    [Theory][InlineData(HttpRequestMethod.Get)][InlineData(HttpRequestMethod.Head)][InlineData(HttpRequestMethod.Delete)][InlineData(HttpRequestMethod.Options)]
    public async Task Read_and_control_methods_do_not_forward_context_as_body(HttpRequestMethod method)
    {
        var result = await Executor().ExecuteAsync(Context("/ok", method), default);
        Assert.Null(result.ErrorCode); Assert.Empty(server.Observations.Last().Body);
    }
    [Theory][InlineData("/status/400", ExecutionFailureCode.HttpRemoteFailure)][InlineData("/status/500", ExecutionFailureCode.HttpRemoteFailure)]
    [InlineData("/redirect", ExecutionFailureCode.HttpRemoteFailure)]
    [InlineData("/large-fixed", ExecutionFailureCode.HttpResponseLimitExceeded)][InlineData("/large-stream", ExecutionFailureCode.HttpResponseLimitExceeded)]
    [InlineData("/bad-json", ExecutionFailureCode.HttpResponseInvalid)][InlineData("/duplicate-json", ExecutionFailureCode.HttpResponseInvalid)]
    [InlineData("/invalid-utf8", ExecutionFailureCode.HttpResponseInvalid)][InlineData("/encoded", ExecutionFailureCode.HttpResponseInvalid)][InlineData("/text", ExecutionFailureCode.HttpResponseInvalid)]
    public async Task Remote_failure_redirect_invalid_content_and_overflow_have_safe_codes_without_body(string path, ExecutionFailureCode code)
    {
        var before = server.Counts.GetValueOrDefault(path);
        var result = await Executor().ExecuteAsync(Context(path), default);
        Assert.Equal(code, result.ErrorCode); Assert.Null(result.Output);
        Assert.Equal(before + 1, server.Counts[path]);
        Assert.DoesNotContain(server.ApprovedConnections, endpoint => !HttpDestinationPolicy.IsPublic(endpoint.Address));
    }
    [Theory][InlineData("/slow")][InlineData("/slow-body")]
    public async Task Deadline_covers_headers_and_streaming_body(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await Executor(timeout: TimeSpan.FromMilliseconds(200)).ExecuteAsync(Context(path), default);
        Assert.Equal(ExecutionFailureCode.NodeTimeout, result.ErrorCode); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }
    [Fact]
    public async Task External_cancellation_is_propagated_and_no_retry_is_added()
    {
        var before = server.Counts.GetValueOrDefault("/slow");
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor().ExecuteAsync(Context("/slow"), ct.Token));
        Assert.Equal(before + 1, server.Counts["/slow"]);
    }
    [Theory][InlineData("/echo")][InlineData("/echo-escaped")][InlineData("/echo-name")]
    public async Task Literal_and_json_escaped_credential_echo_is_rejected_before_output(string path)
    {
        var metadata = new Credential(Guid.NewGuid(), Owner, "Test", CredentialType.BearerToken, HttpsOrigin.Parse(server.Origin), null, DateTimeOffset.UnixEpoch).Snapshot;
        var result = await Executor(new(new(metadata, new CredentialSecret("ficticio-" + Guid.NewGuid().ToString("N")))))
            .ExecuteAsync(Context(path, reference: new(metadata.Id, Owner)), default);
        Assert.Equal(ExecutionFailureCode.HttpResponseSensitive, result.ErrorCode); Assert.Null(result.Output); Assert.Equal(1, result.CredentialRevisionUsed);
    }
    [Fact]
    public async Task Another_owner_and_missing_or_revoked_reference_do_not_reach_the_transport()
    {
        var store = new CredentialStub(null); var before = server.Counts.GetValueOrDefault("/ok");
        Assert.Equal(ExecutionFailureCode.CredentialUnavailable, (await Executor(store).ExecuteAsync(Context("/ok", reference: new(Guid.NewGuid(), Guid.NewGuid())), default)).ErrorCode);
        Assert.Equal(0, store.Calls);
        Assert.Equal(ExecutionFailureCode.CredentialUnavailable, (await Executor(store).ExecuteAsync(Context("/ok", reference: new(Guid.NewGuid(), Owner)), default)).ErrorCode);
        Assert.Equal(1, store.Calls); Assert.Equal(before, server.Counts.GetValueOrDefault("/ok"));
    }
    [Fact]
    public async Task Dns_rebinding_cannot_change_the_approved_connection_and_no_second_lookup_occurs()
    {
        var calls = 0; var options = new HttpNodeOptions([server.Origin]);
        var policy = new HttpDestinationPolicy(options, (_, _) => Task.FromResult(new[] { IPAddress.Parse(++calls == 1 ? "8.8.8.8" : "127.0.0.1") }));
        var result = await new HttpRequestNodeExecutor(new CredentialStub(null), policy, server.Transport(), options).ExecuteAsync(Context("/ok"), default);
        Assert.Null(result.ErrorCode); Assert.Equal(1, calls); Assert.Equal(IPAddress.Parse("8.8.8.8"), server.ApprovedConnections.Last().Address);
    }
    [Fact]
    public async Task Tls_still_requires_a_trusted_certificate_and_matching_original_hostname()
    {
        var options = new HttpNodeOptions([server.Origin]); var policy = new HttpDestinationPolicy(options, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
        var untrusted = await new HttpRequestNodeExecutor(new CredentialStub(null), policy, server.Transport(false), options).ExecuteAsync(Context("/ok"), default);
        Assert.Equal(ExecutionFailureCode.HttpTransportFailed, untrusted.ErrorCode);
        var wrongOrigin = "https://other-flowforge.test:" + server.Port; var wrongOptions = new HttpNodeOptions([wrongOrigin]);
        var context = new NodeRunContext(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), NodeType.HttpRequest,
            new HttpRequestConfiguration(new Uri(wrongOrigin + "/ok"), HttpRequestMethod.Get)), Json("{}"), Owner);
        var wrong = await new HttpRequestNodeExecutor(new CredentialStub(null), new(wrongOptions, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })), server.Transport(), wrongOptions).ExecuteAsync(context, default);
        Assert.Equal(ExecutionFailureCode.HttpTransportFailed, wrong.ErrorCode);
    }
    [Fact]
    public async Task Empty_success_has_explicit_null_body()
    {
        var result = await Executor().ExecuteAsync(Context("/empty"), default);
        Assert.Null(result.ErrorCode); Assert.Equal(JsonValueKind.Null, result.Output!.Value.GetProperty("body").ValueKind);
    }
    internal sealed class CredentialStub(ResolvedCredential? resolved) : ICredentialStore
    {
        public int Calls { get; private set; }
        public Task<ResolvedCredential?> ResolveAsync(Guid id, Guid owner, HttpsOrigin origin, CancellationToken ct = default) { Calls++; return Task.FromResult(resolved); }
        public Task CreateAsync(CredentialSnapshot metadata, CredentialSecret secret, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialSnapshot?> GetAsync(Guid id, Guid owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CredentialSnapshot>> ListAsync(Guid owner, int offset, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialSnapshot> RotateAsync(Guid id, Guid owner, int expectedRevision, CredentialSecret secret, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialSnapshot> RevokeAsync(Guid id, Guid owner, int expectedRevision, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
