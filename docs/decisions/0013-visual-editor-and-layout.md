# ADR 0013 — Editor visual e layout versionado

Status: aceita e implementada na Fase 12 em 13/10/2026; CI do PR e da main aprovados. Evidências na [revisão da Fase 12](../phase-12-review.md).

## Problema

O formulário já preservava configuração/revisão e o redesign do [PR #9](https://github.com/gabxw/flowforge/pull/9) mostrava um esquemático. Faltava montar e modificar conexões diretamente, posicionar cards e reabrir o mesmo layout sem alterar regras ou perder dados.

## Alternativas e escolha

| Problema | Alternativas | Escolha e custo |
| --- | --- | --- |
| Interação no grafo | Estender SVG próprio ou React Flow | @xyflow/react 12.12.0 fixado, para arrastar/pan/zoom/portas/foco. Uma dependência runtime nova com suas transitivas; aumenta o bundle e exige cuidar da projeção |
| Estado | Persistir o objeto da biblioteca ou manter DTO | Rascunho da Fase 11 é a fonte de dados. Canvas projeta IDs/tipos/posições; medidas, seleção e câmera ficam em memória |
| Layout | Tabela/endpoint separados ou PositionX/PositionY existentes | Metadados de posição já aceitos pela API. Nenhuma migration ou novo serviço; layout acompanha a versão imutável |
| Organização | Dagre/ELK ou arranjo pequeno explícito | Ação Organizar nodes, limitada a 50 nodes, sem outra dependência. O usuário decide e salva |
| Validação | Regras completas no browser ou feedback local | Cliente recusa gestos incompatíveis; Domain continua validando publicação, configuração e DAG completo |

A escolha mantém Domain/Application/Infrastructure/Worker intactos. O React Flow participa apenas da apresentação e interação. A [ADR 0012](0012-visual-identity-schematic-and-trace.md), a fonte Archivo e o trace foram preservados.

## Contrato e dados

Payload de rascunho continua com nodeId, position {x,y}, configuration e credentialId, além de connections com id/sourceNodeId/targetNodeId/sourcePort. Não serializamos ReactFlow.toObject, data, measured, seleção ou viewport.

Arrastar usa grade de 20px, altera somente position e conserva a ordem/IDs/configuração/credencial. Selecionar, usar pan ou zoom não suja o rascunho. As dimensões medidas pela biblioteca ficam em memória e permitem enquadrar nodes adicionados. Keyboard Tab foca, Enter/Espaço seleciona e setas movem quando editável.

Layout é independente do caminho: a engine segue connections/sourcePort. Posições ficam em colunas próprias, separadas de Configuration. São versionadas junto da definição; alterar uma publicação exige novo rascunho. Esse custo mantém o comportamento de imutabilidade existente e evita outra entidade/endpoint de layout antes da necessidade.

O esquemático salvo agora lê position; a translação para coordenadas negativas existe só para enquadrar o SVG. Organizar nodes não é executado automaticamente ao abrir. Rascunhos antigos podem ter sobreposição e o usuário pode organizar explicitamente.

## Portas e revisão

Condition tem true/false; demais tipos têm next. Trigger inicia o grafo e não recebe entrada. O cliente impede self-link, ciclos indiretos, referência ausente, segunda conexão na mesma saída e limite de conexões. Convergência de ramos em um node é permitida.

A lista de conexões usa a mesma função de feedback do canvas. Conexões inválidas já existentes são conservadas, desenhadas com indicação de revisão e explicam o motivo ao selecionar. Trocar tipo não apaga silenciosamente a estrutura.

Validação local cobre só interação; pode divergir caso o contrato evolua. API/Domain continuam impondo publicação completa, credenciais/ownership, cardinalidade, alcance e configuração. Erro/409 conserva texto e layout locais; descarte e recarregamento seguem a decisão explícita da Fase 11.

## Design e evidência

Tema azul-prussiano, acentos semânticos, glifos e tipografia do usuário foram mantidos. Cards comparáveis fazem sentido para nodes; o grafo é o destaque. Removemos o halo redundante de seleção e usamos texto normal nas contagens.

Prints reais/contraste/teclado/responsividade são revisados conforme design-de-interface. MCP context7/chrome-devtools não estão disponíveis nesta sessão; APIs foram verificadas na documentação oficial/tipos instalados e as inspeções ocorreram com Chromium/Playwright. Não se declara execução desses MCPs. LCP/CLS de página pública não se aplica ao ambiente local/privado.

Referências primárias: [nodes customizados](https://reactflow.dev/learn/customization/custom-nodes), [handles/portas](https://reactflow.dev/learn/customization/handles), [componente controlado](https://reactflow.dev/api-reference/react-flow), [acessibilidade](https://reactflow.dev/learn/advanced-use/accessibility).
