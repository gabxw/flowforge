using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FlowForge.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlowForge.IntegrationTests;
// Serviço HTTPS separado da engine, em socket real. CA, chaves e observações são descartáveis e só existem no processo de teste.
public sealed class ControlledHttpsServer : IAsyncLifetime
{
    private WebApplication app = null!;
    private readonly ConcurrentDictionary<string, string> serverNames = new();
    private X509Certificate2 ca = null!;
    private X509Certificate2 certificate = null!;
    public sealed class Gate { public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public ConcurrentDictionary<string, Gate> Gates { get; } = new();
    public int Port { get; private set; }
    public string Origin => "https://flowforge-http.test:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public ConcurrentDictionary<string, int> Counts { get; } = new();
    public ConcurrentQueue<(string Host, string? Sni, string? Authorization, string? ApiKey, byte[] Body)> Observations { get; } = new();
    public ConcurrentQueue<IPEndPoint> ApprovedConnections { get; } = new();
    internal X509Certificate2 CertificateForRawServer => certificate;
    public X509ChainPolicy Trust()
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(ca); return policy;
    }
    public PinnedHttpTransport Transport(bool trust = true) => new(async (endpoint, ct) => {
        ApprovedConnections.Enqueue(endpoint);
        // O mapeamento é interno ao assembly de teste; comprova qual IP o callback recebeu sem publicar uma exceção de SSRF no produto.
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), ct); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }, trust ? Trust() : null);
    public async Task InitializeAsync()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=FlowForge disposable test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=flowforge-http.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("flowforge-http.test"); req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var signed = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var pair = signed.CopyWithPrivateKey(key);
        // Importação independente mantém a chave utilizável pelo Schannel após descartar o RSA de geração.
        certificate = X509CertificateLoader.LoadPkcs12(pair.Export(X509ContentType.Pfx), null);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https => {
            https.ServerCertificate = certificate;
            https.ServerCertificateSelector = (connection, hostname) => {
                if (connection is not null) serverNames[connection.ConnectionId] = hostname ?? "";
                return certificate;
            };
        })));
        app = builder.Build();
        app.Run(async context => {
            var path = context.Request.Path.Value ?? "/";
            Counts.AddOrUpdate(path, 1, (_, n) => n + 1);
            using var input = new MemoryStream(); await context.Request.Body.CopyToAsync(input, context.RequestAborted);
            Observations.Enqueue((context.Request.Host.Value ?? "", serverNames.GetValueOrDefault(context.Connection.Id),
                context.Request.Headers.Authorization.SingleOrDefault(), context.Request.Headers["X-Api-Key"].SingleOrDefault(), input.ToArray()));
            context.Response.ContentType = "application/json; charset=utf-8";
            if (path == "/disconnect") { context.Abort(); return; }
            if (path == "/redirect") { context.Response.StatusCode = 302; context.Response.Headers.Location = "https://127.0.0.1/metadata"; return; }
            if (path.StartsWith("/status/", StringComparison.Ordinal)) { context.Response.StatusCode = int.Parse(path[8..], System.Globalization.CultureInfo.InvariantCulture); return; }
            if (path == "/empty") { context.Response.StatusCode = 204; return; }
            if (path == "/slow") { await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted); return; }
            if (path == "/slow-body") { await context.Response.WriteAsync("{", context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted); await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted); return; }
            if (path == "/large-fixed") { context.Response.ContentLength = 32769; await context.Response.Body.WriteAsync(new byte[32769], context.RequestAborted); return; }
            if (path == "/large-stream") { await context.Response.Body.WriteAsync(new byte[16384], context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted); await context.Response.Body.WriteAsync(new byte[16385], context.RequestAborted); return; }
            if (path == "/bad-json") { await context.Response.WriteAsync("{invalid", context.RequestAborted); return; }
            if (path == "/duplicate-json") { await context.Response.WriteAsync("{\"a\":1,\"a\":2}", context.RequestAborted); return; }
            if (path == "/invalid-utf8") { await context.Response.Body.WriteAsync(new byte[] { 34, 0xc3, 0x28, 34 }, context.RequestAborted); return; }
            if (path == "/encoded") { context.Response.Headers.ContentEncoding = "gzip"; await context.Response.Body.WriteAsync(new byte[] { 1, 2, 3 }, context.RequestAborted); return; }
            if (path == "/text") { context.Response.ContentType = "text/plain"; await context.Response.WriteAsync("text", context.RequestAborted); return; }
            if (path == "/echo" || path == "/echo-escaped" || path == "/echo-name")
            {
                var secret = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal);
                if (secret.Length == 0) secret = context.Request.Headers["X-Api-Key"].ToString();
                if (path == "/echo-escaped") await context.Response.WriteAsync("{\"echo\":\"" + string.Concat(secret.Select(c => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture))) + "\"}", context.RequestAborted);
                else if (path == "/echo-name") await context.Response.WriteAsJsonAsync(new Dictionary<string, bool> { [secret] = true }, context.RequestAborted);
                else await context.Response.WriteAsJsonAsync(new { echo = secret }, context.RequestAborted);
                return;
            }
            if (Gates.TryGetValue(path, out var gate)) { gate.Started.TrySetResult(); await gate.Release.Task.WaitAsync(context.RequestAborted); }
            // Nunca devolver headers secretos como body no caso de sucesso; ainda comprova que headers de resposta são ignorados.
            context.Response.Headers["X-Api-Key"] = context.Request.Headers["X-Api-Key"].ToString();
            context.Response.Headers.SetCookie = "private=fictitious; Secure";
            JsonElement body; using var document = JsonDocument.Parse(input.Length == 0 ? "{}"u8.ToArray() : input.ToArray()); body = document.RootElement.Clone();
            await context.Response.WriteAsJsonAsync(new { accepted = true, input = body }, context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Port = new Uri(address).Port;
    }
    public async Task DisposeAsync() { await app.DisposeAsync(); certificate.Dispose(); ca.Dispose(); }
}
