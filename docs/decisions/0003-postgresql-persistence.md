# ADR 0003 — Persistência PostgreSQL de workflows

Data: 2026-10-09. Status: adotada; validação de integração registrada em [phase-3-review.md](../phase-3-review.md).

## Problema

O domínio da Fase 2 mantém um rascunho e publicações imutáveis. Precisamos recuperar o mesmo agregado depois de outro processo iniciar, impedir relações entre proprietários ou versões diferentes e evitar que duas edições sobrescrevam uma à outra. Uma falha no grafo não pode deixar os metadados parcialmente gravados.

## Escolha e alternativas

Usamos EF Core/Npgsql somente em Infrastructure. Registros internos representam cinco tabelas; portas específicas em Application expõem operações do agregado e uma projeção de listagem. Domain continua sem pacotes, atributos EF ou setters de armazenamento. Uma factory de reidratação valida snapshots e preserva identidades, datas, revisões e coleções protegidas.

Mapear diretamente as entidades reduziria a conversão, mas exigiria adaptar coleções e configurações imutáveis ao materializador. Guardar o agregado inteiro em JSON simplificaria o insert, mas perderia FKs dos endpoints e índices relacionais. Dapper/SQL direto seria possível, com mais código de schema/materialização. O modelo escolhido custa um mapper explícito, limitado à fronteira de persistência; não introduz repositório genérico ou Unit of Work adicional.

Identidade, ownership e relações são colunas com PKs/FKs compostas. Somente a configuração variável de cada node usa JSONB, com schemaVersion explícito e codec fechado para os seis tipos. Não persistimos nomes de classes ou tipos CLR. Construtores do domínio validam o resultado da desserialização. Credenciais aparecem somente como identificadores; a tabela e os valores secretos pertencem a uma fase futura.

## Concorrência e consistência

Save recebe a revisão originalmente lida. Um UPDATE condicional da raiz exige ID, proprietário e revisão esperada; zero linhas significa conflito. O UPDATE e todas as alterações do grafo usam uma única transação. O lock normal da linha no PostgreSQL dura até commit/rollback. Não precisamos de Redis ou lock distribuído para editar este agregado.

A alternativa de apenas ler Revision e depois atualizar deixaria uma janela de sobrescrita. Locks pessimistas desde a leitura manteriam transações abertas durante a edição do usuário. A revisão otimista permite editar fora da transação; o cliente que perde recarrega o workflow e decide como reaplicar sua alteração. Não há retry automático de conflito.

O store compara o histórico persistido antes de salvar. Versões publicadas permanecem intactas; somente nodes/conexões do rascunho anterior são substituídos. Promover esse rascunho antes de inserir outro respeita o índice parcial de um draft por workflow. Inserts seguem raiz sem ponteiro → versão → nodes → conexões → ponteiro de publicação, dentro da mesma transação.

Get monta o agregado em várias consultas curtas sob Repeatable Read, evitando misturar uma raiz antiga com um grafo novo. List projeta só metadados, com proprietário, ordenação estável e paginação limitada. Essa escolha custa consultas adicionais na leitura detalhada e regrava o draft inteiro; cabe nos limites atuais de 100 nodes e 200 conexões. Paginação por cursor e atualização diferencial exigiriam necessidade medida.

## Fidelidade do armazenamento

PostgreSQL armazena timestamptz com microssegundos; DateTimeOffset tem ticks de 100 ns. Normalizamos UTC e truncamos à precisão do banco na fronteira. A restauração do domínio continua preservando ticks; a perda de precisão ocorre explicitamente no adaptador, inclusive nas comparações de identidade temporal.

JSONB reorganiza propriedades e normaliza números. A comparação das configurações usa significado JSON, não texto serializado. Objetos com propriedades duplicadas são rejeitados pelo codec, inclusive dentro de literais, porque JSONB descartaria informação. Ausência de expectedValue e literal JSON null continuam distintos. Duração usa ticks inteiros; literais são JsonElement clonados, sem conversão para double.

## Limites e operação

Migrations são versionadas e executadas explicitamente. Não há EnsureCreated ou migration no startup da API/Worker. Testcontainers aplica a migration em PostgreSQL 17 descartável; o CI executa todos os testes, verifica drift do modelo e gera SQL idempotente.

Arquivamento é terminal e preserva histórico; os stores não oferecem exclusão física. FKs usam NO ACTION. As constraints garantem relações, unicidade e checks locais; validação do DAG e imutabilidade histórica também dependem do domínio/store. Escrita administrativa direta no banco não passa por essas regras de negócio.

users contém somente Id e CreatedAt. Ownership não é autenticação: na futura composição HTTP, a identidade do proprietário deve vir do contexto confiável do servidor. API e Worker ainda não usam os stores nesta fase. Roles de migration/runtime e proteção das credenciais serão definidas quando houver integração dos hosts, sem antecipar autenticação.

## Como explicar em entrevista

“Separei o domínio do armazenamento para preservar invariantes e histórico. FKs compostas impedem relações entre versões e usuários. Uso uma revisão esperada e uma transação para que uma edição concorrente perca claramente, sem sobrescrever dados. Testei tanto dois clientes concorrentes quanto uma falha depois do UPDATE, verificando que o banco inteiro volta ao estado anterior. Evitei Redis porque o PostgreSQL já resolve esse lock transacional.”

## Referências

- [Concorrência otimista no EF Core](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).
- [Chaves estrangeiras e principais no EF Core](https://learn.microsoft.com/en-us/ef/core/modeling/relationships/foreign-and-principal-keys).
- [Mapeamento JSON no Npgsql](https://www.npgsql.org/efcore/mapping/json.html).
