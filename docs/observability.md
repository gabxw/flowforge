# Investigar execuções com OpenTelemetry

API e Worker usam SDK OpenTelemetry para traces, métricas e logs correlacionados. Exportação começa desativada. O profile observability acrescenta só o painel Aspire standalone; não há AppHost, collector, banco de telemetria ou Redis no caminho de execução.

## Ambiente local

Na mesma sessão que contém os secrets do Compose:

~~~powershell
$env:FLOWFORGE_OTLP_ENDPOINT = 'http://dashboard:18889'
docker compose --profile observability up --build --detach --wait
./scripts/migrate-compose.ps1
~~~

Abra http://localhost:18888. O painel é anônimo apenas neste profile local, ligado a 127.0.0.1; a porta OTLP permanece na rede Docker. Para outro collector, configure explicitamente FlowForge:Telemetry:OtlpEndpoint. Exportação não é pré-condição para executar workflows. Credenciais de exportação e destino público exigem configuração própria do operador.

## Traces e logs

A atividade da request é salva como trace_parent em workflow_executions, metadado nullable de 55 caracteres. O dispatcher recupera esse contexto e inicia span de publicação. RabbitMQ recebe somente header W3C traceparent conhecido; nenhum baggage/header arbitrário é copiado. O consumer inicia workflow.process e os executores geram workflow.node. HttpClient gera o span externo sob o node HTTP. O propagador sem saída mantém a instrumentação, mas não envia traceparent/baggage para o destino externo.

Delay/retry terminam a entrega atual. A continuação usa o contexto persistido da execução, mantendo TraceId após pausa/recriação do Worker. Não existe span vivo ocupando a espera durável. Spans de continuação são ligados à aceitação; histórico persistido continua como fonte de verdade.

Logs próprios incluem TraceId/SpanId, ExecutionId e CorrelationId. Exportação aceita só a categoria FlowForge. Trace usa allowlist de atributos: tipos/status e IDs de correlação; URL completa/query, corpos, configurações, e-mail, headers, password e descrições livres de exceção são removidos. Eventos de exceção do HTTP/ASP não são coletados. Uma falha não mapeada na API responde 500 genérico e gera log de erro com EventCode unhandled_exception, tipo da exceção, método, rota (o padrão, sem IDs), TraceId e a pilha de chamadas; a mensagem da exceção fica de fora porque pode conter dados do request. Dependências indisponíveis (503) geram aviso dependency_unavailable com os mesmos campos, sem pilha.

## Métricas

| Instrumento | Significado |
| --- | --- |
| flowforge.execution.accepted | Aceites persistidos por ingresso manual/webhook/replay; duplicata de webhook não incrementa |
| flowforge.execution.finished | Conclusões observadas após commit, por resultado |
| flowforge.node.active.duration | Tempo ativo de tentativa; tipo/outcome são dimensões limitadas |
| flowforge.wait.scheduled | Espera planejada de Delay/retry |
| flowforge.wait.duration | Espera real de Delay encerrado |
| flowforge.node.retry | Retry durável confirmado |
| flowforge.message.published / received / dead_letter | Transporte observado; mensagens duplicadas podem incrementar received |
| flowforge.outbox.dispatch.lag | Atraso entre available_at e tentativa de publicação; exclui espera planejada |
| flowforge.outbox.ready / oldest.age | Backlog disponível e idade mais antiga, amostrados no PostgreSQL a cada cinco segundos |
| flowforge.execution.waiting | Execuções com retomada agendada |

Nenhum ExecutionId, WorkflowId, UserId, URL ou token é dimensão de métrica. IDs aparecem somente nos traces/logs para investigação. Instrumentos são observacionais: reinício/perda de processo pode subcontar eventos e retenção do painel é em memória. Não são ledger de negócio.

O sampler remove a amostra ao perder acesso ao banco; não apresenta zero falso. Tráfego ASP/HttpClient fornece duração/status, sem payload. Compare tempo ativo, espera real e atraso de outbox para separar problemas do destino e do despacho.

## Jornada

1. Crie/publice um workflow com Condition/Delay/Log ou HTTP para origem autorizada.
2. Ative webhook e envie JSON fictício com o secret no header.
3. Use CorrelationId/ExecutionId do recibo para localizar workflow.process no painel.
4. Abra o trace e veja request, publicação, consumer, node e HTTP externo.
5. Compare sucesso/falha/retry com /api/executions/{id}/attempts; DLQ/redrive seguem o contrato existente.

[Decisão](decisions/0015-opentelemetry-and-durable-context.md) e revisão da Fase 14 registram testes e evidência.
