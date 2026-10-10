# HTTP Request e Credentials — Fase 8

Ambiente privado/local. JWT/RBAC entram na Fase 13. O proprietário técnico vem da configuração do servidor; requests não escolhem OwnerUserId.

## Destinos e orçamento

No Compose, configure FLOWFORGE_HTTP_ALLOWED_ORIGINS com origens HTTPS exatas separadas por vírgula. Exemplo fictício: https://api.parceiro.example,https://api.outro.example:8443. Vazio bloqueia tudo. A allowlist é do operador; criar uma credencial não autoriza um destino.

No host: FlowForge__Http__AllowedOrigins, FlowForge__Http__TimeoutSeconds (1 a 8, padrão 8) e FlowForge__Http__MaxResponseBytes (1 a 32768, padrão 32768). Esses dois últimos limites podem ser fornecidos como environment ao Worker; Compose publica apenas a allowlist por padrão. Mudar a configuração exige recriar o Worker.

URL estática até 2.048 caracteres, HTTPS sem userinfo/fragmento. Allowlist inclui porta e hostname canônico; não aceita subdomínios por aproximação, IP literal, wildcard ou ponto final. Todos os endereços DNS devem ser públicos e permitidos pela política. A conexão fixa esses IPs; redirects/proxy/cookies ficam desativados. IPv6, blocos especiais e metadados são cobertos nos testes. Detalhes e trade-offs na [ADR 0008](decisions/0008-secure-http-and-credentials.md).

## Credenciais

| Método/rota | Entrada | Resultado |
| --- | --- | --- |
| POST /api/credentials | name, type, origin, headerName opcional, secret | 201 metadados, Location, Cache-Control: no-store |
| GET /api/credentials?offset=0&limit=20 | paginação, limit 1 a 100 | lista de metadados do dono |
| GET /api/credentials/{id} | identidade | metadados, inclusive revogadas |
| POST /api/credentials/{id}/rotate-secret | expectedRevision, secret | 200 metadados; 409 revisão antiga |
| POST /api/credentials/{id}/revoke | expectedRevision | 200 metadados; repetição na revisão atual é idempotente |

Type é bearerToken ou apiKey. Bearer não aceita headerName; apiKey exige X-Api-Key ou X-Auth-Token, comparados sem distinguir caixa. Secret exige 16 a 4.096 caracteres ASCII visíveis, sem espaço/CR/LF. Nenhuma resposta repete o valor recebido ou retorna ProtectedValue. Rotacionar preserva origem/tipo/header; para mudar esses dados, crie outro recurso. Revogada não pode ser reativada/rotacionada. Não há exclusão física.

Entrada JSON UTF-8, sem compressão, até 32 KiB, leitura até 10 s. Campos desconhecidos/duplicados e enums numéricos são recusados. Errors: 400 configuração/corpo, 404 outro dono/missing/revogada/origem incompatível, 409 revisão, 408 leitura, 413 corpo, 415 conteúdo e 503 proteção/configuração. A API retorna mensagens sanitizadas.

## Configurar um node

A configuração continua mínima:

~~~json
{
  "nodeId": "00000000-0000-0000-0000-000000000002",
  "credentialId": "00000000-0000-0000-0000-000000000003",
  "configuration": {
    "type": "httpRequest",
    "url": "https://api.parceiro.example/orders",
    "method": "post"
  }
}
~~~

Os UUIDs são exemplos; crie a credencial e use o ID retornado. CredentialId é opcional. Salvar draft/publicar exige credencial ativa, do dono e da mesma origem do HTTP. A FK protege a relação no banco; o Worker verifica novamente antes de enviar. Segredos devem ficar em Credential, nunca na URL ou definição.

POST/PUT/PATCH enviam o input corrente como application/json; outros métodos não enviam body. Headers são produzidos pelo executor: Host do URI, Accept JSON, Content-Type quando aplicável e o header da credencial. Não há headers arbitrários, body estático separado, scripts ou interpolação. Transform da Fase 9 preparará o input quando necessário.

