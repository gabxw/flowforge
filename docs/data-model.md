# Modelo inicial de dados

Status: cinco tabelas de definição implementadas na Fase 3. A Fase 5 acrescenta workflow_executions, outbox_messages e inbox_messages; estado e revisão em [phase-5-review.md](phase-5-review.md). A Fase 6 acrescenta node_executions, execution_logs e checkpoint/contexto/cancelamento; são dez tabelas de aplicação. Extensões seguintes continuam conceituais.

PostgreSQL é a fonte de verdade. UUID identifica os recursos; datas são instantes UTC, armazenados como timestamptz. Estados têm valores explícitos e transições validadas, sem depender da ordem numérica de enums. Configurações variáveis usam JSONB; identidade, ownership, relações e campos consultáveis usam colunas.

## Schema implementado na Fase 3

As cinco tabelas usam UUIDs definidos pelo domínio, timestamptz, nomes snake_case e NO ACTION nas FKs. Nodes têm PK composta (workflow_version_id, node_id); ambos os endpoints da conexão referenciam nodes da mesma versão. FKs compostas também mantêm versão/raiz/proprietário coerentes. O ponteiro opcional de publicação referencia uma versão do mesmo workflow/proprietário.

Há UNIQUE de número de versão, índice parcial de um draft (status=1) por workflow, unicidade de porta de saída e ordinais para preservar a ordem das coleções. A listagem usa (owner_user_id, updated_at DESC, id DESC). Revisões são inteiros não negativos; checks cobrem status/datas, configurações schemaVersion=1, IDs não vazios e posições finitas.

O codec é responsável pela forma completa da configuração; o domínio valida o DAG ao publicar/restaurar publicações. CredentialId é apenas uma referência, sem FK até existir o armazenamento seguro de credenciais. Consulte [ADR 0003](decisions/0003-postgresql-persistence.md) e [operação](persistence.md).

## Schema acrescentado na Fase 5

Execuções fixam versão/workflow/proprietário com FK composta. Outbox tem uma mensagem v1 por execução; inbox associa MessageId/ExecutionId à outbox e impede dois recebimentos independentes do mesmo trabalho. Claims usam token e expiração, e índices parciais atendem ao polling de mensagens ainda não confirmadas.

Os checks originais aceitavam Pending, Running e Failed/engineUnavailable. A migration da Fase 6 amplia estados/resultados, preservando os históricos antigos. CorrelationId e horários continuam persistidos; o contexto cifrado agora reside em workflow_executions. Nenhum material de Credential é resolvido nesta fase.

Consulte [ADR 0005](decisions/0005-durable-execution-dispatch.md) e [operação do despacho](execution-dispatch.md).

## Schema acrescentado na Fase 6

workflow_executions ganha cancel_requested_at, next_node_id, checkpoint_revision e execution_context_protected (bytea). Próximo node referencia o par versão/node da própria execução. node_executions tem FK execução/versão, FK versão/node e unicidade execução/node; guarda estados/datas, AttemptCount e summaries JSONB de tipo/tamanho. execution_logs referencia NodeExecution/ExecutionId, contém event_code, message_byte_length e message_protected; um evento lógico por node Log impede duplicação por replay.

O contexto admite 64 KiB de JSON operacional e até 128 KiB de envelope cifrado. Snapshot não preserva conteúdo nem serve como input de retomada. Logs/inputs/outputs HTTP expõem metadados, nunca ciphertext ou conteúdo. A chave fica em keyring persistente separado do banco; a mensagem Log usa purpose diferente. Não há tabela de tentativas ou scheduler ainda.

ClaimToken identifica o dono; ClaimAttempts é a geração crescente da inbox. Checkpoints validam ambos e a lease, além da revisão. A transação terminal inclui execução, nodes pendentes como Skipped, evento Log quando aplicável e inbox concluída. Consultas de histórico têm teto natural de 50 nodes/50 eventos nesta versão.

[ADR 0006](decisions/0006-sequential-engine-and-checkpoints.md) e [operação da engine](execution-engine.md) detalham captura, keyring, recuperação e cancelamento.

