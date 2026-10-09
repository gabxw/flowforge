using System.Text.Json;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Workflows;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowForge.Api;

internal sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // EF pode envolver o erro do provider; somente wrappers conhecidos são abertos.
        if (exception is DbUpdateException or InvalidOperationException && exception.InnerException is NpgsqlException)
            exception = exception.InnerException;
        var (status, title, detail) = exception switch
        {
            WorkflowValidationException => (422, "Grafo de workflow inválido.", "Corrija as regras indicadas em errors."),
            WorkflowConcurrencyException => (409, "Conflito de revisão.", "Recarregue o workflow antes de repetir a alteração."),
            WorkflowStateConflictException state => (409, "Operação incompatível com o estado.", state.Message),
            KeyNotFoundException => (404, "Recurso não encontrado.", "O recurso não está disponível para este proprietário."),
            BadHttpRequestException bad => (bad.StatusCode, "Requisição inválida.", "Verifique o corpo, os parâmetros e o tipo de conteúdo."),
            JsonException or ArgumentException => (400, "Requisição inválida.", "Verifique os campos, configurações e limites documentados."),
            WorkflowUnavailableException => (503, "Workflows indisponíveis.", "Verifique a configuração privada do proprietário e do PostgreSQL."),
            PostgresException { SqlState: "23505" } => (409, "Conflito de identidade.", "Uma identidade do grafo já está em uso. Gere outra identidade e tente novamente."),
            NpgsqlException db when db is not PostgresException || db.SqlState is "42P01" or "3D000" or "28P01" or "57P03" =>
                (503, "Workflows indisponíveis.", "O PostgreSQL não está disponível ou as migrations ainda não foram aplicadas."),
            _ => (500, "Erro interno.", "Não foi possível concluir a operação.")
        };
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        if (exception is WorkflowValidationException validation) problem.Extensions["errors"] = validation.Errors;
        // Results.Problem fornece fallback JSON mesmo quando Accept recusa o writer padrão.
        await Results.Problem(problem).ExecuteAsync(context);
        return true;
    }
}
