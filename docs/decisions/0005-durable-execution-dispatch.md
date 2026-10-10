# ADR 0005 — Despacho durável com outbox, confirmações e inbox

Status: aceita para a Fase 5. Data: 10/10/2026.

## Problema

PostgreSQL e RabbitMQ não compartilham uma transação. Gravar a execução e publicar diretamente na request abre duas falhas: execução aceita sem mensagem, ou mensagem publicada sem execução. Além disso, uma confirmação perdida pode provocar republicação; um consumer pode cair depois de gravar o resultado e antes do ack.

## Alternativas

| Abordagem | Consequência |
| --- | --- |
| Publicar dentro da request, antes/depois do commit | Continua existindo uma janela de perda/inconsistência; a API depende do broker |
| Transação distribuída | Coordenação e suporte operacional desproporcionais ao projeto |
| Outbox + publisher confirms + inbox | Transações locais, recuperação e duplicação explícita; exige polling e tabelas adicionais |
| Lock no Redis | Outra dependência sem resolver a atomicidade entre estado da execução e deduplicação |

Escolhemos a terceira alternativa, com claims no PostgreSQL. O Worker hospeda dois loops: publicador da outbox e consumer. A API grava a solicitação e responde 202 sem abrir conexão RabbitMQ.

## Decisão e garantias

1. A raiz do workflow é bloqueada brevemente enquanto a solicitação verifica proprietário, arquivamento e publicação vigente. A execução fixa o ID dessa versão imutável. Execução e outbox são inseridas na mesma transação.
2. O dispatcher reserva uma mensagem por vez com UPDATE/RETURNING, FOR UPDATE SKIP LOCKED e lease de 30 segundos. Nenhuma transação fica aberta esperando a rede. O relógio das leases é o do banco.
3. Mensagens persistentes seguem para exchange/queue duráveis. Publicação usa mandatory e publisher confirms; published_at só muda após confirmação. Timeout de conexão: 5 s; operação de publicação: 10 s.
4. Confirmação seguida de queda antes da gravação pode republicar o mesmo MessageId. Isso é esperado: entrega pelo menos uma vez.
5. O consumer usa prefetch 1 e ack manual. A inbox registra recebimento e uma lease recuperável. Recebimento não significa conclusão.
6. Claim e conclusão bloqueiam primeiro a execução, depois a inbox. Tokens diferentes impedem a conclusão por um dono anterior; resultado e completed_at são gravados na mesma transação.
7. Duplicata concluída recebe ack; claim ativo aguarda sem ack. Após interrupção, a mensagem retorna ao broker e uma lease expirada permite recuperar o trabalho.
8. Conexões/canais são reutilizados. A recuperação explícita da sessão cobre também a primeira conexão e recria a topologia. Não combinamos esse loop com automatic recovery do cliente.
9. Erros transitórios retêm a entrega sem ack e aguardam um segundo. Outbox malsucedida fica disponível após dois segundos; polling ocioso usa um segundo. São intervalos operacionais simples, não retries de nodes.
10. Contrato inválido ou identidade que não corresponde à outbox é rejeitado sem requeue e sem persistir/logar o corpo. Nesta fase, mensagens inválidas são descartadas; quarentena/DLQ e política de redrive pertencem à Fase 10.

## Limites e trade-offs

A API aceita somente solicitação manual sem corpo. Cada POST cria uma execução independente; idempotência de requests/webhooks ainda não está disponível. A mensagem v1 contém somente versão de contrato, MessageId, ExecutionId e CorrelationId. Payload e credenciais não atravessam a fila.

A engine pertence à Fase 6. Nesta fase o handler persiste Running → Failed com código engineUnavailable e nenhum node é executado. Esse resultado explicita o limite da entrega.

Há custo de polling, gravações e retenção das mensagens processadas. Não há limpeza automática da inbox/outbox: apagar a deduplicação antes de encerrar a janela de replay seria incorreto. Retenção será definida com operação real.

A lease atual atende ao handler curto. Execuções longas da Fase 6 precisam de renovação/recuperação apropriada; o token protege commits locais, não impede efeitos externos de um processo atrasado. Não prometemos exactly-once de chamadas HTTP.

Queue durável e mensagem persistente não equivalem a alta disponibilidade: o Compose tem um único broker com volume local. Não adicionamos cluster ou quorum sem necessidade operacional.

## Como explicar em entrevista

“Não consigo fazer commit atômico entre banco e fila. Por isso gravo a intenção na mesma transação da execução e publico depois com confirmação. Uma queda após confirmar pode duplicar a mensagem, então uso inbox e claim recuperável. Confirmo o recebimento só depois do resultado durável. A garantia é entrega pelo menos uma vez e deduplicação do processamento local; efeitos externos precisam de sua própria estratégia de idempotência.”

## Validação e fontes

A revisão da Fase 5 registra os resultados. Testes com PostgreSQL/RabbitMQ reais cobrem rollback da inserção e da conclusão, broker indisponível, mensagem sem rota, duas disputas concorrentes, duplicação após confirmação e interrupção após claim.

O [guia .NET do RabbitMQ](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) explica lifetime de canais/conexões, limites da recuperação e buffers de mensagens. O [tutorial de publisher confirms](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet) fundamenta a confirmação aguardada e erros de publicação/retorno. O [PostgreSQL SELECT](https://www.postgresql.org/docs/17/sql-select.html) descreve SKIP LOCKED para acesso concorrente a tabelas que funcionam como filas.
