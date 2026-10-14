using FlowForge.Application.Users;
using FlowForge.Application.Credentials;
using FlowForge.Application.Webhooks;
using FlowForge.Infrastructure.Runtime;
using System.Diagnostics;
using System.Text.Json;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Workflows;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowForge.Api;

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // EF pode envolver o erro do provider; somente wrappers conhecidos são abertos.
        if (exception is DbUpdateException or InvalidOperationException && exception.InnerException is NpgsqlException)
            exception = exception.InnerException;
        var (status, title, detail) = exception switch
        {
            AuthenticationRejectedException => (401, "Sessão inválida.", "Verifique suas credenciais ou entre novamente."),
            AccountConflictException => (409, "Cadastro não concluído.", "Use uma conta existente ou solicite suporte ao administrador."),
            CredentialConcurrencyException => (409, "Conflito de revisão.", "Recarregue a credencial antes de repetir a alteração."),
            CredentialUnavailableException => (404, "Credencial indisponível.", "Verifique proprietário, origem e revogação."),
            WebhookRejectedException => (404, "Webhook indisponível.", "Verifique o endpoint e o segredo do header."),
            WebhookIdempotencyConflictException => (409, "Chave de idempotência em conflito.", "Esta chave já foi usada com outro corpo."),
            WebhookPayloadLimitException => (413, "Payload excede o limite.", "O corpo e o contexto JSON devem respeitar o limite de 64 KiB."),
            RuntimeConfigurationException => (503, "Proteção de dados indisponível.", "Verifique a configuração privada e o keyring persistente."),
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
        if (status >= 500) Record(context, exception, status);
        context.Response.StatusCode = status;
        if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        if (exception is WorkflowValidationException validation) problem.Extensions["errors"] = validation.Errors;
        // Results.Problem fornece fallback JSON mesmo quando Accept recusa o writer padrão.
        await Results.Problem(problem).ExecuteAsync(context);
        return true;
    }

    // A mensagem da exceção pode carregar dados do request ou do banco, então fica fora do log.
    // Tipo, rota (o padrão, sem IDs), trace e pilha (só nomes de métodos) bastam para investigar.
    private void Record(HttpContext context, Exception exception, int status)
    {
        // O middleware de exceção limpa o endpoint antes do handler; o original fica na feature.
        var endpoint = context.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? context.GetEndpoint();
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "sem_rota";
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        Activity.Current?.SetTag("error.type", exception.GetType().FullName);
        Activity.Current?.SetStatus(ActivityStatusCode.Error);
        if (status == 500)
            logger.LogError("Falha não tratada na API. EventCode={EventCode} ErrorType={ErrorType} Method={Method} Route={Route} TraceId={TraceId} StackTrace={StackTrace}",
                "unhandled_exception", exception.GetType().FullName, context.Request.Method, route, traceId, Stack(exception));
        else
            logger.LogWarning("Dependência indisponível na API. EventCode={EventCode} ErrorType={ErrorType} Method={Method} Route={Route} TraceId={TraceId}",
                "dependency_unavailable", exception.GetType().FullName, context.Request.Method, route, traceId);
    }

    private static string Stack(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null && parts.Count < 4; current = current.InnerException)
            parts.Add(current.GetType().FullName + Environment.NewLine + current.StackTrace);
        return string.Join(Environment.NewLine + "--- exceção interna ---" + Environment.NewLine, parts);
    }
}
