using System.Text.Json;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Persistence.Serialization;
using Xunit;

namespace FlowForge.IntegrationTests;

public sealed class NodeConfigurationJsonTests
{
    [Theory]
    [InlineData(NodeType.WebhookTrigger, "{\"schemaVersion\":1}")]
    [InlineData(NodeType.HttpRequest, "{\"schemaVersion\":1,\"url\":\"https://example.com/orders\",\"method\":2}")]
    [InlineData(NodeType.Delay, "{\"schemaVersion\":1,\"durationTicks\":123456789}")]
    [InlineData(NodeType.Log, "{\"schemaVersion\":1,\"message\":\"Received\"}")]
    [InlineData(NodeType.Condition, "{\"schemaVersion\":1,\"sourcePointer\":\"/total\",\"operation\":4,\"expectedValue\":123456.7890123456789}")]
    [InlineData(NodeType.TransformJson, "{\"schemaVersion\":1,\"fields\":[{\"targetProperty\":\"value\",\"sourcePointer\":\"/total\"},{\"targetProperty\":\"meta\",\"literal\":{\"x\":[null,true,1]}}]}")]
    public void Round_trip_matches_independent_json_contract(NodeType type, string json)
    {
        var configuration = NodeConfigurationJson.Deserialize(type, json);
        Assert.Equal(type, configuration.Type);
        using var expected = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(NodeConfigurationJson.Serialize(configuration));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    [Fact]
    public void Null_literal_differs_from_absent_literal_and_survives_document_disposal()
    {
        var condition = Assert.IsType<ConditionConfiguration>(NodeConfigurationJson.Deserialize(NodeType.Condition,
            """{"schemaVersion":1,"sourcePointer":"/value","operation":2,"expectedValue":null}"""));
        Assert.True(condition.ExpectedValue.HasValue);
        Assert.Equal(JsonValueKind.Null, condition.ExpectedValue.Value.ValueKind);
        var exists = Assert.IsType<ConditionConfiguration>(NodeConfigurationJson.Deserialize(NodeType.Condition,
            """{"schemaVersion":1,"sourcePointer":"/value","operation":1}"""));
        Assert.Null(exists.ExpectedValue);
        var transform = Assert.IsType<TransformJsonConfiguration>(NodeConfigurationJson.Deserialize(NodeType.TransformJson,
            """{"schemaVersion":1,"fields":[{"targetProperty":"empty","literal":null}]}"""));
        Assert.Equal(JsonValueKind.Null, Assert.Single(transform.Fields).Literal!.Value.ValueKind);
        Assert.Contains("\"literal\":null", NodeConfigurationJson.Serialize(transform));
    }

    [Fact]
    public void Duplicate_literal_properties_are_rejected_instead_of_being_silently_discarded_by_jsonb()
    {
        using var literal = JsonDocument.Parse("""{"outer":{"x":1,"x":2}}""");
        var configuration = new TransformJsonConfiguration([TransformField.FromValue("meta", literal.RootElement)]);
        Assert.Throws<ArgumentException>(() => NodeConfigurationJson.Serialize(configuration));
        Assert.Throws<ArgumentException>(() => NodeConfigurationJson.Deserialize(NodeType.TransformJson,
            """{"schemaVersion":1,"fields":[{"targetProperty":"meta","literal":{"x":1,"x":2}}]}"""));
    }

    [Fact]
    public void Jsonb_semantic_comparison_accepts_property_reordering_and_equal_numbers()
    {
        using var first = JsonDocument.Parse("""{"a":1.00,"b":[null,1e2]}""");
        using var second = JsonDocument.Parse("""{"b":[null,100],"a":1}""");
        Assert.True(JsonElement.DeepEquals(first.RootElement, second.RootElement));
    }

    [Theory]
    [InlineData(NodeType.WebhookTrigger, "[]")]
    [InlineData(NodeType.WebhookTrigger, "{}")]
    [InlineData(NodeType.WebhookTrigger, "{\"schemaVersion\":2}")]
    [InlineData(NodeType.WebhookTrigger, "{\"schemaVersion\":1,\"extra\":true}")]
    [InlineData(NodeType.WebhookTrigger, "{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData(NodeType.WebhookTrigger, "not-json")]
    [InlineData((NodeType)99, "{\"schemaVersion\":1}")]
    [InlineData(NodeType.HttpRequest, "{\"schemaVersion\":1,\"url\":\"http://example.com\",\"method\":1}")]
    [InlineData(NodeType.Delay, "{\"schemaVersion\":1,\"durationTicks\":1.2}")]
    [InlineData(NodeType.Log, "{\"schemaVersion\":1,\"message\":null}")]
    [InlineData(NodeType.Condition, "{\"schemaVersion\":1,\"sourcePointer\":\"/x\",\"operation\":2}")]
    [InlineData(NodeType.Condition, "{\"schemaVersion\":1,\"sourcePointer\":\"/x\",\"operation\":1,\"expectedValue\":null}")]
    [InlineData(NodeType.TransformJson, "{\"schemaVersion\":1,\"fields\":[{\"targetProperty\":\"x\"}]}")]
    [InlineData(NodeType.TransformJson, "{\"schemaVersion\":1,\"fields\":[{\"targetProperty\":\"x\",\"sourcePointer\":\"/x\",\"literal\":null}]}")]
    public void Rejects_unsupported_or_invalid_storage_contracts(NodeType type, string json) =>
        Assert.ThrowsAny<ArgumentException>(() => NodeConfigurationJson.Deserialize(type, json));
}
