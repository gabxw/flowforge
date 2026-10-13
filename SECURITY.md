# Segurança

FlowForge é um projeto de portfólio com ambiente local/privado. A configuração atual bloqueia proteção criptográfica em Production até existir wrapping externo do keyring. Não exponha o Compose de demonstração como serviço público.

JWT dura cinco minutos; refresh tem rotação, revogação por família e vida absoluta de sete dias. O cookie é HttpOnly/SameSite Strict e os comandos de sessão exigem CSRF. API valida dono de cada recurso, inclusive para Admin. Setup/cadastro local da primeira conta precisa acontecer antes de compartilhar acesso; nunca há password padrão.

HTTP nodes usam allowlist de origens HTTPS, DNS aprovado e conexão ao endereço validado, TLS obrigatório, sem proxy/cookies/redirects. Limites de payload/resposta/execução e regras declarativas impedem eval, shell e acesso arbitrário ao filesystem.

Passwords usam hash IdentityV3; refresh/webhook secret ficam como hash; credentials/input/output usam Data Protection. Secrets não devem aparecer em URL, Git, artifacts, logs ou atributos de telemetria. Banco e keyring precisam de backup coordenado. Keyring local é protegido pelo SO/volume, não por certificado ou KMS.

Para relatar falha, use um canal privado disponível no perfil do mantenedor no GitHub. Não publique tokens, payload real, chaves ou prova que exponha dados de terceiros em issue. Informe versão, comportamento e reprodução com dados fictícios. Não há programa de recompensa nem SLA de suporte contratado.

Correções de segurança devem incluir reprodução controlada e teste pertinente, sem desativar isolamento ou TLS para fazer o teste passar. Política de dependências e evidências ficam em [validação](docs/validation.md).
