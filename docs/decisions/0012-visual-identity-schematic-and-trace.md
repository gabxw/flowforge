# ADR 0012 — Identidade visual, esquemático e trace de execução

Status: aceita e integrada à main pelo [PR #9](https://github.com/gabxw/flowforge/pull/9) em 13/10/2026. Não inicia a Fase 12. A paleta azul e o esquemático SVG foram substituídos pela [ADR 0017](0017-graphite-theme-and-single-canvas.md).

## Problema

A interface da Fase 11 resolvia o contrato (estados, conflitos, cancelamento), mas visualmente era um painel administrativo genérico: sidebar verde, cards brancos idênticos e formulários longos. Para um projeto de portfólio, a tela não mostrava o que o backend tem de interessante: um grafo versionado e execuções com fila, etapas, esperas duráveis e tentativas.

O redesign precisava trocar a camada visual sem alterar contratos, rótulos acessíveis ou o comportamento coberto pelos 27 testes de navegador.

## Alternativas e escolha

| Problema | Alternativas | Escolha e custo |
| --- | --- | --- |
| Biblioteca de UI | shadcn/ui, Material ou CSS próprio | CSS próprio com tokens em `index.css` e Tailwind 4 já existente. Nenhum componente novo foi necessário; o custo é manter os estilos à mão |
| Tipografia | Fonte do sistema, Google Fonts ou fonte empacotada | `@fontsource-variable/archivo`, empacotada no build. Funciona offline, no container nginx e sem requisição a terceiros. Uma família, usando o eixo de largura (expandida nos títulos, semicondensada na UI). Custo: cerca de 210 KB de woff2, carregados sob demanda por faixa Unicode |
| Visão do grafo | React Flow agora ou SVG próprio somente leitura | SVG próprio. React Flow pertence ao editor visual da Fase 12; aqui o desenho é só leitura e não persiste nada |
| Layout do esquemático | Usar `position` salva ou derivar da topologia | Derivar da topologia: coluna pela maior profundidade a partir dos gatilhos e ramo `false` uma linha abaixo. Até a Fase 12, o editor por formulário grava `position` como índice × 240 numa linha, sem intenção de layout. Quando o editor visual persistir posições reais, o esquemático deve passar a usá-las |
| Leitura da execução | Tabela de metadados ou cascata de tempo | Cascata no formato de tracing distribuído, calculada só com `createdAt`, `startedAt` e `finishedAt` que a API já expõe. Nenhum endpoint novo e nenhuma métrica inventada |

## O que a interface mostra

- **Esquemático da versão exibida**: nodes com tipo, resumo da configuração e portas `true`/`false`. Em versão publicada, um sinal animado percorre as conexões; em rascunho, as conexões ficam tracejadas. O desenho mostra o que está salvo, não as edições locais pendentes.
- **Trace da execução**: a primeira faixa é a espera na fila (da aceitação pela API até um Worker assumir); depois vem uma faixa por etapa. O Delay durável aparece como a etapa dominante, sem nenhum texto explicativo.
- **Leituras rápidas**: espera na fila, duração, etapas registradas e tentativas, todas derivadas da resposta da API.
- **Versões somente leitura**: a configuração completa fica recolhida, porque o esquemático já resume a estrutura.

A cor só aparece com significado: âmbar para fluxo e ação principal, menta para concluído, azul para execução ativa, coral para falha.

## Acessibilidade

- Contraste de texto acima de 4,5:1 em todos os pares de tokens. Bordas de campos, fios e contornos dos nodes acima de 3:1.
- Foco visível em todos os elementos interativos, com ordem de tabulação lógica.
- Sem rolagem horizontal da página a 720 px (equivalente a 200% de zoom em 1440 px).
- Esquemático e barras do trace têm nome acessível (`role="img"` com `aria-label`). A informação completa continua acessível no editor e nos cards de node.
- `prefers-reduced-motion` desliga o sinal animado e a animação das barras.
