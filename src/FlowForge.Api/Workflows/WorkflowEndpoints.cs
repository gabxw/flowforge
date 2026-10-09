using FlowForge.Application.Workflows;

namespace FlowForge.Api.Workflows;

internal static class WorkflowEndpoints
{
    public static void MapWorkflows(this WebApplication app)
    {
        var group = app.MapGroup("/api/workflows").WithTags("Workflows");
        group.MapPost("/", async (CreateWorkflowRequest request, WorkflowService service, TechnicalOwner owner, CancellationToken ct) =>
        {
            var workflow = await service.CreateAsync(owner.Id, request.Name, request.Description, ct);
            return Results.Created($"/api/workflows/{workflow.Id}", WorkflowTransport.ToDto(workflow));
        }).WithName("CreateWorkflow").Produces<WorkflowDto>(201).Errors(400, 409, 503);

        group.MapGet("/", async (WorkflowService service, TechnicalOwner owner, CancellationToken ct,
            int offset = 0, int limit = 20, bool includeArchived = false) =>
            Results.Ok(new WorkflowPageDto(await service.ListAsync(owner.Id, offset, limit, includeArchived, ct), offset, limit)))
            .WithName("ListWorkflows").Produces<WorkflowPageDto>().Errors(400, 503);

        group.MapGet("/{id:guid}", async (Guid id, WorkflowService service, TechnicalOwner owner, CancellationToken ct) =>
            Results.Ok(WorkflowTransport.ToDto(await service.GetAsync(owner.Id, id, ct))))
            .WithName("GetWorkflow").Produces<WorkflowDto>().Errors(400, 404, 503);

        group.MapGet("/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, WorkflowService service, TechnicalOwner owner, CancellationToken ct) =>
        {
            var workflow = await service.GetAsync(owner.Id, id, ct);
            var version = workflow.Versions.SingleOrDefault(v => v.Id == versionId) ??
                throw new KeyNotFoundException("Versão não encontrada.");
            return Results.Ok(WorkflowTransport.ToDto(version));
        }).WithName("GetWorkflowVersion").Produces<VersionDto>().Errors(400, 404, 503);

        group.MapPut("/{id:guid}", async (Guid id, UpdateWorkflowRequest request, WorkflowService service, TechnicalOwner owner, CancellationToken ct) =>
            Results.Ok(WorkflowTransport.ToDto(await service.UpdateAsync(owner.Id, id, request.ExpectedRevision, request.Name, request.Description, ct))))
            .WithName("UpdateWorkflow").Produces<WorkflowDto>().Errors(400, 404, 409, 503);

        group.MapPost("/{id:guid}/drafts", async (Guid id, RevisionRequest request, WorkflowService service, TechnicalOwner owner, CancellationToken ct) =>
        {
            var workflow = await service.CreateDraftAsync(owner.Id, id, request.ExpectedRevision, ct);
            return Results.Created($"/api/workflows/{id}/versions/{workflow.DraftVersion!.Id}", WorkflowTransport.ToDto(workflow));
        }).WithName("CreateWorkflowDraft").Produces<WorkflowDto>(201).Errors(400, 404, 409, 503);

        group.MapPut("/{id:guid}/draft", async (Guid id, HttpRequest http,
            WorkflowService service, TechnicalOwner owner, CancellationToken ct) =>
        {
            var request = await WorkflowTransport.ReadDraftAsync(http, ct);
            ArgumentNullException.ThrowIfNull(request.Nodes);
            ArgumentNullException.ThrowIfNull(request.Connections);
            if (request.Nodes.Count > 50 || request.Connections.Count > 100)
                throw new ArgumentException("O rascunho aceita até 50 nodes e 100 conexões.");
            return Results.Ok(WorkflowTransport.ToDto(await service.ReplaceDraftAsync(owner.Id, id, request.ExpectedRevision,
                request.Nodes.Select(WorkflowTransport.ToDefinition).ToArray(),
                request.Connections.Select(WorkflowTransport.ToDefinition).ToArray(), ct)));
        }).WithName("ReplaceWorkflowDraft").Accepts<ReplaceDraftRequest>("application/json")
            .Produces<WorkflowDto>().Errors(400, 404, 409, 415, 422, 503);

        group.MapPost("/{id:guid}/publish", async (Guid id, RevisionRequest request, WorkflowService service, TechnicalOwner owner, CancellationToken ct) =>
            Results.Ok(WorkflowTransport.ToDto(await service.PublishAsync(owner.Id, id, request.ExpectedRevision, ct))))
            .WithName("PublishWorkflow").Produces<WorkflowDto>().Errors(400, 404, 409, 422, 503);

        group.MapPost("/{id:guid}/archive", async (Guid id, RevisionRequest request, WorkflowService service, TechnicalOwner owner, CancellationToken ct) =>
            Results.Ok(WorkflowTransport.ToDto(await service.ArchiveAsync(owner.Id, id, request.ExpectedRevision, ct))))
            .WithName("ArchiveWorkflow").Produces<WorkflowDto>().Errors(400, 404, 409, 503);
    }

    private static RouteHandlerBuilder Errors(this RouteHandlerBuilder builder, params int[] statuses)
    {
        foreach (var status in statuses) builder.ProducesProblem(status);
        return builder.ProducesProblem(500);
    }
}
