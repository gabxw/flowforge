# Fase 3 — Plano de persistência

**Goal:** Persistir o domínio com constraints e concorrência em PostgreSQL real.
**Spec:** docs/superpowers/specs/2026-10-09-phase-3-persistence-design.md.
**Execução:** preparação e worktrees herdadas da sessão anterior; continuação serial, integração e revisão pelo agente principal. Evidências atuais em ../../phase-3-review.md.

## Global Constraints

Domain sem EF/framework/pacotes. Somente Fase 3; preservar arquivo original e trabalho local. PostgreSQL17 descartável para testes; não operar banco existente. Cinco tabelas, sem auth/Credential/executions. Secrets só em memória/ambiente. Locks versionados e commits descritivos em português/gabxw. .local/phase-3 guarda ledger/briefs/logs para execução PowerShell nativa.

## Review Focus

1. JSON null vs ausência e ticks/decimal: Task1 testa literais e discriminadores reais.
2. Round-trip de histórico/revisões: Task1 restauração preserva valores e protege coleções; Task3 verifica após outro DbContext.
3. Dois clientes e falha após CAS: Task3 confirma estado integral vencedor e rollback do perdedor, sem somente assert da exceção.
4. Publicar e criar outro draft antes de Save: Task3 testa ordenação que não viola o índice parcial.
5. Snapshot válido mas histórico adulterado: Task3 rejeita troca de publicação existente, sem alterar banco.

## Preparação root

Gerar projeto tests/FlowForge.IntegrationTests com os três pacotes xUnit existentes, Testcontainers.PostgreSql4.16.0 e referência Infrastructure. Infrastructure recebe EFCore/Design10.0.12 e providerNpgsql10.0.3; toolmanifest dotnet-ef10.0.12. Gerar locks/solução e verificar baseline685. Commit bootstrap após restore/build/revisão. Tasks1/2 em worktrees próprias sobre bootstrap para evitar colisões; Task3 aguarda integração revisada.

### Task 1: Reidratação e codec de configuração

**Files:** modificar src/FlowForge.Domain/Workflows/Workflow.cs (partial/factory); criar Workflow.Restoration.cs e WorkflowSnapshot.cs no mesmo diretório. Modificar WorkflowVersion.cs somente se necessário para restauração interna. Criar src/FlowForge.Infrastructure/Persistence/Serialization/NodeConfigurationJson.cs e Properties/AssemblyInfo.cs (InternalsVisibleTo FlowForge.IntegrationTests). Criar tests/FlowForge.Domain.Tests/WorkflowRestorationTests.cs e tests/FlowForge.IntegrationTests/NodeConfigurationJsonTests.cs.

**Interfaces:** produz Workflow.Restore e snapshots com assinaturas da spec; codec interno Serialize/Deserialize com JSON schemaVersion1. Task3 consome todos. Não depende Task2.

- [ ] Escrever testes de restauração com duas publicações e um draft, counters/timestamps literais, ponteiro, status, arquivado, coleções defensivas e falhas de IDs/status/números/data/ponteiro/publicação inválida.
- [ ] Stubs compiláveis → rodar filtro WorkflowRestorationTests e registrar RED funcional antes da factory.
- [ ] Implementar restauração validada sem comandos públicos/revisões novas/IDs novos, preservando imutabilidade.
- [ ] Testar seis codecs com valores literais: Condition Equals JSON null versus Exists sem expectedValue; Transform com path e literal null/objeto/array; precisão decimal/ticks; input malformado, schema e tipos desconhecidos, campos simultâneos/ausentes. Registrar RED antes dos corpos.
- [ ] Implementar codec fechado chamando construtores Domain, sem reflexão. Rodar filtros e suíte inteira disponível na worktree, build0warnings/errors, auto-revisar, commit somente seus arquivos e relatório task-1-report.md.

### Task 2: Modelo EF, migrations e constraints

