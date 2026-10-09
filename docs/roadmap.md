# Roadmap técnico

O trabalho é incremental. Cada fase entrega um comportamento executável, documentação atualizada e revisão técnica antes da próxima. A Fase 1 prepara a solução; as fases seguintes não estão implementadas por antecipação.

Não se avança só porque uma pasta ou endpoint existe. O critério de saída inclui build, testes pertinentes e verificação do comportamento anunciado. Uma verificação impedida pelo ambiente permanece pendente e deve ser informada; ela não equivale a um resultado aprovado.

## Marcos de escopo

| Marco | Resultado |
| --- | --- |
| Fase 1 | Ambiente e hosts mínimos; nenhum workflow ou engine |
| Fase 4 | Definição e publicação de workflows via API privada |
| Fase 7 | Webhook aceito e execução assíncrona básica |
| Fase 10 | MVP de backend com confiabilidade e segurança do fluxo |
| Fase 12 | Fluxo visual demonstrável localmente |
| Fase 16 | Projeto de portfólio documentado, autenticado, observável e com demonstração controlada |

O MVP de backend está fechado em [architecture.md](architecture.md). A versão de portfólio não acrescenta microsserviços ou novos conectores: melhora a experiência e a capacidade de testar, explicar e operar o mesmo produto.

## Fase 1 — Estrutura da solução e ambiente Docker

Entregas:

- Solução .NET 10 com Domain, Application, Infrastructure, Api e Worker.
- Dependências direcionadas e projetos de teste preparados, sem regras de negócio fictícias.
- API com /health/live e OpenAPI nativo em Development, em /openapi/v1.json.
- Worker que inicializa, registra seu ciclo de vida e encerra corretamente.
- Shell React 19/TypeScript/Vite/Tailwind CSS 4; sem CRUD ou editor.
- Dockerfiles e Compose com API, Worker, PostgreSQL, RabbitMQ e frontend; Redis no profile opcional redis.
- Segredo local do PostgreSQL fornecido fora do repositório; sem valores reais em arquivos versionados.
- README inicial, decisões, modelo conceitual e roadmap.

Critério de saída: restore/build/test da solução, smoke HTTP do host, verificação TypeScript/build do frontend, validação de Compose e inicialização dos serviços disponíveis. Verificar que a API não declara prontidão do banco quando ainda não o utiliza.

Revisão: ausência de dependências proibidas no Domain/Application, segredos fora dos arquivos, superfície HTTP mínima e documentação distinguindo ambiente preparado de funcionalidades implementadas.

Não inclui: entidades, EF Core, migrations, fila, consumers, autenticação, credenciais ou engine. PostgreSQL e RabbitMQ sobem como infraestrutura preparada, sem integração com os hosts.

Commit sugerido: Preparar solução e ambiente de desenvolvimento.

## Fase 2 — Domínio de workflows

Entregas: entidades e valores mínimos de definição; tipo do node; portas; validação de DAG; publicação imutável; estados e transições iniciais. Especificar o contrato de Condition e Transform sem executar código livre.

Critério de saída: testes unitários de um trigger, reachability, ciclo, porta inválida, referência entre versões, nodes duplicados e edição de versão publicada. Testar exemplos aceitos e rejeitados, além de casos limites.

Revisão: regras não dependem de EF, ASP.NET ou broker; não há abstração genérica maior que o problema existente.

Commit sugerido: Implementar domínio de workflows e validação do grafo.

## Fase 3 — Persistência PostgreSQL

Entregas: EF Core/Npgsql, DbContext, mappings e migrations somente para o domínio já definido. Registro User técnico mínimo para ownership/FKs, sem senha/login; FKs de nodes/conexões conscientes da versão e concorrência do rascunho.

Critério de saída: integração com Testcontainers/PostgreSQL para aplicar migrations em banco vazio, persistir/ler o grafo e rejeitar vínculos inválidos. Verificar constraints, revisão concorrente e consultas escopadas.

Revisão: transações curtas, índices ligados a consultas reais e comportamento de arquivamento/remoção explícito.

Commit sugerido: Persistir versões de workflows no PostgreSQL.

## Fase 4 — API CRUD de workflows

Entregas: criar/listar/editar rascunho, consultar detalhe, publicar e arquivar; DTOs, paginação, validação de transporte, Problem Details e OpenAPI. Proprietário técnico explícito enquanto o ambiente continuar privado.

Critério de saída: integração HTTP de CRUD, publicação de grafo válido, erros de validação, concorrência de edição e tentativa de acesso a outro proprietário. Executar um roteiro via OpenAPI ou coleção de requests reproduzível.

Revisão: controllers/endpoints delegam casos de uso; publicação fixa o contrato do grafo que a engine consumirá.

Commit sugerido: Disponibilizar API de gerenciamento de workflows.

## Fase 5 — RabbitMQ e Worker

