# Acesso e sessões

Em Development local, a primeira conta criada pela tela configura Admin. Ela adota FlowForge:Authentication:InitialOwnerId (ou o TechnicalOwnerId legado) e preserva o histórico desse dono. Contas seguintes são Owner. O setup inicial só é permitido em Development; mantenha as portas em loopback e conclua antes de compartilhar o ambiente.

## Entrar e operar

1. Inicie o Compose e aplique migrations.
2. Abra a interface e crie a primeira conta com uma senha de 15 a 128 caracteres.
3. Entre e crie/publique workflows. Sair revoga a família atual; edição não salva exige decisão explícita.
4. Admin pode criar Owner via POST /api/admin/users e revogar famílias em POST /api/admin/users/{id}/sessions/revoke. GET /api/admin/users é restrito a Admin. Isso não permite ler workflows/credenciais de outro dono.

GET /api/auth/csrf fornece token antiforgery e cookie; envie X-CSRF-Token no POST de /api/auth/login, register, refresh e logout. Refresh/logout usam cookie de atualização. Recursos /api/workflows, /api/executions, /api/credentials e administração de webhook exigem Authorization Bearer. POST /hooks/{id} continua usando secret de webhook no header.

JWT dura cinco minutos; família tem prazo absoluto de sete dias e no máximo dez sessões ativas por usuário. Hash de refresh fica no banco; token raw permanece no cookie HttpOnly. Rotação concorrente do mesmo token tem um vencedor e depois revoga toda a família. Revogação é conferida no banco em cada request. Banco indisponível impede uso da sessão.

CSRF e autenticação são independentes: bearer não é cookie de refresh. A SPA guarda JWT somente em memória. Reautenticação conserva a edição local da mesma conta; reload completa do browser continua descartando mudanças não salvas conforme aviso.

## Smokes autenticados

~~~powershell
$env:FLOWFORGE_SMOKE_EMAIL = Read-Host 'E-mail da conta local de validação'
$env:FLOWFORGE_SMOKE_PASSWORD = [System.Net.NetworkCredential]::new('', (Read-Host 'Senha' -AsSecureString)).Password
./scripts/smoke-workflows.ps1
~~~

O helper mantém JWT somente na memória da execução PowerShell, tenta login e pode cadastrar conta em Development. Testes CI usam identidade fictícia, senha aleatória e quota explícita para a carga dos smokes. Não use contas/credenciais pessoais nesses testes.

Default: 30 operações de login/cadastro e 120 requests de sessão (status, CSRF, refresh, logout) por minuto e por endereço de cliente, e 120 requests por conta autenticada/minuto, sem fila. Um cliente abusivo esgota só a própria cota; login e renovação de sessão dos demais seguem disponíveis. O endereço vem do X-Forwarded-For apenas quando a conexão parte de uma rede listada em FlowForge:Network:TrustedProxies; no Compose, as redes privadas onde o nginx roda. Sem essa lista, o header é ignorado. A quota por conta pode ser configurada por FLOWFORGE_ACCOUNT_PERMIT_LIMIT (1..10000). Rate limit é local ao processo.

Cinco falhas de senha bloqueiam a conta existente por quinze minutos, sem bloquear outros usuários. É o trade-off conhecido do bloqueio por conta: ele impede força bruta contra uma senha, mas alguém que conheça o e-mail pode manter a conta bloqueada repetindo erros. A cota por endereço limita esse abuso a partir de um cliente; um atacante distribuído ainda consegue. A alternativa seria bloquear por conta e endereço com limite global mais alto, ao custo de uma tabela de tentativas.

Preserve users, refresh_sessions, refresh_tokens e execution_keyring no backup. jwt-signing.protected precisa do mesmo keyring. Nunca versionar valores de keys, cookies, passwords ou variáveis de secrets. HTTPS é obrigatório ao implantar; Production não habilita cadastro/setup público automaticamente.

[ADR 0014](decisions/0014-authentication-and-session-families.md) registra alternativas e limites.
