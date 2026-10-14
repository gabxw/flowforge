# Revisão técnica — Fase 11

Data: 13/10/2026.
Status: concluída, revisada e publicada na main pelo [PR #8](https://github.com/gabxw/flowforge/pull/8). Build, testes, frontend e Docker aprovados localmente e no CI do PR/main. Fase 12 não iniciada.

## Entrega

Interface funcional para lista/criação/edição/publicação/versionamento/arquivamento e execução, com histórico remoto, nodes, tentativas, logs e cancelamento. Editor por formulário/estrutura dos seis tipos, sem React Flow ou autenticação antecipados.

- Navegação por URL com hash; links recarregáveis e IDs validados.
- Cliente fetch same-origin com timeout/abort e Problem Details sanitizado.
- Revisão do workflow nos comandos de edição/publicação/novo rascunho/arquivamento; erro/409 preserva rascunho e descarte é explícito.
- Estado confirmado do backend, sem publicação ou cancelamento otimista.
- Polling sequencial ativo/visível, resposta antiga invalidada na navegação/mutação.
- Proteção de precisão JSON na leitura/edição; nenhuma condição decimal é arredondada silenciosamente.
- GET paginado de execuções por workflow, owner-scoped e sem dados protegidos.
- Playwright como única dependência nova de desenvolvimento; CI com mocks e serviços reais.

[ADR 0011](decisions/0011-frontend-and-server-authority.md) registra alternativas e trade-offs; [operação](frontend.md) explica execução e limites.

## Validação

Build Release do backend: zero warnings/erros. Suíte completa: **1.247 aprovados**, zero falhas/ignorados (787 Domain + 111 Application + 125 API + 224 Integration). Os sete cenários novos cobrem paginação, workflow sem execuções, histórico arquivado, isolamento e parâmetros inválidos.

Lint/typecheck/build finais e npm audit aprovados, zero vulnerabilidades. **27 testes do frontend aprovados**: 13 jornadas Chromium com API controlada e 14 verificações de JSON exato; zero falhas/ignorados/retries.

Docker isolado flowforge-phase11-validation-20261013: build de todas as imagens, nginx -t, migrations aplicadas duas vezes, smoke/proxy e jornada real no navegador aprovados. A jornada cria/publica/executa/consulta histórico e arquiva apenas seu workflow fictício. Uma segunda demonstração percorre o formulário de Condition/Transform/Delay e uma execução concluída, com inspeção de lista/editor/execução em 1440px e 390px; sem pageerrors ou overflow horizontal da página. Screenshots reais foram inspecionados e salvos em docs/screenshots. Worker encerrou exited:0:false. Apenas o projeto descartável e seus volumes foram removidos.

[CI do PR](https://github.com/gabxw/flowforge/actions/runs/38393442034) e [CI da main](https://github.com/gabxw/flowforge/actions/runs/38394353322) aprovados para b6d4507dd22ea63d78faaaff0920dcde2618293f: 1.247 xUnit, 27 frontend e uma jornada real Chromium no Docker. Os três jobs passaram; EF sem alterações pendentes no modelo, SQL idempotente e smokes anteriores de recuperação/encerramento preservados.

## Revisão técnica e riscos

Domain permanece independente de ASP/EF e não foi alterado. A consulta nova utiliza a porta de armazenamento existente; nenhuma migration ou dependência backend foi acrescentada. Identidades, revisões, versão fixa e criptografia permanecem sob autoridade do servidor.

Revisão encontrou arredondamento de decimal longo em JSON.parse; a correção valida tokens antes do parse e recusa números incompatíveis com o roundtrip do navegador, com texto/dados preservados. Revisão também protegeu callbacks após desmontagem e invalidou leituras anteriores ao aceite de mutações.

Logs/input/output mantêm a política de metadados protegidos. Erros não reproduzem corpo técnico recebido. Sem eval, HTML injetado, localStorage de dados operacionais ou secrets no bundle.

O editor depende de JSON por configuração e não faz merge concorrente. Alguns números válidos na API ficam inacessíveis na interface por precisão; não são modificados. Histórico usa paginação por offset, sem snapshot entre páginas; versões do workflow continuam conforme contrato existente. Cache compartilhado, filtros adicionais e editor visual permanecem fora desta fase.

Uso local/privado sem JWT/RBAC. Autorização real continua pendente da Fase 13; a resposta 401 é exercitada como contrato de falha, sem simular login. Retenção/quotas/HA/keyring Production são os riscos já documentados na Fase 10. Banco e volumes operacionais não foram migrados/removidos.

## Publicação

Commits em português, autoria gabxw/noreply reconhecida pelo GitHub:

- 5f71af4 — Disponibilizar histórico paginado de execuções por workflow.
- 60ac9c2 — Criar interface de workflows e acompanhar execuções no navegador.
- b6d4507 — Documentar decisões e operação da interface com capturas reais.

[PR #8](https://github.com/gabxw/flowforge/pull/8) integrado em 13/10/2026, preservando os três commits. O código validado no PR e na main é b6d4507dd22ea63d78faaaff0920dcde2618293f. O commit posterior de encerramento altera somente documentação/instruções, sem modificar os binários validados.

A revisão final conferiu os links locais e corrigiu descrições antigas de startup do shell, comandos com revisão, abertura de rascunho e recuperação HTTP opt-in. Todos os critérios de saída foram atendidos no ambiente local/privado. Os riscos acima continuam explícitos; Fase 12 não iniciada.
