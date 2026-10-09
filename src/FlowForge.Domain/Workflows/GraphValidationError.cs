namespace FlowForge.Domain.Workflows;

public enum GraphErrorCode
{
    EmptyGraph = 1,
    TriggerCount = 2,
    DuplicateNodeId = 3,
    DuplicateConnectionId = 4,
    NodeVersionMismatch = 5,
    ConnectionVersionMismatch = 6,
    MissingSourceNode = 7,
    MissingTargetNode = 8,
    CredentialOwnerMismatch = 9,
    TriggerHasIncomingConnection = 10,
    InvalidSourcePort = 11,
    DuplicateSourcePort = 12,
    ConditionBranchesIncomplete = 13,
    Cycle = 14,
    UnreachableNode = 15,
    NodeLimitExceeded = 16,
    ConnectionLimitExceeded = 17
}

public sealed record GraphValidationError(
    GraphErrorCode Code, string Message, Guid? NodeId = null, Guid? ConnectionId = null);
