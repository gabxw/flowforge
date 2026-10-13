# Revisão da Fase 15 — Testes e CI/CD

CI mantém jobs independentes obrigatórios, restore locked, build/test, migrations, auditoria NuGet/npm, lint e jornadas reais do Compose. Docker preserva a camada de restore ao mudar fontes C#. Main protegida exige PR/checks do GitHub Actions, inclusive para o administrador.

## Evidência

- [Fase 14 na main](https://github.com/gabxw/flowforge/actions/runs/38454995333): os três jobs aprovados em 5d61583, cobrindo 1.282 testes backend, 43 frontend e duas jornadas reais. Artifacts backend, frontend e navegador presentes/não expirados.
- [PR #12](https://github.com/gabxw/flowforge/pull/12), código 7af553f: [CI](https://github.com/gabxw/flowforge/actions/runs/38455946181) aprovado nos três jobs. Backend em 2m16s e frontend em 42s; containers validam o ambiente real e falhas/retomadas já especificadas.
- Clone GitHub raso da branch publicada: restore locked e build Release sem avisos/erros, 794 Domain + 111 Application, npm ci/lint/typecheck/build aprovados. A suíte completa roda no checkout limpo do CI. Diretório de validação separado e nenhuma alteração do usuário sobrescrita.
- Builds locais API/Worker aprovados. Depois de aquecer o clone, comentário temporário diferente em C# recompilou as duas imagens em **39,6 segundos**, com os quatro passos de restore CACHED. Os bytes originais foram restaurados e git status ficou limpo. Não altera produto nem cria commit artificial.
- Auditoria local NuGet: zero vulnerabilidades High/Critical. CI guarda o JSON e bloqueia essas severidades; npm também bloqueia High/Critical.
- [PR negativo #11](https://github.com/gabxw/flowforge/pull/11): erro real de compilação em branch isolada, [CI reprovado](https://github.com/gabxw/flowforge/actions/runs/38455253940), backend FAILURE, frontend SUCCESS e containers SKIPPED. GitHub retornou mergeStateStatus=BLOCKED com enforce_admins=true. PR fechado e branch remota removida, sem merge.

## Revisão

Proteção foi consultada na API do GitHub, não presumida a partir do YAML. Checks estão vinculados ao app 15368, strict=true, zero revisores obrigatórios, force push/deletion bloqueados e histórico linear. Um projeto individual não exige aprovação artificial de outro usuário.

Todo PR executa o workflow, inclusive documentação; não há workflow obrigatório silenciosamente pulado por filtro de paths. Concorrência cancela execução antiga da mesma branch. Jobs paralelos reduzem latência, com custo de containers poderem executar mesmo se outro job falhar. Testes não são repetidos automaticamente para mascarar flakiness.

Docker copia csproj/lockfiles antes das fontes e mantém usuário não root. Build do clone não depende do cache de artefatos binários do projeto original. Secrets de teste são aleatórios/em memória, e trace de navegador com serviços reais fica desligado. TRX/SQL/relatórios têm retenção de sete dias.

Nenhum destino de deploy foi escolhido. A fase entrega validação e promoção controlada de código; não declara deploy público ou operação de produção. [Política de segurança](../SECURITY.md) mantém os limites explícitos.

Critérios verificados; o commit documental final também precisa dos três checks antes de integrar o PR #12. Resultado desse gate e SHA integrado são registrados no fechamento de portfólio da Fase 16.
