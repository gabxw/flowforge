
# Condition, Transform JSON e Delay — Fase 9

O Worker registra os seis executores iniciais: Webhook Trigger, HTTP Request, Delay, Condition, Transform JSON e Log. O grafo publicado é acíclico e percorre um caminho por vez. A API salva a definição e aceita execução; quem avalia JSON ou executa nodes é o Worker.

## Condition

Configuração de exemplo:

~~~json
{
  "type": "condition",
  "sourcePointer": "/order/total",
  "operation": "greaterThan",
  "expectedValue": 100
}
~~~

| Operador | Regra |
| --- | --- |
| exists | true para valor presente, incluindo null; sem expectedValue |
| equals / notEquals | Igualdade tipada; strings ordinais e números exatamente representáveis em decimal |
| greaterThan / greaterThanOrEqual / lessThan / lessThanOrEqual | Ambos os operandos precisam ser números exatamente representáveis em decimal |

Ausência retorna false em todos os operadores, inclusive notEquals. Não convertemos "100" em 100. Para igualdade, tipos diferentes são distintos; comparar um objeto com um literal escalar é diferente. Para ordem, null, string, boolean, array ou objeto produzem conditionValueNotComparable. Números com overflow, underflow ou arredondamento também produzem esse erro; 100, 100.0 e 1e2 representam o mesmo número.

