# ADR 0008 — HTTP com conexão aprovada e credenciais por origem

Status: aceita para o MVP privado da Fase 8.

## Problema

Um HTTP Request Node produz efeitos fora do banco e pode acessar redes às quais o usuário não deveria ter acesso. Validar só a URL e depois delegar DNS/conexão ao cliente HTTP permite que validação e conexão usem endereços distintos. Credenciais também precisam de isolamento entre donos, destinos e revisões, sem aparecer em configuração, histórico ou logs.

## Alternativas consideradas

| Alternativa | Avaliação |
| --- | --- |
| HttpClient convencional após checar hostname | Simples, mas permite segunda resolução, proxy e redirects fora da política. Insuficiente para SSRF. |
| Permitir qualquer destino público | Maior flexibilidade; aumenta a superfície de abuso e exige operação de egress mais forte. Fora deste MVP. |
| Gateway de egress dedicado | Oferece controle de rede adicional, mas acrescenta serviço, implantação e operação antes de existir necessidade medida. |
| AES-GCM e gestão manual de chaves | Viável, mas duplica versionamento, distribuição e rotação já disponíveis no keyring existente. |
| Cofre externo de segredos | Caminho possível para produção; neste incremento, Data Protection atende ao ambiente privado com limites explícitos. |

## Escolha

O operador configura até 32 origens HTTPS exatas, sem wildcard; vazio nega todo HTTP externo. Cada origem identifica hostname DNS canônico e porta. IP literal não é aceito. Toda resolução A/AAAA deve conter de 1 a 16 respostas seguras; um endereço privado/especial reprova o conjunto, mesmo que outro seja público. A política bloqueia os blocos especiais usados pelo MVP e o endpoint interno Azure 168.63.129.16. IPv6 fica restrito ao espaço global 2000::/3, excluindo blocos especiais, documentação e transição; IPv4-mapped, NAT64 e scope IDs ficam recusados.

SocketsHttpHandler.ConnectCallback conecta a IPEndPoint já aprovado, sem outra consulta DNS. URL, Host e validação TLS usam o hostname original. Fallback só ocorre entre endereços aprovados quando a conexão TCP falha. Proxy, cookies, redirects, decompressão automática e propagação automática de headers de tracing ficam desativados. HTTP/1.1 e um handler por node evitam compartilhamento de conexões entre destinos/execuções. Isso custa handshakes e conexões; pooling pode voltar com uma política de revalidação comprovada.

O handler do .NET pode repetir uma requisição sem body após EOF TLS antes da resposta, mesmo sem uma política de retry da aplicação. Limitamos ConnectCallback a uma entrada por chamada; uma segunda sessão é recusada. Fallback TCP dentro da primeira entrada continua elegível antes de qualquer envio HTTP. O teste de EOF ordenado recebeu quatro conexões sem essa proteção e uma com ela; RST tem um teste separado. Isso evita retry implícito inclusive em GET/DELETE, cuja ausência de efeitos não é garantida pelo serviço remoto.

O contrato inicial envia o contexto JSON em POST/PUT/PATCH; GET/HEAD/DELETE/OPTIONS enviam corpo vazio. Não há headers arbitrários ou templates de URL/body. Respostas de sucesso precisam ser JSON UTF-8 ou corpo vazio. O orçamento HTTP padrão de 8 s inclui DNS, credencial, conexão, headers e leitura; a engine mantém seu teto de 10 s. Headers de resposta têm teto de 8 KiB; body até 32 KiB e contexto final até 64 KiB. Compressão é recusada, evitando expansão não limitada.

Credential contém somente metadados no Domain. Bearer Token usa Authorization; API key admite X-Api-Key e X-Auth-Token. O valor é ASCII visível de 16 a 4.096 caracteres, sem espaço/control characters. Esse limite define o contrato, não prova entropia. Não há Basic Auth, query auth, credencial embutida em URL nem segredo nos JSONB dos nodes. Webhook Secret da entrada continua gerenciado pelo endpoint/hash da Fase 7.

