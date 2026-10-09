namespace FlowForge.Domain.Workflows.Configuration;

public sealed class JsonPointer
{
    public JsonPointer(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 1024 || (value.Length > 0 && value[0] != '/'))
            throw new ArgumentException("O pointer deve ser vazio ou começar com / e ter até 1.024 unidades UTF-16.", nameof(value));

        var segments = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '/' && ++segments > 32)
                throw new ArgumentException("O pointer deve ter até 32 segmentos.", nameof(value));
            if (character == '~')
            {
                if (index + 1 == value.Length || value[index + 1] is not ('0' or '1'))
                    throw new ArgumentException("O pointer contém um escape inválido.", nameof(value));
                index++;
            }
            else if (char.IsHighSurrogate(character))
            {
                if (index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ArgumentException("O pointer contém Unicode malformado.", nameof(value));
                index++;
            }
            else if (char.IsLowSurrogate(character))
                throw new ArgumentException("O pointer contém Unicode malformado.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}