Entregas: WorkflowExecution inicial, OutboxMessage, InboxMessage, publisher confirms, acknowledgements manuais, contrato de mensagem versionado, consumer e claim mínimo no PostgreSQL. Mensagens levam IDs e contexto, sem secrets ou payload integral.

Critério de saída: Testcontainers para PostgreSQL/RabbitMQ; criar execução e outbox atomicamente, publicar depois de indisponibilidade do broker, receber duplicação e impedir dois Workers de assumir o mesmo trabalho. Uma recepção registrada e interrompida deve poder ser recuperada.

Revisão: a falha entre commit e publish não perde execução; a falha entre confirm e atualização da outbox não executa trabalho duplicado. Received na inbox não é tratado como Done.

Commit sugerido: Despachar execuções com outbox transacional.

## Fase 6 — Execution Engine

Entregas: carregar versão fixada, percorrer caminho sequencial, gravar estados e checkpoints com contexto operacional limitado/protegido e keyring persistente, contrato dos executores e primeiro executor Log. Execução não implementada retorna erro claro, sem sucesso simulado. Cancelamento cooperativo básico e proposta de NodeExecution Cancelled.

Critério de saída: unitários de travessia e transições; integração de execução simples, retomada de checkpoint, falha de node, cancelamento antes de iniciar e entre nodes. Versão publicada depois do enqueue não modifica a execução anterior.

Revisão: um executor não determina sozinho o próximo node; a engine controla fluxo e persistência. Checkpoint não afirma garantia sobre efeitos externos.

Commit sugerido: Implementar execução sequencial de workflows.

## Fase 7 — Webhook Trigger

Entregas: POST /hooks/{endpointId}, segredo em header com hash no banco, validação de endpoint/versionamento, limite de payload, rate limit local e resposta 202 após transação. Reserva inicial de Idempotency-Key por proprietário/endpoint e comparação do conteúdo.

Critério de saída: requests válidos, segredo errado, endpoint desativado, payload excedido e indisponibilidade do broker. Duas chamadas simultâneas com a mesma chave geram uma execução; mesma chave e conteúdo diferente retorna conflito.

Revisão: segredo ausente em URL, logs e traces; aceitação não aguarda a automação; eventos distintos com payload igual não são indevidamente descartados.

Commit sugerido: Receber execuções por webhook autenticado.

## Fase 8 — HTTP Request Node e Credentials

Entregas: executor HTTP, configuração validada, referências a Credential, criptografia com keyring compartilhado/persistente fora do banco, timeout, limites e política de destinos. Allowlist, resolução/conexão aprovadas, tratamento IPv4/IPv6, redirects desativados e credenciais restritas à origem.

Critério de saída: servidor externo controlado para sucesso, falha, timeout, body excedido e headers protegidos. Testes de SSRF cobrem rede privada/loopback/link-local, DNS rebinding, redirecionamento, metadados e IPv6. Credenciais de outro dono são recusadas e não aparecem em snapshots/logs.

Revisão: validar hostname sem controlar a conexão é insuficiente. Timeout não é prova de ausência de efeito remoto; política de rotação de credenciais entre tentativas fica explícita.

Commit sugerido: Executar requisições HTTP com destinos e credenciais protegidos.

## Fase 9 — Condition, Transform e Delay

Entregas: operadores declarativos de Condition com portas true/false; transformação JSON limitada; Delay com ResumeAt durável, suspensão, scheduler e continuação por outbox. Delay entra aqui para consolidar todos os seis tipos iniciais antes do MVP.

Critério de saída: testes de paths ausentes, tipos incompatíveis, limites de transformação, escolha exclusiva de ramo e nodes Skipped. Reiniciar Worker durante Delay deve permitir retomada sem consumir uma thread ou manter delivery em aberto durante a espera.

Revisão: nenhum eval/script/shell; convergência não marca como Skipped um node que ainda pertence ao caminho escolhido; inputs e outputs têm contrato determinístico.

Commit sugerido: Adicionar condições transformações JSON e esperas duráveis.

## Fase 10 — Retry, DLQ e idempotência

Entregas: NodeExecutionAttempt, retry exponencial com jitter/prioridade de Retry-After limitado, máximo de tentativas e deadline. Lease renovável com fencing, recuperação de claims vencidos, deduplicação completa, concorrência limitada, DLQ, replay explícito e cancelamento em andamento.

Critério de saída: duplicação de publish/delivery, queda antes/depois de commit/ack, Worker antigo tentando gravar após lease vencida, retomadas repetidas, broker indisponível e cancelamento durante HTTP/Delay. Validar POST com efeito sem retry automático e destino compatível recebendo chave de idempotência estável.

Revisão: medir garantia at-least-once e registrar efeitos externos desconhecidos; definir janela de retenção para inbox, replay e chaves de entrada. Falha de negócio não deve ir indiscriminadamente para DLQ.

