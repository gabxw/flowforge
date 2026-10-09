namespace FlowForge.Domain.Workflows;

public enum NodeType
{
    WebhookTrigger = 1,
    HttpRequest = 2,
    Delay = 3,
    Condition = 4,
    TransformJson = 5,
    Log = 6
}
