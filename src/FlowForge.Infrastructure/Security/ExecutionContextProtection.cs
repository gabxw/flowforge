using System.Text;
using System.Text.Json;
using FlowForge.Application.Executions;
using Microsoft.AspNetCore.DataProtection;

namespace FlowForge.Infrastructure.Security;
public sealed class ExecutionContextProtection(IDataProtectionProvider provider)
{
    private IDataProtector Context(Guid id) => provider.CreateProtector("FlowForge.ExecutionContext.v1", id.ToString("N"));
    private IDataProtector Trigger(Guid id) => provider.CreateProtector("FlowForge.TriggerInput.v1", id.ToString("N"));
    private IDataProtector Log(Guid id) => provider.CreateProtector("FlowForge.ExecutionLog.v1", id.ToString("N"));
    public byte[] Protect(JsonElement value, Guid executionId)
    {
        _ = ExecutionPayload.Summarize(value);
        return Context(executionId).Protect(JsonSerializer.SerializeToUtf8Bytes(value));
    }
    public JsonElement Unprotect(byte[] protectedValue, Guid executionId)
    {
        if (protectedValue.Length > 131072) throw new ExecutionPayloadLimitException();
        var plain = Context(executionId).Unprotect(protectedValue);
        if (plain.Length > ExecutionPayload.MaxBytes) throw new ExecutionPayloadLimitException();
        using var document = JsonDocument.Parse(plain);
        var value = document.RootElement.Clone(); _ = ExecutionPayload.Summarize(value); return value;
    }
    public byte[] ProtectTrigger(JsonElement value, Guid executionId)
    {
        _ = ExecutionPayload.Summarize(value);
        return Trigger(executionId).Protect(JsonSerializer.SerializeToUtf8Bytes(value));
    }
    public JsonElement UnprotectTrigger(byte[] protectedValue, Guid executionId)
    {
        if (protectedValue.Length > 131072) throw new ExecutionPayloadLimitException();
        var plain = Trigger(executionId).Unprotect(protectedValue);
        if (plain.Length > ExecutionPayload.MaxBytes) throw new ExecutionPayloadLimitException();
        using var document = JsonDocument.Parse(plain);
        var value = document.RootElement.Clone(); _ = ExecutionPayload.Summarize(value); return value;
    }
    public byte[] ProtectLog(string message, Guid nodeExecutionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 2000) throw new ArgumentException("Mensagem excede o limite.");
        return Log(nodeExecutionId).Protect(Encoding.UTF8.GetBytes(message));
    }
}
