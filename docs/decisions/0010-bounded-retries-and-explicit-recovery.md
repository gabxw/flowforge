# ADR 0010 — Tentativas limitadas e recuperação explícita

Status: aceita, implementada e validada na Fase 10 em 12/10/2026; publicação e evidências na [revisão da fase](../phase-10-review.md).

## Problema e alternativas

Uma falha HTTP pode ocorrer depois de o destino produzir um efeito. Repetir indiscriminadamente multiplica operações; não repetir nenhuma falha impede recuperação de serviços temporariamente indisponíveis. Uma lease também pode vencer depois de a delivery duplicada ter sido confirmada, exigindo recuperação independente do broker.

| Alternativa | Vantagem | Trade-off |
| --- | --- | --- |
| Retry em memória/Polly dentro do executor | Curto para chamadas isoladas | Perde espera/histórico no reinício e ocupa a entrega |
| Framework de workflows ou scheduler novo | Recursos prontos | Novo modelo e operação antes de necessidade |
| Outbox/ResumeAt existentes | Estado e continuação atômicos | Polling e mais registros por tentativa |
| Retry de todo HTTP | Mais recuperação aparente | Pode duplicar efeito remoto |
| Retry opt-in com contrato de segurança | Risco explícito e verificável | Configuração conservadora e dependência do contrato externo |
| Reabrir execução terminal | Reaproveita identidade | Sobrescreve histórico e torna auditoria ambígua |
| Nova execução para replay | Preserva original/versão | Pode repetir efeitos; exige reconhecimento explícito |

Escolhemos a outbox existente, política pequena e tentativas persistidas. Nenhuma biblioteca ou Redis foi acrescentado. O sistema continua um monólito modular com API/Worker.

## Retry e journal

Configurações antigas preservam maxAttempts = 1. Novas permitem 1..5. GET/HEAD/OPTIONS podem optar por retry; os demais métodos exigem supportsIdempotency. Aplicamos política mais conservadora que simplesmente classificar PUT/DELETE pelo método. A semântica HTTP permite repetir operações idempotentes e restringe repetição automática de operações não idempotentes. [RFC 9110, seção 9.2.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-9.2.2)

Uma tentativa nasce antes do executor. HTTP registra DispatchedAt/revisão de credencial sob fencing antes de SendAsync; o envio não mantém a transação aberta. O resultado atualiza a tentativa e o checkpoint na mesma transação. Interrupção deixa ExternalOutcome Unknown; não inferimos ausência de efeito pela falta de resposta.

