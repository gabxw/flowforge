# Fase 3 — Persistência PostgreSQL

Data: 2026-10-09. Base: 0622f800954a9de8336978d1f55b7eaf59941ef0.

## Intenção e escopo

O usuário autorizou continuar a próxima fase do roadmap, preservando execução incremental, testes, revisão, commits em português sem prefixos e publicação na conta gabxw. Esta entrega persiste o domínio da Fase 2 e comprova integridade, ownership e concorrência em PostgreSQL real. Fase 4 e posteriores permanecem futuras. O arquivo original e os arquivos locais preexistentes são preservados.

Cinco tabelas: users (registro técnico Id/CreatedAt), workflows, workflow_versions, workflow_nodes e workflow_connections. Não há senha/login, Credential, entidades de execução ou mensageria. API/Worker continuam com os hosts existentes; os adaptadores são exercitados por testes e migrations explícitas. Não executar migrations automaticamente no startup nem alterar bancos existentes do usuário.

## Arquitetura e alternativas

Escolha: registros de armazenamento internos e mappings em Infrastructure; portas específicas em Application; reidratação validada no Domain, sem EF. Mapeamento direto do agregado exigiria binding especial das coleções/hierarquia imutáveis. Armazenar o grafo inteiro num JSON eliminaria as FKs entre versões e nodes. A representação relacional adicional fica limitada à fronteira de armazenamento, sem repositório genérico.

Dependências verificadas no registry NuGet: Microsoft.EntityFrameworkCore e Design 10.0.12; Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3; Testcontainers.PostgreSql 4.16.0; dotnet-ef local 10.0.12. Design é PrivateAssets=all. Pacotes de teste mantêm versões existentes. PostgreSQL usa a mesma linha postgres:17-alpine do Compose. Locks regenerados e restore travado em toda verificação final.

## Reidratação de domínio

Namespace FlowForge.Domain.Workflows. Criar records de transporte:

- `WorkflowSnapshot(Guid Id, Guid OwnerUserId, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt, int Revision, Guid? CurrentPublishedVersionId, IReadOnlyList<WorkflowVersionSnapshot> Versions)`.
- `WorkflowVersionSnapshot(Guid Id, int VersionNumber, WorkflowVersionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, int Revision, IReadOnlyList<WorkflowNode> Nodes, IReadOnlyList<WorkflowConnection> Connections)`.
- `Workflow.Restore(WorkflowSnapshot snapshot)` devolve um agregado independente. Pode ficar em Workflow.Restoration.cs usando partial; não expor setters/mutadores novos na versão.

Restaurar IDs, números, revisões, datas UTC, ponteiro, draft e publicações exatamente, sem incrementar contadores ou gerar IDs. Copiar todas as coleções; elementos continuam imutáveis. Rejeitar nulos, IDs vazios/duplicados, revisões negativas, números de versão não contíguos a partir de 1, status desconhecido, múltiplos drafts, draft que não seja a última versão, status/data de publicação incoerentes, timestamps fora da vida do agregado e ponteiro divergente da última publicação (ou não nulo sem publicação). ArchivedAt, quando presente, coincide com UpdatedAt. Toda versão publicada passa pelo validador de grafo. Draft pode continuar incompleto conforme a Fase 2. As versões recebem WorkflowId e OwnerUserId do agregado.

Unicidade: versão.Id e conexão.Id em todo o agregado; NodeId dentro da versão (pode repetir em outras versões). Add/Save/mapper rejeitam WorkflowVersionId de node/conexão divergente da versão que contém sua lista, inclusive no draft. Nunca reparentar silenciosamente IDs fornecidos. Draft não precisa passar pelas regras de DAG/publicação para ser persistido.

## Configuração JSONB

`NodeConfigurationJson` interno em FlowForge.Infrastructure.Persistence.Serialization: `string Serialize(NodeConfiguration configuration)` e `NodeConfiguration Deserialize(NodeType type, string json)`. JSON usa schemaVersion inteiro 1, propriedades camelCase e discriminador NodeType da coluna; nenhum nome de classe, reflection ou ativação de tipos externos. Rejeitar schema desconhecido, forma inválida, propriedades desconhecidas, membros obrigatórios ausentes e fontes/literais simultâneos.

- Webhook: `{schemaVersion:1}`.
- HTTP: url (string HTTPS), method (inteiro do enum).
- Delay: durationTicks (inteiro Int64), preservando precisão TimeSpan.
- Log: message (string).
- Condition: sourcePointer (string), operation (inteiro), expectedValue opcional. Diferenciar ausência de literal e literal JSON null.
- Transform: fields (array), cada item com targetProperty e exatamente sourcePointer (string) ou literal (JSON arbitrário definido, inclusive null).

