# Operação do despacho — Fases 5 a 9

Esta fase entrega API → PostgreSQL/outbox → RabbitMQ → Worker → resultado durável. A Fase 6 executa Trigger/Log com checkpoints e histórico protegido. A falha engineUnavailable é resultado histórico da Fase 5; execuções já concluídas são preservadas. Os seis executores estão disponíveis na Fase 9; Delay libera a mensagem atual e cria continuação com outro MessageId na mesma transação do prazo. Consulte [operação da engine](execution-engine.md) e [espera durável](declarative-nodes-and-delay.md).

## Contrato HTTP privado

| Método | Caminho | Resultado |
| --- | --- | --- |
| POST | /api/workflows/{id}/executions | Sem corpo. 202 com execução Pending e Location |
| GET | /api/executions/{id} | 200 com estado, versão fixa, CorrelationId e horários |

A API usa o proprietário técnico do servidor. Workflow não encontrado ou de outro proprietário retorna 404. Rascunho sem publicação ou workflow arquivado retorna 409. Corpo no comando retorna 400. Configuração/banco indisponível retorna 503. Respostas de erro usam Problem Details sanitizado.

Cada POST cria uma execução nova. CorrelationId e IDs são gerados no servidor; payloads, webhook, autenticação e idempotência de solicitações não fazem parte desta fase. A resposta 202 representa o estado gravado pelo aceite; uma consulta posterior pode já mostrar o resultado do Worker.

## Estado e armazenamento

| Tabela | Responsabilidade |
| --- | --- |
| workflow_executions | Proprietário, versão imutável, estado, CorrelationId, horários e código de erro |
| outbox_messages | Intenção persistida, versão v1, disponibilidade, confirmação e claim temporário |
| inbox_messages | Recebimento, claim recuperável e conclusão atômica com o resultado |

Pending é criado na transação da outbox. O claim muda para Running, preservando started_at em recuperações, exceto quando o cancelamento foi pedido antes de iniciar. A engine grava checkpoints e marca a inbox concluída na mesma transação do resultado terminal. Tokens/gerações antigos não podem gravar após expiração/reaquisição.

O PostgreSQL controla relógio e leases. API e Worker usam factories de DbContext e transações curtas. Redis permanece opcional e sem integração. A Fase 6 acrescenta node_executions e execution_logs, com conteúdo operacional protegido.

## RabbitMQ

Exchange flowforge.executions (direct, durável), routing key execution.requested.v1, queue flowforge.executions.v1 (durável). Corpo JSON máximo: 2048 bytes. Propriedades AMQP incluem delivery persistente, tipo workflow.execution.requested.v1, MessageId e CorrelationId.

~~~json
{
  "contractVersion": 1,
  "messageId": "b0ca9016-cfc7-452d-89af-1b555e552795",
  "executionId": "d961981e-d064-4e5a-b352-3a254e0d1467",
  "correlationId": "1d6595d2-7612-476a-8128-5194b431818d"
}
~~~

O consumer valida versão, tamanho, formato e associação da mensagem à outbox. Campos desconhecidos e duplicados são rejeitados. O corpo, propriedades arbitrárias e texto de exceções não entram nos logs estruturados.

Prefetch 1 limita entregas por consumer. Aguardamos claims ativos e falhas transitórias sem ack, evitando requeue em loop. Reiniciar o consumer devolve unacked à fila; uma lease pendente pode exigir até 30 s para expirar. Mensagens inválidas são descartadas sem requeue nesta fase; DLQ/redrive entram na Fase 10.

## Executar

Configure FLOWFORGE_POSTGRES_PASSWORD e FLOWFORGE_RABBITMQ_PASSWORD na sessão, conforme o README. São Compose secrets montados somente nos serviços que os usam. O broker lê o secret no entrypoint, pois a imagem atual não aceita RABBITMQ_DEFAULT_PASS_FILE; a senha não é argumento nem conteúdo versionado.

O broker usa hostname estável rabbitmq e volume persistente. Use a mesma senha enquanto reutilizar volumes; variáveis de bootstrap não rotacionam usuários existentes. A API não depende de disponibilidade do broker para aceitar solicitações. O Worker tenta reconectar enquanto ele está indisponível.

~~~powershell
docker compose up --build --detach --wait
.\scripts\migrate-compose.ps1
.\scripts\smoke-executions.ps1
.\scripts\smoke-executions.ps1 -BaseUri http://127.0.0.1:5173
~~~

O smoke cria/publica um workflow mínimo, solicita execução Trigger → Log, consulta até Succeeded e verifica histórico e arquiva o exemplo. Não remove histórico ou volumes. Migrations continuam explícitas; após banco vazio, o Worker aguarda as tabelas enquanto a API responde 503 nas operações persistidas.

No host, API precisa da configuração PostgreSQL/proprietário. Worker precisa da configuração PostgreSQL e FlowForge__RabbitMq__Host, FlowForge__RabbitMq__Username e FlowForge__RabbitMq__PasswordFile. O password file é caminho fornecido pelo operador, nunca pelo usuário de um workflow. Port opcional do broker: 5672. Segredos não ficam em appsettings. A Fase 6 exige também keyring persistente em Development, conforme [execution-engine.md](execution-engine.md).

## Recuperação e diagnóstico

| Sintoma | Comportamento e ação |
| --- | --- |
| Pending com outbox não publicada | Verificar conectividade/autenticação/topologia do broker; dispatcher retoma automaticamente |
| Running com inbox incompleta após queda | Aguardar expiração do claim e redelivery; Received não bloqueia definitivamente |
| Outbox confirmada novamente | Mesmo MessageId; inbox concluída evita repetir o resultado |
| Consumer desconectado | Host refaz sessão em intervalos de 2 s |
| Erro de persistência durante recebimento | Entrega permanece sem ack; nenhuma conclusão falsa é gravada |
| Contrato desconhecido | Reject sem requeue; corrigir produtor antes de reenviar |

Logs mostram EventCode, ErrorType e IDs relevantes, sem senha, URI de conexão ou corpo. CorrelationId conecta aceite e processamento; tracing distribuído e métricas serão consolidados na Fase 14.

A [ADR 0005](decisions/0005-durable-execution-dispatch.md) explica escolhas e limites. A suíte usa [Testcontainers RabbitMQ](https://dotnet.testcontainers.org/modules/rabbitmq/) e PostgreSQL descartáveis, sem depender de dados do Compose operacional.