## Relações

```mermaid
erDiagram
    User ||--o{ Workflow : owns
    Workflow ||--o{ WorkflowVersion : versions
    WorkflowVersion ||--|{ WorkflowNode : nodes
    WorkflowVersion ||--o{ WorkflowConnection : connections
    Workflow ||--o{ WebhookEndpoint : endpoints
    WorkflowVersion ||--o{ WorkflowExecution : executions
    WorkflowExecution ||--|{ NodeExecution : nodes
    NodeExecution ||--o{ NodeExecutionAttempt : attempts
    WorkflowExecution ||--o{ ExecutionLog : logs
    User ||--o{ Credential : owns
    User ||--o{ RefreshSession : sessions
```

O diagrama é conceitual: WorkflowConnection também referencia os dois WorkflowNodes; NodeExecution referencia o node da mesma versão que WorkflowExecution. OutboxMessage e InboxMessage são registros de integração descritos adiante.

## Entidades do produto

| Entidade | Campos iniciais propostos | Invariantes principais |
| --- | --- | --- |
| User | Id, EmailNormalized, PasswordHash, Role, CreatedAt, UpdatedAt, DisabledAt | Registro técnico mínimo na Fase 3 para ownership/FKs; credenciais de login e autenticação só na Fase 13. PasswordHash permanece ausente para o proprietário técnico, que não pode autenticar. Nunca persistir senha em texto. |
| Workflow | Id, OwnerUserId, Name, Description, CurrentPublishedVersionId, CreatedAt, UpdatedAt, ArchivedAt, Revision | Proprietário obrigatório. Revision permite detectar edição concorrente. Arquivar preserva histórico. |
| WorkflowVersion | Id, WorkflowId, OwnerUserId, VersionNumber, Status, PublishedAt, CreatedAt, Revision | Status Draft/Published. Uma versão publicada é imutável. Apenas um rascunho ativo por workflow. |
| WorkflowNode | WorkflowVersionId, NodeId, WorkflowId, OwnerUserId, Type, Configuration, CredentialId, PositionX, PositionY | PK composta por versão e NodeId. Type define schema de Configuration. Secret não entra em JSONB. |
| WorkflowConnection | Id, WorkflowVersionId, SourceNodeId, TargetNodeId, SourcePort | Ambos os nodes pertencem à versão. Porta true/false para Condition; next para outros tipos. |
| WorkflowExecution | Id, WorkflowId, WorkflowVersionId, OwnerUserId, Status, TriggerInput, ExecutionContextProtected, CheckpointRevision, CreatedAt, StartedAt, FinishedAt, DeadlineAt, ResumeAt, CancelRequestedAt, LeaseOwner, LeaseExpiresAt, FencingToken, CorrelationId, TraceId, ErrorCode, ErrorSummary | Versão fixa. Datas e estados coerentes. Claims e checkpoints verificam token da lease. |
| NodeExecution | Id, WorkflowExecutionId, WorkflowVersionId, NodeId, Status, StartedAt, FinishedAt, Input, Output, AttemptCount, CredentialRevisionUsed, ErrorCode, ErrorSummary | Um registro lógico por execução/node. A versão deve ser a da execução. Input/output sanitizados e limitados. |
| Credential | Id, OwnerUserId, Name, Type, ProtectedValue, Revision, CreatedAt, UpdatedAt, RevokedAt | Valor protegido por criptografia autenticada. API retorna metadados, nunca ProtectedValue. |
| WebhookEndpoint | Id, WorkflowId, OwnerUserId, SecretHash, Enabled, CreatedAt, RotatedAt | ID público na URL. Secret em header, hash no banco. Apenas aceita workflow com versão publicada. |
| ExecutionLog | Id, WorkflowExecutionId, NodeExecutionId, Level, EventCode, Message, SanitizedProperties, CreatedAt | Log sem segredos. NodeExecutionId opcional, porém precisa pertencer à execução quando informado. |

NodeId pode ser reutilizado entre versões para representar a mesma posição lógica do editor. Por isso, uma FK apenas em NodeId seria incorreta. A identidade de um node publicado é o par WorkflowVersionId/NodeId.

