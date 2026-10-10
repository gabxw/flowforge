using System.Text.Json;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Application.Executions;

public sealed class TransformJsonNodeExecutor : INodeExecutor
{
    public NodeType Type => NodeType.TransformJson;
    public bool CanReplayAfterInterruption => true;

    public Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var config = (TransformJsonConfiguration)context.Node.Configuration;
        try
        {
            using var output = new BoundedOutput();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = 32 }))
            {
                writer.WriteStartObject();
                foreach (var field in config.Fields)
                {
                    ct.ThrowIfCancellationRequested();
                    var value = field.Literal.GetValueOrDefault();
                    if (field.SourcePointer is not null)
                    {
                        var resolved = JsonPointerLookup.Resolve(context.Input, field.SourcePointer, ct, out value);
                        if (resolved != PointerResolution.Found)
                            return Task.FromResult(NodeResult.Failure(resolved == PointerResolution.Ambiguous
                                ? ExecutionFailureCode.JsonPointerAmbiguous : ExecutionFailureCode.TransformSourceMissing));
                    }
                    writer.WritePropertyName(field.TargetProperty); value.WriteTo(writer);
                }
                writer.WriteEndObject(); writer.Flush();
            }
            using var document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            return Task.FromResult(NodeResult.Success(document.RootElement));
        }
        catch (ExecutionPayloadLimitException) { return Task.FromResult(NodeResult.Failure(ExecutionFailureCode.ContextLimitExceeded)); }
        catch (InvalidOperationException) { return Task.FromResult(NodeResult.Failure(ExecutionFailureCode.TransformValueInvalid)); }
        catch (JsonException) { return Task.FromResult(NodeResult.Failure(ExecutionFailureCode.TransformValueInvalid)); }
    }

    private sealed class BoundedOutput : MemoryStream
    {
        private void Check(int bytes) { if (Length + bytes > ExecutionPayload.MaxBytes) throw new ExecutionPayloadLimitException(); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
