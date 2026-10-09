# Fase 2 — Domínio de workflows

## Intenção e escopo

Implementar a próxima fase autorizada do FlowForge: uma definição publicável de workflow, independente de frameworks, que possa ser testada e explicada em entrevista. A Fase 1 está concluída. Esta fase não cria banco, API CRUD, executores, fila, credenciais persistidas, autenticação ou engine. Os contratos partem de architecture.md, data-model.md, ADR 0001 e do critério de saída da Fase 2 no roadmap.

O código usa .NET 10, somente a biblioteca padrão no Domain, e xUnit 2.9.3 no novo FlowForge.Domain.Tests. UUIDs não vazios identificam recursos; instantes são normalizados para UTC. Enums têm números explícitos. Não entram repositories genéricos, mediator, plugins ou classes-base universais.

## Alternativas e escolha

1. Entidades públicas mutáveis tornam a edição simples, mas permitem alterar publicações por referências compartilhadas.
2. Copiar todo o grafo para outro modelo ao publicar duplica contratos e exige sincronizar duas representações.
3. Escolha: valores imutáveis de definição e um agregado Workflow que controla rascunho, publicação, metadados e arquivamento. WorkflowVersion só expõe leitura; métodos internos são usados pelo agregado. Uma validação pura permite testar grafos incompletos sem construir uma API.

O custo é copiar pequenas coleções ao substituir o grafo e ao iniciar novo rascunho. O limite de 50 nodes mantém esse custo previsível. Uma futura camada de persistência deverá preservar essas invariantes e adicionar controle transacional; o modelo em memória não promete concorrência entre processos.

## Tipos e configurações

Namespace `FlowForge.Domain.Workflows`. NodeType: WebhookTrigger=1, HttpRequest=2, Delay=3, Condition=4, TransformJson=5, Log=6. NodePosition contém X/Y finitos. CredentialReference contém Id/OwnerUserId não vazios; só HttpRequest pode carregar essa referência. Não há valor secreto nem consulta externa no domínio. A aplicação futura resolve a credencial real e verifica existência, propriedade e revogação; a publicação já rejeita referência com proprietário divergente.

`WorkflowNode(Guid workflowVersionId, Guid nodeId, NodeType type, NodeConfiguration configuration, NodePosition position = default, CredentialReference? credential = null)` possui somente getters e rejeita tipo desconhecido ou configuração incompatível. `WorkflowConnection(Guid id, Guid workflowVersionId, Guid sourceNodeId, Guid targetNodeId, string sourcePort)` também é imutável. IDs vazios e porta nula/em branco são erros de argumento; porta desconhecida pode existir em rascunho e é rejeitada na publicação. NodeId pode reaparecer em outra versão.

NodeConfiguration é uma hierarquia fechada no assembly, sem extensão por código de usuário. Cada variante informa seu NodeType:

- WebhookTriggerConfiguration(): sem parâmetros nesta fase.
- HttpRequestConfiguration(Uri url, HttpRequestMethod method): URL HTTPS absoluta, sem userinfo/fragmento, e método Get=1/Post=2/Put=3/Patch=4/Delete=5/Head=6/Options=7. Não há request, DNS ou garantia de segurança de rede: allowlist/SSRF/timeout/body entram na Fase 8.
- DelayConfiguration(TimeSpan duration): maior que zero e até 24 horas.
- LogConfiguration(string message): mensagem fixa não vazia, até 2.000 unidades UTF-16. Não existe interpolação executável.
- ConditionConfiguration(JsonPointer sourcePointer, ConditionOperator operation, JsonElement? expectedValue = null): contrato adiante.
- TransformJsonConfiguration(IEnumerable<TransformField> fields): cópia defensiva de 1 a 50 campos com destino não vazio de até 128 unidades UTF-16, sem duplicação ordinal.

JSON recebido em configurações é clonado para sobreviver ao descarte do JsonDocument original. JsonElement Undefined é rejeitado. Coleções públicas não permitem modificar os arrays/listas internos. Configurações não possuem setters ou coleções mutáveis expostas.

