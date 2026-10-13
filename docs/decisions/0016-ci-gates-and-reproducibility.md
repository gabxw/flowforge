# ADR 0016 — CI obrigatório e builds reproduzíveis

Status: implementada na Fase 15; evidência final na revisão.

YAML de CI não garante que uma falha impeça merge. Protegemos main com três checks estáveis associados ao GitHub Actions, inclusive para o administrador, branch atualizada e PR obrigatório. Exigir outro aprovador tornaria o projeto individual dependente de uma revisão artificial; mantemos zero aprovações obrigatórias e revisão técnica registrada por fase.

Alternativas eram checks apenas informativos ou um job genérico que ocultasse falhas de etapas. Mantemos resultados separados de backend, frontend e containers e os executamos em paralelo. Isso reduz latência e pode gastar trabalho de containers mesmo quando outro job falha. Novo commit cancela a execução obsoleta.

Todo PR roda CI, mesmo quando muda só documentação: filtro de paths em um workflow obrigatório pode deixá-lo pendente para sempre. A validação negativa usa PR descartável com erro de compilação, fechado sem merge depois de confirmar bloqueio real. Não é funcionalidade nem atividade artificial integrada ao histórico.

NuGet/npm locked, restore separado das fontes Docker e artifacts curtos tornam regressões investigáveis e reduzem downloads repetidos. Não adotamos serviços de produção, deploy automático ou cache remoto sem destino concreto. -m:1 e orçamentos distintos de timeout/sucesso tratam disputa de CPU sem retry que esconda falhas.

Fontes: [checks e branch protegida](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches), [API de proteção](https://docs.github.com/en/rest/branches/branch-protection).
