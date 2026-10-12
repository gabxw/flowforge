using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlowForge.Application.Credentials;
using FlowForge.Application.Executions;
using FlowForge.Domain.Credentials;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Infrastructure.Http;
public sealed class HttpRequestNodeExecutor(ICredentialStore credentials, HttpDestinationPolicy policy,
    PinnedHttpTransport transport, HttpNodeOptions options) : INodeExecutor
{
    public NodeType Type => NodeType.HttpRequest;
    // Timeout/queda podem ocorrer depois do efeito remoto; nunca repetir por causa de uma redelivery.
    public bool CanReplayAfterInterruption => false;
    public async Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(options.Timeout);
        int? revision = null; var sent = false;
        NodeResult Fail(ExecutionFailureCode code) => NodeResult.Failure(code, revision) with {
            ExternalOutcome = sent ? ExternalOutcome.Unknown : ExternalOutcome.NotStarted };
        try
        {
            var config = (HttpRequestConfiguration)context.Node.Configuration;
            var destination = await policy.ApproveAsync(config.Url, deadline.Token);
            ResolvedCredential? credential = null;
            if (context.Node.Credential is { } reference)
            {
                if (context.OwnerUserId == Guid.Empty || reference.OwnerUserId != context.OwnerUserId)
                    return Fail(ExecutionFailureCode.CredentialUnavailable);
                credential = await credentials.ResolveAsync(reference.Id, context.OwnerUserId, destination.Origin, deadline.Token);
                if (credential is null) return NodeResult.Failure(ExecutionFailureCode.CredentialUnavailable);
                revision = credential.Metadata.Revision;
            }
            using var request = new HttpRequestMessage(new HttpMethod(config.Method.ToString().ToUpperInvariant()), config.Url);
            request.Headers.Accept.Add(new("application/json"));
            if (config.Method is HttpRequestMethod.Post or HttpRequestMethod.Put or HttpRequestMethod.Patch)
            {
                _ = ExecutionPayload.Summarize(context.Input);
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(context.Input));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }
            if (credential is not null)
            {
                if (credential.Metadata.Type == CredentialType.BearerToken)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret.Value);
                else request.Headers.Add(credential.Metadata.HeaderName!, credential.Secret.Value);
            }
            if (config.SupportsIdempotency)
            {
                if (credential?.Metadata.HeaderName?.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase) == true)
                    return Fail(ExecutionFailureCode.CredentialUnavailable);
                request.Headers.Add("Idempotency-Key", HttpRetryPolicy.IdempotencyKey(context.ExecutionId, context.Node.NodeId));
            }
            if (context.BeforeExternalCall is { } journal)
            {
                var permission = await journal(revision, deadline.Token);
                if (permission == LeaseStatus.Lost) throw new ExecutionAuthorityLostException();
                if (permission == LeaseStatus.CancelRequested) return NodeResult.Cancelled() with { CredentialRevisionUsed = revision, ExternalOutcome = ExternalOutcome.NotStarted };
                if (permission == LeaseStatus.DeadlineExceeded) return Fail(ExecutionFailureCode.ExecutionDeadlineExceeded);
            }
            sent = true;
            var response = await transport.SendAsync(request, destination, options.MaxResponseBytes, deadline.Token);
            if (response.StatusCode is < 200 or >= 300) return Fail(ExecutionFailureCode.HttpRemoteFailure) with {
                HttpStatusCode = response.StatusCode, RetryAfter = response.RetryAfter, ExternalOutcome = ExternalOutcome.KnownFailure };
            if (response.Encoded || (response.Body.Length > 0 && (!IsJson(response.MediaType) ||
                (response.Charset is not null && !response.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))))
                return Fail(ExecutionFailureCode.HttpResponseInvalid);
            var body = response.Body.Length == 0 ? "null"u8.ToArray() : response.Body;
            _ = new UTF8Encoding(false, true).GetCharCount(body);
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
            // Um destino autorizado ainda pode ecoar Authorization/API key. Rejeita o resultado antes de qualquer checkpoint.
            if (credential is not null && ContainsSecret(json.RootElement, credential.Secret.Value))
                return Fail(ExecutionFailureCode.HttpResponseSensitive);
            var output = JsonSerializer.SerializeToElement(new { statusCode = response.StatusCode, body = json.RootElement });
            _ = ExecutionPayload.Summarize(output);
            return NodeResult.Success(output, credentialRevisionUsed: revision) with { HttpStatusCode = response.StatusCode, ExternalOutcome = ExternalOutcome.KnownSuccess };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { return Fail(ExecutionFailureCode.NodeTimeout); }
        catch (CredentialRevisionChangedException) { revision = null; return Fail(ExecutionFailureCode.CredentialUnavailable); }
        catch (HttpDestinationDeniedException) { return Fail(ExecutionFailureCode.HttpDestinationDenied); }
        catch (CredentialUnavailableException) { return Fail(ExecutionFailureCode.CredentialUnavailable); }
        catch (HttpResponseLimitException) { return Fail(ExecutionFailureCode.HttpResponseLimitExceeded); }
        catch (ExecutionPayloadLimitException) { return Fail(ExecutionFailureCode.ContextLimitExceeded); }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        { return Fail(ExecutionFailureCode.HttpResponseInvalid); }
        catch (Exception e) when (e is HttpRequestException or System.Net.Sockets.SocketException or IOException)
        { return Fail(ExecutionFailureCode.HttpTransportFailed); }
    }
    private static bool IsJson(string? type) => type is not null && (type.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
        (type.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)));
    private static bool ContainsSecret(JsonElement value, string secret) => value.ValueKind switch {
        JsonValueKind.String => value.GetString()!.Contains(secret, StringComparison.Ordinal),
        JsonValueKind.Number => value.GetRawText().Contains(secret, StringComparison.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Any(v => ContainsSecret(v, secret)),
        JsonValueKind.Object => value.EnumerateObject().Any(p => p.Name.Contains(secret, StringComparison.Ordinal) || ContainsSecret(p.Value, secret)),
        _ => false
    };
}
