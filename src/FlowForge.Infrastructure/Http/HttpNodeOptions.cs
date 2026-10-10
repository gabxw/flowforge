using FlowForge.Domain.Credentials;
using FlowForge.Infrastructure.Runtime;
using Microsoft.Extensions.Configuration;

namespace FlowForge.Infrastructure.Http;
public sealed class HttpNodeOptions
{
    public IReadOnlySet<string> AllowedOrigins { get; }
    public TimeSpan Timeout { get; }
    public int MaxResponseBytes { get; }
    public HttpNodeOptions(IEnumerable<string> origins, TimeSpan? timeout = null, int maxResponseBytes = 32768)
    {
        ArgumentNullException.ThrowIfNull(origins);
        var set = origins.Select(o => HttpsOrigin.Parse(o).Value).ToHashSet(StringComparer.Ordinal);
        if (set.Count > 32) throw new ArgumentException("A allowlist admite até 32 origens.");
        Timeout = timeout ?? TimeSpan.FromSeconds(8);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromSeconds(8) || maxResponseBytes is < 1 or > 32768)
            throw new ArgumentException("Limites HTTP inválidos.");
        AllowedOrigins = set; MaxResponseBytes = maxResponseBytes;
    }
    public static HttpNodeOptions FromConfiguration(IConfiguration configuration)
    {
        try
        {
            var raw = configuration["FlowForge:Http:AllowedOrigins"] ?? "";
            if (raw.Length > 16384) throw new ArgumentException();
            var timeout = configuration["FlowForge:Http:TimeoutSeconds"];
            var max = configuration["FlowForge:Http:MaxResponseBytes"];
            return new(raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                TimeSpan.FromSeconds(timeout is null ? 8 : int.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture)),
                max is null ? 32768 : int.Parse(max, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) { throw new RuntimeConfigurationException(); }
    }
}