Desserialização chama construtores validados do Domain e clona JSON. JSONB conserva significado JSON, não formatação/ordem de propriedades de objetos. As listas de nodes, conexões e campos Transform preservam ordem. O armazenamento pode rejeitar valores JSON que PostgreSQL não represente; a transação não deixa alterações parciais.

## Modelo relacional

Namespace FlowForge.Infrastructure.Persistence. `FlowForgeDbContext(DbContextOptions<FlowForgeDbContext> options)` público; records internos em Records e mapeamentos em Configurations. Tabelas/colunas/constraints com nomes snake_case explícitos, UUIDs fornecidos pelo domínio, timestamptz UTC. Sem lazy loading.

DateTimeOffset no Domain conserva ticks de 100 ns; PostgreSQL timestamptz conserva microssegundos. Mapper/store normalizam UTC truncando UtcTicks para múltiplo de 10 na gravação e nas comparações identitárias/históricas. Restore conserva exatamente os valores do snapshot recebido. Round-trip de banco conserva instantes na precisão documentada de microssegundos. Add seguido de Save no mesmo agregado com ticks fracionários deve funcionar, sem falso conflito de CreatedAt/publicação.

- TechnicalUserRecord: Id, CreatedAt. Tabela users, PK id.
- WorkflowRecord: Id, OwnerUserId, Name, Description, CreatedAt, UpdatedAt, ArchivedAt, Revision, CurrentPublishedVersionId. PK id; alternate key (id,owner_user_id); FK owner_user_id→users.id; FK (current_published_version_id,id,owner_user_id)→versions(id,workflow_id,owner_user_id), opcional. Revision como concurrency token. Índice owner_user_id/updated_at/id.
- WorkflowVersionRecord: Id, WorkflowId, OwnerUserId, VersionNumber, Status, CreatedAt, PublishedAt, Revision. PK id; alternate key (id,workflow_id,owner_user_id); FK (workflow_id,owner_user_id)→workflows(id,owner_user_id); unique (workflow_id,version_number); unique parcial workflow_id WHERE status=1 (Draft).
- WorkflowNodeRecord: WorkflowVersionId, NodeId, WorkflowId, OwnerUserId, Type, Configuration (string/jsonb), CredentialId opcional, PositionX, PositionY, Ordinal. PK (workflow_version_id,node_id); FK tripla para versão; unique (workflow_version_id,ordinal).
- WorkflowConnectionRecord: Id, WorkflowVersionId, SourceNodeId, TargetNodeId, SourcePort, Ordinal. PK id; FKs (workflow_version_id,source_node_id) e (workflow_version_id,target_node_id)→nodes; unique (workflow_version_id,source_node_id,source_port), unique (workflow_version_id,ordinal); índice (workflow_version_id,target_node_id).

CHECKs: UUIDs não vazios; revisões/ordinais não negativos; número de versão positivo; enum status 1/2 e tipo 1..6; PublishedAt presente somente para Published e >=CreatedAt; timestamps coerentes; nome não vazio até 200 e descrição até 2000; source_port não vazio; credencial somente em HTTP; configuração raiz JSON object e schemaVersion=1; posições finitas. SQL não prova DAG, configuração completa, status do ponteiro ou existência da credencial. O mapper e o domínio guardam esses contratos. CredentialId sem FK até Fase 8, com owner declarado validado antes de gravar e reconstruído a partir do owner do node.

Todas as FKs têm NO ACTION/RESTRICT explícito. Arquivamento mantém versões; não há exclusão física pública. Rascunhos incompletos podem ser salvos, mas duplicações, portas repetidas ou referências quebradas são recusadas pelos constraints. Ordinal preserva a ordem das listas sem atribuir semântica de execução.

Criar migration inicial e snapshot via dotnet-ef, não EnsureCreated. `FlowForgeDbContextFactory` implementa IDesignTimeDbContextFactory e exige FLOWFORGE_POSTGRES_CONNECTION_STRING no ambiente, sem default de produção ou impressão de seu valor. Migrations/rollback são comandos explícitos, aplicados nos testes somente ao container descartável. Sem SQL triggers de negócio.

## Portas e adaptadores

Namespace FlowForge.Application.Workflows:

- `IWorkflowStore.AddAsync(Workflow workflow, CancellationToken cancellationToken = default)` retorna Task.
- `GetAsync(Guid workflowId, Guid ownerUserId, CancellationToken cancellationToken = default)` retorna Task<Workflow?>.
- `SaveAsync(Workflow workflow, int expectedRevision, CancellationToken cancellationToken = default)` retorna Task.
- `ListAsync(Guid ownerUserId, int offset = 0, int limit = 20, bool includeArchived = false, CancellationToken cancellationToken = default)` retorna Task<IReadOnlyList<WorkflowSummary>>.
- WorkflowSummary record: Id, Name, Description, CreatedAt, UpdatedAt, ArchivedAt, Revision, CurrentPublishedVersionId.
- WorkflowConcurrencyException: exceção específica, sem detalhes de conexão. Consulta/save com owner errado não revelam nem modificam outro workflow; Get retorna null, Save lança KeyNotFoundException.

