using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using FlowForge.Domain.Credentials;

namespace FlowForge.Infrastructure.Http;
public sealed class HttpResponseLimitException : Exception;
internal sealed record HttpWireResponse(int StatusCode, byte[] Body, string? MediaType, string? Charset, bool Encoded);
public sealed class PinnedHttpTransport
{
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect;
    private readonly X509ChainPolicy? testTrust;
    public PinnedHttpTransport() { connect = ConnectSocketAsync; }
    // Apenas assembly de testes pode remapear o socket e confiar em sua CA descartável. Não há opção de runtime para isso.
    internal PinnedHttpTransport(X509ChainPolicy trust) { connect = ConnectSocketAsync; testTrust = trust; }
    internal PinnedHttpTransport(Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect, X509ChainPolicy? trust = null)
    { this.connect = connect; testTrust = trust; }
    internal async Task<HttpWireResponse> SendAsync(HttpRequestMessage request, ApprovedHttpDestination destination,
        int maxBytes, CancellationToken ct)
    {
        if (request.RequestUri is null || HttpsOrigin.FromUrl(request.RequestUri) != destination.Origin || request.Headers.Host is not null)
            throw new HttpDestinationDeniedException();
        var connectionCallbackEntered = 0;
        using var handler = new SocketsHttpHandler {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, MaxResponseHeadersLength = 8,
            ConnectTimeout = Timeout.InfiniteTimeSpan, ActivityHeadersPropagator = null,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = testTrust },
            ConnectCallback = async (context, token) => {
                // O handler pode tentar reconectar após EOF em métodos sem body. Uma tentativa de node só abre uma sessão.
                if (Interlocked.Exchange(ref connectionCallbackEntered, 1) != 0)
                    throw new HttpRequestException("Reconexão automática recusada.");
                if (!context.DnsEndPoint.Host.Equals(destination.Origin.Host, StringComparison.OrdinalIgnoreCase) ||
                    context.DnsEndPoint.Port != destination.Origin.Port) throw new HttpDestinationDeniedException();
                // Somente falhas de conexão TCP permitem tentar outro endereço já aprovado; nunca resolve novamente.
                foreach (var address in destination.Addresses)
                {
                    try { return await connect(new IPEndPoint(address, destination.Origin.Port), token); }
                    catch (SocketException) when (address != destination.Addresses[^1]) { }
                }
                throw new HttpRequestException("Conexão indisponível.");
            }
        };
        // Uma conexão por node: sem pool compartilhado/coalescing, cookies, proxy ou política escondida de redirects.
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        request.Version = HttpVersion.Version11; request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300) return new(status, [], null, null, false);
        if (response.Content.Headers.ContentLength > maxBytes) throw new HttpResponseLimitException();
        var encoded = response.Content.Headers.ContentEncoding.Count != 0;
        if (encoded) return new(status, [], null, null, true);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes + 1 - (int)body.Length)), ct);
            if (count == 0) break;
            body.Write(buffer, 0, count);
            if (body.Length > maxBytes) throw new HttpResponseLimitException();
        }
        return new(status, body.ToArray(), response.Content.Headers.ContentType?.MediaType,
            response.Content.Headers.ContentType?.CharSet?.Trim('"'), false);
    }
    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(endpoint, ct); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
}
