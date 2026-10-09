namespace FlowForge.Domain.Workflows;

public sealed class CredentialReference
{
    public CredentialReference(Guid id, Guid ownerUserId)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A credencial deve ter uma identidade.", nameof(id));
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException("O proprietário deve ter uma identidade.", nameof(ownerUserId));

        Id = id;
        OwnerUserId = ownerUserId;
    }
    public Guid Id { get; }
    public Guid OwnerUserId { get; }
}