## Condition e Transform: contrato sem execução

JsonPointer(string value) admite a forma string do [RFC 6901](https://www.rfc-editor.org/rfc/rfc6901.html): vazio representa a raiz; `/total` seleciona uma propriedade; `~0` e `~1` escapam `~` e `/`. Rejeitar fragmentos URI, falta da barra inicial, escapes inválidos e Unicode malformado. Limites: 1.024 unidades UTF-16 e 32 segmentos. Tokens como `*` são nomes literais, sem wildcard, filtros ou funções. Validação da existência e do índice de array depende do input e pertence ao executor futuro.

ConditionOperator: Exists=1, Equals=2, NotEquals=3, GreaterThan=4, GreaterThanOrEqual=5, LessThan=6, LessThanOrEqual=7. Exists não aceita expectedValue. Os demais exigem literal escalar JSON: string, número, booleano ou null; operadores ordenados exigem número. Objetos/arrays/Undefined não são comparandos válidos. Todos os comparandos numéricos, inclusive os de igualdade, devem admitir conversão para decimal por JsonElement.TryGetDecimal; o executor usará a mesma conversão e reportará erro se o input exceder esse domínio numérico. Sem conversão de strings em números.

Na Fase 9: Exists retorna falso para campo ausente e verdadeiro para campo presente, inclusive null. Nos demais operadores, fonte ausente gera erro determinístico. Igualdade compara escalares por tipo/valor (números por valor decimal); tipos diferentes são distintos. Comparação ordenada com tipo não numérico gera erro. Condition repassa o input, escolhendo exclusivamente true ou false. Esta fase valida o contrato, sem avaliar payloads.

TransformField.FromPath(string targetProperty, JsonPointer sourcePointer) ou TransformField.FromValue(string targetProperty, JsonElement literal) torna impossível definir fonte e literal simultaneamente. Literais JSON podem ser escalares, objetos ou arrays; não existe avaliação de seu conteúdo. Na Fase 9, Transform produz novo objeto somente com os campos declarados, sem alterar o input; fonte ausente gera erro determinístico. Não há JSONPath, eval, scripts ou operações arbitrárias.

## Validação do grafo

`WorkflowGraphValidator.Validate(Guid workflowVersionId, Guid ownerUserId, IReadOnlyList<WorkflowNode> nodes, IReadOnlyList<WorkflowConnection> connections)` retorna lista somente leitura de GraphValidationError(Code, Message, NodeId?, ConnectionId?). GraphErrorCode tem valores explícitos. Limites: 50 nodes e 100 conexões; exceder um limite retorna erro sem percorrer o grafo. Erros independentes são acumulados; a publicação só ocorre se a lista estiver vazia.

Rejeitar: grafo vazio, contagem de trigger diferente de um, NodeId duplicado, ID de conexão duplicado, node/conexão de outra versão, endpoint inexistente, credencial de outro dono, entrada no trigger, porta inválida ou repetida para a mesma origem, Condition sem uma conexão true e uma false, ciclo e node inalcançável.

Nodes comuns têm zero ou uma conexão next; Condition exige exatamente uma por true/false. Portas são strings ordinais e sensíveis a maiúsculas. Trigger sozinho é publicável. As duas portas de Condition podem apontar para o mesmo destino; convergências são válidas porque só um ramo executa. Não tratar múltiplas entradas como paralelismo. Usar busca a partir do trigger para alcance e ordenação topológica de Kahn para ciclo, contando cada aresta (inclusive duas portas com o mesmo par origem/destino). Posição visual não determina ordem.

## Ciclo de vida

`Workflow(Guid id, Guid ownerUserId, string name, DateTimeOffset createdAt, string? description = null)` começa sem versões. Nome é normalizado com Trim e tem 1..200 unidades UTF-16; descrição opcional até 2.000. Workflow expõe Id/OwnerUserId/Name/Description/CreatedAt/UpdatedAt/ArchivedAt/Revision/CurrentPublishedVersionId/DraftVersion/Versions.

- `CreateDraft(Guid versionId, DateTimeOffset now)` retorna WorkflowVersion. Permite somente um draft e IDs de versão únicos. VersionNumber é atribuído na criação (1, 2, ...). O primeiro é vazio; os próximos copiam a publicação ativa, preservando NodeId e configurações imutáveis, trocando WorkflowVersionId e criando IDs novos para conexões.
- `ReplaceDraftGraph(IEnumerable<WorkflowNode> nodes, IEnumerable<WorkflowConnection> connections, DateTimeOffset now)` copia as coleções e aceita grafos incompletos/temporariamente inválidos. Não aceita coleções/elementos nulos. A validação completa ocorre ao publicar.
- `PublishDraft(DateTimeOffset now)` retorna a mesma versão, muda Draft=1 para Published=2, grava PublishedAt, troca o ponteiro ativo e remove o draft ativo. Validação ou data inválida não modifica estado, revisões nem ponteiro. Publicação anterior permanece acessível e intacta.
- `UpdateDetails(string name, string? description, DateTimeOffset now)` altera somente os metadados do workflow.
- `Archive(DateTimeOffset now)` é terminal para edição/criação/publicação e preserva versões/ponteiro. Não há restore nesta fase.

WorkflowVersion expõe Id/WorkflowId/OwnerUserId/VersionNumber/Status/CreatedAt/PublishedAt/Revision/Nodes/Connections, sem métodos públicos de alteração. Operações incrementam revisões explicitamente; alterações no grafo incrementam a revisão do workflow e do draft. Instantes não podem retroceder UpdatedAt. Mudanças são preparadas/validadas antes de mutar o agregado. WorkflowValidationException expõe cópia somente leitura dos erros de publicação.

## Estados operacionais iniciais

Namespace `FlowForge.Domain.Executions`. Não criar entidades de execução: somente enums e `ExecutionTransitions.CanTransition(from, to)`/`EnsureTransition(from, to)` sobrecargas para os dois tipos. EnsureTransition lança InvalidOperationException para transição não permitida. Valores de enum desconhecidos, repetições do mesmo estado e saídas de terminal são rejeitados.

- WorkflowExecutionStatus: Pending=1, Running=2, Succeeded=3, Failed=4, Cancelled=5. Pending → Running/Cancelled. Running → Succeeded/Failed/Cancelled.
- NodeExecutionStatus: Pending=1, Running=2, Succeeded=3, Failed=4, Retrying=5, Skipped=6, Cancelled=7. Pending → Running/Skipped. Running → Succeeded/Failed/Retrying/Cancelled. Retrying → Running/Cancelled. Succeeded/Failed/Skipped/Cancelled são terminais.

Cancelled do node passa de proposta a decisão do domínio. Idempotência de comandos/redelivery e persistência dessas transições pertencem às fases operacionais futuras.

## Verificação e critérios de saída

Testes reais de domínio, sem mocks: definição/configuração, grafo aceito/rejeitado, limites 50/51 e 100/101, branches convergentes, ciclos alcançáveis/desconectados, todos os escopos/identidades, publicação inválida sem efeitos, imutabilidade por coleções/JSON, clonagem entre versões, revisão/UTC/arquivamento e matriz completa das transições permitidas/proibidas.

Executar restore em modo travado após gerar o lock do novo projeto, build Release e toda a solução de testes (incluindo os três testes HTTP existentes). CI passa a se chamar Validação do projeto e continua validando backend/frontend/containers; não muda sua abrangência para infraestrutura de fases futuras. Registrar resultado, decisões, riscos e pendências em docs/phase-2-review.md e ADR 0002. Atualizar README/arquitetura/modelo/roadmap sem apresentar persistência ou engine como implementadas. Revisão independente antes de integração/publicação. Commits em português, sem prefixos, na identidade gabxw. Não iniciar Fase 3.
