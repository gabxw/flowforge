# ADR 0007 — Aceite de webhooks e idempotência de entrada

Status: aceita; implementação da Fase 7. Data: 10/10/2026.

## Problema

O remetente pode repetir um evento quando perde a resposta HTTP. A API precisa aceitar sem depender do RabbitMQ, preservar o input privado antes de responder e fixar o grafo que será executado. Desativar/rotacionar um endpoint durante uma leitura não pode permitir um novo aceite com autorização antiga.

## Alternativas e decisão

| Alternativa | Avaliação |
| --- | --- |
| Segredo na URL | Recusado: URLs podem aparecer em proxy, histórico e diagnóstico. Usar header também exige omitir headers/conteúdo dos logs. |
| Senha escolhida pelo usuário | Exigiria hashing lento e política de senha. O servidor gera 256 bits aleatórios, apresentados uma vez; SHA-256 e comparação de tamanho fixo em tempo constante são adequados a esse token de alta entropia. |
| Publicar diretamente no broker dentro da request | Cria falha entre banco e fila e acopla disponibilidade/latência. Reutilizamos a outbox da Fase 5. |
| Deduplicar por hash do payload | Descartaria eventos legítimos com conteúdo igual. A chave é opcional e fornecida pelo remetente. |
| Canonicalizar JSON para comparar requests | Acrescenta regras sobre números, ordenação e equivalência. Comparar SHA-256 dos bytes exatos do corpo mantém um contrato verificável; espaços/ordem diferentes geram conflito na mesma chave. |
| Distributed lock no Redis | PostgreSQL já ordena publicação, arquivamento e aceite. Um lock da raiz e do endpoint, seguido de constraints, resolve a corrida sem outro serviço. |
| Limiter por ID/IP arbitrário | Crescimento de partições e confiança em proxy/IP exigiriam outra política. Começamos com uma partição fixa por processo para a rota de ingresso, sem fila de espera. |

Um workflow possui no máximo um endpoint estável, com UUID público. O segredo vai em X-FlowForge-Webhook-Secret, nunca como parâmetro da rota. Criação e rotação retornam o valor somente naquela resposta, com Cache-Control: no-store; GET retorna metadados. Rotação revoga o segredo antigo imediatamente após commit e preserva a habilitação. PUT enabled é idempotente e usa o último comando confirmado; não acrescentamos uma revisão de endpoint nesta fase.

## Transação de aceite

1. Validar segredo e disponibilidade antes de ler o corpo.
2. Ler no máximo 64 KiB com prazo de 10 s; exigir JSON UTF-8, profundidade até 32 e propriedades únicas. Sem compressão ou query string.
3. Abrir transação; adquirir workflow FOR UPDATE, depois endpoint FOR UPDATE. Essa ordem também é seguida na administração do endpoint.
4. Revalidar segredo, habilitação, workflow ativo e ponteiro de publicação. Assim, a validação inicial não concede autorização durável durante a leitura.
5. Consultar a reserva de Idempotency-Key do endpoint/proprietário. Se ainda válida, comparar o digest e devolver a execução original ou conflito. Publicação posterior não muda o replay.
6. Para um novo aceite, fixar WorkflowVersionId e inserir execução Pending, input original protegido, outbox e reserva juntos. Só responder 202 depois do commit.

O endpoint serializa aceites do mesmo workflow, inclusive aqueles sem chave. Isso simplifica a consistência e limita throughput por workflow; é uma escolha inicial, sem lock mantido durante processamento. A constraint de chave e as FKs continuam protegendo o schema. Requisições em workflows diferentes podem avançar simultaneamente. Rate limit é local, enquanto reserva/idempotência funciona entre instâncias que compartilham PostgreSQL.

A chave admite 1–128 caracteres ASCII visíveis sem espaços. Somente seu digest é persistido. A janela começa no primeiro aceite, dura 24 h por padrão e aceita configuração entre 1 e 168 h. Replay não estende a janela. Uma chave expirada pode criar outra execução; a linha da reserva é reutilizada sem apagar a execução anterior. Expiração não representa limpeza: exclusão periódica/retenção será consolidada na Fase 10. Digests não são criptografia e podem revelar igualdade ou permitir adivinhação de corpos previsíveis a quem acessar o banco.

Desativação, arquivamento e rotação são verificados também para replay: possuir uma chave antiga não permite contornar a autorização atual. Eventos sem chave, ou com chaves diferentes, criam execuções distintas mesmo com corpo igual. A resposta é um recibo de aceite, não um relatório de estado; a consulta privada informa o resultado atual.

## Input protegido e retomada

trigger_input_protected é imutável por operação de aceite; execution_context_protected continua sendo o checkpoint mutável. Ao inicializar, a engine lê o input original e cria NodeExecutions na mesma transação, mantendo o marcador de contexto nulo que já existia na Fase 6. Não preenchemos o contexto antes de criar nodes. Requests manuais e legados continuam com input {}.

Data Protection usa purpose FlowForge.TriggerInput.v1 e ExecutionId, diferente do purpose de checkpoint. Substituir ciphertext entre execuções ou entre essas finalidades falha. API/Worker usam o mesmo application name FlowForge e o volume persistente execution_keyring. Corpo e contexto normalizado têm limites próprios de 64 KiB; o JSON normalizado pode crescer ao escapar Unicode. RabbitMQ transporta somente os quatro campos técnicos já existentes.

## Custos, limites e operação

- Dois envelopes cifrados podem coexistir para original/checkpoint. São necessários para preservar origem e retomada; retenção ainda depende da próxima etapa de confiabilidade.
- Header secret é um token bearer: exige TLS fora do ambiente local privado. Assinatura HMAC de requests e proteção contra replay fora da chave opcional não fazem parte deste contrato.
- O limite inicial é 60 ingressos/minuto por processo, incluindo recusas, com 429/Retry-After. Um endpoint pode consumir o orçamento de outros; reinício e múltiplas réplicas mudam o orçamento. Não é quota distribuída nem proteção DDoS.
- As chaves de Development ficam protegidas pelas permissões do volume, sem wrapping. Runtime recusa este perfil em Production. Preservar/restaurar keyring junto do banco; não expor APIs administrativas antes da autenticação da Fase 13.
- Não capturamos body, headers ou secrets em logs operacionais. O teste HTTP inspeciona logs e tags de Activity; OpenTelemetry e políticas do exporter entram na Fase 14.
- 202 significa commit confirmado pela API. A execução pode já terminar antes do GET. Se o cliente perde a resposta do commit, deve repetir a mesma chave/corpo; sem chave, pode duplicar a entrada.

## Como explicar em entrevista

“Uma repetição do remetente e uma repetição de mensagem na fila são problemas distintos. A primeira é resolvida pela chave do evento, reservada junto da execução e outbox em uma transação. A segunda continua coberta pela inbox/claim/checkpoints. Eu fixo a versão durante o aceite e guardo o payload protegido no banco, então o broker pode ficar indisponível sem perder uma entrada confirmada. Escolhi locks curtos do PostgreSQL e limites locais para este MVP; throughput, quota distribuída e retenção têm limites documentados.”

Referências: [rate limiting do ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0), [configuração e compartilhamento do Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).
