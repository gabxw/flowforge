
# Confiabilidade e recuperação — Fase 10

O MVP de backend contém os seis executores e processamento assíncrono com versão fixa, tentativas, retry durável, deadline, cancelamento, deduplicação e DLQ técnica. Permanece privado/local; telas, JWT/RBAC e OpenTelemetry pertencem aos próximos incrementos.

## Configuração HTTP

~~~json
{
  "type": "httpRequest",
  "url": "https://api.example.test/orders",
  "method": "post",
  "maxAttempts": 3,
  "supportsIdempotency": true
}
~~~

Default maxAttempts é 1; intervalo permitido 1..5. Método com efeito exige contrato declarado para repetir. supportsIdempotency gera Idempotency-Key estável por execução/node, mesmo em uma única tentativa. O endpoint precisa efetivamente deduplicar, preservar conteúdo/principal e manter a chave pelo deadline de 48h. Allowlist/SSRF/TLS/headers/limites da [Fase 8](http-and-credentials.md) continuam obrigatórios.

Somente transporte, timeout e 408/429/500/502/503/504 elegíveis repetem. Backoff exponencial com jitter não ocupa delivery durante a espera. Retry-After até 60s tem prioridade como mínimo; maior que o orçamento encerra sem retry. Não há retry de segredo, destino, JSON inválido, Transform/Condition ou falha de negócio local.

A próxima tentativa usa o mesmo contexto de entrada protegido. Rotação/revogação entre tentativas recusa outro envio; nova execução resolve credenciais atuais. Uma chamada já preparada conserva sua revisão resolvida, conforme contrato anterior.

## Estado e histórico

GET /api/executions/{id} inclui deadlineAt e resumeAt. Running com resumeAt pode representar Delay ou espera de retry. Node Retrying tem retryAt/errorCode e conserva seu primeiro StartedAt; ao iniciar outra tentativa, volta a Running e incrementa attemptCount. Deadline/cancelamento em espera não criam outra tentativa.

GET /api/executions/{id}/attempts retorna metadados por tentativa:

- Number, Status, StartedAt e FinishedAt;
- ErrorCode, HttpStatusCode e RetryAt;
- ExternalOutcome, DispatchedAt, CredentialRevisionUsed e Legacy.

Não contém bodies, URL, headers, valor de credencial ou contexto cifrado. Unknown significa que não podemos afirmar o resultado externo. KnownFailure indica resposta HTTP de falha observada, não prova rollback remoto. KnownSuccess pode coexistir com execução Failed/Cancelled se a observação chegar tarde.

Uma tentativa de HTTP nasce antes do envio e o journal registra sua autoridade/revisão antes de SendAsync. Após queda, só se repete com contrato e orçamento elegíveis. Local replay interrompido fica registrado separadamente; tentativa concluída não é sobrescrita.

## Deadline, concorrência e scanner

Deadline de 48h parte do aceite, usando relógio PostgreSQL; abrange backlog, Delays e retries. Um Delay maior que o tempo restante acorda no deadline e falha executionDeadlineExceeded. Cancelamento tem prioridade quando já pedido.

Prefetch 1 e execução sequencial limitam concorrência por host; várias instâncias podem processar execuções diferentes. Lease/token/geração/revisão/sequência impedem gravação concorrente da mesma execução. Antes do HTTP há nova checagem de autoridade; perder lease impede novo envio, mas não desfaz chamada anterior.

Scanner periódico recupera até 10 itens por ciclo de 5s, por outbox, sem exigir redelivery. Intervalo de recuperação de 30s reduz republicações de um claim ainda não retomado. Uma mensagem publicada que nenhum Worker reivindicou em 5 minutos (por exemplo, enviada à DLQ antes de criar a inbox) também volta à fila; se ela só estava atrasada, a duplicata é absorvida pela inbox. Esse retorno automático para depois de cinco publicações da mesma mensagem, que então aguarda o deadline ou um redrive. Cinco claims/replays locais esgotam recuperação com attemptLimitExceeded. Indisponibilidade do banco/broker pode atrasar a observação de deadline/cancelamento.

## DLQ e transporte

Fila/routing originais v1 continuam iguais. A nova fila flowforge.executions.dead.v1 é durável e recebe envelopes técnicos, não bodies originais. Os motivos são InvalidContract, UnknownIdentity e ProcessingExhausted.

O consumer publica a cópia persistente com mandatory/confirms antes do ACK original. Falha de rota/confirm conserva a delivery original; queda após confirm pode duplicar. Reenvio transitório carrega contador x-flowforge-delivery-attempt e termina após cinco falhas registradas. Esse header é interno, não configurável via API de workflow.

A DLQ não contém secrets copiados de um produtor inválido, e logs guardam tipo de erro/IDs. Nunca publique manualmente conteúdo arbitrário da DLQ como workflow. Se não houver IDs válidos, corrija o produtor e envie um novo evento legítimo. Workflow Failed comum recebe ACK e não vai automaticamente para DLQ.

