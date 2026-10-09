namespace FlowForge.Application.Workflows;
public sealed class WorkflowConcurrencyException : Exception
{
    public WorkflowConcurrencyException() : base("O workflow foi alterado por outro cliente. Recarregue antes de salvar.") { }
}
