# Operação do frontend — Fase 11

A interface privada permite listar/criar workflows, editar detalhes e estrutura, publicar versões, abrir novo rascunho, executar a versão publicada, consultar histórico/etapas/tentativas/logs e solicitar cancelamento. A Fase 12 acrescenta o [editor visual](visual-editor.md) com posições/conexões persistentes; a Fase 13 entregou login/sessões e preservação de rascunho na reautenticação, descritos em [autenticação](authentication.md).

## Executar e navegar

Use o Compose e as migrations explícitas do [README](../README.md). Frontend em http://127.0.0.1:5173 utiliza /api pelo Nginx; PostgreSQL/RabbitMQ não precisam publicar portas.

Para desenvolver a interface com API no host:

~~~powershell
cd frontend
npm ci
npm run dev
~~~

Vite escuta 127.0.0.1:5173 e encaminha /api para localhost:5080. Não há URL de conexão nem secrets no bundle. Com API/banco indisponível, a tela mostra erro e oferece nova consulta; iniciar o host não aplica migrations.

| URL | Uso |
| --- | --- |
| /#/workflows | Lista paginada; incluir arquivados e consultar execução por ID |
| /#/workflows/new | Nome/descrição e criação de rascunho vazio |
| /#/workflows/{id} | Detalhes, versões, estrutura e histórico |
| /#/executions/{id} | Estado, nodes, tentativas, logs e cancelamento |

Links podem ser copiados/recarregados. IDs inválidos mostram página não encontrada; recursos ausentes/de outro proprietário recebem 404. Desde a Fase 13, ownership usa a conta autenticada e os endpoints privados exigem JWT/RBAC. Admin não tem bypass de recursos de outro dono; login e reautenticação preservam o rascunho da mesma conta.

## Jornada inicial

1. Clique em Novo workflow e informe nome/descrição fictícios.
2. Use o exemplo Trigger → Log ou adicione nodes e conexões manualmente.
3. Edite a configuração JSON por node; tipos são Webhook Trigger, HTTP Request, Delay, Condition, Transform JSON e Log.
4. Salve o rascunho e publique a versão. Grafo inválido retorna os erros da API e conserva a edição.
5. Execute o workflow. A ação manual envia input vazio {}; entrada com payload permanece no [webhook](webhooks.md).
6. Acompanhe o estado e abra os metadados das tentativas. Logs não exibem a mensagem protegida.
7. Volte ao workflow para consultar o histórico persistido. Arquivar impede novas edições/execuções e preserva as anteriores.

O exemplo com ramos utiliza Condition, Transform e Delay; não faz HTTP externo. HTTP começa com URL fictícia e exige um destino HTTPS autorizado pelo operador antes da execução. Segredos ficam em Credential, referenciada por UUID; não inserir secrets na URL/JSON.

Publicação é imutável. Para editar depois, clique em Abrir novo rascunho. O seletor permite consultar versões anteriores em modo de leitura. Trocar de versão ou sair com edição não salva pede confirmação; cancelar a saída conserva a edição.

## Conflitos e resultados incertos

Editar detalhes/estrutura, publicar, abrir novo rascunho e arquivar um workflow existente enviam expectedRevision. Criação, solicitação de execução e cancelamento seguem seus próprios contratos sem esse campo. Em 409, a estrutura local permanece disponível para revisão/cópia; Recarregar versão do servidor exige descarte explícito. Não há merge automático.

Salvar/publicar/arquivar só atualiza a interface depois da resposta confirmada. Uma perda de resposta pode ocorrer depois do commit: comandos não são repetidos automaticamente. Consulte o servidor antes de emitir outro comando, especialmente uma nova execução.

Cancelamento é cooperativo. Enquanto o backend ainda retorna Pending/Running com CancelRequestedAt, a tela informa que aguarda confirmação. Não conclui artificialmente e não desfaz efeitos externos.

A tela de execução consulta primeiro seu estado, depois nodes/logs/tentativas. Essas leituras não formam um snapshot único entre endpoints; o próximo polling/manual refresh converge para o histórico confirmado. Atualiza a cada 3s enquanto ativa, sem sobreposição, pausa em aba oculta e para em estado terminal/erro. Se uma atualização falhar, dados antigos ficam marcados como última consulta bem-sucedida. Navegação cancela requests anteriores.

## Limites do editor

As regras de publicação continuam no Domain/API. O formulário limita 50 nodes/100 conexões, configuração JSON, profundidade, UUIDs e referências; não executa scripts/expressões livres. Posição já existente é preservada para a evolução do editor visual.

Números JSON que não sobrevivem ao roundtrip de Number/JSON.stringify são recusados antes de alteração. Exemplo: uma condição com 28 casas decimais não é arredondada para 17. O workflow permanece válido/intacto na API, mas precisa de um cliente que suporte sua precisão para editá-lo. Strings com dígitos são preservadas. [ADR 0011](decisions/0011-frontend-and-server-authority.md).

A interface mostra apenas tamanho/tipo do input/output, evento/tamanho de Log e status/horários/códigos das tentativas. Não há armazenamento local persistente desses dados, cofre de credenciais ou tela de replay/redrive na Fase 11.

## Testar

~~~powershell
cd frontend
npm ci
npx playwright install chromium
npm run lint
npm run build
npm test
~~~

npm test usa Chromium e API interceptada para falhas, 409, cancelamento ainda ativo, corrida de respostas, validação JSON e fluxo funcional; inclui testes de preservação numérica. A configuração inicia Vite automaticamente em 5180, ou usa FLOWFORGE_E2E_BASE_URL quando fornecida.

Com Compose já preparado, execute a jornada com serviços reais:

~~~powershell
$env:FLOWFORGE_E2E_BASE_URL = 'http://127.0.0.1:5173'
npm run test:backend
~~~

A jornada cria somente dados fictícios, publica Trigger → Log, espera Succeeded, verifica histórico e arquiva apenas o workflow criado. Não remove volumes. Report/trace/screenshots ficam em frontend/playwright-report e frontend/test-results, ignorados pelo Git. Use esses artefatos apenas com dados fictícios.

O CI executa lint/build/audit, jornadas determinísticas e uma jornada real pelo Nginx após migrations. A validação Docker anterior continua cobrindo recuperação e encerramento do Worker.