CurrentPublishedVersionId é uma referência opcional da Workflow para uma versão publicada do mesmo workflow. Ao aceitar um webhook, esse ponteiro é lido e WorkflowExecution fixa a versão dentro da transação. A execução nunca consulta o ponteiro para decidir qual grafo carregar.

A posição visual pertence à definição; não influencia ordem de execução. Ordem vem das conexões e da seleção de porta da Condition.

### Estados

WorkflowExecution: Pending → Running → Succeeded, Failed ou Cancelled. Pode passar diretamente de Pending para Cancelled. Retomada depois de uma suspensão não cria uma segunda execução. Running com ResumeAt futuro representa Delay ou retry durável.

NodeExecutionStatus: Pending, Running, Succeeded, Failed, Retrying, Skipped e Cancelled. Cancelled foi adotado na Fase 2 para um node interrompido. Pending pode transicionar para Running ou Skipped; Running para Succeeded, Failed, Retrying ou Cancelled; Retrying para Running ou Cancelled. A execução futura usará Skipped para nodes não iniciados e preservará as tentativas anteriores ao retomar Retrying.

As políticas puras de transição da Fase 2 rejeitam estados desconhecidos, repetições e saídas de estados terminais. Elas não implementam redelivery nem persistência: os futuros casos de uso precisam aplicá-las e garantir que uma atualização SQL não transforme uma execução terminal de volta em Running.

## Registros de confiabilidade e autenticação

| Registro | Fase | Campos propostos e propósito |
| --- | --- | --- |
| OutboxMessage | 5 | Id/MessageId, Type, ContractVersion, WorkflowExecutionId, Payload, CreatedAt, AvailableAt, PublishedAt, AttemptCount, LastErrorCode. Publicação confiável a partir da mesma transação da execução ou continuação. |
| InboxMessage | 5 | ConsumerName, MessageId, WorkflowExecutionId, Status, ReceivedAt, CompletedAt. PK em ConsumerName/MessageId; Received não equivale a processamento concluído. |
| WebhookIdempotencyRecord | 7, completado na 10 | OwnerUserId, WebhookEndpointId, KeyDigest, RequestHash, WorkflowExecutionId, CreatedAt, ExpiresAt. Reserva atômica da chave e comparação do conteúdo. |
| NodeExecutionAttempt | 10 | Id, NodeExecutionId, AttemptNumber, StartedAt, FinishedAt, Status, ErrorCode, ErrorSummary, DurationMs, ExternalOutcome, CredentialRevisionUsed. Uma linha por tentativa, inclusive falha e resultado externo desconhecido. |
| RefreshSession | 13 | Id, UserId, FamilyId, TokenHash, CreatedAt, ExpiresAt, RevokedAt, ReplacedBySessionId, ReuseDetectedAt. Token opaco com alta entropia, apenas hash no banco, rotação e revogação de família. |

Outbox e inbox são suportes de integração, não eventos de domínio completos e nem um sistema de event sourcing. O payload de mensagem contém IDs e contexto técnico; o Worker busca o input autorizado no PostgreSQL.

NodeExecution conserva a visão lógica do node. NodeExecutionAttempt evita sobrescrever uma falha anterior com o sucesso posterior. AttemptCount é um resumo derivado e deve ser atualizado na mesma transação da tentativa.

ExternalOutcome distingue KnownSuccess, KnownFailure e Unknown. Uma falha de transporte depois de enviar a requisição não permite afirmar que o efeito remoto não aconteceu.

CredentialRevisionUsed registra a versão de metadados utilizada, sem congelar ou copiar o segredo. Publicar um workflow congela o grafo, não o estado de serviços externos. A política de rotação entre tentativas será definida na Fase 8 e testada; não assumimos replay idêntico de chamadas externas.

## Integridade referencial e ownership

A proposta usa constraints para evitar associações entre versões e proprietários, além da autorização de aplicação:

