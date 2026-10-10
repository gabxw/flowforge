# Operação de webhooks — Fase 7

A API aceita POST /hooks/{endpointId}, grava input protegido, execução e outbox no PostgreSQL e responde 202. Worker executa Trigger → Log. HTTP Request, Condition, Transform e Delay ainda não possuem executores; usar esses tipos continua produzindo unsupportedNode.

## Administração privada

| Método/rota | Resultado |
| --- | --- |
| POST /api/workflows/{id}/webhook, sem corpo | 201 com endpoint e secret exibido uma vez. Exige workflow ativo/publicado; um endpoint por workflow. |
| GET /api/workflows/{id}/webhook | Metadados sem owner, hash ou segredo. |
| PUT /api/workflows/{id}/webhook, {"enabled": false} | Desativa ingresso/replay. enabled é obrigatório; o comando pode ser repetido. |
| PUT na mesma rota, {"enabled": true} | Reativa apenas se o workflow estiver ativo/publicado. |
| POST /api/workflows/{id}/webhook/rotate-secret, sem corpo | Novo secret uma vez, mesmo ID; segredo anterior revogado. Preserva enabled e reservas. |

Essas rotas usam o proprietário técnico configurado no servidor. São administrativas e ainda não têm login/RBAC; executá-las somente em ambiente privado. Owner/versão/IDs de execução não são parâmetros de ingresso. Criação duplicada retorna 409; um recurso de outro proprietário retorna 404. Arquivar workflow recusa novos eventos sem cancelar aqueles já aceitos.

## Contrato do remetente

Headers obrigatórios: Content-Type: application/json (UTF-8) e X-FlowForge-Webhook-Secret. Idempotency-Key é opcional, mas recomendado quando o remetente repete eventos. Não enviar segredo na URL/query string. O corpo aceita qualquer raiz JSON válida, incluindo array, string e null, com propriedades únicas e profundidade máxima 32.

O segredo é hexadecimal maiúsculo com 64 caracteres, gerado pelo servidor. O servidor não permite consultar seu valor novamente: se for perdido, rotacionar. Nunca colar segredo em exemplos versionados, logs, LinkedIn ou argumentos de shell compartilhados. Use TLS fora da demonstração local privada; o token bearer não assina o corpo.

| Resposta | Significado |
| --- | --- |
| 202 | Commit confirmado; recibo com executionId, workflowVersionId, correlationId, createdAt e replayed. Location aponta à consulta privada. |
| 400 | JSON/UTF-8 inválido, propriedades duplicadas, chave inválida/múltipla ou query string. |
| 404 | Endpoint ausente/desativado, segredo ausente/errado ou workflow arquivado/sem publicação. Mesma resposta para essas recusas. |
| 408 | Leitura do corpo excedeu 10 s. |
| 409 | Mesma chave válida com bytes de corpo diferentes. |
| 413 | Corpo recebido ou contexto JSON normalizado excede 64 KiB. |
| 415 | Content-Type/charset não suportado ou Content-Encoding presente. Compressão não é aceita. |
| 429 | Rate limit local esgotado, com Retry-After. |
| 503 | PostgreSQL, migrations ou proteção persistente indisponíveis/configurados incorretamente. |

O proxy /hooks do frontend também aplica 64 KiB; seus erros de tamanho podem ser HTML do Nginx. A resposta 202 não depende da fila nem do término de nodes. A API não permite recuperar conteúdo/ciphertext por GET de histórico. Não é uma API pública pronta para produção.

## Chave e janela

Uma chave identifica um evento no endpoint/proprietário. Deve conter 1–128 caracteres ASCII visíveis sem espaços; UUID é uma boa opção. Na janela, o mesmo corpo exato e a mesma chave retornam a execução original, inclusive após conclusão ou nova publicação. Espaços, ordem das propriedades ou representação de números diferentes geram conflito; não há canonicalização. Sem chave, corpo igual cria outra execução. Chaves distintas também representam eventos distintos.

