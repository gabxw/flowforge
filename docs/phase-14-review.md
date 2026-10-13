# Revisão da Fase 14 — Observabilidade

API/Worker usam OpenTelemetry, contexto durável no PostgreSQL, header W3C no RabbitMQ e spans de nodes/HTTP. Exportação OTLP é opcional; painel Aspire standalone permanece em profile local. Domain não recebeu dependências.

## Verificação

- Build Release final: zero avisos/erros. **1.282 casos xUnit aprovados por projeto**: 794 Domain + 111 Application + 146 API + 231 Integration. Integration foi repetida inteira após a última correção; cinco casos de bootstrap/login/anonimato da API passaram novamente no SDK final.
- Sete casos de telemetria verificam contexto persistido, RabbitMQ real, HTTP com TLS real, falha/retry, dimensões limitadas, sanitização e ausência de headers de contexto enviados a terceiros.
- Nona migration adiciona somente trace_parent nullable. Validação de modelo e aplicação idempotente no Compose.
- Docker isolado: imagens, nginx, migrations duas vezes, smoke autenticado, duas jornadas reais pelo editor e shutdown normal do Worker. Ambiente anterior do usuário preservado.
- Painel recebeu API/Worker reais: screenshot [Traces](screenshots/phase-14-dashboard.png) inspecionado, com 102 traces e uma aceitação ligada aos seis spans do Worker. A checagem exige os dois serviços; abrir um painel vazio não conta como evidência.
- Frontend não foi alterado nesta fase. Os 43 testes e QA de autenticação continuam registrados na Fase 13; o CI valida novamente o conjunto.
- [CI da Fase 13](https://github.com/gabxw/flowforge/actions/runs/38446945971): três jobs aprovados no commit 6f24d2d.

## Revisão técnica

A exportação inicial de logs não tinha service.name; corrigimos para usar os mesmos nomes flowforge-api/flowforge-worker em todos os sinais. A asserção de headers também detectou reinjeção pelo SDK: configuração agora é TraceContext somente, sem baggage, testada pelo runtime real.

Contrato JSON AMQP v1 preservado. Metadado malformado não muda decisões de negócio. Outbox recupera o pai persistido após Delay/retry; não mantém span aberto durante a espera. HTTP mantém spans sem propagar contexto ao destino. Testes detectaram que propagador null desativava a instrumentação; foi substituído pelo propagador oficial sem saída.

Nenhum ID único é dimensão de métrica própria. Tags de trace usam allowlist e descrições de falha são genéricas. Exportação de logs é restrita aos eventos próprios. Métricas são observacionais após commit, sem garantia transacional de contagem; PostgreSQL continua autoridade do histórico. Sampler invalida amostra quando perde o banco, evitando zero falso.

Custo adicional: um read curto por claim, três consultas de backlog a cada cinco segundos no Worker e SDK nos dois hosts. Nenhum Redis/collector/AppHost foi introduzido. Painel tem retenção em memória e porta anônima em loopback; operação pública exige collector autenticado e política de retenção.

## Entrevista

“Persisti o contexto junto da execução porque a automação continua depois que a request termina e pode reiniciar. A outbox leva esse contexto ao broker sem alterar a mensagem de negócio. Separar duração ativa, espera e atraso de despacho explica onde a execução perdeu tempo. Os IDs ficam em traces, enquanto as métricas têm dimensões limitadas.”

Critérios locais concluídos em 13/10/2026. Publicação e CI da Fase 14 serão registrados na revisão da Fase 15 autorizada.
