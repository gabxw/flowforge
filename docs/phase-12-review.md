# Revisão técnica — Fase 12

Data: 13/10/2026.
Status: implementação e revisão em validação; publicação/CI pendentes. Fase 13 não iniciada.

## Entrega

Editor visual com React Flow, cards dos seis tipos, portas next/true/false, drag, pan/zoom, configuração existente, grade, organização explícita e persistência/reabertura de layout. Preserva o redesign do usuário integrado no PR #9 (base ad5bbee), incluindo Archivo, azul-prussiano, esquemático e trace.

- Estado local da Fase 11 continua como fonte de dados; modelo da biblioteca não entra no DTO.
- Seleção/medidas/câmera não fazem parte da persistência.
- Arrastar muda só posição; IDs, configuração, credencial e ordem são conservados.
- Cliente bloqueia gestos incompatíveis e Domain valida publicação completa.
- Conflito/erro conserva texto e posição locais; publicação/arquivo é somente leitura.
- Esquemático representa posições salvas; organizar não acontece automaticamente.
- @xyflow/react 12.12.0 é a única dependência runtime nova; sem migration, engine nova, layout engine ou autenticação.

[ADR 0013](decisions/0013-visual-editor-and-layout.md) e [operação](visual-editor.md).

## Validação

Backend: restore locked/build Release sem warnings/erros; **1.247 xUnit aprovados**, zero falhas/ignorados (787 Domain + 111 Application + 125 API + 224 Integration). Os projetos backend não foram alterados.

Frontend: lint/typecheck/build aprovados, audit sem vulnerabilidades e **40 testes aprovados**, sem falhas, retry ou ignorados: 27 anteriores, 7 regras de grafo e 6 jornadas visuais, incluindo teclado/foco e reflow em 390/720px. Bundle JavaScript de aproximadamente 468 kB (149 kB gzip); React Flow é a única dependência runtime acrescentada.

Docker isolado flowforge-phase12-validation-20261013 aprovado na versão final: build das imagens, nginx -t, migrations idempotentes duas vezes, proxy/smoke, **duas jornadas reais Chromium** e shutdown normal do Worker (exit 0). O editor criou/publicou/executou os ramos true e false em versões distintas, preservou a primeira versão/execução e reabriu posições/configuração. Só os volumes desse projeto de validação foram removidos; API, Worker, banco e fila da demonstração em 5173/5080 foram preservados.

Prints reais inspecionados: [editor](screenshots/phase-12-editor.png) e [publicação com layout salvo](screenshots/phase-12-published.png), além de capturas locais em 390px e 720px. Nenhum overflow horizontal da página, erro de console ou pageerror. 720px representa a largura útil equivalente a reflow de 200% sobre 1440px; não houve alteração do zoom do browser. Enquadrar é uma visão geral do grafo, especialmente pequena no mobile; zoom/pan e formulário mantêm acesso aos detalhes. Contraste e navegação por teclado conferidos abaixo.

## Revisão técnica e design

A biblioteca inicialmente recebia posições sem guardar suas medidas na projeção; adicionar um node deixava suas portas fora do enquadramento. A correção conserva measured em memória e espera nodes inicializados; os testes de conexão efetiva passaram. measured não é enviado ao backend.

Uma jornada real encontrou um teste que aceitava o selo Concluída de um node antes de a execução terminar. Os dois testes de backend agora aguardam esse estado no cabeçalho da execução; a repetição final passou sem retry.

A instrução de teclado foi ajustada ao comportamento verificado: Tab foca, Enter/Espaço seleciona e setas movem. Nome acessível do canvas/controles, campos rotulados e hierarquia do formulário original preservados.

Contraste calculado: texto secundário mínimo **4,99:1**, contornos novos **3,02:1** sobre card e **3,93:1** sobre canvas; campo existente **3,49:1**; foco azul **7,36:1**; sinais gráficos 7,77..11,13:1 sobre canvas. Contorno anterior de card 2,82:1 foi trocado pelo token trace. Halo decorativo de seleção foi retirado.

MCP context7/chrome-devtools indisponíveis. APIs conferidas em fontes primárias e versão instalada; browser real usado via Playwright. Esses MCPs não foram executados, e seu uso não é apresentado como verificação aprovada. LCP/CLS de página pública não se aplica à interface privada.

Domain continua independente de ASP/EF; nenhuma regra de execução foi movida para o browser. IDs e posições ficam no contrato existente, sem serializar dimensões/estado da biblioteca. Validação local é parcial; publicação permanece no backend. Layout acompanha a versão imutável e uma mudança visual exige rascunho/publicação.

Limites anteriores de precisão JSON, API privada sem JWT/RBAC, retenção/HA/keyring Production continuam explícitos. A organização é simples e não resolve roteamento ótimo em todo grafo. SVG antigo passa a usar coordenadas reais; rascunhos com sobreposição precisam de ação explícita do usuário.

## Publicação

Pendentes: commits em português/autoria gabxw, PR, CI do PR/main e encerramento documental.
