using System.Text;
using System.Text.Json.Nodes;
using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Messaging;
using Xunit;

namespace FlowForge.IntegrationTests;

public sealed class ExecutionMessageJsonTests
{
    private static ExecutionRequestedMessage New() => new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Wire_contract_contains_only_version_and_identifiers_and_round_trips()
    {
        var message = New();
        var body = ExecutionMessageJson.Serialize(message);
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(new[] { "contractVersion", "correlationId", "executionId", "messageId" }, json.Select(p => p.Key).Order());
        Assert.Equal(message, ExecutionMessageJson.Deserialize(body));
        Assert.True(body.Length <= ExecutionMessageJson.MaxBodyBytes);
    }

    [Fact]
    public void Unknown_duplicate_missing_and_invalid_fields_are_rejected()
    {
        var message = New();
        var json = Encoding.UTF8.GetString(ExecutionMessageJson.Serialize(message));
        Assert.Null(ExecutionMessageJson.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"contractVersion\":1", "\"contractVersion\":2"))));
        Assert.Null(ExecutionMessageJson.Deserialize(Encoding.UTF8.GetBytes(json.Replace("\"contractVersion\":1", "\"contractVersion\":1,\"contractVersion\":1"))));
        Assert.Null(ExecutionMessageJson.Deserialize(Encoding.UTF8.GetBytes(json.Replace("{", "{\"payload\":{},"))));
        Assert.Null(ExecutionMessageJson.Deserialize(Encoding.UTF8.GetBytes(json.Replace(message.ExecutionId.ToString(), Guid.Empty.ToString()))));
        Assert.Null(ExecutionMessageJson.Deserialize(Encoding.UTF8.GetBytes("{}")));
        Assert.Null(ExecutionMessageJson.Deserialize(Encoding.UTF8.GetBytes("null")));
        Assert.Throws<ArgumentException>(() => ExecutionMessageJson.Serialize(message with { ContractVersion = 2 }));
    }

    [Fact]
    public void Oversized_empty_or_invalid_json_body_is_rejected()
    {
        Assert.Null(ExecutionMessageJson.Deserialize(new byte[ExecutionMessageJson.MaxBodyBytes + 1]));
        Assert.Null(ExecutionMessageJson.Deserialize(ReadOnlyMemory<byte>.Empty));
        Assert.Null(ExecutionMessageJson.Deserialize(Encoding.UTF8.GetBytes("{broken")));
    }
}