Sucesso 2xx com JSON UTF-8 produz {statusCode, body}; resposta vazia produz body=null. Headers não entram no contexto. 3xx/4xx/5xx falham sem seguir Location ou capturar corpo de erro. Body até 32 KiB, headers até 8 KiB, profundidade JSON até 32, contexto resultante até 64 KiB. Encoding/compressão, JSON inválido/duplicado ou eco do segredo geram falha. Não aceitamos download binário nesta versão.

| ErrorCode | Significado |
| --- | --- |
| httpDestinationDenied | Origem/DNS/rede fora da política, antes do envio |
| credentialUnavailable | Referência sem resolução autorizada, antes do envio |
| httpRemoteFailure | Status externo fora de 2xx; corpo descartado |
| httpResponseLimitExceeded | Body excedido |
| httpResponseInvalid | Media type/encoding/UTF-8/JSON incompatível |
| httpResponseSensitive | Valor da credencial encontrado no JSON de resposta |
| httpTransportFailed | Falha DNS/TCP/TLS/leitura, sem mensagem sensível |
| nodeTimeout | Prazo esgotado; efeito remoto pode ter ocorrido |
| interruptedNode | HTTP estava Running quando a execução foi recuperada; sem replay |
| contextLimitExceeded | JSON serializado final excede o contexto |

Snapshots de /nodes e /logs mostram metadados, nunca headers/bodies. CredentialRevisionUsed é conhecida somente quando o resultado correspondente foi salvo. Captura de conteúdo privado está limitada ao checkpoint cifrado necessário ao próximo node. Não suponha que detectar o texto do segredo elimina todo dado sensível de uma resposta arbitrária.

## Rotação, interrupção e migração

A resolução fixa o valor/revisão para a chamada corrente. Rotação posterior vale para a próxima execução; revogação não recolhe uma requisição já enviada. Não há retry de HTTP nesta fase, inclusive reconexão implícita do handler após EOF TLS; somente fallback TCP entre IPs aprovados antes do envio. Se cair após enviar e antes de gravar, a recuperação marca InterruptedNode e preserva AttemptCount; a lease protege somente nosso banco.

Antes de atualizar uma instalação da Fase 7, faça backup do banco e execution_keyring e pare API/Worker. A migration 20261010183259_CredenciaisEHttpProtegidos cria credentials, FK CredentialId/OwnerUserId, revisão usada por node e checks dos códigos HTTP. Há 13 tabelas de aplicação e 5 migrations. Aplique o SQL explícito antes de iniciar os novos hosts.

Até a Fase 7, CredentialId era uma referência sem store/FK. Se uma instalação possui IDs fictícios antigos, a nova FK recusará a migration, mantendo o banco anterior pelo rollback da transação. Verifique referências legadas por IDs/versionamento antes do upgrade; não limpe publicações ou invente segredos automaticamente. A recuperação desse conjunto exige um plano específico que preserve seu histórico. O ambiente descartável validado não possui tais referências.

Down remove credenciais/revisões e restringe códigos a 1..7; históricos HTTP podem fazê-lo falhar. Não é rollback operacional seguro. Produção continua recusada para proteger/desproteger com chaves sem proteção em repouso; Development exige path absoluto e volume persistente compartilhado. Preserve banco e keyring juntos.

## Verificar

~~~powershell
# Com Docker ativo e migrations aplicadas; usa somente valores fictícios.
.\scripts\smoke-http.ps1
.\scripts\smoke-http.ps1 -BaseUri http://127.0.0.1:5173
~~~

O smoke cria/rotaciona uma credencial, valida CAS, publica Trigger → HTTP → Log e confirma que o destino reservado fora da allowlist falha e pula Log. Depois revoga/arquiva. Modos -PendingOnly e -ExecutionId/-CredentialId verificam retomada após broker/processos serem recriados, sem transportar valores secretos. Ele mantém volumes e histórico.

Os testes automatizados de sucesso/falha/timeout/body/headers usam HTTPS real controlado, com CA e mapeamento de socket exclusivos do assembly de testes; não dependem de serviços de terceiros nem habilitam exceções de SSRF no Worker. Também exercitam o connector real com IP explícito e o fluxo pelo RabbitMQ. [Revisão da Fase 8](phase-8-review.md).
