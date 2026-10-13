# Editor visual — Fase 12

O editor usa React Flow com cards dos seis tipos e portas next/true/false. O formulário de configuração JSON e a publicação/execução da Fase 11 continuam disponíveis. Tema, fonte e trace da interface redesenhada foram preservados.

## Usar

1. Crie um workflow ou abra novo rascunho de uma publicação.
2. Use um exemplo ou selecione Tipo do novo node e Adicionar node.
3. Arraste os cards; ligue a saída à entrada de outro node.
4. Selecione um card e clique em Configurar node selecionado; também funciona por duplo clique. O formulário mantém JSON e referência de credencial.
5. Use Organizar nodes se desejar um arranjo por profundidade/ramo. Essa ação altera somente posições e precisa ser salva.
6. Salve o rascunho. Reabra para confirmar o layout; publique e execute a versão aceita pelo servidor.

Selecionar uma conexão permite removê-la explicitamente; a lista de conexões oferece o mesmo controle por formulário. Remover um node informa e remove suas conexões incidentes. Versões publicadas/arquivadas têm pan/zoom e configuração recolhida em modo de leitura, sem arrastar ou conectar.

Teclado: Tab percorre elementos; Enter/Espaço seleciona o node focado; setas movem um node editável. Os formulários são a alternativa para criar/editar/remover conexões sem drag. A seleção e os controles mostram foco visível.

## Regras

Condition oferece true/false; os demais tipos, next. Uma saída só pode ter uma conexão. Trigger não recebe entrada. O editor recusa ciclos, ligação consigo mesmo e referências inválidas. Mais de uma conexão pode chegar ao mesmo node para convergência de ramos.

Publicação continua sob autoridade do Domain/API. Campos JSON, tipo do node, existência/ownership de Credential, alcance e DAG completo são verificados no servidor. Uma mensagem de erro mantém as alterações locais disponíveis; 409 não faz merge nem atualiza silenciosamente a revisão.

O campo de porta mostra opções do tipo de origem. Conexão incompatível já salva é sinalizada e pode ser corrigida/removida; trocar tipo não elimina conexões sem decisão do usuário.

## Persistência

Salvar continua usando o endpoint de rascunho com expectedRevision. Só os campos do contrato são enviados. Posições em grade de 20px são metadados visuais; nenhuma ordem de execução é derivada delas. A engine segue conexões e portas.

Layout é versionado com a definição. Para mudar uma publicação, abra novo rascunho. IDs de nodes/configurações/credenciais são preservados. O esquema SVG agora representa coordenadas persistidas, inclusive negativas, em vez de inventar outro arranjo.

Dimensões do React Flow, seleção e zoom/pan ficam em memória. Não existe autosave, storage local de credenciais, tabela nova de layout ou algoritmo de layout externo. Algumas posições antigas se sobrepõem: Organizar nodes é uma ação explícita para esse caso.

Precisão JSON, limites de configuração/grafo, input/output protegido e resultados incertos seguem [frontend.md](frontend.md). O editor permanece local/privado até autenticação na Fase 13.

## Verificar

~~~powershell
cd frontend
npm ci
npx playwright install chromium
npm run lint
npm run build
npm test
~~~

Os mocks iniciam Vite em 5180 para conviver com a demonstração em 5173. FLOWFORGE_E2E_BASE_URL seleciona uma aplicação já preparada. Com Compose/migrations:

~~~powershell
$env:FLOWFORGE_E2E_BASE_URL = 'http://127.0.0.1:5173'
npm run test:backend
~~~

A jornada visual real cria dados fictícios, conecta um ramo no canvas, arrasta, salva/reabre, publica e executa true/false em duas versões. Confere configuração/layout/IDs e preservação da primeira versão; arquiva apenas o workflow criado. Não remove volumes.

[ADR 0013](decisions/0013-visual-editor-and-layout.md) explica alternativas e custos; [revisão da Fase 12](phase-12-review.md) registra evidências definitivas.