Namespace FlowForge.Application.Users: `ITechnicalUserStore.EnsureExistsAsync(Guid id, DateTimeOffset createdAt, CancellationToken cancellationToken = default)` retorna Task. Upsert idempotente preserva CreatedAt inicial; ID vazio rejeitado. Não representa autorização ou autenticação.

Adaptadores `PostgresWorkflowStore` e `PostgresTechnicalUserStore` em Infrastructure.Persistence, recebem IDbContextFactory<FlowForgeDbContext>. Cada operação usa/dispose um contexto próprio e propaga CancellationToken. `WorkflowPersistenceMapper` interno converte records/configurações/snapshots. Sem connection strings embutidas, sensitive-data logging ou retries que sobrescrevam conflitos.

Get faz leitura coerente em transação curta RepeatableRead para raiz/versões/grafos consultados separadamente, sempre escopados por workflow/owner. List ordena UpdatedAt desc, Id desc, filtra owner e arquivados por padrão; offset>=0 e limit 1..100, UUID owner válido. Não carregar grafo para lista.

Add usa transação: inserir raiz sem ponteiro, inserir versões/grafos, atualizar ponteiro, commit. Save exige expectedRevision>=0 e Revision>expectedRevision. Carrega raiz pelo id/owner, verifica identidades/CreatedAt e tempo sem retrocesso; raiz já arquivada é terminal. Atualiza raiz via compare-and-swap SQL WHERE id/owner/revision esperada, lançando WorkflowConcurrencyException se zero linhas. A revisão nova, metadados e demais alterações fazem parte da mesma transação. O ponteiro para publicação nova só muda depois de inserir a versão correspondente.

Save nunca remove nem altera publicação existente, e rejeita divergência de seus dados/grafo/revisão ou desaparecimento de qualquer versão persistida. Só substitui nodes/conexões do draft anterior (excluir conexões antes de nodes) e acrescenta versões novas. Promover o draft antigo deve acontecer antes de inserir um draft novo, para respeitar o índice parcial. Aceitar várias operações de domínio antes de um save, inclusive publicar e abrir outro draft. Falha de FK/unique/configuração/cancelamento reverte também o CAS, os metadados e o ponteiro. Após falha, contexto é descartado; nenhuma alteração fica pronta para flush posterior.

Comparar configurações/publicações por significado JSON (objetos sem depender da ordem de propriedades, números por valor), não por strings GetRawText/Serialize, pois JSONB normaliza a representação. Precisão de timestamps usa a normalização acima; ordinais preservam ordem das listas.

## Verificação

Testes Domain de reidratação válida/inválida e cópia defensiva; testes de codec com as seis variantes, JSON null/ausência, ticks, decimal e erros. Testcontainers sobe PostgreSQL17 com senha temporária aleatória e porta dinâmica; fixture migra banco novo, sem reutilização/skip se Docker indisponível. Testes usam dados próprios por caso. Sem InMemory/SQLite/mock de DbContext.

Integração: migration aplicada/idempotente e modelo sem drift; raw SQL rejeita FKs de owner/versão/endpoints/ponteiro, duplicações e índices parciais; round-trip histórico/draft/arquivamento/configurações; consultas isoladas/paginação; edição concorrente por duas leituras e gravações independentes, perdedor sem efeitos; falha após CAS reverte tudo; publicação e novo draft em um save; publicação histórica não pode ser substituída via snapshot reconstruído; leitura usa configurações válidas e listas protegidas.

Verificação final: tool restore, restore --locked-mode, build Release, suíte inteira (inclui os 685 testes anteriores), migrations has-pending-model-changes/script, revisão independente por tarefa e conjunto, CI completo backend/frontend/containers. Documentar ADR0003, modelo implementado, operação/migrations, riscos e revisão da Fase 3. Integrar/publicar na main autorizada e acompanhar CI real antes do fechamento. Não iniciar Fase 4.

## Referências primárias

- [Concorrência otimista EF Core](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)
- [Construtores e materialização EF Core](https://learn.microsoft.com/en-us/ef/core/modeling/constructors)
- [Npgsql EF Core 10](https://www.npgsql.org/efcore/release-notes/10.0.html)
- [PostgreSQL Testcontainers .NET](https://dotnet.testcontainers.org/modules/postgres/)
