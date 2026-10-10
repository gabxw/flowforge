using System.Text.Json;

namespace FlowForge.Domain.Workflows.Configuration;

public abstract class NodeConfiguration
{
    private protected NodeConfiguration() { }
    public abstract NodeType Type { get; }
}

public sealed class WebhookTriggerConfiguration : NodeConfiguration
{
    public WebhookTriggerConfiguration() { }
    public override NodeType Type => NodeType.WebhookTrigger;
}

public enum HttpRequestMethod
{
    Get = 1,
    Post = 2,
    Put = 3,
    Patch = 4,
    Delete = 5,
    Head = 6,
    Options = 7
}

public sealed class HttpRequestConfiguration : NodeConfiguration
{
    public HttpRequestConfiguration(Uri url, HttpRequestMethod method)
    {
        ArgumentNullException.ThrowIfNull(url);
        // StrongAuthority preserva o delimitador @ mesmo quando userinfo está vazio.
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps ||
            url.GetComponents(UriComponents.StrongAuthority, UriFormat.UriEscaped).Contains('@') || url.Fragment.Length != 0 || url.AbsoluteUri.Length > 2048)
            throw new ArgumentException("A URL deve ser HTTPS absoluta, sem userinfo ou fragmento.", nameof(url));
        if (!Enum.IsDefined(method))
            throw new ArgumentOutOfRangeException(nameof(method), "Método HTTP desconhecido.");

        Url = url;
        Method = method;
    }

    public override NodeType Type => NodeType.HttpRequest;
    public Uri Url { get; }
    public HttpRequestMethod Method { get; }
}

public sealed class DelayConfiguration : NodeConfiguration
{
    public DelayConfiguration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(duration), "A duração deve ser positiva e de até 24 horas.");
        Duration = duration;
    }

    public override NodeType Type => NodeType.Delay;
    public TimeSpan Duration { get; }
}

public sealed class LogConfiguration : NodeConfiguration
{
    public LogConfiguration(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 2000)
            throw new ArgumentException("A mensagem deve ter até 2.000 unidades UTF-16.", nameof(message));
        Message = message;
    }

    public override NodeType Type => NodeType.Log;
    public string Message { get; }
}

public enum ConditionOperator
{
    Exists = 1,
    Equals = 2,
    NotEquals = 3,
    GreaterThan = 4,
    GreaterThanOrEqual = 5,
    LessThan = 6,
    LessThanOrEqual = 7
}

public sealed class ConditionConfiguration : NodeConfiguration
{
    public ConditionConfiguration(JsonPointer sourcePointer, ConditionOperator operation, JsonElement? expectedValue = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePointer);
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), "Operador de condição desconhecido.");

        if (operation == ConditionOperator.Exists)
        {
            if (expectedValue.HasValue)
                throw new ArgumentException("Exists não aceita comparando.", nameof(expectedValue));
        }
        else
        {
            if (!expectedValue.HasValue)
                throw new ArgumentException("A comparação exige um literal JSON.", nameof(expectedValue));
            var value = expectedValue.Value;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or
                JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw new ArgumentException("O comparando deve ser um escalar JSON.", nameof(expectedValue));
            var ordered = operation is ConditionOperator.GreaterThan or ConditionOperator.GreaterThanOrEqual or
                ConditionOperator.LessThan or ConditionOperator.LessThanOrEqual;
            if (ordered && value.ValueKind != JsonValueKind.Number)
                throw new ArgumentException("Comparações ordenadas exigem número.", nameof(expectedValue));
            if (value.ValueKind == JsonValueKind.Number && !value.TryGetDecimal(out _))
                throw new ArgumentException("O número deve ser representável por decimal.", nameof(expectedValue));
        }

        SourcePointer = sourcePointer;
        Operation = operation;
        ExpectedValue = expectedValue?.Clone();
    }

    public override NodeType Type => NodeType.Condition;
    public JsonPointer SourcePointer { get; }
    public ConditionOperator Operation { get; }
    public JsonElement? ExpectedValue { get; }
}

public sealed class TransformJsonConfiguration : NodeConfiguration
{
    public TransformJsonConfiguration(IEnumerable<TransformField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var copy = new List<TransformField>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field is null)
                throw new ArgumentException("Os campos não podem ser nulos.", nameof(fields));
            if (copy.Count == 50)
                throw new ArgumentException("A transformação deve ter até 50 campos.", nameof(fields));
            if (!targets.Add(field.TargetProperty))
                throw new ArgumentException("Os destinos não podem ser duplicados.", nameof(fields));
            copy.Add(field);
        }
        if (copy.Count == 0)
            throw new ArgumentException("A transformação exige ao menos um campo.", nameof(fields));

        Fields = copy.AsReadOnly();
    }

    public override NodeType Type => NodeType.TransformJson;
    public IReadOnlyList<TransformField> Fields { get; }
}
