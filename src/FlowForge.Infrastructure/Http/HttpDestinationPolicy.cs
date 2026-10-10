using System.Net;
using System.Net.Sockets;
using FlowForge.Domain.Credentials;

namespace FlowForge.Infrastructure.Http;
public sealed class HttpDestinationDeniedException : Exception;
public sealed class ApprovedHttpDestination
{
    public HttpsOrigin Origin { get; }
    public IReadOnlyList<IPAddress> Addresses { get; }
    internal ApprovedHttpDestination(HttpsOrigin origin, IPAddress[] addresses) { Origin = origin; Addresses = Array.AsReadOnly(addresses); }
}
public sealed class HttpDestinationPolicy
{
    private readonly HttpNodeOptions options;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve;
    public HttpDestinationPolicy(HttpNodeOptions options) : this(options, (host, ct) => Dns.GetHostAddressesAsync(host, ct)) { }
    internal HttpDestinationPolicy(HttpNodeOptions options, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    { this.options = options; this.resolve = resolve; }
    public async Task<ApprovedHttpDestination> ApproveAsync(Uri url, CancellationToken ct)
    {
        HttpsOrigin origin;
        try { origin = HttpsOrigin.FromUrl(url); }
        catch (ArgumentException) { throw new HttpDestinationDeniedException(); }
        if (url.AbsoluteUri.Length > 2048 || !options.AllowedOrigins.Contains(origin.Value)) throw new HttpDestinationDeniedException();
        // Resolve uma vez; qualquer resposta insegura reprova todo o destino, inclusive conjuntos mistos A/AAAA.
        var addresses = await resolve(origin.Host, ct);
        if (addresses.Length is < 1 or > 16 || addresses.Any(a => !IsPublic(a))) throw new HttpDestinationDeniedException();
        return new(origin, addresses.Distinct().ToArray());
    }
    private static readonly IPNetwork[] DeniedV4 = new[] {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.0.2.0/24", "192.31.196.0/24", "192.52.193.0/24", "192.88.99.0/24", "192.168.0.0/16",
        "192.175.48.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "168.63.129.16/32" // Endpoint interno de plataforma Azure, embora pareça IPv4 público.
    }.Select(IPNetwork.Parse).ToArray();
    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] DeniedV6 = new[] {
        "2001::/23", "2001:db8::/32", "2002::/16", "2620:4f:8000::/48", "3fff::/20"
    }.Select(IPNetwork.Parse).ToArray();
    internal static bool IsPublic(IPAddress address)
    {
        // Política conservadora: blocos especiais IANA, transição/NAT64 e IPv4-mapped não são destinos do MVP.
        if (address.AddressFamily == AddressFamily.InterNetwork) return !DeniedV4.Any(n => n.Contains(address));
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 && !address.IsIPv4MappedToIPv6 &&
            GlobalV6.Contains(address) && !DeniedV6.Any(n => n.Contains(address));
    }
}