Marco: MVP de backend concluído somente se o fluxo completo webhook → Condition → HTTP Request → Log e um fluxo com Transform/Delay forem executáveis e os testes de falha passarem.

Commits sugeridos: Implementar tentativas limitadas e recuperação de entregas; Cobrir cenários de falha das execuções.

## Fase 11 — Frontend funcional

Entregas: listagem, formulário de criação/edição, publicação, execução, detalhe dos nodes, logs e cancelamento. Primeiro editor baseado em formulário/estrutura, com tratamento de loading, erro e conflito.

Critério de saída: lint/typecheck/build; teste de uma jornada de criação e consulta de execução, incluindo falha de API e autorização quando disponível. Usar dados fictícios.

Revisão: interface mostra o estado do backend; não gera sucesso otimista para publicação ou cancelamento ainda pendente.

Commit sugerido: Criar telas de workflows e execuções.

## Fase 12 — Editor visual

Entregas: React Flow, cards e conexões por porta, configuração de nodes e validação com mensagens compreensíveis. Layout visual persiste separado da semântica de execução.

Critério de saída: editar/salvar/reabrir sem perder configuração; representar portas true/false; rejeitar conexões incompatíveis; executar workflow publicado criado pelo editor.

Revisão: backend continua sendo autoridade da validação; nenhuma animação ou polimento visual bloqueia contrato correto.

Commit sugerido: Implementar editor visual de workflows.

## Fase 13 — Autenticação e RBAC

Entregas: autenticação do User já introduzido para ownership, JWT, RefreshSession, rotação, detecção de reuse, revogação, password hashing, roles Owner/Admin e autorização por objeto. Cookie de refresh e proteção CSRF/CORS conforme a implantação.

Critério de saída: login, expiração, rotação concorrente definida, logout, revogação de família, reutilização de token e tentativas de acessar recursos/credenciais de outro dono. Testes de isolamento e limites por usuário.

Revisão: endpoints não confiam em OwnerUserId fornecido pelo cliente; tokens e passwords ausentes dos logs.

Commit sugerido: Proteger acesso e rotacionar sessões de atualização.

## Fase 14 — Observabilidade

Entregas: OpenTelemetry na API, Worker e HTTP; propagação de trace pelo RabbitMQ; spans de nodes; correlação de logs; métricas e painel mínimo para investigar uma execução. Sem payload em atributos de trace.

Critério de saída: acompanhar um webhook da aceitação ao node externo; observar sucesso, falha e retry; medir duração ativa versus espera, backlog e atraso de outbox.

Revisão: ExecutionId e outros IDs únicos não são labels de métricas; logs e traces continuam sanitizados; coleta tem configuração explícita.

Commit sugerido: Rastrear execuções entre API e Worker.

## Fase 15 — Testes e CI/CD

Entregas: GitHub Actions com restore/build/test, serviços de integração/Testcontainers, lint/typecheck/build do frontend e validação/build dos containers. Consolidar a suíte já criada e política de dependências/segredos. Deploy só entra quando houver destino escolhido e autorizado.

Critério de saída: pipeline reproduzível em clone limpo, PR com falha real sendo bloqueado, artefatos relevantes e teste de smoke do Compose. Não exigir serviços de produção para CI.

Revisão: testes verificam comportamento e falhas relevantes; não apenas getters, mocks ou detalhes internos. Flakiness e custo têm tratamento explícito.

Commit sugerido: Automatizar validação do backend frontend e containers.

## Fase 16 — Refino para portfólio

Entregas: README profissional, diagrama, screenshots reais, exemplo de workflow reproduzível, ADRs finais, instruções de operação/retenção, limitações conhecidas, roteiro de demonstração e temas técnicos para entrevista/LinkedIn.

Critério de saída: outra pessoa inicia ambiente com configuração documentada, executa exemplo e investiga uma falha. Rever exposição de portas, secrets, SSRF, quotas, backup e persistência de keyring. Screenshot mostra sistema implementado; funcionalidades futuras ficam no roadmap.

Revisão: toda tecnologia tem justificativa; material distingue garantia local de efeito externo; nenhuma decisão arquitetural importante está escondida em código gerado.

Commit sugerido: Documentar arquitetura e operação da plataforma.

## Como trabalhar em cada fase

1. Definir comportamento e contrato pequeno, incluindo erros e limites.
2. Implementar a menor fatia executável.
3. Rodar build e testes apropriados; registrar impedimentos reais.
4. Revisar domínio, transações, concorrência, segredos e documentação.
5. Fazer commits pequenos que correspondam a mudanças reais.
6. Encerrar a fase com resultado e critérios pendentes explícitos.

A fase atual não será estendida silenciosamente para implementar a próxima. Sugestões de commits neste documento são planejamento, não histórico de commits existentes.
