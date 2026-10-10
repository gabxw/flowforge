# ADR 0006 — Engine sequencial, checkpoints e captura protegida

Status: aceita na Fase 6; evidências em [phase-6-review.md](../phase-6-review.md).

## Problema

A entrega do RabbitMQ pode ser repetida, e o Worker pode cair depois de executar parte do grafo. Precisamos continuar com a versão aceita e o resultado verdadeiro do predecessor, registrar cada node e impedir um Worker que perdeu a lease de sobrescrever o resultado de outro. Também precisamos distinguir cancelamento do usuário de encerramento do processo.

## Alternativas

1. Executar tudo em memória e repetir desde o trigger após redelivery: simples, mas perde progresso e pode repetir efeitos.
2. Guardar apenas snapshots visíveis: insuficiente se a captura estiver omitida/truncada; pode perder dados ou expor conteúdo privado.
3. Adotar um framework de orquestração, Redis como estado/lock ou event sourcing: aumenta operação, modelo e dependências antes de existir necessidade.
4. Guardar checkpoint transacional no PostgreSQL, com contexto separado e protegido: escolhido por reutilizar a fonte de verdade e os claims da Fase 5.

## Escolha

Domain valida estados de WorkflowExecution/NodeExecution e fornece ExecutionPath. Application coordena executores específicos; Infrastructure persiste checkpoints. Um executor devolve JSON, erro estável ou porta declarada; não escolhe um node arbitrário. A engine encontra a conexão da versão fixada e controla a continuidade.

Trigger é apenas passagem da entrada técnica do grafo; o endpoint webhook continua na Fase 7. Log repassa o input e registra uma mensagem protegida na mesma transação do checkpoint. HTTP, Delay, Condition e Transform retornam unsupportedNode até seus incrementos. Nenhum resultado simulado substitui uma integração ausente.

Há duas transações por node: início durável e checkpoint do resultado. Nenhuma fica aberta enquanto o executor trabalha. O checkpoint reúne NodeExecution, contexto/progresso, evento Log e, ao terminar, resultado da execução/inbox. Um erro no commit não deixa uma conclusão parcial.

A lease padrão dura 30 s e é renovada a cada 5 s. Token aleatório identifica o dono; ClaimAttempts fornece geração crescente na inbox. Cada leitura/gravação exige token, geração, lease válida e execução ativa sob lock execução → inbox → nodes. CheckpointRevision também detecta gravação sobre progresso desatualizado. Um processo que perde a renovação interrompe o executor e não confirma o trabalho como concluído.

CancelRequestedAt é persistido de forma idempotente. O Worker verifica entre nodes e no heartbeat; executores recebem CancellationToken. Antes de iniciar: execução Cancelled, sem StartedAt, todos os nodes Skipped. Durante um node interrompido: node Cancelled. Nodes concluídos permanecem Succeeded e nodes não iniciados ficam Skipped. Se o pedido já estiver gravado antes do checkpoint final, a execução termina Cancelled; um node que já produziu resultado completo pode permanecer Succeeded. Pedir cancelamento após término preserva o resultado terminal.

Shutdown ou perda de lease deixam a execução retomável, sem inventar cancelamento do usuário. Trigger e Log podem repetir um node Running sem checkpoint: são operações locais e o Log só fica durável junto do resultado. Nodes Succeeded nunca são executados novamente. CanReplayAfterInterruption é explícito; um executor inseguro interrompido falha com interruptedNode. A Fase 8 deverá definir resultados externos desconhecidos, e a Fase 10 acrescentará histórico de tentativas/retry. AttemptCount nesta fase conta tentativas lógicas de processamento, inclusive retomada, sem histórico por tentativa.

## Conteúdo e chaves

O contexto operacional tem limite de 64 KiB de JSON UTF-8 serializado. É cifrado com ASP.NET Core Data Protection, purpose FlowForge.ExecutionContext.v1 e identidade da execução. Não existe truncamento silencioso. O banco admite até 128 KiB de ciphertext para acomodar o envelope. Log usa purpose separado FlowForge.ExecutionLog.v1 e identidade do NodeExecution, com mensagem de até 2.000 unidades UTF-16.

O histórico HTTP retorna somente tipo/tamanho dos inputs/outputs, estados, horários, contagem de tentativas e códigos. Log retorna logRecorded e tamanho da mensagem, sem texto ou ciphertext. O texto é registrado protegido; não é enviado ao ILogger. Optamos por omitir todo conteúdo arbitrário porque procurar campos chamados token/password não é sanitização suficiente. Uma política futura de captura pode liberar campos fictícios/permitidos, com ownership/autenticação apropriados.

As configurações dos workflows continuam visíveis ao proprietário técnico e persistidas como JSONB. Não coloque segredos na mensagem/configuração de um node. A cópia protegida no histórico não torna secreta a configuração original. Credential continua na Fase 8; executores atuais não resolvem credenciais.

A dependência nova DataProtection.Extensions evita inventar criptografia e gerenciamento de chaves. O Worker exige caminho absoluto de keyring persistente. No Compose, o volume execution_keyring é separado do PostgreSQL, montado somente no Worker e inicializado com diretório 0700 pertencente ao usuário não root. Application/Domain não referenciam Data Protection.

O perfil Development privado usa chaves XML sem wrapping criptográfico em disco, protegido por permissões/volume. O runtime recusa esse perfil em Production; não existe fallback efêmero. Antes de produção é necessário implementar wrapping com certificado/provedor de secrets, acesso/backup/restauração e rotação. Persistir chaves em filesystem não cifra automaticamente essas chaves, conforme a [documentação oficial](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0). Perder o keyring impede retomar contextos existentes; não regeneramos input {} para fingir recuperação.

## Trade-offs e limites

- Mais gravações por node e locks curtos em troca de progresso recuperável e estado verificável.
- Consulta de histórico limitada naturalmente a 50 nodes/50 eventos Log por execução; não introduzimos paginação genérica nesta fase.
- Dois JSONs com o mesmo conteúdo podem ter representação diferente; contexto usa a representação serializada, e snapshots apenas metadados.
- Timeout de node padrão: 10 s. CancellationToken e WaitAsync limitam a espera; não matam threads nem desfazem efeitos externos. Executores devem cooperar com cancelamento, conforme o [modelo de cancelamento do .NET](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads).
- Checkpoints/fencing protegem nosso banco. Não garantem exactly-once nem exclusividade de efeitos em serviços remotos.
- Contextos/logs permanecem após término. Limpeza/retenção, captura ampliada e retry de nodes não foram antecipados.
- Retenção, backup do banco e backup do keyring precisam ser coordenados. Não usar down --volumes em dados que devam ser preservados.
- Sem Redis, scheduler, endpoint webhook, executores externos ou abstração genérica de orquestração neste incremento.

## Como explicar em entrevista

“Separei executor de engine: o executor resolve uma etapa, a engine controla o grafo e o checkpoint. Uso PostgreSQL para guardar o progresso junto do resultado e uma inbox para reconhecer redelivery. Se o processo cair, retomo o próximo node com contexto cifrado; se perder a lease, não posso escrever com token antigo. Isso protege a consistência local, mas não promete exactly-once para um HTTP que possa ter sido aplicado antes da queda. Cancelamento do usuário é um pedido durável; shutdown apenas libera a oportunidade de recuperação.”
