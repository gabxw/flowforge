# Fase 4: API privada de workflows

Base: Fase 3 integrada em d0ec59b. Escopo autorizado: somente Fase 4 do roadmap.

## Contrato

O servidor obtém o proprietário de FlowForge:TechnicalOwnerId. Corpo, query e headers não escolhem o dono. Não há login nesta fase: a API deve permanecer em ambiente privado, com portas locais no Compose.

| Método e caminho | Comportamento |
| --- | --- |
| POST /api/workflows | Cria workflow com rascunho vazio; 201 e Location |
| GET /api/workflows | Lista do dono; offset >= 0, limit 1..100 (20 padrão), includeArchived=false |
| GET /api/workflows/{id} | Detalhe e histórico de versões |
| PUT /api/workflows/{id} | Altera nome/descrição |
| POST /api/workflows/{id}/drafts | Abre rascunho copiando a publicação vigente |
| PUT /api/workflows/{id}/draft | Substitui integralmente nodes/conexões do rascunho |
| POST /api/workflows/{id}/publish | Valida e publica o rascunho |
| POST /api/workflows/{id}/archive | Arquivamento terminal, preservando histórico |
| GET /api/workflows/{id}/versions/{versionId} | Consulta uma versão do mesmo workflow/dono |

Toda mutação de recurso existente exige expectedRevision inteiro não negativo, obtido do workflow. Revisão obsoleta retorna 409; o CAS transacional do store também protege a corrida após a leitura. Sucesso retorna o detalhe atualizado (201 na criação de workflow/rascunho). A revisão do workflow é distinta da revisão de cada versão.

A resposta de uma mutação representa o estado produzido por aquela operação, sem outra consulta depois do commit. Datas geradas pelos casos de uso usam UTC com precisão de microssegundos, preservada pelo PostgreSQL.

Rascunhos podem estar vazios, desconectados, sem branches ou conter ciclos/portas ainda inválidas. Identidades duplicadas, referências inexistentes, reutilização de conexão histórica, limites de 50 nodes/100 conexões e incompatibilidade de credencial não são persistidos. Publicação aplica integralmente o validador do domínio. Versões publicadas permanecem imutáveis.

## Transporte

DTOs próprios da API, separados das entidades e do codec de armazenamento. Node: nodeId, configuration, position (x/y, padrão zero), credentialId opcional. Configuration usa discriminador type com webhookTrigger, httpRequest, delay, condition, transformJson ou log. Sem owner/version/status/revision fornecidos pelo cliente no grafo.

Delay usa durationTicks (100 ns); Condition distingue expectedValue ausente de JSON null. Transform usa campos targetProperty e exatamente um de sourcePointer/literal, inclusive literal null. Enums são strings camelCase, sem números. JSON rejeita membros desconhecidos e propriedades duplicadas. Configurações passam pelos construtores do domínio.

Erros usam application/problem+json: 400 transporte/configuração inválida; 404 ausente ou outro dono; 409 revisão/estado; 422 grafo inválido, com errors (code/message/nodeId/connectionId); 503 configuração/conexão indisponível; 500 inesperado, sem detalhes internos. Cancelamento da requisição é propagado.

## Composição e operação

Application coordena portas existentes, domínio e TimeProvider; endpoints só convertem transporte e delegam. Infraestrutura continua responsável por transações/SQL. A criação assegura idempotentemente o usuário técnico, sem senha/login.

Conexão via FLOWFORGE_POSTGRES_CONNECTION_STRING no host/testes; Compose monta o segredo e configura host/database/username/passwordFile. Nenhuma migration no startup. Um script explícito gera SQL idempotente e aplica dentro do container PostgreSQL, sem publicar a porta ou imprimir segredo. Liveness/OpenAPI iniciam sem banco/configuração; somente operações de workflow dependem deles.

O prefixo /api é parte da rota real. O proxy mantém /api/workflows e preserva os aliases existentes de liveness/OpenAPI para compatibilidade.

## Aceitação

Testes HTTP com WebApplicationFactory e PostgreSQL 17 Testcontainers: jornada CRUD/publicação/novo rascunho/arquivo, seis configurações, erros de transporte/grafo, paginação, duas edições concorrentes e isolamento entre dois servidores com donos diferentes. Banco e credenciais de teste são descartáveis e separados do Compose existente. Roteiro reproduzível exercitado no Compose com migrations explícitas. OpenAPI descreve DTOs/respostas e continua disponível somente em Development.

Não inclui engine, execução, fila integrada, autenticação ou frontend funcional. Referências a credenciais são identificadores; existência e acesso ao segredo entram na Fase 8.