A janela padrão é 24 h, configurável por FlowForge__Webhooks__IdempotencyHours entre 1 e 168. O tempo é medido no PostgreSQL, contado do aceite inicial e não renovado por replay. Após expirar, a mesma chave pode aceitar novo conteúdo e gerar outra execução. Reciclar a reserva não altera o histórico anterior; ainda não há limpeza periódica de linhas expiradas.

Rotacionar/desativar durante a leitura é tratado por revalidação dentro da transação. Reenvio exige segredo atual e endpoint ativo, mesmo quando a chave já está reservada. A versão é a publicação vigente no novo aceite; execuções e replays preservam a versão previamente fixada.

## Configuração e dados

API e Worker precisam de Development, FlowForge__DataProtection__KeyRingPath absoluto e o mesmo keyring/application name. Compose prepara /var/lib/flowforge/keys com UID não root e permissão 0700, montado pelo volume execution_keyring em ambos. Runtime recusa o perfil sem wrapping em Production; testes usam substituição explícita somente no host de teste. Backup/restauração deve preservar keyring e banco compatíveis.

Input original cifrado tem purpose diferente do checkpoint e fica separado dele. A engine inicializa os nodes lendo esse input e passa a salvar o contexto corrente. Comandos manuais continuam com {}. Hash do corpo/chave não é um mecanismo de ocultação de dados previsíveis; acesso ao banco deve permanecer restrito.

Rate limiting usa uma partição fixa por processo, sem Redis, com 60 requests por 60 s e sem espera por padrão. FlowForge__Webhooks__PermitLimit (1–10000) e FlowForge__Webhooks__WindowSeconds (1–3600) são opções do host. O orçamento inclui recusas e é compartilhado pelos endpoints; múltiplas réplicas têm orçamentos próprios. Não há confiança em X-Forwarded-For para contagem, nem criação de partições por IDs aleatórios. Isso não limita o backlog acumulado ao longo de vários minutos; quotas/retenção serão revistas com a confiabilidade e autenticação.

## Atualização do ambiente

Aplicar a quarta migration antes de aceitar webhooks. O Worker da Fase 6 não conhece o input original e trataria uma entrada nova como {}; por isso, não misturar API da Fase 7 com Worker antigo. Neste ambiente privado, interromper API/Worker, preservar banco/keyring, aplicar migration explícita e iniciar os dois hosts atualizados antes de liberar o ingresso. Não é uma estratégia de rolling upgrade sem indisponibilidade.

Down remove input original/reservas/endpoints e pode perder a entrada de execuções ainda não inicializadas. Não utilizá-lo como rollback operacional; restaurar backup consistente ou corrigir adiante após interromper o ingresso. Logs operacionais não substituem o payload protegido. A compatibilidade com comandos manuais/históricos da Fase 6 é preservada no caminho de atualização.

## Demonstração e recuperação

Depois de preparar os secrets de infraestrutura no processo, subir Compose e aplicar migrations conforme README:

~~~powershell
.\scripts\smoke-webhooks.ps1
.\scripts\smoke-webhooks.ps1 -BaseUri 'http://127.0.0.1:5173'
~~~

O smoke cria um workflow Trigger → Log com dados fictícios, guarda o segredo apenas em memória, verifica aceite/replay/conflito/desativação/rotação, consulta metadata e arquiva o exemplo ao concluir. Preserva os volumes operacionais. Para exercitar indisponibilidade, CI pausa o aplicativo RabbitMQ, aceita uma entrada Pending com -PendingOnly, recria API/Worker, reativa o broker e consulta a execução original com -ExecutionId/-ExpectedInputBytes. Esse teste verifica os dados aceitos antes de inicializar a engine, além dos testes PostgreSQL de checkpoint.

[ADR 0007](decisions/0007-webhook-acceptance-and-idempotency.md) explica escolhas e trade-offs. [Revisão da Fase 7](phase-7-review.md) contém a evidência verificada.
