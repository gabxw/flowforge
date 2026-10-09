using System.Text.Json;

namespace FlowForge.Domain.Workflows.Configuration;

public sealed class TransformField
{
    private TransformField(string targetProperty, JsonPointer? sourcePointer, JsonElement? literal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProperty);
        if (targetProperty.Length > 128)
            throw new ArgumentException("O destino deve ter até 128 unidades UTF-16.", nameof(targetProperty));

        TargetProperty = targetProperty;
        SourcePointer = sourcePointer;
        Literal = literal;
    }

    public string TargetProperty { get; }
    public JsonPointer? SourcePointer { get; }
    public JsonElement? Literal { get; }
    public static TransformField FromPath(string targetProperty, JsonPointer sourcePointer)
    {
        ArgumentNullException.ThrowIfNull(sourcePointer);
        return new TransformField(targetProperty, sourcePointer, null);
    }

    public static TransformField FromValue(string targetProperty, JsonElement literal)
    {
        if (literal.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("O literal deve ser um valor JSON definido.", nameof(literal));
        return new TransformField(targetProperty, null, literal.Clone());
    }
}
