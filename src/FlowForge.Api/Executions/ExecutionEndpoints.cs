using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;

namespace FlowForge.Api.Executions;

internal sealed record ExecutionDto(Guid Id, Guid WorkflowId, Guid WorkflowVersionId, Guid CorrelationId,
    WorkflowExecutionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, ExecutionFailureCode? ErrorCode, DateTimeOffset? CancelRequestedAt, DateTimeOffset? ResumeAt)
{
    public static ExecutionDto From(WorkflowExecutionSnapshot s) =>
        new(s.Id, s.WorkflowId, s.WorkflowVersionId, s.CorrelationId, s.Status, s.CreatedAt, s.StartedAt, s.FinishedAt, s.ErrorCode, s.CancelRequestedAt, s.ResumeAt);
}

internal static class ExecutionEndpoints
{
    public static void MapExecutions(this WebApplication app)
    {
        app.MapPost("/api/workflows/{id:guid}/executions", async (Guid id, HttpRequest request,
            ExecutionService service, TechnicalOwner owner, CancellationToken ct) =>
        {
            if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
                throw new BadHttpRequestException("Este comando não aceita corpo.");
            var execution = await service.RequestAsync(owner.Id, id, ct);
            return Results.Accepted($"/api/executions/{execution.Id}", ExecutionDto.From(execution));
        }).WithTags("Execuções").WithName("RequestExecution").Produces<ExecutionDto>(202)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503).ProducesProblem(500);

        app.MapGet("/api/executions/{id:guid}", async (Guid id, ExecutionService service,
            TechnicalOwner owner, CancellationToken ct) => Results.Ok(ExecutionDto.From(await service.GetAsync(owner.Id, id, ct))))
            .WithTags("Execuções").WithName("GetExecution").Produces<ExecutionDto>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(503).ProducesProblem(500);

        app.MapPost("/api/executions/{id:guid}/cancel", async (Guid id, HttpRequest request,
            ExecutionService service, TechnicalOwner owner, CancellationToken ct) =>
        {
            if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
                throw new BadHttpRequestException("Este comando não aceita corpo.");
            var execution = await service.CancelAsync(owner.Id, id, ct);
            var dto = ExecutionDto.From(execution);
            return execution.Status is WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running
                ? Results.Accepted($"/api/executions/{id}", dto) : Results.Ok(dto);
        }).WithTags("Execuções").WithName("CancelExecution").Produces<ExecutionDto>(202).Produces<ExecutionDto>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(503).ProducesProblem(500);

        app.MapGet("/api/executions/{id:guid}/nodes", async (Guid id, ExecutionService service,
            TechnicalOwner owner, CancellationToken ct) => Results.Ok((await service.HistoryAsync(owner.Id, id, ct)).Nodes))
            .WithTags("Execuções").WithName("GetExecutionNodes").Produces<IReadOnlyList<NodeExecutionSnapshot>>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(503).ProducesProblem(500);
        app.MapGet("/api/executions/{id:guid}/logs", async (Guid id, ExecutionService service,
            TechnicalOwner owner, CancellationToken ct) => Results.Ok((await service.HistoryAsync(owner.Id, id, ct)).Logs))
            .WithTags("Execuções").WithName("GetExecutionLogs").Produces<IReadOnlyList<ExecutionLogSummary>>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(503).ProducesProblem(500);
    }
}