**Files:** criar Infrastructure/Persistence/FlowForgeDbContext.cs, FlowForgeDbContextFactory.cs, Records/PersistenceRecords.cs, Configurations/{TechnicalUserConfiguration,WorkflowConfiguration,WorkflowVersionConfiguration,WorkflowNodeConfiguration,WorkflowConnectionConfiguration}.cs e Migrations/* gerados. Criar tests/FlowForge.IntegrationTests/{PostgresFixture,PersistenceSchemaTests}.cs.

**Interfaces:** contexto público ctor DbContextOptions; DbSets internos Users/Workflows/WorkflowVersions/WorkflowNodes/WorkflowConnections (records conforme spec). Task3 usa esses nomes. Fixture pública [CollectionDefinition("PostgreSQL")] e [Collection("PostgreSQL")] com PostgreSqlFixture : IAsyncLifetime, propriedades ConnectionString e Factory (IDbContextFactory<FlowForgeDbContext>); testes compartilham schema migrado mas IDs exclusivos. Tabela users mínima; nenhum seed automático. Records/mappings independentes de Task1, sem friend assembly adicional (Task1 adiciona).

- [ ] Testes de migration em container recém-criado, aplicação repetida, has-pending-model-changes falso, insert/constraints por SQL parametrizado. Criar stubcontext vazio/records se necessário e observar RED de funcionalidade ausente; Docker indisponível não equivale a skip.
- [ ] Implementar cinco mappings com keys/FKs/unique parcial/checks/índices da spec. NO ACTION/RESTRICT explícito. Testar relações owner/versão/endpoints/ponteiro, draft duplicado, porta duplicada, mesmo NodeId em versões distintas aceito e delete restritivo.
- [ ] Gerar migration inicial/snapshot com dotnet-ef local e factory env-only; aplicar via MigrateAsync (não EnsureCreated). PostgreSqlBuilder com image explícita evita APIs obsoletas.
- [ ] Rodar testes de schema e suíte inteira disponível, build0warnings/errors; gerar scriptmigration e verificar modelo. Auto-revisar, commit só seus arquivos e relatório task-2-report.md. Não criar codec/store/ports nem editar locks/projetos/docs.

### Task 3: Stores, ownership e concorrência transacional

**Files:** Application/Workflows/{IWorkflowStore,WorkflowSummary,WorkflowConcurrencyException}.cs e Application/Users/ITechnicalUserStore.cs; Infrastructure/Persistence/{WorkflowPersistenceMapper,PostgresWorkflowStore,PostgresTechnicalUserStore}.cs; tests/FlowForge.IntegrationTests/{WorkflowStoreTests,WorkflowStoreConcurrencyTests,TechnicalUserStoreTests}.cs. Pode separar helper WorkflowStoreFixtures.cs nos testes e helper específico do store se clareza exigir, sem abstração genérica.

**Interfaces:** consome snapshots/codec Task1, records/context/fixture Task2. Portas e assinaturas exatamente da spec. Stores recebem IDbContextFactory, um contexto por operação, ct propagado. DTO Summary tem campos da spec.

- [ ] Criar fixtures reais: user técnico, workflow publicado com seis tipos, outro draft e dono distinto. Testar EnsureExists idempotente, round-trip por outro contexto, JSON/ordem, Get/List escopados e paginação, publicação+novo draft e arquivamento. Stubs → RED antes dos corpos.
- [ ] Implementar mapper, ownerstore e Get/List/Add com transações e constraints reais. Get RepeatableRead para leitura coerente; list sem grafo. Add raiz sem ponteiro antes de versões.
- [ ] Testar Add seguido de Save no mesmo agregado com timestamps de ticks 100 ns não múltiplos de 10 e literais JSON com propriedades em outra ordem: não deve ocorrer falso conflito histórico. Mapper/comparações normalizam UTC para microssegundos, igualdade de configurações é semântica JSON.
- [ ] RED para duas leituras na mesma revisão; salvar primeiro, segundo deve falhar WorkflowConcurrencyException e não alterar seu grafo/metadados/ponteiro. RED para falha FK depois de CAS: estado completo/revisão anterior preservados. Contexto novo permite operação válida após falha.
- [ ] Implementar Save com revisão esperada (Revision>expected), CAS e rollback integral; histórico intocado, draft promovido antes de novo draft, nodes/conexões substituídos só no draft e conexão apagada primeiro. Rejeitar metadados identitários/tempo regressivo/arquivo terminal e histórico adulterado por Restore, sem sobrescrita silenciosa.
- [ ] Testar input IDs/revisões/paginação inválidos e cancelamento, sem acessar outro owner. Rodar filtros e suíte completa, auto-revisar transações, build0warnings/errors. Commit próprios arquivos e task-3-report.md; sem push/merge.

### Task 4: Integração, operação e fechamento

**Files root:** README.md, AGENTS.md, docs/{architecture,data-model,roadmap,phase-3-review}.md; docs/decisions/0003-postgresql-persistence.md; docs/persistence.md; spec/plano. CI só adapta timeout/documentação de Docker se medições reais exigirem, mantendo testes inteiros/travados. Compose só comentário de estado, sem segredo/porta nova.

- [ ] Revisar tarefas independentes e integrar commits na worktree. Registrar divergências e corrigir findings importantes com testes focalizados.
- [ ] Tool restore, restorelocked, Release build e solução inteira; dotnet-ef has-pending-model-changes e migrations script. Confirmar685 testes anteriores, novos testes reais e locks compatíveis; verificarUTF8/links/diff.
- [ ] Documentar cinco tabelas, reidratação/JSONB, CAS/rollback/isolamento, índices/arquivo, migrations explícitas e conexão ambiente sem valores secretos; explicar alternativas/trade-offs/entrevista emADR0003. Status honesto atéCI passar.
- [ ] Revisão ampla final; integrar fast-forward main autorizada, verificar resultado e publicar gabxw. Acompanhar CI backend/frontend/containers, ler TRX e atualizar revisão com evidência real em commitdocs. Preservar originais, arquivar logs e limpar só worktrees criadas. Não iniciar Fase4.

## Resultado da continuação

As worktrees e os testes parciais foram preservados; o incremento foi integrado serialmente na worktree de persistência. Os 754 testes locais disponíveis passaram. As verificações com Docker foram executadas no CI, sem declarar falha de ambiente como RED de regra de negócio. A execução 7 aprovou 781 testes sem skip, incluindo 27 com PostgreSQL. Build, locks, drift, script, frontend e containers também passaram. A revisão técnica foi feita pelo agente principal; não houve revisão independente nesta continuação. Detalhes e limitações em [phase-3-review.md](../../phase-3-review.md).
