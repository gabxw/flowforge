# ADR 0015 — OpenTelemetry e contexto durável

Status: aceita e implementada na Fase 14.

## Problema e alternativas

API responde antes da execução e o Worker pode pausar/reiniciar. Logs de processos separados não explicavam onde o tempo era gasto. Poderíamos manter só logs/correlationId, adotar um APM específico ou OpenTelemetry.

Escolhemos SDK OpenTelemetry com Activity/Meter do BCL na Application, adaptadores/exportador na Infrastructure e instrumentação de host na API/Worker. Domain permanece independente. Os novos pacotes têm função concreta: host/lifetime, OTLP e instrumentação ASP/HttpClient.

## Contexto e transporte

Persistimos trace_parent na execução, em vez de depender da memória da request ou colocar contexto no payload de negócio. OutboxClaim recebe metadado opcional e o dispatcher consulta a execução. Custa um read curto por claim e uma coluna nullable. Não altera contrato JSON AMQP v1.

Publisher/consumer propagam apenas W3C traceparent em header conhecido, validado e limitado. Baggage/tracestate e headers arbitrários ficam fora do contrato. Contexto malformado não determina falha de negócio. A continuação durável usa a mesma aceitação; não mantém um span aberto durante Delay/retry e não exige fila nova.

## Exportação, custo e segurança

Exportação exige endpoint explícito. Um único painel Aspire standalone no profile local torna a cadeia investigável sem introduzir AppHost ou vários serviços. Porta UI em loopback e OTLP interno; não é monitoramento de produção nem armazenamento permanente. A ausência do exportador não bloqueia execução.

Allowlist remove URL completa/query, conteúdo e atributos desconhecidos; status de exceção é genérico e eventos HTTP/ASP de exceção não são gravados. Logs exportados são só eventos próprios. Métricas usam tipos/resultados limitados, nunca IDs únicos. CorrelationId/ExecutionId são atributos de trace/log.

Métricas de evento ficam após commit, mas não são transacionais com PostgreSQL e podem subcontar numa queda. Backlog é amostrado a cada cinco segundos; falha no banco invalida a amostra. Esse custo e resolução bastam ao monólito atual. Alarmes, retention do collector e múltiplas instâncias devem ser definidos junto de uma implantação.

O SDK usa apenas TraceContext, sem extrair baggage do cliente. Isso também impede reinjeção de contexto composto pelo SDK. O transporte HTTP usa CreateNoOutputPropagator: atribuir null ao propagador também suprimia os spans. Testes com TLS real verificam o trace e a ausência de headers de contexto no destino. [API oficial](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.distributedcontextpropagator.createnooutputpropagator?view=net-10.0).

Fontes primárias: [SDK .NET](https://opentelemetry.io/docs/languages/dotnet/), [instrumentação](https://opentelemetry.io/docs/languages/dotnet/libraries/), [painel standalone](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone).
