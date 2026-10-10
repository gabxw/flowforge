# Revisão da Fase 4

Data de fechamento: 2026-10-10. Status: concluída, revisada de forma independente e integrada/publicada na main como gabxw. Código validado: [7f04437](https://github.com/gabxw/flowforge/commit/7f0443741723a0ef7f61ad7c1cac369f9070e0e7), com [CI aprovado](https://github.com/gabxw/flowforge/actions/runs/37991399269/attempts/2).

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
- Depois da integração por fast-forward, restore locked, build Release e os 849 testes foram executados novamente no checkout principal: zero avisos, erros, falhas ou ignorados. Os arquivos pessoais não rastreados permaneceram intactos.

## Evidências de CI e critério de saída

A execução 37991399269, tentativa 2, aprovou backend e containers em 2026-10-10, preservando a aprovação anterior do frontend. Backend: restore locked com auditoria online, build Release, drift/script idempotente do EF e **849/849 testes, zero falhos/ignorados** — 726 Domain, 71 API e 52 Integration. Frontend: npm ci, lint, build e auditoria. Artefatos de backend incluem TRX, lockfiles e migrations.sql.

Containers: build das três imagens, nginx -t, Compose saudável, migration explícita aplicada duas vezes, roteiro de seis tipos direto e pelo proxy, recriação da API com recuperação do proxy, Redis opcional e encerramento normal do Worker. A limpeza dos recursos descartáveis também passou.

A primeira tentativa encontrou o limite de downloads anônimos do Docker Hub ao iniciar a fixture PostgreSQL da API. Os testes dependentes falharam por indisponibilidade da imagem e o job de containers foi pulado; esse resultado não foi tratado como aprovação. A repetição dos jobs falhos passou sem modificar o produto, filtrar testes ou desabilitar verificações.

Critério de saída atendido: jornada HTTP persistida, publicação válida e rejeitada, revisão/concorrência, isolamento entre proprietários, OpenAPI tipado e roteiro reproduzível aprovados localmente e no CI. O commit de fechamento altera somente documentação; a execução vinculada valida o código em 7f04437. A Fase 5 permanece planejada.

## Revisão independente

Revisão única do diff completo d0ec59b..7f04437 por um agente com contexto fresco (gpt-6-astra/high). Resultado: nenhum achado Critical ou Important; pronto para integração com dois achados Minor. Foram conferidos isolamento de proprietário, revisão/CAS, imutabilidade do histórico, resposta de cada mutação, configurações JSON, sanitização, configuração lazy, proxy e migrations explícitas.

O revisor reproduziu os dois casos menores com o binário compilado e PostgreSQL descartável, sem alterar o checkout. Processo e container de reprodução foram encerrados. A avaliação pelo efeito confirma severidade menor: ambos recusam uma operação e classificam mal o erro, sem perda/corrupção de dados demonstrada. Permanecem pendentes, sem abrir um novo ciclo de implementação nesta fase.

## Pendências menores

1. Connection string direta sintaticamente válida sem Host (por exemplo, Database=unused) retorna 400 em GET /api/workflows, em vez de 503 por configuração inválida. Liveness continua 200. A validação em src/FlowForge.Api/WorkflowRuntime.cs aceita a string antes da falha posterior do provider; pendente validar os campos indispensáveis e cobrir com regressão de runtime.
2. NUL (U+0000) em texto de entrada de POST /api/workflows chega ao PostgreSQL e retorna 500 sanitizado por SQLSTATE 22021, em vez de 400. O banco rejeita a gravação. Pendente rejeitar NUL nos textos persistidos antes do store e cobrir com teste HTTP.

## Decisões e limites

[ADR 0004](decisions/0004-private-workflow-api.md), [contrato/roteiro](api.md), [spec](superpowers/specs/2026-10-09-phase-4-api-design.md) e [plano](superpowers/plans/2026-10-09-phase-4-api.md).

Sem autenticação: portas locais/ambiente privado são obrigatórios até a Fase 13. Credencial é somente referência; Worker não executa nodes, publica mensagens ou recebe webhooks. Migrations são explícitas. Detalhe retorna histórico completo; paginação de versões e retenção não entram nesta fase.

Os quatro pontos que o revisor deixou fora do julgamento foram decididos explicitamente, na ordem abaixo:

1. Autenticação e exposição pública: manter o serviço privado e as portas em loopback, conforme o escopo. Custo se essa restrição não for suficiente: expor o serviço sem autenticação permitiria acesso às definições do proprietário técnico.
2. Existência/acesso ao segredo de credenciais: manter somente a referência, sem resolver segredo ou alegar existência validada; pertence à Fase 8. Custo se inadequado: uma definição pode conter referência inexistente, exigindo correção antes de executar com credencial.
3. Nodes, webhooks, filas e Worker: publicar gerencia definições; execução e gatilhos pertencem às fases seguintes. Custo se inadequado: publicar não produz execução enquanto o motor e os gatilhos não forem implementados.
4. Paginação/retenção de versões e snapshot entre páginas: manter histórico completo no detalhe e páginas de workflows sem snapshot, como documentado na ADR 0004. Custo se inadequado: históricos grandes aumentam payload e alterações concorrentes podem deslocar itens entre páginas.

Nenhum arquivo pessoal, trabalho das worktrees antigas da Fase 3 ou banco existente foi alterado.
