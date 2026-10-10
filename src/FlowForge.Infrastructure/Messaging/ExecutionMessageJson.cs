using System.Text.Json;
using System.Text.Json.Serialization;
using FlowForge.Application.Executions;

namespace FlowForge.Infrastructure.Messaging;

public static class ExecutionMessageJson
{
    public const int MaxBodyBytes = 2048;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static byte[] Serialize(ExecutionRequestedMessage message)
    {
        if (!message.IsValid()) throw new ArgumentException("Contrato de mensagem inválido.");
        return JsonSerializer.SerializeToUtf8Bytes(message, Options);
    }

    public static ExecutionRequestedMessage? Deserialize(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > MaxBodyBytes) return null;
        try
        {
            var message = JsonSerializer.Deserialize<ExecutionRequestedMessage>(body.Span, Options);
            return message?.IsValid() == true ? message : null;
        }
        catch (JsonException) { return null; }
    }
}
