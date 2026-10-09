# ADR 0002 — Definição de workflows e publicação imutável

Status: adotada e implementada na Fase 2; validação remota pendente. Data: 2026-10-09.

## Problema

O produto precisa rejeitar definições que não possam executar um caminho sequencial e preservar exatamente o grafo publicado. Impedir alterações somente na API deixaria listas, configurações JSON e outros chamadores capazes de modificar uma publicação por referências compartilhadas.

## Alternativas

- Entidades e coleções públicas mutáveis: convenientes para serialização/ORM, mas não protegem as invariantes fora do endpoint.
- Outro modelo de snapshot para publicação: separa edição de execução, porém duplica representações e conversões antes de existir uma engine.
- Valores imutáveis e agregado responsável pelo ciclo de vida: concentram as regras, mantêm uma representação e permitem testes sem banco. Esta é a escolha.

## Decisão

Workflow controla edição de metadados, um único rascunho, substituição do grafo, publicação e arquivamento. WorkflowVersion expõe somente leitura. Publicar valida primeiro e altera o mesmo rascunho para Published; falhas preservam status, revisões, datas e ponteiro ativo. Uma edição posterior cria outro WorkflowVersionId e número, reaproveita NodeIds e copia o grafo da publicação ativa. Conexões recebem IDs novos. Publicações anteriores continuam disponíveis e intactas.

O grafo tem exatamente um Webhook Trigger e todos os nodes são alcançáveis. A busca de alcance e a ordenação topológica de Kahn verificam a definição sem recursão. Nodes comuns podem terminar o caminho ou possuir uma conexão next; Condition exige uma true e uma false. Convergências são permitidas, inclusive as duas portas apontando para o mesmo destino, pois somente um ramo será executado. Não há fan-out paralelo ou join de sincronização.

Configurações são variantes tipadas e fechadas no assembly. O tipo declarado precisa corresponder à configuração; UUIDs, posições e valores são verificados na construção. Coleções são copiadas e JSON é clonado. Credenciais são referências por ID/proprietário, sem valor secreto: o domínio rejeita um proprietário divergente, mas existência, revogação e confirmação do dono no armazenamento são responsabilidades futuras da aplicação.

Condition usa uma expressão declarativa com JSON Pointer e operadores limitados. Transform constrói um novo objeto por campos vindos de paths ou literais. Não há linguagem de scripts. A Fase 2 valida essas definições; avaliar payload, selecionar ramo ou produzir output pertence aos executores da Fase 9. O [contrato detalhado](../superpowers/specs/2026-10-09-phase-2-domain-design.md) especifica ausências, tipos, limites e erros.

Os limites iniciais consolidados são 50 nodes, 100 conexões, 50 campos Transform e pointers de até 1.024 unidades UTF-16/32 segmentos. Delay aceita até 24 horas. São limites de definição, não implementação de scheduling, quotas de usuários ou proteção completa de HTTP.

Estados operacionais recebem enums e políticas puras de transição, sem entidades de execução ou persistência antecipadas. Cancelled passa a fazer parte de NodeExecutionStatus. Estados terminais e valores desconhecidos não retornam a Running; repetir o mesmo estado não é uma transição. A idempotência de comandos/redelivery será tratada pelos casos de uso operacionais.

## Trade-offs e limites

- Cópias defensivas custam pequenas alocações, mas tornam a imutabilidade verificável. O tamanho limitado do grafo evita justificar otimizações prematuras.
- Um agregado em memória não resolve concorrência entre requisições/processos. A Fase 3 deve mapear revisões e constraints; a publicação persistida exigirá transação.
- JSON Pointer evita criar uma linguagem própria. A existência do caminho depende do input; definições válidas ainda podem encontrar dados incompatíveis durante a futura execução.
- Variantes tipadas tornam configurações inválidas difíceis de construir, mas a futura API precisa traduzir e validar o discriminador recebido. Não há contrato de serialização HTTP implementado nesta fase.
- HTTPS sintático na configuração não prova segurança de rede. Allowlist, resolução/conexão controlada, redirects, limites e política de credenciais permanecem na Fase 8.
- Usar decimal para os comparandos de Condition evita coerção de strings e fixa o domínio numérico; inputs fora desse domínio devem gerar erro determinístico no executor futuro.

## Como explicar em entrevista

“Coloquei as invariantes na definição e no agregado, porque bloquear um botão de edição não torna uma versão imutável. Primeiro valido o grafo inteiro; só depois mudo o status e o ponteiro ativo. Um novo rascunho recebe outra identidade, então uma edição futura não altera uma execução que venha a fixar a publicação anterior.”

“Usei alcance e ordenação topológica para testar um DAG pequeno. Condition tem duas saídas possíveis, mas a execução futura escolhe uma só; convergir ramos não implica sincronizar execução paralela. Limitei a linguagem de configuração em vez de colocar eval dentro do Worker.”

## Evidências

Testes de definição, limites, configurações, DAGs aceitos/rejeitados, publicação atômica em memória, cópias defensivas, versões e matriz de transições passaram na solução integrada: 682 testes Domain e três testes API, com build Release sem avisos/erros. Revisões independentes aprovadas; CI ainda pendente. Resultados e limites estão na [revisão da Fase 2](../phase-2-review.md).