Pointer vazio representa a raiz. /a~1b/m~0n consulta a propriedade a/b e depois m~n. O escape ~01 resolve o nome ~1, sem decodificar duas vezes. Índices de array aceitam 0, 1, 2 etc.; 01, +0, -1 e - não identificam elementos. Nomes de propriedades são exatos e diferenciam maiúsculas/minúsculas. Propriedades duplicadas no trecho consultado produzem jsonPointerAmbiguous. [RFC 6901](https://www.rfc-editor.org/rfc/rfc6901)

Condition devolve o input inteiro, sem alterá-lo, e somente a porta true ou false. O domínio exige uma conexão por porta ao publicar. Um ramo não escolhido fica Pending enquanto o percurso ainda está ativo; ao terminar, os nodes nunca iniciados ficam Skipped. A convergência continua alcançável pelo ramo escolhido.

## Transform JSON

~~~json
{
  "type": "transformJson",
  "fields": [
    {"targetProperty": "total", "sourcePointer": "/order/total"},
    {"targetProperty": "original", "sourcePointer": ""},
    {"targetProperty": "source", "literal": "webhook"},
    {"targetProperty": "optional", "literal": null}
  ]
}
~~~

Saída é um novo objeto com as propriedades escolhidas. Cada campo tem sourcePointer ou literal, exclusivamente. Literais e valores copiados podem ser objetos/arrays; targetProperty é um nome direto, não um caminho de escrita. O input permanece intacto.

Definição admite de 1 a 50 campos, nomes únicos de até 128 unidades UTF-16 e Pointers de até 1.024 unidades/32 segmentos. Configurações já são validadas pelo domínio/contrato HTTP. Em execução, saída tem teto de 64 KiB de JSON UTF-8 e profundidade 32 incluindo o objeto externo. Isso impede que 50 cópias de um input válido produzam um contexto operacional sem limite. O buffer do writer é transitório; o limite de 64 KiB é do JSON persistido, não uma promessa sobre toda alocação temporária.

Caminho ausente falha com transformSourceMissing; ambiguidade com jsonPointerAmbiguous; profundidade incompatível com transformValueInvalid; tamanho excedido com contextLimitExceeded. Nenhum resultado parcial se torna checkpoint. Não há interpolação, template, cálculo, eval, código livre ou acesso ao filesystem.

## Delay e retomada

~~~json
{"type": "delay", "durationTicks": 50000000}
~~~

O exemplo representa cinco segundos: um tick .NET tem 100 ns. Duração deve ser positiva e no máximo 24 horas. A definição preserva os ticks exatos; o instante durável é arredondado para cima ao microssegundo do PostgreSQL.

O deadline é o primeiro StartedAt do Delay mais a duração, não o momento de publicação no RabbitMQ nem o momento de reinício. A transação de suspensão conclui a inbox atual, libera sua lease e grava uma nova outbox com AvailableAt no prazo. Depois do commit, o consumer dá ACK na entrega anterior. O poller existente de outbox atua como scheduler; não existe outro processo, plugin RabbitMQ ou Redis para essa espera.

~~~mermaid
sequenceDiagram
    participant W as Worker
    participant DB as PostgreSQL
    participant MQ as RabbitMQ
    W->>DB: Início durável do Delay
    W->>DB: ResumeAt + nova outbox + conclusão da inbox (transação)
    W->>MQ: ACK da mensagem atual
    Note over W,DB: Sem lease ou delivery durante a espera
    W->>DB: Claim da outbox com AvailableAt vencido
    W->>MQ: Publicar continuação com novo MessageId
    MQ->>W: Entregar continuação
    W->>DB: Concluir o mesmo Delay e avançar checkpoint
    W->>MQ: ACK após progresso durável
~~~

A execução e o node continuam Running enquanto esperam; FinishedAt/output do node ficam nulos. GET /api/executions/{id} expõe resumeAt nullable. Uma retomada normal conclui o mesmo node, mantém seu StartedAt e attemptCount = 1 e não repete HTTP/Transform anteriores.

DispatchSequence identifica a continuação no banco. A mensagem RabbitMQ continua v1 e transporta apenas IDs/correlação. Cada Delay ganha um novo MessageId. Inbox mantém os dispatches já concluídos e apenas uma linha ativa por execução. Mensagem antiga, duplicada ou adiantada não avança o percurso; a mensagem adiantada não cria uma reserva de inbox que impediria o wakeup correto.

ResumeAt representa o instante mínimo de elegibilidade. Polling de 1 segundo, backlog ou broker indisponível podem atrasar a execução. Timeouts de processamento não limitam os 24h de espera, porque a engine devolve o controle logo após persistir a suspensão. Não há deadline global/retry de negócio nesta fase.

## Cancelamento e falhas

POST /api/executions/{id}/cancel permanece sem corpo, idempotente e privado. Durante Delay, atualiza o pedido e acelera AvailableAt da continuação já criada. Responde 202 enquanto a execução está ativa; o Worker encerra com Delay Cancelled e demais nodes não iniciados Skipped. Não execute engine no endpoint.

Se o pedido vencer antes do commit da pausa, não criamos uma mensagem futura. Se o broker estiver indisponível, o pedido fica durável e aguarda recuperação da entrega. Cancelamento não garante observação instantânea.

Queda depois do commit da pausa: a outbox e o contexto estão persistidos; a nova instância continua após o prazo. Queda antes do commit: a transação não cria uma pausa parcial, a delivery volta e a lease expira. Delay permite replay local usando o início original; a recuperação antes de suspender pode incrementar attemptCount por interrupção, mas o simples wakeup durável não incrementa.

HTTP anterior com checkpoint persistido não se repete. HTTP interrompido sem resultado durável continua falhando com interruptedNode, sem reenvio automático. Não há garantia exactly-once sobre efeito remoto. Retry exponencial, tentativas individuais e DLQ pertencem à Fase 10.

## Schema e upgrade

A migration 20261010194759_CondicoesTransformacoesEEsperaDuravel mantém treze tabelas e acrescenta:

- workflow_executions.resume_at e dispatch_sequence, com checks de coerência;
- outbox_messages.dispatch_sequence e unicidade por execução/sequência;
- unicidade parcial de inbox_messages.execution_id quando completed_at é nulo;
- códigos de falha 15..18 em execução/node.

A sequence inicial é zero para registros legados. Apply é explícito com migrate-compose.ps1; nenhum host roda migrations no startup. Pare API/Workers antes do upgrade, faça backup consistente do banco/keyring e recrie os hosts com a mesma versão depois de migrar. O banco operacional não foi alterado pela validação desta fase.

Down não é rollback operacional seguro após gerar múltiplas continuações ou históricos com os novos códigos. Não exclua registros para forçar unicidade antiga. Prefira corrigir para frente; uma restauração exige banco/keyring consistentes e avaliação das mensagens já existentes no broker.

## Validar

~~~powershell
dotnet restore FlowForge.slnx --locked-mode
dotnet build FlowForge.slnx -c Release --no-restore
dotnet test FlowForge.slnx -c Release --no-build

.\scripts\smoke-declarative.ps1
.\scripts\smoke-declarative.ps1 -BaseUri http://127.0.0.1:5173

$paused = .\scripts\smoke-declarative.ps1 -PausedOnly -DelaySeconds 45
.\scripts\smoke-executions.ps1
docker compose up --detach --force-recreate --no-deps worker
.\scripts\smoke-declarative.ps1 -ExecutionId $paused.ExecutionId
~~~

O smoke cria dados fictícios, percorre true/false com convergência, verifica contexto por metadados, testa cancelamento de 24h e arquiva exemplos. A retomada usa uma execução já suspensa. Não remove volumes nem altera keyring. O CI também verifica recriação de hosts e preservação do keyring. RabbitMQ e HTTPS reais com os seis nodes são cobertos nos testes de integração.

| Sintoma | Diagnóstico |
| --- | --- |
| Running com resumeAt futuro | Espera prevista; não é lease presa |
| Running com resumeAt vencido | Verificar outbox disponível, dispatcher/broker e consumer |
| Retomada antes do previsto | Inspecionar cancelRequestedAt; cancelamento pode acelerar a continuação |
| Contexto ilegível após recriação | Restaurar keyring correto; não substituir por JSON vazio |
| conditionValueNotComparable | Revisar tipo/representação decimal, sem coerção implícita |
| transformSourceMissing / jsonPointerAmbiguous | Revisar Pointer e payload; ausência não equivale a null |
| contextLimitExceeded / transformValueInvalid | Reduzir projeção, tamanho ou profundidade |

[ADR 0009](decisions/0009-declarative-nodes-and-durable-delay.md) explica alternativas e trade-offs. Evidências e publicação em [phase-9-review.md](phase-9-review.md).
