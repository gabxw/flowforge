# Revisão técnica da Fase 5

Status: implementação e validações de backend concluídas; validação Compose/publicação em andamento. Data: 10/10/2026. Base: main e402ce7.

## Entrega

- Modelo de WorkflowExecution independente de frameworks, com versão publicada fixa e estado inicial Pending.
- API privada: solicitar execução sem corpo (202/Location) e consultar por proprietário técnico.
- Execução/outbox atômicas e dispatcher com claim curto, SKIP LOCKED, timeout e publisher confirms.
- Consumer com contrato v1 restrito, prefetch 1, ack manual, inbox e claim recuperável.
- Running → Failed/engineUnavailable persistido junto com conclusão da inbox. A engine não faz parte desta fase.
- Worker reconecta sessão/broker; nenhum Redis ou serviço adicional foi integrado.
- Secret próprio para RabbitMQ no Compose; API não depende do broker.
- Smoke direto/pelo proxy, CI e documentação de operação/decisões ampliados.

## Validação executada

| Verificação | Resultado |
| --- | --- |
| Restore --locked-mode | Aprovado |
| Build Release | Aprovado, 0 warnings e 0 erros |
| Unit tests Domain | 729 aprovados |
| HTTP/contrato API | 76 aprovados |
| Integration PostgreSQL/RabbitMQ/codec | 74 aprovados |
| Total xUnit | 879 aprovados; 0 falhas/ignorados |
| EF pending model changes | Nenhuma alteração pendente |
| npm ci/lint/build/audit | Aprovados; nenhuma vulnerabilidade encontrada |
| Compose completo local | Em validação; Docker Hub apresentou timeout no download de imagens |
| GitHub Actions | Pendente de publicação |

Os testes locais em .local/phase-5-evidence cobrem inserção de outbox que falha, falha no commit da conclusão, associação versão/proprietário, arquivamento, claims concorrentes, expiração/token antigo, duplicação, janela confirm→marcação, janela commit→ack, broker indisponível, mensagem sem rota e consumer interrompido após Received. PostgreSQL e RabbitMQ são containers reais descartáveis. Os testes de replay verificam que o resultado e os horários não mudam.

A primeira execução identificou a exceção específica PublishReturnException para mensagem sem rota; a expectativa foi corrigida e repetida com sucesso. Essa falha não foi suprimida. A suíte de integração final passou integralmente.

## Revisão

Revisão técnica feita sobre o código, migration, testes e roteiro operacional desta entrega; sem alegar revisão independente.

- A transação de aceite não publica em rede. Falha na inserção da outbox desfaz a execução.
- Um lock curto na raiz serializa o aceite com publicação/arquivamento.
- Confirmação não é ack de consumer. published_at depende do confirm; perda de marcação causa republicação esperada.
- A inbox não trata Received como Done. O claim expira, e a troca de token impede a conclusão por um dono anterior.
- Execução e inbox sempre usam a mesma ordem de locks; commit do resultado e da conclusão é atômico.
- Nenhuma transação fica aberta aguardando RabbitMQ. Canais/conexões são reutilizados e publicação concorrente no mesmo canal é serializada.
- Mensagem transporta apenas quatro campos de contrato/IDs. O consumer valida associação à outbox, tamanho, propriedades AMQP e JSON estrito.
- Logs do despacho não incluem corpos ou texto/objetos de exceções. Senhas são secrets externos.
- Migration acrescenta três tabelas, FKs e índices ligados a consultas reais. Nome da FK foi revisado para caber no limite do PostgreSQL.
- A configuração PostgreSQL compartilhada entre API/Worker também resolve a pendência menor de Host ausente da Fase 4: retorna 503 sanitizado, com regressão HTTP.
- Os Dockerfiles usam instruções padrão e agora o interpretador embutido, removendo o download auxiliar docker/dockerfile:1 que havia falhado por rede.

## Limites e riscos aceitos

- Nenhum node é executado: erro engineUnavailable é o resultado intencional até a Fase 6.
- POSTs repetidos criam execuções diferentes; deduplicação atual cobre mensagens, não requests/webhooks.
- Lease de 30 s atende ao handler curto. A engine deverá tratar execução longa/renovação e recuperação. Token local não garante exclusividade de efeitos HTTP.
- Mensagens inválidas/desconhecidas são rejeitadas sem requeue e descartadas. Quarentena, DLQ/redrive e retry de nodes pertencem à Fase 10.
- Um broker com volume local não oferece alta disponibilidade. Não há cluster/quorum ou promessa de exactly-once.
- Inbox/outbox concluídas permanecem armazenadas. Retenção e limpeza não foram antecipadas.
- Autenticação/RBAC/rate limiting e observabilidade completa permanecem no roadmap. Ambiente continua privado.
- Pendência menor da Fase 4: U+0000 em texto persistido ainda retorna 500 sanitizado em vez de 400; rejeitado pelo PostgreSQL, sem gravação.

Decisão e explicação para entrevista: [ADR 0005](decisions/0005-durable-execution-dispatch.md). Operação: [execution-dispatch.md](execution-dispatch.md). Não iniciar a Fase 6 sem pedido do usuário.
