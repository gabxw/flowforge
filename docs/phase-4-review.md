# Revisão da Fase 4

Status: implementação e roteiro local validados na branch fase-4-api; revisão independente, integração/publicação e CI ainda pendentes.

## Entrega

API privada de definição de workflows: criar/listar/detalhar/editar, substituir rascunho, publicar, abrir nova versão e arquivar. DTOs separados do domínio/JSON de persistência, paginação, revisão otimista obrigatória, Problem Details e OpenAPI nativo. Proprietário técnico obtido exclusivamente do servidor; usuário criado idempotentemente.

Application coordena os casos de uso sobre os stores da Fase 3. Domain e modelo SQL não foram alterados. Publicações são imutáveis, arquivamento preserva histórico e todos os acessos ao store recebem o dono. Uma resposta de mutação representa sua própria gravação, sem leitura pós-commit.

## Evidências locais

- Base d0ec59b: 781/781 testes aprovados, inclusive PostgreSQL real após liberação do Docker.
- Fase 4: restore locked, build Release sem warnings/erros e 849/849 testes aprovados (726 Domain + 71 API + 52 Integration), zero ignorados.
- Api.Tests: 60 casos HTTP com fixture PostgreSQL 17, 9 testes de host/runtime e 2 casos de uso concorrência/resposta. Integração cobre jornada, configurações, null, revisão/estado, paginação, limites, duplicação/referências e outro dono.
- RED observado nos endpoints ausentes (16 casos) antes da implementação; falhas adicionais reproduziram coerção numérica, discriminador ausente/fora de ordem, indisponibilidade envolvida pelo EF, colisão SQL e leitura posterior ao commit. GREEN confirmado após correção.
- Frontend: npm ci, lint, TypeScript/build e auditoria aprovados, zero vulnerabilidades reportadas.
- Compose validado em projetos descartáveis, sem tocar volumes existentes: build das três imagens, nginx -t, startup sem schema retornando 503, migration explícita aplicada duas vezes e roteiro completo direto (15080) e via proxy (15173).
- Roteiro publicou grafo válido com os seis tipos, consultou histórico, abriu novo rascunho e arquivou. Recriar a API preservou as duas definições e o proxy recuperou. Redis opcional e Worker encerrando em exited:0:false aprovados. Todos os containers/volumes de teste foram removidos.
- O teste real encontrou dois erros no script: argumento curto de dotnet-ef e leitura de Problem Details como bytes no PowerShell. Corrigidos e roteiro repetido até aprovação. O SQL do modelo permaneceu sem drift.

## Decisões e limites

[ADR 0004](decisions/0004-private-workflow-api.md), [contrato/roteiro](api.md), [spec](superpowers/specs/2026-10-09-phase-4-api-design.md) e [plano](superpowers/plans/2026-10-09-phase-4-api.md).

Sem autenticação: portas locais/ambiente privado são obrigatórios até a Fase 13. Credencial é somente referência; Worker não executa nodes, publica mensagens ou recebe webhooks. Migrations são explícitas. Detalhe retorna histórico completo; paginação de versões e retenção não entram nesta fase.

Nenhum arquivo pessoal, trabalho das worktrees antigas da Fase 3 ou banco existente foi alterado.
