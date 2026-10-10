# Operação da engine — Fase 6

A API privada aceita um comando sem corpo e fixa a versão publicada. O Worker executa Trigger → Log (ou uma sequência de Logs) e registra progresso durável. O input inicial é {}. O webhook e os demais executores continuam nas fases seguintes.

## Contrato HTTP

| Método | Caminho | Resultado |
| --- | --- | --- |
| POST | /api/workflows/{id}/executions | Sem corpo; 202 Pending e Location |
| GET | /api/executions/{id} | Estado, versão fixa, CorrelationId, horários, ErrorCode e CancelRequestedAt |
| GET | /api/executions/{id}/nodes | Até 50 nodes, ordenados pelo ordinal do grafo; horário determina a execução efetiva |
| GET | /api/executions/{id}/logs | Até 50 eventos logRecorded, ordenados por CreatedAt/Id; tamanho, sem mensagem/ciphertext |
| POST | /api/executions/{id}/cancel | Sem corpo; 202 para execução ativa, 200 para terminal; pedido idempotente |

Proprietário é fornecido pelo servidor. Recurso de outro dono ou ausente retorna 404; corpo em comandos de execução/cancelamento retorna 400. Até autenticação, o ambiente é privado e o usuário técnico não representa login/JWT/RBAC.

Nodes podem retornar Pending antes de iniciar, Running durante processamento, Succeeded, Failed, Skipped ou Cancelled. Antes de o Worker inicializar, /nodes pode retornar []. Retrying está reservado à Fase 10. NodeExecution expõe attemptCount, input/output como {byteLength, kind}, horários e código estável; não expõe JSON operacional.

Um workflow com tipo sem executor termina Failed/unsupportedNode; os nodes restantes ficam Skipped. Erros de executores são nodeFailed, timeout nodeTimeout, output acima do limite contextLimitExceeded e contrato incompatível invalidExecutorResult. Texto/objetos de exceções não são copiados ao histórico.

## Checkpoint e recuperação

1. A inbox valida a mensagem e adquire token/geração/lease.
2. O Worker carrega somente a versão fixada e cria os registros Pending dos nodes. Contexto inicial {} é protegido uma vez.
3. Início do node e AttemptCount ficam duráveis antes de chamar o executor.
4. Resultado, próximo node e contexto protegido são gravados na mesma transação; Log também grava seu evento protegido.
5. No último node, o resultado terminal e a conclusão da inbox são atômicos. Só depois o consumer confirma a mensagem.

A engine renova a lease de 30 s a cada 5 s. Cada checkpoint valida autoridade e revisão. Redelivery de inbox concluída não repete nodes, logs ou horários. Queda após checkpoint retoma no próximo node. Queda com node Running permite replay apenas quando o executor declara segurança para isso; Trigger/Log são locais e seguros. Replay não é retry automático de falhas terminais.

Shutdown não persiste CancelRequestedAt. O delivery sem ack volta ao broker, a lease expira e outro Worker retoma. Pedido do usuário é observado entre nodes e no heartbeat; node interrompido fica Cancelled, anteriores são preservados e restantes Skipped. Uma chamada externa pode já ter produzido efeito quando o cancelamento é observado; essa política será detalhada com HTTP.

## Keyring e limites

O volume execution_keyring deve acompanhar o banco em backup/restauração e sobreviver à recriação do Worker. Contextos de até 64 KiB e mensagens Log são cifrados com purposes/identidades diferentes; snapshots HTTP mostram metadados. Não registrar bodies ou secrets na mensagem de Log/configuração do workflow.

No Compose, a configuração e o diretório com permissões 0700 estão preparados. O perfil Development usa chaves XML sem wrapping, em volume separado; o runtime rejeita essa configuração em Production. Wrapping e operação de chaves em produção precisam ser implementados antes de abrir o serviço ao público. Não existe keyring efêmero no runtime.

No host, configure PostgreSQL/RabbitMQ como em [execution-dispatch.md](execution-dispatch.md), e também:

~~~powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:FlowForge__DataProtection__KeyRingPath = Join-Path (Get-Location).Path '.local/execution-keyring'
dotnet run --project src/FlowForge.Worker
~~~

O caminho deve ser absoluto e gravável pelo usuário do Worker. .local é ignorada no Git. Nunca envie keyring, connection strings ou secret files ao repositório. Perder as chaves bloqueia a retomada de ciphertext antigo, não é motivo para substituir o contexto por {}.

## Validar

~~~powershell
docker compose up --build --detach --wait
.\scripts\migrate-compose.ps1
.\scripts\smoke-executions.ps1
.\scripts\smoke-executions.ps1 -BaseUri http://127.0.0.1:5173
~~~

O smoke publica Trigger → Log, espera Succeeded, verifica dois NodeExecutions e um evento protegido, testa cancelamento sobre resultado terminal e arquiva o exemplo. Não remove volumes. CI também recria o Worker, confirma a persistência do keyring, recria a API e testa recuperação do proxy/parada do Worker.

| Sintoma | Ação |
| --- | --- |
| Pending | Verificar dispatcher/outbox/broker e schema aplicado |
| Running após queda | Aguardar lease e redelivery; verificar consumer e checkpoint |
| Running com CryptographicException | Restaurar keyring correto; não apagar contexto ou concluir artificialmente |
| Failed/unsupportedNode | Usar somente executores disponíveis ou aguardar o incremento correspondente |
| Pedido de cancelamento ainda ativo | Verificar Worker/heartbeat; POST não garante observação instantânea |
| Histórico sem conteúdo | Política intencional de metadados, não truncamento/reconstrução de input |

[ADR 0006](decisions/0006-sequential-engine-and-checkpoints.md) explica alternativas, custos e limites. [Revisão da Fase 6](phase-6-review.md) registra a evidência efetivamente executada.
