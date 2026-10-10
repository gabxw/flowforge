# Revisão técnica — Fase 7

Estado: **Fase 7 concluída em 10/10/2026, publicada e integrada à main.** Implementação, revisão técnica, suíte local, Compose e CI PR/main aprovados. A Fase 8 aguarda autorização de novo incremento.

## Incremento e decisão

Webhook Trigger recebe POST /hooks/{endpointId}, com secret em header/hash, um endpoint por workflow, habilitação/rotação, input protegido e versão fixa. Criação/aceite reusam os locks curtos do PostgreSQL e a outbox existente. Idempotency-Key opcional reserva chave/digest/execução na mesma transação. O limiter tem uma partição fixa local, sem Redis ou dependência adicional.

A API responde depois de commit e não executa nodes ou espera o broker. Trigger/Log repassam o payload; outros tipos continuam unsupportedNode. Autenticação administrativa, outros conectores, retry/DLQ, editor e OpenTelemetry seguem no roadmap. [ADR 0007](decisions/0007-webhook-acceptance-and-idempotency.md) e [runbook](webhooks.md) explicam o contrato e as alternativas.

## Critérios de saída

| Critério | Evidência/estado |
| --- | --- |
| Aceite válido e execução independente | HTTP 202/Location/Pending sem broker no host de teste; engine recebe input protegido e conclui Trigger/Log. |
| Segredo errado/ausente, endpoint desativado/arquivado | HTTP 404 uniforme, sem nova execução. Rotação revoga valor antigo, inclusive para replay; há revalidação após preflight. |
| Payload limitado | 64 KiB recebido e normalizado, profundidade 32, UTF-8 explícito, propriedade única; stream sem Content-Length também limitado. |
| Chamadas simultâneas com mesma chave | 12 requests HTTP concorrentes geram uma execução; corpo alterado ou whitespace diferente retorna 409. |
| Eventos distintos com corpo igual | Sem chave ou outra chave/endpoint/proprietário produzem execuções independentes. |
| Reserva atômica | Falha injetada no commit reverte execução, outbox e reserva. Novo aceite com a mesma chave é permitido depois do rollback. |
| Versionamento/retenção | Nova publicação move apenas novos aceites; replay mantém versão/execução. Reserva expirada pode ser reutilizada, preservando históricos. |
| Conteúdo protegido e retomável | Purpose original/checkpoint/ExecutionId distinto; original não é sobrescrito por output. Não inicializar contexto antes dos nodes. |
| Limites e privacidade | 429/Retry-After sem bloquear liveness; logs e tags HTTP sem segredo/payload no teste. Histórico/GET endpoint contêm metadados. |
| Broker parado, recriação dos hosts e proxy | Compose isolado aprovado: 202/Pending com RabbitMQ parado; execução original conclui após recriação API/Worker e retorno do broker. Rota direta e proxy aprovados localmente e nos dois CI. |

## Verificação executada

- Restore locked e build Release sem warnings/erros.
- 960 testes xUnit: Domain 740, Application 26, API 96, Integration 98; sem falhas ou ignorados. Testcontainers PostgreSQL/RabbitMQ reais.
- Quarta migration incremental AceiteDeWebhooks; doze tabelas. EF has-pending-model-changes sem drift e script idempotente gerado. Migrations anteriores preservadas.
- Sintaxe dos scripts PowerShell aprovada; git diff --check aprovado considerando CRLF.
- Compose local isolado aprovado com build novo completo de API/Worker/frontend, migrations duas vezes, smokes de workflow/manual/webhook direto e por proxy, desativação/rotação/idempotência, keyring após recriação, broker parado e input original após recriação API/Worker. Worker encerrado exited:0:false; removidos somente containers/volumes do projeto descartável de validação. Docker Hub respondeu normalmente nesta verificação.
- [CI do PR aprovado](https://github.com/gabxw/flowforge/actions/runs/38063636349) e [CI da main aprovado](https://github.com/gabxw/flowforge/actions/runs/38064004807): todos os três jobs, inclusive imagens novas, suíte completa e cenário de broker parado/recriação. Teste adicional confirmou que os coletores de logs/tags contêm dados e omitem segredo/body.
- Revisão do próprio autor: contratos, ordem de locks workflow → endpoint, ownership/FKs, versionamento, expiração, propósito de cifragem e ausência de secrets em log/DTO de leitura. Não apresentada como revisão independente.

## Limites aceitos

API administrativa ainda usa proprietário técnico e requer ambiente privado. Secret bearer exige TLS fora da demonstração local. Keyring de Development não possui wrapping; esse runtime é recusado em Production. Preservar keyring junto do banco. Retenção periódica de reservas/payload/histórico e quotas de backlog ainda não implementadas. Digest não oculta corpos previsíveis.

Atualização exige migration e API/Worker da Fase 7 juntos; Worker antigo ignoraria o input webhook. No ambiente privado, pausar ambos durante a atualização. Down é destrutivo para inputs/endpoints/reservas e não é um rollback operacional.

Um endpoint pode consumir o orçamento local dos demais; reinício/múltiplas réplicas alteram o orçamento. Locks serializam aceites no mesmo workflow; não há lock durante execução. PUT enabled aplica o último comando confirmado, sem CAS de endpoint. Header/query/body não são capturados por logging HTTP; instrumentação/exporters futuros precisam preservar essa política.

Limitação herdada: U+0000 em texto de definição de workflow pode causar 500 sanitizado, sem gravação; input de webhook fica cifrado em bytea e não segue esse caminho JSONB. Confiabilidade remota, retry/DLQ e cancelamento de chamadas externas continuam nas fases seguintes.

## Publicação e memória

O [PR #4](https://github.com/gabxw/flowforge/pull/4) está MERGED por fast-forward, preservando os três commits: e556392 (contratos/domínio), 4e86822 (aceite/persistência/transporte/testes) e 45f9ede (documentação). Código e CI verificados em 45f9ede5498564e72156c12ae20188860ab4c5fb. O fechamento posterior muda somente README e documentos; não altera código, configuração, migrations ou testes.

Commits em português, sem prefixos, autoria gabxw/noreply verificado no GitHub, sem configuração global. Arquivos pessoais não rastreados preservados e excluídos dos commits. Curadoria permanece pendente porque memory_project_summary/memory_propose_update não estão disponíveis e não há registro curado do projeto. Handoff mínimo local não equivale a atualização do vault.
