
using FlowForge.Application.Executions;

namespace FlowForge.Api.Executions;
internal sealed record ReplayBody { public required bool AcknowledgeExternalEffects { get; init; } }
internal sealed record RedriveBody { public required Guid MessageId { get; init; } public required int ExpectedPublishAttempts { get; init; } }
internal static class RecoveryEndpoints
{
    public static void MapExecutionRecovery(this WebApplication app)
    {
        app.MapGet("/api/executions/{id:guid}/attempts", async (Guid id, CurrentOwner owner, ExecutionRecoveryService service, CancellationToken ct) =>
            Results.Ok(await service.AttemptsAsync(owner.Id, id, ct))).WithTags("Execuções").WithName("GetExecutionAttempts").Produces<IReadOnlyList<FlowForge.Domain.Executions.NodeAttemptSnapshot>>().ProducesProblem(404);
        app.MapGet("/api/executions/{id:guid}/dispatches", async (Guid id, CurrentOwner owner, ExecutionRecoveryService service, CancellationToken ct) =>
            Results.Ok(await service.DispatchesAsync(owner.Id, id, ct))).WithTags("Execuções").WithName("GetExecutionDispatches").Produces<IReadOnlyList<DispatchSummary>>().ProducesProblem(404);
        app.MapGet("/api/executions/{id:guid}/redrives", async (Guid id, CurrentOwner owner, ExecutionRecoveryService service, CancellationToken ct) =>
            Results.Ok(await service.RedrivesAsync(owner.Id, id, ct))).WithTags("Execuções").WithName("GetExecutionRedrives").Produces<IReadOnlyList<RedriveSummary>>().ProducesProblem(404);
        app.MapGet("/api/executions/{id:guid}/replays", async (Guid id, CurrentOwner owner, ExecutionRecoveryService service, CancellationToken ct) =>
            Results.Ok(await service.ReplaysAsync(owner.Id, id, ct))).WithTags("Execuções").WithName("GetExecutionReplays").Produces<IReadOnlyList<ReplaySummary>>().ProducesProblem(404);
        app.MapPost("/api/executions/{id:guid}/redrive", async (Guid id, RedriveBody body, CurrentOwner owner, ExecutionRecoveryService service, CancellationToken ct) =>
            Results.Accepted($"/api/executions/{id}", await service.RedriveAsync(owner.Id, id, new(body.MessageId, body.ExpectedPublishAttempts), ct)))
            .WithTags("Execuções").WithName("RedriveExecutionDispatch").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024))
            .Produces<RedriveSummary>(202).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/executions/{id:guid}/replay", async (Guid id, ReplayBody body, HttpRequest request, CurrentOwner owner, ExecutionRecoveryService service, CancellationToken ct) =>
        {
            var keys = request.Headers["Idempotency-Key"];
            if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0])) throw new BadHttpRequestException("Replay exige Idempotency-Key único.");
            var result = await service.ReplayAsync(owner.Id, id, new(keys[0]!, body.AcknowledgeExternalEffects), ct);
            return Results.Accepted($"/api/executions/{result.Id}", ExecutionDto.From(result));
        }).WithTags("Execuções").WithName("ReplayExecution").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024))
            .Produces<ExecutionDto>(202).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
    }
}
