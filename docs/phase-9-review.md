
# Revisão técnica — Fase 9

Data: 10/10/2026.

Status: concluída, revisada, publicada pelo [PR #6](https://github.com/gabxw/flowforge/pull/6) e integrada à main com CI completo aprovado.

## Entrega

- Condition com sete operadores tipados, JSON Pointer, ausência/null distintos, ambiguidade recusada e precisão decimal sem arredondamento silencioso.
- Transform JSON com projeção/literais, 1..50 campos, contexto de 64 KiB e profundidade 32.
- Delay até 24h com ResumeAt, suspensão/continuação transacionais e o polling existente da outbox como scheduler.
- DispatchSequence no banco, MessageId novo por continuação e inbox com histórico concluído/uma linha ativa por execução.
- ResumeAt no DTO/OpenAPI de execução; cancelamento acelera a continuação existente sem executar engine na API.
- Os seis executores iniciais registrados no Worker; sexta migration, mantendo treze tabelas.
- Smoke direto/proxy, retomada após recriação e critérios acrescidos ao GitHub Actions.

[Contrato/operação](declarative-nodes-and-delay.md) e [ADR 0009](decisions/0009-declarative-nodes-and-durable-delay.md) registram alternativas, trade-offs e explicação para entrevista.

## Verificação executada

Restore em locked mode e build Release da solução aprovados, com zero warnings/erros. Suíte completa local: 1.186 testes aprovados, nenhum ignorado. Após a revisão de precisão temporal, um teste adicional foi compilado/executado e passou: total atual de **1.187 casos** (777 Domain + 90 Application + 113 API + 207 Integration). O [CI do PR](https://github.com/gabxw/flowforge/actions/runs/38082619640) e o [CI da main](https://github.com/gabxw/flowforge/actions/runs/38082995151) executaram a suíte final inteira: **1.187 aprovados**, sem falhas ou testes ignorados.

Unitários verificam Pointer vazio/escapes/arrays/nomes exatos, ausência/null, tipos, comparação decimal, duplicatas e limites de Transform. Testes de domínio verificam suspensão, restauração, prazo mínimo e limpeza da espera ao terminar/cancelar/falhar.

Integração com PostgreSQL/RabbitMQ reais verifica:

- true/false exclusivos, ramo não escolhido Skipped e convergência executada uma vez;
- códigos sanitizados de falha e contexto privado;
- prazo durável sem lease ativa, mensagens antigas/adiantadas e nova execução durante espera de 24h;
- continuação com novo Worker/keyring, versão fixada após nova publicação/arquivamento e tentativa preservada;
- delays consecutivos com mensagens distintas e histórico de inbox;
- cancelamento durante espera e corrida antes da suspensão;
- falha injetada no commit, rollback de prazo/outbox/conclusão e replay com início original;
- arredondamento temporal para cima e claim exclusivo da outbox;
- webhook → Condition → Transform → HTTP HTTPS controlado → Delay → Log, broker real, consumer recriado e HTTP não repetido.

O teste HTTP/API confirma ResumeAt, isolamento de dono e cancelamento durável sem processamento dentro da request.

EF sem model drift, SQL idempotente gerado/aplicado duas vezes no banco descartável. Sintaxe dos scripts PowerShell aprovada.

Docker validado no projeto isolado flowforge-phase9-validation-20261010: imagens novas/cached consistentes com o código, nginx -t, banco vazio, migrations idempotentes, liveness/proxy e smokes anteriores preservados. Smoke declarativo passou em API direta/proxy para ambos os ramos e cancelamento de 24h.

Durante Delay, RabbitMQ informou zero messages_unacknowledged e outra execução Trigger → Log concluiu. Broker foi parado e API/Worker recriados; a espera retomou com mesmo contexto/keyring, sem nova tentativa do Delay. Redis opcional saudável e Worker encerrou exited:0:false. Apenas containers/volumes do projeto descartável foram removidos; banco/keyring operacionais permaneceram preservados.

Um primeiro roteiro local comparava o nome errado da fila. A verificação foi corrigida para flowforge.executions.v1 e repetida com sucesso; o erro do roteiro não foi usado como evidência de validação.

## Revisão técnica

Domain permanece independente de ASP.NET/EF/broker. Application define avaliação declarativa e intenção de suspensão; Infrastructure mantém atomicidade/relógio/claims. Não há pacote novo, Redis integrado, scheduler separado, scripting ou abstração genérica.

A pausa grava prazo/nova sequência/outbox e conclui a inbox antiga atomicamente. Fencing continua exigindo token, geração, lease e agora sequência atual. A continuação não reabre a mensagem concluída nem repete HTTP com checkpoint anterior. Transações/locks são curtos e não abrangem a espera.

Resumo de input/output continua somente tamanho/tipo; contexto operacional permanece protegido e keyring separado do banco. Condition/Transform não resolvem credentials, não executam shell/filesystem e não ampliam a política HTTP.

## Riscos e limites mantidos

ResumeAt indica elegibilidade mínima; polling/backlog/broker podem atrasar retomada. Estado Running também representa espera; clientes devem observar ResumeAt. Duração total do Delay inclui espera; tempo ativo será instrumentado na Fase 14.

Retenção de outbox/inbox, quotas/deadline global, tentativas individuais, retry/jitter e DLQ permanecem na Fase 10. HTTP com resultado externo desconhecido continua sem replay automático. Não há garantia exactly-once sobre destinos.

API permanece privada com dono técnico, sem JWT/RBAC. Keyring Production, egress e operação de backup permanecem riscos documentados das fases anteriores. Upgrade exige hosts coordenados e backup consistente; Down não é rollback seguro após múltiplas continuações/códigos novos.

## Publicação

O [PR #6](https://github.com/gabxw/flowforge/pull/6) foi integrado por fast-forward em 10/10/2026. A autoria dos três commits foi reconhecida como gabxw, com e-mail noreply verificado e mensagens em português sem prefixos.

- ccf9e4f: executores, suspensão, sequência, schema/migration e composição dos hosts.
- 4c52b3b: testes, smoke e CI com fila liberada/broker parado durante Delay.
- d2334e7: contrato, ADR e revisão/documentação.
- [CI do PR aprovado](https://github.com/gabxw/flowforge/actions/runs/38082619640) e [CI completo da main aprovado](https://github.com/gabxw/flowforge/actions/runs/38082995151): backend, frontend e Docker, três jobs em cada execução.

Revisão de código validada: d2334e7debdc59b32f9029be87488002f53b163c. O registro posterior da conclusão altera somente documentação/instruções, preservando os binários aprovados. Nenhum critério de saída da Fase 9 ficou pendente; os riscos operacionais acima permanecem explícitos. A Fase 10 não foi iniciada.
