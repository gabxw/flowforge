namespace FlowForge.Domain.Credentials;

// Identidade da origem: HTTPS + hostname DNS canônico + porta; nunca caminho, query ou wildcard.
public sealed record HttpsOrigin
{
    public string Value { get; }
    public string Host { get; }
    public int Port { get; }
    private HttpsOrigin(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            uri.GetComponents(UriComponents.StrongAuthority, UriFormat.UriEscaped).Contains('@') ||
            uri.HostNameType != UriHostNameType.Dns || uri.IdnHost.EndsWith('.') || !uri.IdnHost.Contains('.') ||
            uri.IdnHost.Length > 253 || uri.IdnHost.Split('.').Any(label => label.Length is < 1 or > 63 ||
                !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]) ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("A origem exige HTTPS e hostname DNS completo, sem userinfo.");
        Host = uri.IdnHost.ToLowerInvariant(); Port = uri.Port;
        Value = "https://" + Host + (Port == 443 ? "" : ":" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public static HttpsOrigin Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 512 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Informe somente a origem HTTPS, sem caminho, query ou fragmento.");
        return new(uri);
    }
    public static HttpsOrigin FromUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.IsAbsoluteUri && uri.Fragment.Length != 0) throw new ArgumentException("Fragmento não permitido.");
        return new(uri);
    }
    public override string ToString() => Value;
}
