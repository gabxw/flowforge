using System.Text;
using System.Text.Json;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Infrastructure.Persistence.Serialization;

internal static class NodeConfigurationJson
{
    internal static string Serialize(NodeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            switch (configuration)
            {
                case WebhookTriggerConfiguration: break;
                case HttpRequestConfiguration http:
                    writer.WriteString("url", http.Url.AbsoluteUri);
                    writer.WriteNumber("method", (int)http.Method);
                    break;
                case DelayConfiguration delay:
                    writer.WriteNumber("durationTicks", delay.Duration.Ticks);
                    break;
                case LogConfiguration log:
                    writer.WriteString("message", log.Message);
                    break;
                case ConditionConfiguration condition:
                    writer.WriteString("sourcePointer", condition.SourcePointer.Value);
                    writer.WriteNumber("operation", (int)condition.Operation);
                    if (condition.ExpectedValue is { } value)
                    { writer.WritePropertyName("expectedValue"); value.WriteTo(writer); }
                    break;
                case TransformJsonConfiguration transform:
                    writer.WriteStartArray("fields");
                    foreach (var field in transform.Fields)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("targetProperty", field.TargetProperty);
                        if (field.SourcePointer is { } pointer) writer.WriteString("sourcePointer", pointer.Value);
                        else { ValidateLiteral(field.Literal!.Value); writer.WritePropertyName("literal"); field.Literal.Value.WriteTo(writer); }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    break;
                default: throw new ArgumentException("Tipo de configuração não suportado.", nameof(configuration));
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static NodeConfiguration Deserialize(NodeType type, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var required = type switch
            {
                NodeType.WebhookTrigger => new[] { "schemaVersion" },
                NodeType.HttpRequest => ["schemaVersion", "url", "method"],
                NodeType.Delay => ["schemaVersion", "durationTicks"],
                NodeType.Log => ["schemaVersion", "message"],
                NodeType.Condition => ["schemaVersion", "sourcePointer", "operation"],
                NodeType.TransformJson => ["schemaVersion", "fields"],
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
            ValidateShape(root, required, type == NodeType.Condition ? ["expectedValue"] : []);
            if (!root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1)
                throw new ArgumentException("Schema de configuração não suportado.", nameof(json));
            return type switch
            {
                NodeType.WebhookTrigger => new WebhookTriggerConfiguration(),
                NodeType.HttpRequest => new HttpRequestConfiguration(new Uri(String(root, "url"), UriKind.Absolute),
                    (HttpRequestMethod)root.GetProperty("method").GetInt32()),
                NodeType.Delay => new DelayConfiguration(TimeSpan.FromTicks(root.GetProperty("durationTicks").GetInt64())),
                NodeType.Log => new LogConfiguration(String(root, "message")),
                NodeType.Condition => new ConditionConfiguration(new JsonPointer(String(root, "sourcePointer")),
                    (ConditionOperator)root.GetProperty("operation").GetInt32(),
                    root.TryGetProperty("expectedValue", out var value) ? value : null),
                NodeType.TransformJson => new TransformJsonConfiguration(ParseFields(root.GetProperty("fields"))),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new ArgumentException("Configuração JSON inválida.", nameof(json), exception);
        }
    }

    private static IReadOnlyList<TransformField> ParseFields(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new ArgumentException("Fields deve ser um array.");
        var fields = new List<TransformField>();
        foreach (var field in element.EnumerateArray())
        {
            ValidateShape(field, ["targetProperty"], ["sourcePointer", "literal"]);
            var hasSource = field.TryGetProperty("sourcePointer", out var source);
            var hasLiteral = field.TryGetProperty("literal", out var literal);
            if (hasSource == hasLiteral) throw new ArgumentException("O campo exige exatamente uma origem ou literal.");
            if (hasLiteral) ValidateLiteral(literal);
            fields.Add(hasSource
                ? TransformField.FromPath(String(field, "targetProperty"), new JsonPointer(source.GetString() ??
                    throw new ArgumentException("Pointer inválido.")))
                : TransformField.FromValue(String(field, "targetProperty"), literal));
        }
        return fields;
    }

    private static void ValidateLiteral(JsonElement literal)
    {
        if (literal.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in literal.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Literal contém propriedades duplicadas que JSONB descartaria.");
                ValidateLiteral(property.Value);
            }
        }
        else if (literal.ValueKind == JsonValueKind.Array)
            foreach (var value in literal.EnumerateArray()) ValidateLiteral(value);
    }

    private static string String(JsonElement element, string property) => element.GetProperty(property).GetString()
        ?? throw new ArgumentException("Propriedade string obrigatória ausente.");

    private static void ValidateShape(JsonElement element, string[] required, string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Configuração deve ser um objeto.");
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !observed.Add(property.Name))
                throw new ArgumentException("Propriedade desconhecida ou duplicada na configuração.");
        if (required.Any(property => !observed.Contains(property)))
            throw new ArgumentException("Propriedade obrigatória ausente na configuração.");
    }
}
