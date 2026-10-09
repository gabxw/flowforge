# Fase 2 — Plano de implementação do domínio

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Definir, validar e publicar workflows imutáveis e políticas iniciais de estados.

**Architecture:** Valores de definição imutáveis e agregado Workflow controlam o ciclo de vida. Validador puro acumula erros de DAG; transições operacionais são políticas puras, sem engine ou persistência.

**Tech Stack:** .NET 10/BCL; xUnit 2.9.3, runner 3.1.5 e Microsoft.NET.Test.Sdk 18.0.0.

**Spec:** `docs/superpowers/specs/2026-10-09-phase-2-domain-design.md`.

## Global Constraints

- Domain sem dependências de outros projetos, ASP.NET, EF ou broker.
- Somente Fase 2; preservar original .txt, arquivos locais e histórico.
- UUIDs não vazios, datas UTC, enums explícitos, nenhuma coleção/JSON mutável exposta.
- 50 nodes, 100 conexões, 50 campos Transform, paths de 1.024 unidades UTF-16/32 segmentos.
- Commits descritivos em português, sem prefixos, gabxw; restaurar com locks.

## Review Focus

- Duas portas entre o mesmo par de nodes não podem criar falso ciclo nem fan-out.
- Publicação rejeitada não altera ponteiro, revisões, status ou datas.
- JsonDocument descartado e listas fornecidas pelo chamador não alteram publicações.
- Novo draft reescopa identidades e não altera versões anteriores.
- Enum desconhecido e estado terminal nunca retornam ao processamento.

## Preparação compartilhada

Criar `tests/FlowForge.Domain.Tests/FlowForge.Domain.Tests.csproj` e adicionar à solução, com apenas as três dependências de teste acima e referência ao Domain. Gerar e versionar seu packages.lock.json. Baseline: três testes API aprovados. Implementação em worktree `fase-2-dominio`; registros em `.local/phase-2/progress.md` por compatibilidade Windows, sem scripts shell auxiliares.

### Task 1: Valores de definição e configurações

**Files:** criar em `src/FlowForge.Domain/Workflows/`: `NodeType.cs`, `NodePosition.cs`, `CredentialReference.cs`, `WorkflowNode.cs`, `WorkflowConnection.cs`, `Configuration/NodeConfiguration.cs`, `Configuration/JsonPointer.cs`, `Configuration/TransformField.cs`. Criar `tests/FlowForge.Domain.Tests/ConfigurationTests.cs` e `DefinitionValueTests.cs`.

**Interfaces:** produz os tipos/construtores da seção Tipos e configurações da spec, inclusive NodeConfiguration.Type, JsonPointer.Value, TransformField.TargetProperty/SourcePointer/Literal, propriedades get-only dos nodes/conexões. Consumidor: Task 2. Nenhuma dependência da Task 3.

- [x] Escrever testes antes dos corpos: tipos incompatíveis/IDs vazios/posições não finitas; URLs/métodos/Delay inválidos; escapes, Unicode e limites de pointer; operadores/valores Condition; duplicação e limites Transform; cópia de lista/JSON e descarte do documento.
- [x] Rodar testes filtrados e guardar RED em `.local/phase-2/task-1-red.log`; falha deve demonstrar funcionalidade ausente, sem erro de fixture.
- [x] Implementar contratos imutáveis e validações exatamente como a spec; sem eval/resolução de pointers/HTTP.
- [x] Rodar `dotnet test tests/FlowForge.Domain.Tests -c Release` (ou filtro enquanto outro implementador estiver em andamento); expected todos os testes desta tarefa aprovados.
- [x] Auto-revisar, fornecer relatório com arquivos, evidência RED/GREEN e nenhuma alteração fora do escopo. Commit após verificação/revisão coordenada.

### Task 2: Validação e ciclo de publicação

**Files:** criar em `src/FlowForge.Domain/Workflows/`: `GraphValidationError.cs`, `WorkflowGraphValidator.cs`, `WorkflowValidationException.cs`, `WorkflowVersionStatus.cs`, `WorkflowVersion.cs`, `Workflow.cs`. Criar `tests/FlowForge.Domain.Tests/GraphValidationTests.cs`, `WorkflowTests.cs` e helper `GraphFixtures.cs` somente de testes.

