# ADR 0014 — Identidade e famílias de sessão

Status: aceita e implementada na Fase 13; build, testes e revisão local aprovados.

## Problema

O dono técnico permitia evoluir o backend privado, mas todas as requests do mesmo host tinham a mesma identidade. Precisávamos de usuários reais, autorização por objeto e revogação, mantendo o Domain independente de ASP.NET/EF.

## Alternativas e escolha

| Decisão | Alternativas | Escolha e custo |
| --- | --- | --- |
| Identidade | IdP OIDC externo, ASP.NET Identity completo ou contas do sistema fechado | Contas locais com PasswordHasher oficial e JWT Bearer oficial. Adequado ao ambiente local/privado e ao aprendizado; não é implementação de OAuth/OIDC nem IdP público |
| Acesso | Cookie de sessão ou JWT | JWT HS512 de cinco minutos em memória da SPA. API emite e valida no mesmo limite de confiança; externalizar autenticação deve usar OIDC/PKCE e chaves assimétricas |
| Revogação | Esperar JWT expirar, cache/Redis ou consulta de sessão | Consulta PostgreSQL por request confirma família e usuário ativos; logout/reuse revogam imediatamente, ao custo de um read e dependência do banco |
| Refresh | Token fixo ou rotação | 32 bytes aleatórios, hash SHA-256 no banco, cookie HttpOnly/Strict com vida absoluta de sete dias |
| Concorrência | Lock distribuído ou lock de linha | FOR UPDATE na família serializa rotação. Segunda utilização do mesmo token revoga a família, incluindo o JWT recém-emitido |
| Inicialização | Senha padrão ou configuração inicial | Cadastro habilitado somente em Development e primeira conta Admin sob lock transacional. Adota o dono legado configurado sem reescrever workflows; inicializar em loopback antes de compartilhar |

## Contrato e segurança

UserRole é Owner/Admin. O sujeito vem de sub validado, nunca de OwnerUserId no body. Admin gerencia contas e revoga sessões, mas operações de workflow/credencial continuam limitadas ao próprio dono. Não existe bypass administrativo de ownership.

A chave HS512 tem 64 bytes e fica protegida por Data Protection em jwt-signing.protected no keyring. Em Development pode ser criada atomicamente; Production exige configuração explícita e mantém o requisito anterior de wrapping do keyring. Não há senha padrão nem token persistido no storage do browser.

Cookie tem Path /api/auth, HttpOnly, SameSite Strict e Secure em HTTPS/Production. POST de login/cadastro/refresh/logout valida antiforgery oficial em filtro; JSON é lido com limite de 8 KiB e timeout. A validação explícita mantém liveness independente da proteção de dados; não existem endpoints de formulário que dependam do middleware antiforgery. CORS permanece fechado. Cookies de autenticação não substituem Bearer nos recursos privados.

PBKDF2 usa o PasswordHasher IdentityV3 com 210 mil iterações; erro de login é genérico, conta desconhecida faz hash dummy e cinco falhas bloqueiam a conta por quinze minutos. Há limite de login e requests por conta, limite de mil contas e dez famílias ativas por usuário. As partições de rate limit são locais ao host; múltiplas APIs exigem rever a estratégia.

Refresh não estende a vida absoluta. Não há janela que aceite replay: respostas perdidas podem exigir login. A SPA compartilha a renovação em voo e usa Web Locks quando disponível para serializar abas. Sem Web Locks, uma corrida pode provocar reautenticação; isso não relaxa a política do servidor.

A tela conserva o rascunho durante reautenticação da mesma conta. Logout avisa sobre edição não salva. Trocar conta cria outra árvore de páginas, sem reutilizar dados do dono anterior.

## Limites e referência

MFA, confirmação de e-mail, recuperação de senha, federação e implantação pública não fazem parte deste incremento. O administrador pode criar Owner pela API quando cadastro público está desativado. A aplicação de portfólio continua local/privada; autenticação não equivale a prontidão para exposição pública.

[JWT Bearer oficial](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0) fundamenta validação de assinatura, issuer, audience e validade e recomenda OIDC para aplicações públicas. [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.14) fundamenta rotação/detecção de replay. [PasswordHasher](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasher-1?view=aspnetcore-10.0) fornece o formato e a verificação de senha.
