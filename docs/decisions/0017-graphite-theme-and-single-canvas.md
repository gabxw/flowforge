# ADR 0017 — Tema grafite, canvas único e caminho da execução

Status: aceita em 14/10/2026. Substitui a paleta e o esquemático SVG da [ADR 0012](0012-visual-identity-schematic-and-trace.md); a fonte Archivo com eixo de largura, os tons de estado e a cascata de trace continuam. Não inicia fase nova.

## Problema

Depois da Fase 12 a página do workflow mostrava o mesmo grafo duas vezes: o esquemático SVG do redesign e, logo abaixo, o canvas React Flow do editor com cards pequenos, rótulos de porta cortados e configuração em caixas de JSON empilhadas sob o canvas. A execução mostrava "Etapa 1 / fe28a379" em vez do que cada etapa era, e uma falha aparecia só como código. O fundo azul-prussiano tingia painéis, bordas, campos e grade; com gradientes e grade decorativa, a tela lia como tema de dashboard e não como ferramenta.

## Escolha

- Superfícies em grafite neutro (`#0f0f11` a `#27272c`). Cor só onde há significado: âmbar para sinal e ação principal, os tons da engine para estados e tipos de etapa. Sem gradientes de fundo nem brilho em badges.
- Um único componente de grafo (`FlowGraph`, sobre React Flow) em três modos: edição no rascunho, leitura na versão publicada (sinal âmbar contínuo nos fios) e execução (etapas com estado, ramos não percorridos apagados e replay do caminho na ordem real de início).
- Painel lateral da etapa com formulário por tipo (Condition, Delay, HTTP, Transform, Log). O JSON continua disponível como modo alternativo e é a fonte de verdade: os campos reescrevem o JSON e ficam bloqueados quando o texto tem chave duplicada ou número que perderia precisão.
- Página do workflow com ações no cabeçalho e abas Grafo, Execuções e Configurações. Na versão publicada o canvas ocupa a largura toda e a etapa abre como gaveta.
- Falhas explicadas em português a partir do código sanitizado, sem expor detalhes do servidor.
- Vocabulário único "etapa" na interface; os nomes de tipo seguem os do domínio.

## Alternativas

Manter o esquemático e esconder o React Flow em modo leitura mantinha duas implementações do mesmo desenho e divergência entre o que se edita e o que se vê. Gerar formulários a partir de um schema exigiria publicar um contrato de schema por tipo na API; seis tipos fixos não justificam essa infraestrutura. Abas por rota (hash) remontariam a página e perderiam o rascunho local; ficam em estado do componente.

## Custos

O replay depende dos horários registrados por etapa; etapas sem início não entram na animação. Grafos largos ficam pequenos no celular e pedem zoom manual. Os testes de navegador passaram a selecionar a etapa e abrir o modo JSON antes de editar, e os rótulos mudaram de "node" para "etapa".
