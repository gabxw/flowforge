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
        int? revision = null;
        try
        {
            var config = (HttpRequestConfiguration)context.Node.Configuration;
            var destination = await policy.ApproveAsync(config.Url, deadline.Token);
            ResolvedCredential? credential = null;
            if (context.Node.Credential is { } reference)
            {
                if (context.OwnerUserId == Guid.Empty || reference.OwnerUserId != context.OwnerUserId)
                    return NodeResult.Failure(ExecutionFailureCode.CredentialUnavailable);
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
            var response = await transport.SendAsync(request, destination, options.MaxResponseBytes, deadline.Token);
            if (response.StatusCode is < 200 or >= 300) return NodeResult.Failure(ExecutionFailureCode.HttpRemoteFailure, revision);
            if (response.Encoded || (response.Body.Length > 0 && (!IsJson(response.MediaType) ||
                (response.Charset is not null && !response.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))))
                return NodeResult.Failure(ExecutionFailureCode.HttpResponseInvalid, revision);
            var body = response.Body.Length == 0 ? "null"u8.ToArray() : response.Body;
            _ = new UTF8Encoding(false, true).GetCharCount(body);
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
            // Um destino autorizado ainda pode ecoar Authorization/API key. Rejeita o resultado antes de qualquer checkpoint.
            if (credential is not null && ContainsSecret(json.RootElement, credential.Secret.Value))
                return NodeResult.Failure(ExecutionFailureCode.HttpResponseSensitive, revision);
            var output = JsonSerializer.SerializeToElement(new { statusCode = response.StatusCode, body = json.RootElement });
            _ = ExecutionPayload.Summarize(output);
            return NodeResult.Success(output, credentialRevisionUsed: revision);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { return NodeResult.Failure(ExecutionFailureCode.NodeTimeout, revision); }
        catch (HttpDestinationDeniedException) { return NodeResult.Failure(ExecutionFailureCode.HttpDestinationDenied, revision); }
        catch (CredentialUnavailableException) { return NodeResult.Failure(ExecutionFailureCode.CredentialUnavailable, revision); }
        catch (HttpResponseLimitException) { return NodeResult.Failure(ExecutionFailureCode.HttpResponseLimitExceeded, revision); }
        catch (ExecutionPayloadLimitException) { return NodeResult.Failure(ExecutionFailureCode.ContextLimitExceeded, revision); }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        { return NodeResult.Failure(ExecutionFailureCode.HttpResponseInvalid, revision); }
        catch (Exception e) when (e is HttpRequestException or System.Net.Sockets.SocketException or IOException)
        { return NodeResult.Failure(ExecutionFailureCode.HttpTransportFailed, revision); }
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
