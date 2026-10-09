# API privada de workflows

A Fase 4 disponibiliza definição, edição e publicação com PostgreSQL. O Worker ainda não executa nodes. A API não tem autenticação: use somente ambiente local/privado. O proprietário vem de FlowForge:TechnicalOwnerId no servidor; headers, query ou corpo não escolhem o dono.

## Subir e preparar o banco

Requisitos: SDK .NET 10, Docker Linux/Compose v2 e PowerShell 7 para os scripts.

Na raiz do projeto, configure o segredo sem registrá-lo no histórico:

~~~powershell
$env:FLOWFORGE_POSTGRES_PASSWORD = [System.Net.NetworkCredential]::new(
  '', (Read-Host 'Senha local do PostgreSQL' -AsSecureString)
).Password
docker compose up --build --detach --wait
.\scripts\migrate-compose.ps1 -GenerateOnly
# Revise .local/migrations.sql antes de aplicar.
.\scripts\migrate-compose.ps1
.\scripts\smoke-compose.ps1
.\scripts\smoke-workflows.ps1
.\scripts\smoke-workflows.ps1 -BaseUri http://127.0.0.1:5173
~~~

O script gera SQL idempotente com dotnet-ef, verifica drift do modelo e aplica por psql dentro do container PostgreSQL. Não publica a porta do banco, não imprime senha e não modifica volumes de outro projeto. -GenerateOnly não altera banco; -ProjectName seleciona um projeto Compose explicitamente criado com docker compose -p. A aplicação não executa migrations no startup. Banco vazio/indisponível retorna 503 para workflows; /health/live continua respondendo.

O Compose usa um proprietário técnico local fixo, visível no compose.yaml. Para usar outro, configure FLOWFORGE_TECHNICAL_OWNER_ID com um UUID não vazio antes de iniciar os serviços. Mantenha esse valor ao reabrir seus workflows; outro dono não verá os registros existentes. Isso demonstra isolamento por objeto, não autenticação.

As portas padrão são 5080 (API) e 5173 (frontend). FLOWFORGE_API_PORT e FLOWFORGE_FRONTEND_PORT permitem outros números, sempre em 127.0.0.1. Use o mesmo segredo ao reutilizar o volume; trocar a variável não troca a senha de um PostgreSQL já inicializado. docker compose down preserva os dados.

No host, configure FLOWFORGE_POSTGRES_CONNECTION_STRING por um mecanismo de segredos e FlowForge__TechnicalOwnerId no ambiente. A conexão deve alcançar um PostgreSQL com as migrations aplicadas. Não copie valores reais para appsettings, requests ou Git. O Compose monta o segredo em /run/secrets/postgres_password e fornece os demais campos por configuração.

## Operações

Base direta: http://127.0.0.1:5080. Pelo Nginx/Vite: /api/workflows, preservando o prefixo.

| Método | Caminho | Corpo/consulta | Sucesso |
| --- | --- | --- | --- |
| POST | /api/workflows | name, description opcional | 201, detalhe e Location |
| GET | /api/workflows | offset=0, limit=20, includeArchived=false | 200, items/offset/limit |
| GET | /api/workflows/{id} | — | 200, detalhe e versões |
| PUT | /api/workflows/{id} | expectedRevision, name, description opcional | 200, detalhe |
| POST | /api/workflows/{id}/drafts | expectedRevision | 201, detalhe e Location da versão |
| PUT | /api/workflows/{id}/draft | expectedRevision, nodes, connections | 200, detalhe |
| POST | /api/workflows/{id}/publish | expectedRevision | 200, detalhe |
| POST | /api/workflows/{id}/archive | expectedRevision | 200, detalhe |
| GET | /api/workflows/{id}/versions/{versionId} | — | 200, versão |

Criar gera IDs de workflow/versão e um rascunho vazio. IDs de nodes/conexões são UUIDs fornecidos pelo editor. A revisão inicial do workflow é 1; cada mutação aceita soma uma revisão. Use sempre revision do workflow retornado, não revision da versão. expectedRevision é obrigatório e deve ser um número inteiro JSON não negativo. Em 409, recarregue e decida como reaplicar a edição.

PUT de detalhes substitui nome/descrição; descrição omitida vira null. PUT de rascunho substitui integralmente o grafo, incluindo layout. Não há patch parcial. Um rascunho pode estar incompleto, mas não pode violar identidades/referências/limites de armazenamento. Publicar exige o DAG completo válido. Depois, POST /drafts copia a publicação vigente para outra versão, preservando IDs dos nodes e gerando novos IDs de conexões.