- Workflow: PK Id e unique em Id/OwnerUserId.
- WorkflowVersion: FK WorkflowId/OwnerUserId → Workflow Id/OwnerUserId; uniques WorkflowId/VersionNumber e Id/WorkflowId/OwnerUserId.
- WorkflowNode: PK WorkflowVersionId/NodeId; FK WorkflowVersionId/WorkflowId/OwnerUserId → WorkflowVersion Id/WorkflowId/OwnerUserId.
- WorkflowConnection: FK WorkflowVersionId/SourceNodeId e WorkflowVersionId/TargetNodeId → WorkflowNode WorkflowVersionId/NodeId.
- Credential: unique Id/OwnerUserId. WorkflowNode CredentialId/OwnerUserId → Credential Id/OwnerUserId, quando houver credencial.
- WorkflowExecution: FK WorkflowVersionId/WorkflowId/OwnerUserId → WorkflowVersion Id/WorkflowId/OwnerUserId; unique Id/WorkflowVersionId.
- NodeExecution: FK WorkflowExecutionId/WorkflowVersionId → WorkflowExecution Id/WorkflowVersionId; FK WorkflowVersionId/NodeId → WorkflowNode WorkflowVersionId/NodeId; unique WorkflowExecutionId/NodeId e Id/WorkflowExecutionId.
- WebhookEndpoint: FK WorkflowId/OwnerUserId → Workflow Id/OwnerUserId.
- ExecutionLog: FK WorkflowExecutionId → WorkflowExecution; FK NodeExecutionId/WorkflowExecutionId → NodeExecution Id/WorkflowExecutionId quando NodeExecutionId estiver presente.
- Workflow CurrentPublishedVersionId/Id/OwnerUserId → WorkflowVersion Id/WorkflowId/OwnerUserId, quando houver publicação.

A duplicação controlada de OwnerUserId e WorkflowVersionId permite constraints entre recursos que precisam compartilhar ownership e versão. Essa escolha custa colunas e índices adicionais; evita confiar apenas em validação HTTP para impedir relações inválidas.

Uma constraint simples não comprova que a versão apontada está Published nem que o grafo é acíclico. Publicação continua sendo uma operação de domínio e uma transação de aplicação. Não usamos triggers SQL como engine de negócio.

As buscas são sempre escopadas pelo usuário autenticado. Antes da autenticação, o ambiente privado pode usar um proprietário técnico explícito; isso não será apresentado como autorização implementada.

## Índices iniciais candidatos

| Índice | Consulta ou garantia atendida |
| --- | --- |
| User EmailNormalized unique | Login sem duplicação de conta |
| Workflow OwnerUserId/UpdatedAt/Id | Listagem paginada de workflows do usuário |
| WorkflowVersion WorkflowId/VersionNumber unique | Número de versão consistente |
| WorkflowVersion WorkflowId unique parcial onde Status = Draft | Apenas um rascunho ativo |
| WorkflowConnection WorkflowVersionId/SourceNodeId/SourcePort unique | Impedir dois destinos na mesma porta |
| WorkflowConnection WorkflowVersionId/TargetNodeId | Validação e navegação reversa do grafo |
| WorkflowExecution OwnerUserId/CreatedAt/Id | Histórico paginado e autorização |
| WorkflowExecution WorkflowId/CreatedAt/Id | Histórico por workflow |
| WorkflowExecution ResumeAt/Id parcial para execuções retomáveis | Scheduler de Delay e retry |
| WorkflowExecution LeaseExpiresAt/Id parcial onde Status = Running | Recuperar claims vencidos |
| NodeExecution WorkflowExecutionId/NodeId unique | Uma execução lógica por node |
| NodeExecutionAttempt NodeExecutionId/AttemptNumber unique | Tentativas sem sobrescrita e sem duplicação |
| ExecutionLog WorkflowExecutionId/CreatedAt/Id | Timeline ordenada e paginação |
| OutboxMessage AvailableAt/Id parcial onde PublishedAt é nulo | Dispatcher sem varrer histórico publicado |
| InboxMessage ConsumerName/MessageId unique | Deduplicação por consumidor |
| WebhookIdempotencyRecord OwnerUserId/WebhookEndpointId/KeyDigest unique | Chave de idempotência isolada por endpoint e dono |
| RefreshSession TokenHash unique; UserId/FamilyId | Encontrar sessão e revogar família |

