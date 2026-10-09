# ADR 0004: API privada e revisão explícita de workflows

Status: implementada na Fase 4.

## Problema

O domínio e os stores existem, mas um cliente ainda precisa de um contrato HTTP para editar um rascunho, receber erros úteis e publicar uma versão sem sobrescrever outra edição. Autenticação pertence à Fase 13; sua ausência não autoriza o cliente a escolher OwnerUserId.

## Alternativas e escolha

Endpoints mínimos delegam a WorkflowService em Application. O serviço coordena usuário técnico, domínio, relógio e portas da Fase 3; endpoints/mapeador tratam somente transporte. Controllers também serviriam, mas não acrescentariam comportamento nesta superfície pequena. Mediator/repositório genérico acrescentariam indireção sem resolver um problema atual.

O dono vem de configuração do servidor. Um dono global implícito esconderia ownership; owner fornecido no request permitiria acesso indevido. Configuração explícita mantém escopo por objeto verificável até a identidade autenticada substituí-la. A API permanece privada; isso não é mecanismo de autenticação.

Toda mutação de recurso existente carrega expectedRevision do workflow. A aplicação rejeita revisão velha antes de mudar o agregado; o store executa CAS na transação para fechar a corrida entre leitura e gravação. ETag/If-Match seria uma alternativa HTTP válida; campo obrigatório foi escolhido por ser simples no formulário/editor futuro e no roteiro. Conflito retorna 409 e exige decisão do cliente, sem retry cego ou last-write-wins.

Uma operação responde com o agregado que ela gravou, sem outra consulta pós-commit. Uma nova leitura poderia retornar edição de outro cliente, perder o ID do rascunho recém-criado ou falhar depois de um commit já efetivado. Os casos de uso geram timestamps UTC com precisão pública de microssegundos, preservada pelo PostgreSQL.

DTOs são separados do domínio e do JSON de armazenamento. Configuration é uma união fechada com type e os seis formatos declarativos. O discriminador, enums e JSON null sobrevivem ao transporte/JSONB sem expor schemaVersion interno. JSON rejeita campos desconhecidos, duplicação e coerção de strings para números. A leitura do corpo do rascunho fica no mapeador de transporte: o serializer lança NotSupportedException para tipo abstrato sem discriminador, que deve virar 400. Accepts declara o DTO real para o OpenAPI nativo.

Rascunhos podem ficar incompletos, mas IDs/referências/limites precisam caber no armazenamento. A aplicação filtra somente essas invariantes para salvar; publicação usa todas as regras do domínio. Uma colisão global de conexão no SQL vira 409 sanitizado e rollback; repetição dentro do próprio grafo/histórico recebe 422 com identificação da regra.

Migrations são explícitas. Startup sem banco continua fornecendo liveness e OpenAPI, enquanto CRUD retorna 503 quando não há configuração/schema/conexão. Rodar migrations em cada réplica ocultaria mudanças operacionais e criaria concorrência desnecessária. O Compose monta o mesmo segredo em API/PostgreSQL; o script aplica SQL idempotente pelo socket interno, sem publicar o banco ou imprimir senha.

O handler usa Results.Problem com fallback JSON: o writer padrão pode recusar Accept: text/plain. Mesmo nesse caso, erros conhecidos, falhas internas e rotas ausentes precisam manter corpo sanitizado no formato anunciado.

## Consequências e limites

- Rascunho é substituído por inteiro; não há merge automático entre clientes.
- A consulta de detalhe inclui histórico completo. Paginação limita workflows, não versões; retenção e histórico volumoso serão tratados quando houver necessidade.
- Offset/limit são simples e estáveis para dados parados; alterações entre páginas podem mudar a janela. Não há promessa de snapshot entre requests.
- Credenciais são somente referências nesta fase. Não há executor, validação de existência do segredo ou autenticação.
- Proxy preserva /api; aliases de liveness/OpenAPI mantêm compatibilidade do shell inicial.
- Configuração lazy permite inspecionar o host indisponível, mas requer verificar o CRUD para afirmar prontidão operacional.

## Verificação e entrevista

WebApplicationFactory usa PostgreSQL 17 real em Testcontainers. Testes cobrem jornada, seis configurações, JSON null, erros, preservação de publicação, duas edições simultâneas e dois donos em servidores distintos. O roteiro Compose confirma migrations explícitas/idempotentes, persistência e proxy.

Em entrevista: explicar por que o dono precisa ser confiável mesmo antes de JWT, por que uma checagem de revisão em memória não fecha a corrida, como o CAS preserva histórico e por que publicar definição ainda não executa automação.

Referências primárias: [erros e Problem Details](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0), [metadados OpenAPI e polimorfismo](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/include-metadata?view=aspnetcore-10.0), [propriedades JSON duplicadas](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.allowduplicateproperties?view=net-10.0), [discriminador fora de ordem](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.allowoutofordermetadataproperties?view=net-10.0) e [wrapper de falha transitória do provider](https://github.com/npgsql/efcore.pg/blob/v10.0.3/src/EFCore.PG/Storage/Internal/NpgsqlExecutionStrategy.cs).