Arquivar é terminal, mantém o histórico e exclui o workflow da lista padrão. includeArchived=true permite listá-lo; GET continua disponível para o dono. Não há exclusão física ou desarquivamento. offset deve ser >= 0 e limit entre 1 e 100. A ordem é updatedAt decrescente, depois id decrescente; páginas refletem o banco no instante de cada consulta, sem snapshot entre requests.

Datas são UTC com precisão de microssegundos. A resposta de uma mutação representa seu próprio resultado, mesmo que outro cliente altere o recurso antes de ela chegar.

## Grafo e configurações

Exemplo de corpo de PUT /api/workflows/{id}/draft:

~~~json
{
  "expectedRevision": 1,
  "nodes": [
    {
      "nodeId": "84bb043c-7767-4425-b8f2-b124c43e10c5",
      "position": { "x": 0, "y": 0 },
      "configuration": { "type": "webhookTrigger" }
    },
    {
      "nodeId": "cd82604d-a2ae-4f8f-a32a-d5322b297fa2",
      "configuration": { "type": "log", "message": "Recebido" }
    }
  ],
  "connections": [
    {
      "id": "1c945ff6-2dfb-4a7d-8fa6-07de6c3fe9cb",
      "sourceNodeId": "84bb043c-7767-4425-b8f2-b124c43e10c5",
      "targetNodeId": "cd82604d-a2ae-4f8f-a32a-d5322b297fa2",
      "sourcePort": "next"
    }
  ]
}
~~~

Limites: 50 nodes, 100 conexões, nome de até 200 e descrição/mensagem de até 2.000 unidades UTF-16. position é opcional e inicia em (0,0); coordenadas devem ser finitas. Somente httpRequest aceita credentialId, sempre associado ao dono do servidor. A Fase 4 armazena apenas essa referência; validação de existência, segredo e execução entram na Fase 8.

| type | Campos |
| --- | --- |
| webhookTrigger | Nenhum |
| httpRequest | url HTTPS absoluta sem userinfo/fragmento; method: get/post/put/patch/delete/head/options |
| delay | durationTicks: inteiro positivo de até 864000000000 (24 horas; tick = 100 ns) |
| condition | sourcePointer, operation, expectedValue conforme operador |
| transformJson | fields: 1..50 campos com targetProperty e exatamente um de sourcePointer/literal |
| log | message não vazia |

Condition usa exists sem expectedValue; equals/notEquals aceitam escalar JSON, inclusive null; greaterThan/greaterThanOrEqual/lessThan/lessThanOrEqual exigem número representável por decimal. JSON null é distinto de propriedade omitida. JSON Pointer vazio representa a raiz; /a~1b representa a propriedade a/b.

Transform permite literal JSON de qualquer tipo, inclusive null, objeto e array. Campos têm destinos únicos; sourcePointer vazio é um path válido. Os seis tipos seguem as regras do [domínio](superpowers/specs/2026-10-09-phase-2-domain-design.md). Enums e nomes de campos usam camelCase. Membros desconhecidos, propriedades duplicadas e números entre aspas são rejeitados. type pode aparecer em qualquer posição do objeto.

## Erros e OpenAPI

Erros usam application/problem+json com status/title/detail. Ausência de recurso e acesso a outro dono retornam o mesmo 404. Mensagens não incluem SQL, conexão ou stack trace.

O formato de erro é preservado mesmo com Accept: text/plain, inclusive em Development; uma falha interna não vira corpo vazio ou página de diagnóstico.

| Status | Significado |
| --- | --- |
| 400 | JSON, campos, UUIDs, configurações ou limites inválidos |
| 404 | Recurso/versão ausente ou de outro dono; rota inexistente |
| 409 | Revisão obsoleta, estado incompatível ou identidade de conexão já em uso |
| 415 | Tipo de conteúdo incompatível com JSON |
| 422 | Grafo inválido; errors contém code/message/nodeId/connectionId |
| 503 | Configuração/banco indisponível ou migrations ausentes |
| 500 | Falha interna sanitizada |

Uma publicação rejeitada mantém rascunho, revisão e ponteiro anteriores. Em 422, os codes são strings camelCase do GraphErrorCode, como emptyGraph, cycle, unreachableNode e conditionBranchesIncomplete.

Em Development, consulte /openapi/v1.json (também /api/openapi/v1.json pelo proxy). O documento descreve operações, DTOs, discriminador dos seis tipos e respostas. Não há interface Swagger instalada. Em Production, esses caminhos retornam 404.

O [roteiro executável](../scripts/smoke-workflows.ps1) cria, edita, salva seis tipos, rejeita publicação vazia e edição obsoleta, publica, consulta versão, abre novo rascunho e arquiva. Pode ser repetido; cada execução deixa um workflow arquivado, sem executar nodes ou chamadas HTTP externas. Os testes automatizados adicionais cobrem concorrência simultânea, isolamento, transporte e persistência.