Retry elegível: transporte/timeout e status 408, 429, 500, 502, 503, 504. Segurança, validação, credencial, representação inválida e falhas locais de negócio não repetem. Backoff parte de 1s e dobra, com jitter entre metade e o valor inteiro, limitado. Retry-After é um mínimo adicional até 60s; conselho acima disso encerra a execução em vez de repetir antes do solicitado. O header aceita segundos ou data HTTP. [RFC 9110, seção 10.2.3](https://www.rfc-editor.org/rfc/rfc9110.html#section-10.2.3)

A tentativa falha fica preservada; NodeExecution passa a Retrying, mantendo o primeiro início. Prazo/continuação/inbox são atômicos e liberam a delivery. Retomada cria a próxima tentativa e reutiliza o input protegido. HTTP com contrato declarado envia uma chave estável por ExecutionId/NodeId. Outra execução recebe outra chave.

A declaração de idempotência não comprova o comportamento do destino. Seu escopo, comparação de conteúdo e validade precisam cobrir a operação e nosso deadline. A revisão da credencial precisa permanecer a mesma entre tentativas, porque mudar principal pode mudar o escopo da chave. Rotação/revogação em voo mantém a regra anterior de resolução por chamada; entre tentativas, mudança de revisão falha fechada, sem copiar o segredo para o checkpoint.

## Deadline e recuperação

Deadline de 48h é fixado no aceite pelo PostgreSQL e inclui fila, processamento e espera. Limita também workflows com muitos Delays; o máximo de 24h por node não garante que 50 Delays terminem. Heartbeat, início do node e journal verificam autoridade/cancelamento/deadline.

Timeout de node continua distinto do deadline total. Uma operação remota pode confirmar depois do deadline; KnownSuccess no journal não significa WorkflowExecution Succeeded. Cancelamento/timeout não desfazem efeitos remotos.

O scanner no Worker recupera até 10 intenções por ciclo de 5s. Busca leases vencidas/deadline/cancelamento sem lease válida, republica o mesmo MessageId via outbox e limita novas recuperações por mensagem a intervalos de 30s. Não precisa de um novo webhook. Duplicata Busy pode receber ACK porque a lease ou esse scanner conserva a responsabilidade. Locks e fencing continuam no banco; por execução só um Worker pode gravar.

Claims de entrega e replays locais também têm orçamento de cinco. Uma falha permanente de carregamento termina com attemptLimitExceeded, sem loop infinito. Deadline/cancelamento podem acordar uma espera; esse wakeup não acrescenta tentativa fictícia.

## DLQ preservando a fila existente

Mantemos exchange, Queue flowforge.executions.v1 e routing key execution.requested.v1. Não mudamos argumentos imutáveis nem migramos entregas existentes. A troca de geração de fila foi descartada após revisão automática apontar risco de deixar mensagens antigas sem consumo.

O consumer faz transferência manual com mensagem persistente, mandatory e publisher confirms, depois confirma a delivery original. A sequência mantém pelo menos uma cópia durável, podendo duplicar na queda entre confirm e ACK. Confirmação de publicação e ACK do consumidor resolvem etapas distintas. [RabbitMQ — confirmações](https://www.rabbitmq.com/docs/4.2/confirms)

Falhas transitórias de entrega republicam somente o contrato canônico e um contador de falhas no header interno. Cinco falhas registradas esgotam a cadeia. Reconexões preservam o contador já publicado; uma queda antes do registro pode repetir uma tentativa, por isso claims/tentativas no PostgreSQL também são limitados.

A DLQ recebe um envelope técnico sanitizado com motivo, contador, ReceiptId e IDs canônicos quando conhecidos. Body original, headers arbitrários e exceções não são copiados. Isso evita transformar uma mensagem maliciosa em armazenamento de secrets. Uma mensagem inválida sem IDs conhecidos não pode ser executada por redrive.

Workflow Failed é resultado de negócio com ACK, não envio automático à DLQ. DLQ não é lista de workflows falhos; é diagnóstico de contrato/entrega.

## Redrive e replay

Redrive solicita novamente a mensagem atual na outbox, com MessageId/expectedPublishAttempts, sem tomar lease válida. Auditoria é única por mensagem/revisão; repetir o pedido retorna o mesmo evento, inclusive após progresso. Lock de outbox protege a corrida com publisher.

Replay de Failed/Cancelled cria outra execução da mesma versão fixada e input original recriptografado para o novo ID. Preserva original, tentativas, horários e resultado. Chave de request é obrigatória e fica somente como digest; novas chaves representam novos replays. Workflow com HTTP exige acknowledgeExternalEffects. A API privada não apresenta isso como autorização administrativa por RBAC; autenticação continua na Fase 13.

Não consumimos e executamos bodies da DLQ. O operador consulta IDs/auditoria e solicita redrive da outbox autorizada ou replay terminal. Uma cópia técnica antiga pode permanecer na DLQ sem representar trabalho pendente.

## Dados, retenção e operação

São três tabelas novas: tentativas, redrives e replays. Elas atendem auditoria/casos concretos; não formam event sourcing ou um barramento interno. Importação de históricos anteriores preserva apenas o último resumo disponível, com Legacy=true. Não inventa horários ou tentativas apagadas.

Retenção definida: inbox/outbox concluídas e DLQ por pelo menos 7 dias após término; ativos nunca são removidos. Auditoria/tentativas/replays por 30 dias; contexto por 7 dias após término. A chave de webhook permanece reservada enquanto a execução está ativa e por pelo menos 24h após término. Chave de replay não é reutilizada enquanto seu registro existir. Limpeza automática está pendente; a política não autoriza apagar volumes ou histórico durante upgrade.

O prazo de garantia de deduplicação depende dessa retenção e do contrato do destino. Não prometemos exactly-once externo. A fila clássica e o único broker Compose não fornecem HA; não acrescentamos cluster para demonstrar uma tecnologia.
