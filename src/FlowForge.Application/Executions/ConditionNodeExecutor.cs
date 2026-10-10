using System.Globalization;
using System.Text.Json;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Application.Executions;

public sealed class ConditionNodeExecutor : INodeExecutor
{
    public NodeType Type => NodeType.Condition;
    public bool CanReplayAfterInterruption => true;

    public Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var config = (ConditionConfiguration)context.Node.Configuration;
        var resolution = JsonPointerLookup.Resolve(context.Input, config.SourcePointer, ct, out var actual);
        if (resolution == PointerResolution.Ambiguous)
            return Task.FromResult(NodeResult.Failure(ExecutionFailureCode.JsonPointerAmbiguous));
        // Ausente não é JSON null e não satisfaz nem NotEquals.
        if (resolution == PointerResolution.Missing) return Success(false);
        if (config.Operation == ConditionOperator.Exists) return Success(true);
        var expected = config.ExpectedValue!.Value;
        var ordered = config.Operation is not (ConditionOperator.Equals or ConditionOperator.NotEquals);
        if (ordered || (actual.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number))
        {
            if (!TryExactDecimal(actual, out var left) || !TryExactDecimal(expected, out var right))
                return Task.FromResult(NodeResult.Failure(ExecutionFailureCode.ConditionValueNotComparable));
            return Success(config.Operation switch {
                ConditionOperator.Equals => left == right,
                ConditionOperator.NotEquals => left != right,
                ConditionOperator.GreaterThan => left > right,
                ConditionOperator.GreaterThanOrEqual => left >= right,
                ConditionOperator.LessThan => left < right,
                ConditionOperator.LessThanOrEqual => left <= right,
                _ => throw new InvalidOperationException()
            });
        }
        var equal = actual.ValueKind == expected.ValueKind && actual.ValueKind switch {
            JsonValueKind.String => string.Equals(actual.GetString(), expected.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false
        };
        return Success(config.Operation == ConditionOperator.Equals ? equal : !equal);
        Task<NodeResult> Success(bool predicate) => Task.FromResult(NodeResult.Success(context.Input, predicate ? "true" : "false"));
    }

    private static bool TryExactDecimal(JsonElement value, out decimal number)
    {
        number = default;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out number)) return false;
        // TryGetDecimal pode arredondar/zerar valores. Recusar perda de precisão evita escolher um ramo incorreto.
        return Normalize(value.GetRawText()) is { } original && original == Normalize(number.ToString("G29", CultureInfo.InvariantCulture));
    }
    private static string? Normalize(string raw)
    {
        var negative = raw[0] == '-'; var text = negative ? raw[1..] : raw;
        var exponentAt = text.IndexOfAny(['e', 'E']); var exponent = 0;
        if (exponentAt >= 0)
        {
            if (!int.TryParse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return null;
            text = text[..exponentAt];
        }
        var dot = text.IndexOf('.'); var scale = dot < 0 ? 0 : text.Length - dot - 1;
        var digits = text.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        var significant = digits.TrimEnd('0'); var power = (long)exponent - scale + digits.Length - significant.Length;
        return (negative ? "-" : "") + significant + ":" + power.ToString(CultureInfo.InvariantCulture);
    }
}
