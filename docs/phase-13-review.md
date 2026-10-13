# Revisão técnica — Fase 13

Status: implementação, build, testes e revisão aprovados em 13/10/2026; publicada na main em [6f24d2d](https://github.com/gabxw/flowforge/commit/6f24d2dc434415f77a2636bbbac2ca8cc2c59931). [CI aprovado](https://github.com/gabxw/flowforge/actions/runs/38446945971) nos três jobs. Etapas 14 a 16 autorizadas pelo usuário.

## Implementação

JWT Bearer oficial, password hashing IdentityV3, UserRole Owner/Admin, ownership por sujeito autenticado, refresh em cookie HttpOnly/Strict, hash no PostgreSQL, rotação serializada com FOR UPDATE, detecção de reuse e revogação imediata. Schema adiciona refresh_sessions/refresh_tokens e campos de conta aos users existentes. Dados de workflow não foram reescritos.

SPA usa JWT só em memória, coalesce de refresh e Web Locks entre abas. Login/logout integram o tema existente. Rascunho da mesma conta permanece montado durante reautenticação. API mantém liveness/OpenAPI local independentes do banco/keyring; webhooks continuam públicos com secret próprio.

## Evidência

- API: 146 testes aprovados, incluindo JWT real/expiração/assinatura/audience/issuer, CSRF, logout, revogação, concorrência e isolamento de credenciais/workflows.
- Domain: 794 testes; Application: 111, aprovados na primeira execução.
- Integration: **224 testes aprovados**, incluindo reexecução idempotente da oitava migration. O caso de resposta sensível agora tem orçamento próprio de cinco segundos; o caso de timeout continua com 200ms. Isso mantém duas asserções distintas sob carga.
- Frontend: **43 testes aprovados**, com lint/build e jornadas de login, bearer, logout, ausência de token no storage, preservação de JSON durante reautenticação e reflow em 390/720px.
- Docker final aprovado: imagens, migrations duas vezes, smoke autenticado, duas jornadas reais pelo login e encerramento normal do Worker. Só o projeto literal de validação foi removido.
- QA: prints reais [desktop](screenshots/phase-13-login.png) e locais 390/720px inspecionados; teclado, foco, ausência de overflow e console conferidos. Contorno dos campos 3,67:1, foco 9,58:1 e texto secundário mínimo 4,99:1. Primeiro print revelou campos sem contorno e foi corrigido. O bootstrap agora evita refresh quando não há cookie.
- MCP context7/chrome-devtools indisponíveis: APIs oficiais/instaladas e Chromium/Playwright usados. Nenhuma execução desses MCPs foi declarada.

## Revisão

Antiforgery foi inicialmente resolvido no startup e tornava health dependente do keyring. Mantemos a validação oficial explícita em filtro dos POST JSON, sem resolução antecipada. Os testes de health/unconfigured protection passaram depois da correção.

O teste de audience estava construindo token com a audience anterior junto da inválida; corrigimos a montagem para exercitar uma audience realmente inválida. O servidor mantém validação padrão de issuer/audience/signature/lifetime/algoritmo, além da família ativa no banco.

Role e proprietário não são entradas aceitas do cliente. Admin não contorna ownership. Rotação concorrente/reuse falha fechada, mesmo que derrube o vencedor; não se promete recuperação automática de resposta perdida. Estado/liveness e mensagens de erro não carregam segredo. O custo de um read por request é explícito; Redis não foi adicionado.

Referências: [operação](authentication.md), [ADR 0014](decisions/0014-authentication-and-session-families.md).
