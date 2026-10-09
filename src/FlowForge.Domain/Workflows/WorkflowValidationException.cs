namespace FlowForge.Domain.Workflows;

public sealed class WorkflowValidationException : InvalidOperationException
{
    public WorkflowValidationException(IEnumerable<GraphValidationError> errors)
        : base("O workflow não pode ser publicado porque o grafo é inválido.")
    {
        ArgumentNullException.ThrowIfNull(errors);
        var copy = errors.ToArray();
        if (copy.Any(error => error is null))
            throw new ArgumentException("Os erros não podem ser nulos.", nameof(errors));
        Errors = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<GraphValidationError> Errors { get; }
}
