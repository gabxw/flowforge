# Revisão técnica — Fase 10

Data: 12/10/2026.
Status: concluída, revisada e publicada na main pelo [PR #7](https://github.com/gabxw/flowforge/pull/7). Build, testes, frontend e Docker aprovados localmente e no CI do PR/main. MVP de backend entregue; Fase 11 não iniciada.

## Entrega e escopo

MVP de backend com seis executores, retry HTTP opt-in, tentativas auditáveis, deadline total, recuperação independente, cancelamento, deduplicação de entrada, DLQ técnica sanitizada, redrive e replay explícitos. Interface, autenticação e observabilidade permanecem nas Fases 11..14.

- maxAttempts 1..5, default 1 preservado; método com efeito exige supportsIdempotency.
- Backoff exponencial/jitter e Retry-After como mínimo até 60s; advice maior encerra sem antecipar.
- NodeExecutionAttempt iniciado antes do executor; dispatch/revisão antes de HTTP, resultado/desconhecido preservados.
- Chave estável por execução/node; rotação entre tentativas falha fechada sem novo envio.
- Deadline de 48h desde aceite, incluindo fila/espera; cancelamento/deadline não criam tentativa fictícia.
- Scanner de leases vencidas, 10 itens/5s, throttle30s, fencing e orçamento de recuperação.
- DLQ via transferência persistente/mandatory/confirms antes do ACK; envelope sem body/headers/exceção.
- Redrive atual com revisão/auditoria e proteção contra publisher/lease; replay terminal cria nova execução fixa, deduplica request e preserva original.
- Reserva de webhook ativa e tail de 24h após término; política de retenção definida sem limpeza automática.
- Sétima migration, dezesseis tabelas; importação Legacy explícita sem inventar tentativas anteriores.

[ADR 0010](decisions/0010-bounded-retries-and-explicit-recovery.md) e [operação](execution-reliability.md) explicam alternativas e garantias.

## Validação local executada

Restore locked/build Release: zero warnings/erros. Suíte completa: **1.240 testes aprovados**, sem falhas/ignorados (787 Domain + 111 Application + 118 API + 224 Integration).

Unitários verificam transições, orçamento, método seguro/efeito, elegibilidade de status, jitter/advice e chave estável. Integração PostgreSQL/RabbitMQ/HTTPS real comprova:

- GET 503→200 com input original, attempts separados, ramo/histórico preservados e duplicatas inofensivas.
- POST sem contrato não repete; POST idempotente repete a chave e produz um único efeito no destino controlado.
- Budget/advice esgotados, cancelamento no retry e deadline durante HTTP/Delay sem nova tentativa fictícia.
- Credential revision antes do envio; rotação bloqueada entre tentativas; Worker sem lease impedido de enviar HTTP.
- Recuperação sem delivery pendente, checkpoint anterior preservado e orçamento de claims.
- Redrive privado/idempotente sem tomar lease; replay com reconhecimento, mesma versão e novo ID.
- Contador de falhas de delivery preservado após recriar consumer; quinto erro vai para envelope técnico.
- Ausência de rota de DLQ conserva original; conteúdo malicioso não aparece no envelope/logs.
- Falha de negócio não vai para DLQ; IDs/routing/fila v1 preservados.
- Migração de banco da Fase 9 com histórico importado Legacy e resultados/contadores anteriores mantidos.

Contrato HTTP/API e escopo por dono aprovados. EF sem model drift, SQL idempotente, sintaxe PowerShell aprovada. Ajuste final encurta identificadores novos ao limite PostgreSQL, com novo build/model-check/upgrade direcionado.

Docker isolado flowforge-phase10-validation-20261012: imagens atualizadas, nginx -t, banco vazio/migrations duas vezes, smoke direto/proxy, ledger/falha permanente/replay, redrive com Worker parado, broker/API/Worker recriados, keyring preservado, Delay liberando delivery, Redis opcional saudável e shutdown exited:0:false. Apenas containers/volumes descartáveis foram removidos; ambiente operacional não foi migrado.

## Revisão e riscos

Domain continua sem EF/ASP/broker; Application mantém política pequena/portas específicas. Nenhuma dependência, Redis integrado, framework genérico ou novo processo de scheduler foi acrescentado.

Antes de publicar, revisão adicionou lock de outbox no redrive para fechar a corrida com publisher e checks NOT NULL em estados terminais/retry. A repetição de redrive retorna auditoria anterior mesmo após progresso.

A revisão automática rejeitou a proposta de trocar fila v1 por v2, por risco de deixar entregas antigas sem consumo. Essa mudança não foi executada nem contornada. A alternativa preserva fila/routing e usa transferência confirmada para DLQ, validada sem migração operacional.

Confirm→ACK pode duplicar; deduplicação e fencing controlam trabalho local. Broker único/classic não oferece HA. Retry/efeito idempotente depende do contrato real do destino e da validade/escopo da chave. KnownFailure não garante rollback remoto; Unknown não autoriza POST sem contrato.

Busy ACK depende do scanner ativo. Polling/backlog/indisponibilidade podem atrasar deadline/cancelamento; limite de 48h não garante execução pontual. Falha persistente de checkpoint pode terminar attemptLimitExceeded, preservando investigação.

Retenção tem política, mas limpeza/quotas automáticas continuam pendentes. API privada sem JWT/RBAC; wrapping do keyring Production/egress/backup continuam riscos operacionais. Down não é rollback seguro com história nova.

## Publicação

Commits em português, autoria gabxw vinculada à conta por e-mail noreply:

- 1981a5a — Persistir tentativas e recuperar execuções com retries limitados.
- 486bb5f — Cobrir efeitos HTTP recuperação e transferência segura para DLQ.
- 8adacd3 — Documentar garantias limites e operação do MVP de backend.

[PR #7](https://github.com/gabxw/flowforge/pull/7) integrado à main em 12/10/2026, preservando os três commits. [CI do PR](https://github.com/gabxw/flowforge/actions/runs/38303437391) e [CI da main](https://github.com/gabxw/flowforge/actions/runs/38304241134) aprovados para o código 8adacd313ce4f47737cbcc153f622df1832cb976. O log da main confirma 787 Domain + 111 Application + 118 API + 224 Integration: **1.240 aprovados, zero falhas/ignorados**. Os jobs de lint/build do frontend e ambiente Docker/encerramento do Worker também passaram.

O commit posterior de encerramento altera somente documentação/instruções, registrando estas evidências; não altera os binários validados. Todos os critérios de saída da Fase 10 foram atendidos dentro do ambiente local/privado. Os riscos operacionais acima continuam explícitos. Fase 11 não iniciada.
