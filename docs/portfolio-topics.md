# Temas técnicos para entrevista e conteúdo

Use implementação, teste e limite de cada decisão. Não apresente número de tecnologias ou quantidade de testes como garantia de qualidade.

## Outbox e aceite durável

Problema: um commit pode acontecer e o publish falhar. Alternativas: publicar antes do commit, transação distribuída ou outbox. Escolha: gravar execução/outbox na mesma transação e publicar depois com confirm. Trade-off: polling, atraso e possibilidade de duplicação. Evidência: broker parado durante aceite e retomada no CI. Explique por que responder 202 não significa execução concluída.

## Lease, fencing e duplicatas

Problema: Worker antigo pode continuar depois da expiração e entregar novamente. Alternativas: lock em memória, Redis ou autoridade no PostgreSQL. Escolha: claim renovável e token/geração verificados em escrita. Trade-off: heartbeats e consultas adicionais. Evidência: stale Worker recusado, queda antes/depois de commit/ACK e recovery. O mecanismo protege estado local, sem desfazer chamada externa.

## Retry e efeitos desconhecidos

Problema: timeout pode ocorrer depois de um POST produzir efeito. Alternativas: repetir tudo, nunca repetir ou classificar elegibilidade. Escolha: método/política, limites, Retry-After/backoff/jitter e chave estável quando o destino suporta. Trade-off: confiabilidade depende do contrato externo. Evidência: servidor TLS controlado e testes de efeito desconhecido. Use “at-least-once e deduplicação local”; não prometa exactly-once externo.

## Delay durável

Problema: espera longa retém thread/delivery e morre com o processo. Alternativas: Task.Delay ou estado agendado. Escolha: ResumeAt, checkpoint e continuação por outbox. Trade-off: estado/scheduler e atraso de polling. Evidência: delivery sem unacked durante Delay e Worker recriado antes de retomar.

## Grafo e versão imutável

Problema: edição posterior mudaria uma execução em curso ou seu histórico. Alternativas: ler o rascunho atual ou fixar publicação. Escolha: DAG validado, snapshot de versão/configuração e layout separado da execução. Trade-off: novas versões ocupam dados e ciclos/fan-out não fazem parte deste engine. Evidência: roundtrip do editor, portas true/false e execução histórica preservada.

## Sessões e isolamento

Problema: JWT roubado/refresh reutilizado ou ID de outro dono. Alternativas: JWT sem estado, cookie de sessão ou família rotativa. Escolha: JWT curto, refresh hash/rotação serializada, replay revoga família, verificação de sessão ativa e ownership em cada objeto. Trade-off: consulta no banco por request e necessidade de coordenação entre abas. Evidência: concorrência/replay/logout/CSRF e Admin sem bypass de recursos alheios. Identity/OIDC externo é uma evolução de implantação, não tecnologia adicionada sem necessidade.

## SSRF e credenciais

Problema: URL configurável pode atingir rede interna ou trocar IP após validação. Alternativas: validar string, allowlist simples ou validar conexão. Escolha: origem exata HTTPS, resolução pública aprovada, socket fixado ao IP com hostname/TLS preservados e sem redirects/proxy/cookies. Trade-off: restringe destinos e pooling. Evidência: IPv4/IPv6, DNS rebinding, redirects e segredo ausente de resposta/log. Keyring local tem limite operacional explícito.

## Observabilidade e CI

Problema: API aceita antes do Worker executar, e cada processo mostra só parte do evento. Escolha: contexto persistido, W3C no RabbitMQ, spans de nodes e métricas com dimensões limitadas. Trade-off: coleta/read/sampler e menos atributos automáticos para preservar privacidade. Evidência: trace API/Worker, HTTP real sem baggage/headers enviados e métricas ativa/espera/fila.

CI dá evidência reproduzível: clone limpo, serviços descartáveis, artifacts e main protegida. O PR #11 reprovado/BLOCKED prova enforcement. Deploy e SLA dependem de um ambiente escolhido; não são inferidos do YAML.

## Sugestões de conteúdo

- “Por que 202 não significa workflow executado”: transação, outbox, publish e retry de transporte.
- “O Worker perdeu a lease, mas ainda está rodando”: autoridade de escrita e fencing.
- “Timeout de HTTP e o pedido que talvez foi criado”: resultado externo desconhecido e contrato de idempotência.
- “Esperar sem prender uma delivery”: checkpoint e retomada após reinício.
- “Um trace atravessando API, broker e Worker”: contexto durável e privacidade.

Publique exemplos com dados fictícios e referências aos testes/ADRs. Conte um cenário, a decisão e seu limite; não afirme uso em produção ou desempenho que não foi medido.
