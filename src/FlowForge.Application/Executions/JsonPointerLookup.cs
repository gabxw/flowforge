using System.Globalization;
using System.Text.Json;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Application.Executions;

internal enum PointerResolution { Found, Missing, Ambiguous }
internal static class JsonPointerLookup
{
    internal static PointerResolution Resolve(JsonElement input, JsonPointer pointer, CancellationToken ct, out JsonElement value)
    {
        value = input;
        if (pointer.Value.Length == 0) return PointerResolution.Found;
        foreach (var encoded in pointer.Value[1..].Split('/'))
        {
            ct.ThrowIfCancellationRequested();
            // RFC 6901: ~1 antes de ~0; ~01 representa o nome literal ~1.
            var token = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                var matches = 0; var selected = default(JsonElement);
                foreach (var property in value.EnumerateObject())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!property.NameEquals(token)) continue;
                    if (++matches > 1) { value = default; return PointerResolution.Ambiguous; }
                    selected = property.Value;
                }
                if (matches == 0) { value = default; return PointerResolution.Missing; }
                value = selected;
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                if (token.Length == 0 || (token.Length > 1 && token[0] == '0') ||
                    token.Any(c => c is < '0' or > '9') ||
                    !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= value.GetArrayLength())
                { value = default; return PointerResolution.Missing; }
                value = value[index];
            }
            else { value = default; return PointerResolution.Missing; }
        }
        return PointerResolution.Found;
    }
}
