# Revisão técnica da Fase 8

Status: implementação, testes locais e Docker aprovados; publicação/CI em andamento. Esta revisão foi feita pelo agente de implementação e não é uma auditoria independente.

## Entrega e limite

HTTP Request integrado ao Worker, allowlist HTTPS do operador, DNS validado com conexão fixada, TLS/Host/SNI preservados, redirects/proxy/cookies desativados, prazos e limites de resposta. Credential real com armazenamento autenticado, keyring compartilhado, ownership/origem, rotação CAS, revogação e API de metadados. Nenhuma dependência nova de runtime; referência ASP.NET adicionada somente ao projeto de testes para o servidor HTTPS controlado.

Não foram iniciados Condition, Transform, Delay/scheduler, retry de HTTP ou DLQ. A Fase 9 é o próximo incremento; o marco MVP de backend continua na Fase 10.

## Verificação local

- Restore locked e Release build: aprovados, zero warnings/erros.
- 1.112 testes: Domain 771, Application 36, API 112, Integration 193; zero falhas/skips.
- PostgreSQL/RabbitMQ reais: migração, FK composta, rotação concorrente, webhook → outbox → consumer → HTTPS → Log e duplicação sem repetir o HTTP.
- Rede: origens/portas exatas, IPv4/IPv6 privadas/especiais/metadados, DNS misto e rebinding, pinning no callback, connector real, hostname e trust TLS, redirect sem novo destino.
- Transporte: sucesso/body JSON, empty/HEAD, falha 4xx/5xx, timeout antes/durante body, limites fixos/streaming, encoding/compressão/UTF-8/JSON inválido.
- Retry implícito: RST e EOF TLS ordenado não repetem HTTP. Remover temporariamente a proteção fez o teste falhar com quatro conexões, em vez de uma; código restaurado e suíte completa aprovada.
- Segredos: cipher por purpose/dono/ID/revisão/origem/header, troca/tamper, outro dono/origem, headers protegidos, eco literal/escapado/nome de campo, ausência em histórico/logs/traces exercitados.
- Rotação em voo e revogação após publicar: chamada atual estável; próxima resolve revisão nova ou recusa revogada.
- Efeito remoto confirmado antes de perda do checkpoint: recuperação termina InterruptedNode, não repete chamada nem acrescenta tentativa fictícia.
- EF: sem model drift; SQL idempotente gerado. Scripts PowerShell: sintaxe aprovada.
- Frontend: lint e build aprovados; npm audit --omit=dev --audit-level=high sem vulnerabilidades.

- Docker Compose: imagens finais novas, nginx -t, banco vazio, migration aplicada duas vezes, smoke direto/proxy, keyring preservado, broker parado e recriação API/Worker com credencial persistida. Redis opcional saudável; Worker exited:0:false. Somente o projeto/volumes descartáveis da validação foram removidos.

## Revisão do código e trade-offs

Domain permanece sem framework/infra; Application usa portas específicas de armazenamento. Credential não contém valor secreto no domínio. O segredo não é serializado na mensagem RabbitMQ ou definição. Locks são curtos e nenhuma transação fica aberta durante HTTP. Worker usa somente destinos aprovados e uma credencial fixa por chamada.

O handler por node tem custo de conexão/handshake; evita pool compartilhado sem política de DNS válida. Destinos e formatos são restritos para manter verificável o escopo. Failures são códigos sanitizados; não guardamos corpo de erro remoto. Snapshots públicos preservam somente tamanho/tipo, e o contexto necessário é cifrado. Timeout/cancelamento/interrupção não provam que o destino não realizou um efeito.

CredentialRevisionUsed é gravada junto do resultado. Se o processo cai ou é cancelado antes desse checkpoint, pode ficar nula. Auditoria por tentativa antes do envio/retry entra na Fase 10. Detectar o valor literal da credencial em JSON não equivale a DLP de conteúdo derivado/recodificado.

## Riscos e condições operacionais

API privada com proprietário técnico; JWT/RBAC só na Fase 13. Keyring Development depende de permissões/backup; Production para proteção de valores continua fail-closed até wrapping das chaves. Restrição de egress de rede ainda não foi configurada. Retenção/limpeza e quotas permanecem no roadmap.

A FK nova recusa referências CredentialId fictícias de instalações antigas; upgrade precisa revisar esse caso sem alterar publicações silenciosamente. Down não é rollback operacional seguro com credenciais/histórico HTTP. [Contrato/operação](http-and-credentials.md) e [ADR 0008](decisions/0008-secure-http-and-credentials.md) detalham limites e alternativas.

## Publicação

Pendente: registrar commits em português, CI do PR e main e confirmar integração. Não declarar conclusão antes dessas evidências.
