using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using Xunit;

namespace FlowForge.Domain.Tests;

public sealed class DefinitionValueTests
{
    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    public void Position_rejects_nonfinite_coordinates(double x, double y) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodePosition(x, y));

    [Fact]
    public void Position_keeps_finite_negative_coordinates_and_default_origin()
    {
        var position = new NodePosition(-12.5, double.MaxValue);
        Assert.Equal(-12.5, position.X);
        Assert.Equal(double.MaxValue, position.Y);
        Assert.Equal(0, default(NodePosition).X);
        Assert.Equal(0, default(NodePosition).Y);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Credential_rejects_empty_identity(bool emptyId) =>
        Assert.Throws<ArgumentException>(() => new CredentialReference(
            emptyId ? Guid.Empty : Guid.NewGuid(), emptyId ? Guid.NewGuid() : Guid.Empty));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Node_rejects_empty_identity(bool emptyVersion) =>
        Assert.Throws<ArgumentException>(() => new WorkflowNode(
            emptyVersion ? Guid.Empty : Guid.NewGuid(), emptyVersion ? Guid.NewGuid() : Guid.Empty,
            NodeType.Log, new LogConfiguration("message")));

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-1)]
    public void Node_rejects_unknown_type(int type) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowNode(Guid.NewGuid(), Guid.NewGuid(),
            (NodeType)type, new LogConfiguration("message")));

    [Fact]
    public void Node_rejects_null_or_incompatible_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkflowNode(Guid.NewGuid(), Guid.NewGuid(), NodeType.Log, null!));
        Assert.Throws<ArgumentException>(() => new WorkflowNode(Guid.NewGuid(), Guid.NewGuid(),
            NodeType.Delay, new LogConfiguration("message")));
    }

    [Theory]
    [InlineData(NodeType.WebhookTrigger)]
    [InlineData(NodeType.Delay)]
    [InlineData(NodeType.Condition)]
    [InlineData(NodeType.TransformJson)]
    [InlineData(NodeType.Log)]
    public void Only_http_nodes_accept_credentials(NodeType type)
    {
        NodeConfiguration configuration = type switch
        {
            NodeType.WebhookTrigger => new WebhookTriggerConfiguration(),
            NodeType.Delay => new DelayConfiguration(TimeSpan.FromSeconds(1)),
            NodeType.Condition => new ConditionConfiguration(new JsonPointer(""), ConditionOperator.Exists),
            NodeType.TransformJson => new TransformJsonConfiguration([TransformField.FromPath("a", new JsonPointer(""))]),
            _ => new LogConfiguration("message")
        };
        Assert.Throws<ArgumentException>(() => new WorkflowNode(Guid.NewGuid(), Guid.NewGuid(), type,
            configuration, credential: new CredentialReference(Guid.NewGuid(), Guid.NewGuid())));
    }

    [Fact]
    public void Http_node_preserves_identity_position_and_credential_without_network_access()
    {
        var versionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var node = new WorkflowNode(versionId, nodeId, NodeType.HttpRequest,
            new HttpRequestConfiguration(new Uri("https://example.invalid/"), HttpRequestMethod.Post),
            new NodePosition(-1, 20), new CredentialReference(credentialId, ownerId));

        Assert.Equal(versionId, node.WorkflowVersionId);
        Assert.Equal(nodeId, node.NodeId);
        Assert.Equal(NodeType.HttpRequest, node.Type);
        Assert.Equal(NodeType.HttpRequest, node.Configuration.Type);
        Assert.Equal(-1, node.Position.X);
        Assert.Equal(20, node.Position.Y);
        Assert.Equal(credentialId, node.Credential!.Id);
        Assert.Equal(ownerId, node.Credential.OwnerUserId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Connection_rejects_each_empty_identity(int emptyIndex)
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        ids[emptyIndex] = Guid.Empty;
        Assert.Throws<ArgumentException>(() => new WorkflowConnection(ids[0], ids[1], ids[2], ids[3], "next"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Connection_rejects_missing_port(string? port) =>
        Assert.ThrowsAny<ArgumentException>(() => new WorkflowConnection(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), port!));

    [Fact]
    public void Connection_keeps_unknown_port_for_draft_validation_without_normalizing()
    {
        var id = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var connection = new WorkflowConnection(id, versionId, sourceId, targetId, " NEXT ");
        Assert.Equal(id, connection.Id);
        Assert.Equal(versionId, connection.WorkflowVersionId);
        Assert.Equal(sourceId, connection.SourceNodeId);
        Assert.Equal(targetId, connection.TargetNodeId);
        Assert.Equal(" NEXT ", connection.SourcePort);
    }
}