Esses índices são hipóteses de acesso, não uma autorização para criar todos imediatamente. A Fase 3 introduz apenas os necessários ao fluxo existente; scheduler, outbox e autenticação adicionam seus índices junto das funcionalidades. EXPLAIN e volume representativo devem orientar otimização posterior. Não há GIN genérico em todos os campos JSONB.

## JSONB, sanitização e retenção

Configuration armazena somente parâmetros permitidos pelo schema do node, incluindo paths JSON e referências a credenciais. Não aceita objetos arbitrários para executar scripts ou escolher classes pelo nome.

TriggerInput, Input e Output preservam snapshots sanitizados e limitados. A captura de um resultado truncado inclui um indicador de truncamento e o tamanho original; ele não pode ser reaproveitado silenciosamente como input real de outro node. O contexto de execução e os snapshots de auditoria são conceitos diferentes: a engine trabalha com conteúdo permitido dentro do limite operacional, e a visualização recebe sua representação sanitizada.

Conteúdo necessário para retomada deve estar durável antes de confirmar a mensagem. ExecutionContextProtected contém o contexto operacional serializado, limitado e criptografado, em bytea, separado dos snapshots JSONB. O contexto cifra e retém payload privado: na Fase 6 conserva o input corrente necessário ao próximo node (inicialmente {}), sem material de credenciais resolvidas ou headers secretos. Preservação independente do payload inicial de webhook será definida na Fase 7. A Fase 6 implementa limite de 64 KiB e captura apenas de metadados; retenção e limpeza ainda são propostas do MVP, sem implementação. CheckpointRevision e fencing protegem sua atualização. A engine não pode depender exclusivamente de objetos em memória nem usar um snapshot truncado para continuar depois de uma queda. O keyring persistente precisa existir antes dessa persistência, com purpose distinto do utilizado para Credential.

Direção inicial de retenção, ainda configurável e pendente de implementação:

- Contexto operacional protegido: preservado durante uma execução retomável e eliminado após a janela de investigação definida; proposta inicial de até 7 dias após término.
- Snapshots de payload: 7 dias em desenvolvimento/demonstração, com possibilidade de captura desativada ou allowlist de campos. Desativar snapshots não elimina a persistência operacional necessária à execução.
- Metadados de execução e logs técnicos sanitizados: 30 dias.
- Outbox publicada, inbox concluída e registros de idempotência: expiração somente depois da janela documentada de redelivery/replay; nunca remover deduplicação enquanto ainda for possível reprocessar a mensagem correspondente.
- Refresh sessions: limpeza após expiração/revogação respeitando a janela de detecção de reutilização.
- Credentials e keyring: rotação e revogação explícitas; nunca tratadas como cache descartável.

A limpeza é uma operação em lotes pequenos e observáveis. A Fase 10 define a relação entre prazo de execução, validade de mensagens, retenção de inbox e replay; a Fase 16 fecha o procedimento operacional.

Excluir workflow não apaga automaticamente histórico. O MVP arquiva workflows e revoga endpoints; exclusão definitiva só entra com uma política explícita de retenção e de relações. Credentials referenciadas não são removidas fisicamente sem tratar os workflows dependentes.

## Concorrência e transações

Revision é um contador explícito para edições concorrentes do rascunho: o cliente informa a revisão lida, e conflito retorna uma resposta apropriada. Não se usa last-write-wins silencioso.

As transações importantes são: publicar versão e trocar ponteiro ativo; aceitar webhook, reservar idempotência, criar execução e outbox; gravar checkpoint/tentativa e sua continuação; renovar lease e validar fencing token.

Claims usam atualização atômica e condição de status/vencimento. Scheduler e dispatcher podem selecionar lotes com SKIP LOCKED dentro de transações curtas. Nenhuma transação fica aberta durante HTTP, Delay ou espera no broker.

O esquema fornece garantias locais. Não há transação distribuída entre PostgreSQL, RabbitMQ e um destino HTTP.