## Comandos privados do operador

| Método/caminho | Resultado |
| --- | --- |
| GET /api/executions/{id}/dispatches | IDs/sequências, disponibilidade, publicação e contador de publish |
| GET /api/executions/{id}/redrives | Auditoria de retomada de entrega |
| POST /api/executions/{id}/redrive | Mensagem atual, sem lease válida; 202, idempotente por MessageId/revisão |
| GET /api/executions/{id}/replays | Ligações entre execução original e novas |
| POST /api/executions/{id}/replay | Nova execução fixa para original Failed/Cancelled; 202 e Location |

Redrive body:

~~~json
{"messageId":"00000000-0000-0000-0000-000000000001","expectedPublishAttempts":2}
~~~

Use valores reais obtidos em dispatches; o UUID acima é somente formato. Mensagem de outro dono/execução ou sequência antiga é recusada. Revisão desconhecida, publish/lease ativos ou execução terminal retornam conflito. Repetição de um evento de redrive já aceito retorna sua auditoria.

Replay exige header Idempotency-Key e body:

~~~json
{"acknowledgeExternalEffects":true}
~~~

Ele começa do trigger/input original e pode repetir etapas que antes tiveram sucesso. Reconhecer efeitos é obrigatório quando a versão contém HTTP. A mesma chave na mesma execução original retorna o mesmo novo ID; outra chave solicita outra execução. Graph/version/history anteriores permanecem preservados, mesmo se o workflow tiver sido republicado/arquivado. Outros donos recebem 404.

Não é uma interface administrativa pública: até RBAC, o operador utiliza o ambiente privado e o proprietário técnico configurado no servidor.

## Idempotência e retenção

Webhook mantém a mesma chave enquanto a execução está Pending/Running, mesmo que ExpiresAt inicial tenha passado. Após término, protege por pelo menos IdempotencyLifetime (24h padrão); ainda compara o digest do conteúdo e recusa conteúdo diferente. Expiração não deduplica eventos distintos sem chave.

Política operacional, sem limpeza automática nesta fase:

| Registro | Janela mínima |
| --- | --- |
| Execução ativa e suas inbox/outbox/tentativas | Conservar até término, inclusive durante indisponibilidade |
| Inbox/outbox concluídas e envelopes DLQ | 7 dias após término |
| Tentativas, redrives e replays | 30 dias |
| Contexto/input/Log protegidos | 7 dias após término |
| Chave de webhook | Ativa + pelo menos 24h após término; respeitar prazo inicial maior |
| Chave de replay | Não reutilizada enquanto auditoria existir |

Depois dessa janela, redelivery/replay tem garantia limitada. Limpeza exige coordenação de FKs e dos registros de deduplicação; não remova inbox enquanto houver possibilidade de reenviar seu MessageId. DLQ stale pode coexistir com execução já concluída; consulte o banco antes de ação.

## Upgrade e verificação

Pare API/Workers, preserve backup consistente de banco/keyring e aplique a sétima migration explicitamente. Existing entregas permanecem na fila v1. Não há conversão, remoção ou drain obrigatório de fila.

A migration acrescenta deadline, RetryAt/RecoveryNotBefore e três tabelas (dezesseis no total). Registros legados ganham prazo de 48h a partir da migração; resultados terminais não reabrem. Histórico anterior importa um único resumo conhecido por node com Legacy=true, mantendo seu AttemptCount, sem inventar tentativas ou horários de dispatch.

Down não é rollback operacional seguro com histórico novo. Corrija para frente ou restaure banco/keyring/broker de forma coordenada; nenhuma validação altera volumes operacionais.

~~~powershell
dotnet restore FlowForge.slnx --locked-mode
dotnet build FlowForge.slnx -c Release --no-restore
dotnet test FlowForge.slnx -c Release --no-build
.\scripts\migrate-compose.ps1
.\scripts\smoke-reliability.ps1
.\scripts\smoke-reliability.ps1 -BaseUri http://127.0.0.1:5173

docker compose stop --timeout 10 worker
$pending = .\scripts\smoke-reliability.ps1 -PendingOnly
docker compose up --detach --force-recreate --no-deps worker
.\scripts\smoke-reliability.ps1 -ExecutionId $pending.ExecutionId
~~~

O smoke cria dados fictícios e verifica tentativas, falha permanente sem retry, redrive e replay; arquiva o exemplo e preserva volumes. Sucesso/retry/efeito idempotente HTTP são verificados por HTTPS real controlado nos testes, sem liberar SSRF no produto.

[ADR 0010](decisions/0010-bounded-retries-and-explicit-recovery.md) explica escolhas e alternativas; [revisão da Fase 10](phase-10-review.md) registra resultados reais.
