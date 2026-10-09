# Revisão técnica da Fase 3

Data: 2026-10-09. Status: implementada e publicada na branch fase-3-persistencia; validação com PostgreSQL/CI e integração à main pendentes.

## Entrega

- Reidratação validada preservando histórico, IDs, revisões e coleções imutáveis.
- Portas específicas em Application e adaptadores EF Core/Npgsql em Infrastructure.
- Cinco tabelas, migration inicial, FKs compostas, checks, índice parcial de draft e índices para consultas por proprietário.
- Codec fechado com schemaVersion, seis tipos, literais JSON, precisão e rejeição de propriedades duplicadas.
- Save com revisão esperada, transação, imutabilidade das publicações e rollback integral; Get coerente sob Repeatable Read.
- Testcontainers PostgreSQL 17; CI mantém a solução inteira, locks, auditoria de dependências, frontend e smoke Docker.

## Evidências locais

Build Release: aprovado, zero avisos/erros. Testes sem PostgreSQL: 754 aprovados, incluindo os 685 anteriores, 44 de restauração e 25 de codec/modelo. O grupo de 27 testes com banco ainda precisa de execução real; o total esperado é 781, não é resultado aprovado.

Restore travado dos oito projetos: aprovado a partir do cache, com NuGetAudit=false somente na execução local devido à restrição de rede. A tentativa com auditoria falhou em NU1900 ao consultar api.nuget.org. Nenhuma supressão foi adicionada aos manifests ou ao CI; a auditoria online continua obrigatória na validação remota.

Tool restore, has-pending-model-changes e geração do script idempotente: aprovados sem abrir conexão de operação. Docker local não está acessível por esta sessão; isso não equivale a sucesso dos testes de integração.

## Revisão de engenharia

Revisão técnica realizada no incremento integrado, sem alegar uma revisão independente. Foi verificada a ordem de insert/promoção/delete, a proteção do CAS e rollback, o escopo de ownership, a manutenção do histórico e a leitura do grafo em um snapshot coerente.

O review identificou e corrigiu: nomes de índices próximos do limite de 63 caracteres; comparação textual incompatível com normalização JSONB; perda de precisão de timestamps nas comparações; duplicação de propriedades em literais JSON. Testes adicionais verificam adulteração de publicação, workflow arquivado terminal, cancelamento e parâmetros inválidos.

O teste de concorrência usa duas leituras e saves concorrentes por contextos independentes, conferindo o estado vencedor. O teste de falha após CAS cria um vínculo inválido e verifica metadados, revisão, ponteiro e grafo anteriores, além de uma gravação válida posterior. O CI precisa comprovar essas hipóteses no PostgreSQL real.

## Limites e próximos passos

API/Worker não usam os stores nesta fase. User é somente registro técnico, sem auth. Credential é referência; não há tabela/valor secreto. DAG e imutabilidade histórica não são garantidos contra SQL administrativo direto. Substituir o draft inteiro custa mais escrita, aceitável para os limites do MVP.

Somente após CI completo aprovado, publicar a integração da Fase 3 à main e registrar os links e contagens reais. Fase 4 não iniciada. Operação: [persistence.md](persistence.md); decisão: [ADR 0003](decisions/0003-postgresql-persistence.md).