**Interfaces:** consome Task 1. Produz `WorkflowGraphValidator.Validate(...)`, `GraphErrorCode`, `WorkflowValidationException.Errors`, Workflow/WorkflowVersion e assinaturas públicas da spec. Não adicionar métodos públicos de alteração à versão. `WorkflowNode` usa propriedades NodeId/WorkflowVersionId/Type/Configuration/Position/Credential; conexão usa Id/WorkflowVersionId/SourceNodeId/TargetNodeId/SourcePort.

- [x] Escrever fixtures literais e testes de trigger único, alcance, ciclos, portas, referências/duplicações, proprietário, 50/51 e 100/101, condições completas e convergência (inclusive mesmo destino nas duas portas).
- [x] Guardar RED antes de implementar o validador. Implementar validação acumulada, BFS e Kahn sem recursão ou exceção em duplicados.
- [x] Testar publicação/novo draft/arquivamento, revisões/datas UTC, falhas sem efeitos e imutabilidade após alterar coleções originais ou descartar JSON. Guardar RED antes dos respectivos corpos.
- [x] Implementar Workflow/WorkflowVersion conforme a spec, preparando alterações antes de modificar o agregado.
- [x] Rodar `dotnet test tests/FlowForge.Domain.Tests -c Release`; expected todos aprovados. Auto-revisar e relatar evidências; commit coordenado após revisão.

### Task 3: Políticas de transição

**Files:** criar `src/FlowForge.Domain/Executions/WorkflowExecutionStatus.cs`, `NodeExecutionStatus.cs`, `ExecutionTransitions.cs`; criar `tests/FlowForge.Domain.Tests/ExecutionTransitionsTests.cs`.

**Interfaces:** independente das Tasks 1/2. Produz dois enums e sobrecargas bool CanTransition(from,to) e void EnsureTransition(from,to). Usa exatamente as arestas da spec, sem inferir pela ordem numérica.

- [x] Escrever matriz de expectativas literais: Workflow permite (1,2),(1,5),(2,3),(2,4),(2,5); Node permite (1,2),(1,6),(2,3),(2,4),(2,5),(2,7),(5,2),(5,7). Todos os outros pares entre membros conhecidos e números desconhecidos são falsos/lançam.
- [x] Rodar e guardar RED; implementar enums/tabelas explícitas; rodar filtro ExecutionTransitionsTests e guardar GREEN.
- [x] Auto-revisar e relatar. Não criar entidades de execução, estado persistente ou engine. Commit coordenado após revisão.

### Task 4: Integração, decisões e fechamento

**Files:** modificar `FlowForge.slnx`, `.github/workflows/validacao.yml`, README.md, docs/architecture.md, docs/data-model.md, docs/roadmap.md; criar docs/decisions/0002-workflow-domain-and-publication.md e docs/phase-2-review.md.

**Interfaces:** consome todas as tarefas. O CI já usa a solução inteira; somente renomear o workflow para Validação do projeto, preservando comandos travados e smoke dos containers.

- [x] Executar `dotnet restore FlowForge.slnx --locked-mode`, `dotnet build FlowForge.slnx -c Release --no-restore`, `dotnet test FlowForge.slnx -c Release --no-build`; expected zero erros/avisos/falhas e três testes API preservados.
- [x] Solicitar revisão técnica independente dos incrementos e do conjunto; corrigir findings importantes com testes RED/GREEN e registrar decisões.
- [x] Documentar contrato Condition/Transform, limites, publicação, estados e trade-offs; estado atual distingue domínio de engine/API/persistência futuras. Validar links locais e UTF-8; documentação não ganha testes que espelhem texto.
- [x] Integrar por fast-forward na main, preservar arquivos não rastreados preexistentes e publicar commits autorizados. Acompanhar CI do commit de implementação; registrar link/resultado real na revisão. Não declarar fase concluída com testes ou CI pendentes e não iniciar Fase 3.
