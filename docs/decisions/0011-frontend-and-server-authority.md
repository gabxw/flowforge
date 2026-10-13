# ADR 0011 — Interface incremental e autoridade do servidor

Status: aceita, implementada e validada na Fase 11 em 13/10/2026; publicação/evidências na [revisão da fase](../phase-11-review.md).

## Problema

O backend já define versões imutáveis, aceita execuções assíncronas e registra histórico. Faltava uma interface para usar esse comportamento sem scripts, incluindo edição concorrente, falhas de rede e acompanhamento do cancelamento.

A interface precisa mostrar o resultado confirmado do servidor. Um POST aceito não significa execução concluída; timeout de transporte não prova que o comando deixou de ser aplicado.

## Alternativas e escolha

| Problema | Alternativas | Escolha e custo |
| --- | --- | --- |
| Navegação | React Router ou URLs com hash | Hash para quatro telas e links recarregáveis, sem dependência; rotas/guards locais precisam ser mantidos |
| Estado remoto | Query library/Redux ou hooks locais | Hooks pequenos por tela; cancelamento, polling e erros ficam explícitos, sem cache compartilhado |
| Edição do grafo | React Flow imediato ou formulário | Formulário de nodes/conexões e JSON por configuração; mantém identidades e prepara o editor visual da Fase 12 |
| Erros | Mostrar corpo bruto ou contrato normalizado | Status HTTP e mensagens locais; detalhes de infraestrutura nunca entram na tela |
| Histórico | IDs em localStorage ou consulta persistida | GET paginado por workflow; sobrevive à sessão e respeita o proprietário do servidor |
| Teste da experiência | Só mocks/unitários ou navegador | Playwright como única dependência nova de desenvolvimento; mocks determinísticos e uma jornada com Docker real |

Frontend usa somente React, fetch e a estrutura existente. A API continua separada do Domain; a nova leitura atravessa Application/Infrastructure e não altera engine, schema ou processamento.

## Estado, edição e comandos

Somente respostas bem-sucedidas de mutações atualizam revisão, publicação e arquivamento. POST de execução navega para a execução aceita; cancelamento mostra CancelRequestedAt e aguarda estado terminal do Worker.

expectedRevision vem do workflow retornado. Erro/409 preserva JSON local. Recarregar ou trocar de versão com alterações exige decisão explícita de descarte; publicação/execução ficam bloqueadas até salvar. Abrir novo rascunho copia a publicação, preservando IDs dos nodes e recebendo novos IDs de conexões do backend. Não existe merge automático de grafos concorrentes.

Leituras são canceladas ao navegar e protegidas contra respostas obsoletas. Uma mutação aceita invalida leituras anteriores. Polling sequencial de 3s acompanha somente Pending/Running, pausa em aba oculta e para em estado terminal ou erro. Cada request tem timeout de 10s. Não há retry automático de comandos: a interface avisa sobre resultado incerto e orienta consultar o servidor.

Sem Redux/query cache, o usuário pode repetir algumas leituras ao navegar. Esse custo é aceitável no volume atual e evita invalidar caches entre várias mutações/versionamentos antes de existir essa necessidade.

## JSON e precisão

O formulário verifica sintaxe, chaves duplicadas, profundidade, tamanho, identidades e referências básicas. Validação de DAG/configuração continua na API; não duplicamos as invariantes do Domain em TypeScript.

Number do JavaScript não representa todos os números JSON que a API aceita. Ler uma condição decimal longa e salvar outro node poderia alterar seu valor silenciosamente. Validamos tokens numéricos no texto antes de JSON.parse, comparando a forma decimal escrita com o roundtrip de JSON.stringify(Number(token)). Inteiros inseguros, valores não finitos e valores arredondados são recusados, inclusive na leitura da definição.

O trade-off é explícito: alguns workflows válidos na API não podem ser abertos para edição nesta interface. Permanecem intactos no servidor. Não implementamos aritmética decimal arbitrária nem adicionamos um parser externo apenas para ocultar essa diferença.

## Dados e segurança

URLs são relativas ao proxy existente; não se cria CORS amplo nem se coloca conexão/secret no bundle. Não há armazenamento de payloads/credenciais em localStorage. Logs, inputs, outputs e tentativas mostram apenas os metadados já autorizados pela API; não criamos endpoints para descriptografar conteúdo.

Configuração do workflow não é um cofre de segredos. HTTP usa credentialId; o editor informa isso. React renderiza nomes, JSON e erros como texto, sem eval ou HTML injetado. Não há autenticação simulada: a interface permanece local/privada até a Fase 13.

O histórico usa offset/limit 1..100 e ordem createdAt/id decrescente. Páginas não representam snapshot entre requests; inserções novas podem deslocar itens. O índice existente por proprietário/data atende a base atual, sem migration especulativa; um índice incluindo workflow deve ser decidido com medição do volume/plano.

## Como explicar em entrevista

“Construí a interface em cima do contrato existente, sem mover regras de negócio para o browser. Um 202 muda a tela para uma execução pendente e um cancelamento continua pendente até o Worker confirmar. Tratei revisão concorrente e resposta perdida como estados do produto, sem anunciar sucesso antes da confirmação.”

“Usei formulário antes do editor visual e estado local antes de uma biblioteca de cache. A nova consulta paginada tornou o histórico persistente. Testei a jornada em um navegador com API, PostgreSQL, RabbitMQ e Worker reais; mocks cobrem falhas e corridas que seriam difíceis de reproduzir manualmente.”