Ciphertext autenticado usa purpose próprio, vinculado a dono, ID, revisão, tipo, origem e header. O keyring persistente é compartilhado API/Worker e separado do banco; não há pacote novo de runtime. Configuração inválida/Production continua recusada para operações de proteção: falta proteção das chaves em repouso para uma implantação pública. Rotação usa revisão esperada sob lock curto; origem/tipo/header são imutáveis. Revogação é terminal e preserva referências históricas. FK composta CredentialId/OwnerUserId protege ownership também no PostgreSQL.

A credencial é resolvida uma vez no início de cada chamada. Rotação/revogação posterior não recolhe um header já enviado. Uma execução seguinte resolve a revisão ativa mais recente; revogada/missing/outro dono/origem falha antes do envio. Quando o resultado HTTP é persistido, CredentialRevisionUsed registra a revisão conhecida. Uma queda ou cancelamento que perde esse resultado pode deixar a revisão nula; ela não é inferida a partir do estado atual da credencial. A Fase 10 acrescentará histórico de tentativas antes dos efeitos.

Nenhum header de resposta é capturado. A engine continua expondo apenas tipo/tamanho de input/output. Uma resposta JSON que contém o valor da credencial resolvida, inclusive em nomes de propriedades ou strings escapadas, é recusada antes de entrar no checkpoint. Isso não é DLP para transformações como base64/hashes: o corpo arbitrário continua dado privado, protegido no contexto, com política de retenção ainda pendente.

HTTP tem CanReplayAfterInterruption=false. Se o destino aplicou o efeito e o checkpoint se perdeu, a retomada termina em InterruptedNode. Recusar replay não incrementa AttemptCount. Timeout/falha/cancelamento não provam ausência de efeito remoto. Nenhum retry de node, DLQ ou promessa de exactly-once entra nesta fase.

## Consequências e limites

A allowlist reduz flexibilidade, e a política conservadora pode recusar endereços especiais roteáveis. Nem todo serviço externo aceita o contrato JSON/headers inicial. Bloqueios de aplicação não substituem regras de egress no ambiente nem protegem contra um destino permitido que faça proxy de forma insegura. As transações de credential/lease são curtas; nenhuma fica aberta enquanto esperamos HTTP.

Validar a credencial ao salvar/publicar melhora o feedback, mas não congela sua disponibilidade. A resolução no Worker é a verificação válida no momento da chamada. O keyring precisa de backup junto do banco e controle de acesso próprio; strings secretas existem transitoriamente na memória gerenciada para montar o header e não são cacheadas.

## Evidências e entrevista

Testes usam servidor Kestrel HTTPS independente, CA descartável e sockets reais. O mapeamento de IP para o fixture e confiança nessa CA existem somente em construtores internos acessíveis ao assembly de testes; o runtime não oferece exceção de rede privada/TLS. Há testes separados da política, do connector real e do hostname/certificado. PostgreSQL/RabbitMQ reais cobrem webhook → HTTP → Log, duplicação, rotação em voo, revogação e queda depois do efeito remoto.

Em entrevista: “Validar o DNS e conectar pelo hostname deixaria uma janela de rebinding. Eu fixo o endereço validado no socket, preservando a identidade TLS. Também separo a consistência local do efeito remoto: a lease impede gravar um checkpoint antigo, mas não desfaz um POST. Uma redelivery não autoriza repeti-lo.”

## Referências

[OWASP SSRF Prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html), [ConnectCallback no .NET](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0), [Host/SNI e validação de certificado](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-sni), [registro IPv4 da IANA](https://www.iana.org/assignments/iana-ipv4-special-registry), [registro IPv6 da IANA](https://www.iana.org/assignments/iana-ipv6-special-registry), [tratamento de EOF no HttpConnection do .NET 10](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnection.cs) e [retry interno do pool](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/ConnectionPool/HttpConnectionPool.cs).
