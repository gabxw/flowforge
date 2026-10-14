# Editor visual — Fase 12

O editor usa React Flow com cards dos seis tipos e portas next/true/false. O mesmo componente mostra a versão publicada em modo leitura e o caminho percorrido por uma execução. A configuração fica num painel lateral com campos por tipo e um modo JSON. Tema e decisões visuais estão na [ADR 0017](decisions/0017-graphite-theme-and-single-canvas.md).

## Usar

1. Crie um workflow ou abra novo rascunho de uma publicação.
2. Use um exemplo ou escolha o tipo na barra do canvas e clique em Adicionar etapa.
3. Arraste os cards; ligue a saída à entrada de outra etapa.
4. Selecione um card para abrir o painel da etapa (duplo clique leva o foco ao painel). Use Campos para o formulário do tipo ou JSON para o texto completo; os dois editam a mesma configuração.
5. Use Organizar etapas se desejar um arranjo por profundidade/ramo. Essa ação altera somente posições e precisa ser salva.
6. Salve o rascunho. Reabra para confirmar o layout; publique e execute a versão aceita pelo servidor.

Selecionar uma conexão abre origem, porta e destino no painel; Todas as etapas volta à lista de etapas e conexões, que também cria e remove conexões por formulário. Remover uma etapa informa e remove suas conexões incidentes. Versões publicadas/arquivadas têm pan/zoom e abrem a etapa numa gaveta somente leitura, sem arrastar ou conectar.

Teclado: Tab percorre elementos; Enter/Espaço seleciona a etapa focada; setas movem uma etapa editável; Esc fecha a gaveta da versão publicada. O formulário Ligar a saída é a alternativa para criar conexões sem arrastar. As abas usam setas, Home e End.

## Regras

Condition oferece true/false; os demais tipos, next. Uma saída só pode ter uma conexão. Trigger não recebe entrada. O editor recusa ciclos, ligação consigo mesmo e referências inválidas. Mais de uma conexão pode chegar ao mesmo node para convergência de ramos.

Publicação continua sob autoridade do Domain/API. Campos JSON, tipo do node, existência/ownership de Credential, alcance e DAG completo são verificados no servidor. Uma mensagem de erro mantém as alterações locais disponíveis; 409 não faz merge nem atualiza silenciosamente a revisão.

O campo de porta mostra opções do tipo de origem. Conexão incompatível já salva é sinalizada e pode ser corrigida/removida; trocar tipo não elimina conexões sem decisão do usuário.

## Persistência

Salvar continua usando o endpoint de rascunho com expectedRevision. Só os campos do contrato são enviados. Posições em grade de 20px são metadados visuais; nenhuma ordem de execução é derivada delas. A engine segue conexões e portas.

Layout é versionado com a definição. Para mudar uma publicação, abra novo rascunho. IDs de nodes/configurações/credenciais são preservados.

Abaixo de 600 px de largura, a versão publicada e o caminho da execução são exibidos de cima para baixo, num arranjo calculado pelas conexões; isso não altera as posições salvas. Dimensões do React Flow, seleção e zoom/pan ficam em memória. Não existe autosave, storage local de credenciais, tabela nova de layout ou algoritmo de layout externo. Algumas posições antigas se sobrepõem: Organizar etapas é uma ação explícita para esse caso.

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
