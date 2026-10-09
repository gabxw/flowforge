# ADR 0001 — Arquitetura inicial, escopo e garantias

Data: 2026-10-09
Status: decisão inicial de direção; funcionalidades serão implementadas nas fases do roadmap.
Escopo: novo projeto FlowForge no diretório atual, com implementação limitada inicialmente à Fase 1.

## Contexto

O projeto precisa ensinar e demonstrar engenharia de backend, mantendo uma automação executável a cada incremento. O domínio envolve definições em grafo, execuções independentes, processamento externo sujeito a falhas e conteúdo potencialmente sensível.

Uma solução que apenas encadeia chamadas HTTP dentro da request não atende confiabilidade e operação. Uma plataforma genérica com plugins, microsserviços e código livre cria riscos e trabalho que o escopo inicial não justifica.

## 1. Monólito com responsabilidades separadas e dois hosts

Problema: separar HTTP, regras e processamento pesado sem exigir a operação de vários serviços de domínio.

Alternativas: aplicação ASP.NET monolítica executando tudo na request; microsserviços por node/funcionalidade; um produto com API e Worker compartilhando domínio, aplicação e banco.

Escolha: Domain, Application, Infrastructure, Api e Worker. API e Worker são processos independentes do mesmo produto. Dependências apontam para Domain; infraestrutura implementa portas concretas necessárias aos casos de uso.

Trade-offs: Worker e API exigem deploys/configuração compatíveis e um contrato de mensagem versionado. O banco compartilhado limita autonomia de evolução, mas reduz custo de consistência e operação. Interfaces genéricas prematuras não entram.

Em entrevista: “Separei o ciclo HTTP da execução pesada porque têm necessidades diferentes de latência e concorrência. Mantive um único domínio e banco porque a equipe e o escopo não justificam transações distribuídas entre microsserviços.”

## 2. .NET 10 LTS e dependências introduzidas por necessidade

Problema: começar com runtime moderno e suportado, evitando uma migração imediata e uma stack inflada.

Alternativas: uma versão STS, uma LTS anterior ou a LTS atual; instalar desde o primeiro commit todos os pacotes futuros.

Escolha: .NET 10 LTS, com global.json restrito à linha 10.0 estável (mínimo 10.0.100, rollForward latestFeature). EF Core, RabbitMQ, Testcontainers, React Flow e OpenTelemetry entram junto dos comportamentos que justificam seu uso. [Suporte oficial .NET](https://dotnet.microsoft.com/en-us/platform/support/policy)

Trade-offs: bibliotecas precisam ser verificadas contra .NET 10; um ambiente sem SDK ou rede pode bloquear restore. A política evita mudar a versão major ou aceitar previews, mas permite feature bands diferentes; lockfiles e futura revisão de imagens completam a reprodução do ambiente.

Em entrevista: “Escolhi a LTS atual pelo horizonte de suporte. Não transformei a lista desejada de tecnologias em dependências antecipadas: cada pacote entra com um caso de uso e sua validação.”

## 3. Grafo limitado e versões publicadas imutáveis

Problema: representar automações e saber exatamente qual definição uma execução usou.

Alternativas: lista linear; grafo livre com ciclos/paralelismo; DAG limitado; executar sempre a versão mais recente mutável.

Escolha: DAG com um único trigger e caminho sequencial. Condition escolhe exclusivamente true ou false. Versões publicadas são imutáveis; WorkflowExecution fixa a versão aceita.

Trade-offs: o MVP não resolve loops, fan-out ou joins. Imutabilidade aumenta armazenamento e exige um fluxo de publicação. Ganham-se validação simples, histórico confiável e recuperação sem misturar mudanças de edição.

Em entrevista: “Um DAG com ramificação exclusiva cobre os primeiros casos sem introduzir coordenação de joins. Fixar a versão elimina o problema de um workflow mudar enquanto já está na fila.”

## 4. PostgreSQL relacional com JSONB seletivo

Problema: conciliar configuração variável de nodes com integridade de relações, consulta de histórico e concorrência.

Alternativas: armazenar o grafo inteiro em JSON; modelar cada campo de cada node em tabelas; usar modelo relacional com JSONB para configuração e snapshots.

Escolha: entidades e relações em tabelas; JSONB para conteúdo que varia pelo tipo. FKs compostas garantem que nodes, conexões e execuções pertencem à mesma versão; ownership participa das relações sensíveis. Revisão explícita detecta edição concorrente.

Trade-offs: duplicar chaves de owner/versão custa colunas e índices. JSONB ainda precisa de schema validado pelo domínio; não é licença para aceitar qualquer configuração. Novos índices dependem de consulta real.

Em entrevista: “Usei JSONB onde o formato varia, mas preservei relações importantes no banco. Uma conexão não pode apontar acidentalmente para um node de outra versão.”

## 5. RabbitMQ com outbox e contrato at-least-once

Problema: aceitar um webhook com baixa latência sem perder a execução entre gravar o banco e publicar a mensagem.

Alternativas: executar na request; publicar e depois gravar; gravar e publicar sem recuperação; transação distribuída; outbox transacional.

Escolha: criar execução e outbox na mesma transação PostgreSQL. Dispatcher publica com confirms; Worker confirma consumo após checkpoint durável. Mensagens têm identidade e contrato versionado. Inbox e estado suportam duplicação desde a Fase 5.

Trade-offs: há dispatcher, limpeza e monitoramento adicionais. A publicação pode ser repetida após uma queda. Não prometemos exactly-once para chamadas HTTP; o destino pode ter produzido efeito sem confirmação local. [Confiabilidade RabbitMQ](https://www.rabbitmq.com/docs/reliability)

Em entrevista: “A outbox elimina perda entre commit e publish, mas não elimina duplicação. Por isso o consumidor é idempotente no estado local, e efeitos externos usam chave estável quando o destino oferece esse contrato.”

## 6. Claim, lease e fencing no PostgreSQL; Redis opcional

Problema: impedir Workers concorrentes de gravar a mesma execução e permitir recuperação quando um processo morre.

Alternativas: lock de linha durante todo o workflow; lock Redis separado do estado; atualização atômica com lease na própria fonte de verdade.

Escolha: claim atômico, lease com vencimento e renovação, fencing token exigido nos checkpoints. Transações são curtas. Redis fica no profile opcional, sem uso até surgir uma necessidade concreta.

Trade-offs: renovar e recuperar leases cria lógica que deve ser testada; clock e latência precisam ser considerados. Fencing rejeita writes de um Worker antigo, mas não impede sozinho um efeito HTTP já em voo. Um lock Redis introduziria outro componente crítico e ainda exigiria coordenação com o banco.

Em entrevista: “O lock precisa proteger o estado da execução. Mantê-lo no banco permite validar ownership e checkpoint atomicamente. Redis não resolveria a janela de efeito remoto nem dispensaria fencing.”

## 7. Delay e retry como agendamento durável

Problema: esperar horas ou repetir uma tentativa sem perder o prazo ao reiniciar o processo.

Alternativas: Task.Delay longo; manter uma delivery aberta; usar TTL do broker como fonte única do prazo; salvar due time no PostgreSQL e publicar uma continuação.

Escolha: ResumeAt persistido, checkpoint de suspensão, scheduler e outbox. Retry possui elegibilidade, jitter, limite de tentativas e prazo total; NodeExecutionAttempt registra cada tentativa.

Trade-offs: é necessário scheduler e recuperação de continuações. Um mesmo prazo pode ser observado por dois processos; claim e idempotência precisam impedir trabalho duplicado. Retry de uma operação não idempotente pode duplicar efeitos, por isso não é padrão para POST/PATCH.

Em entrevista: “Espera é estado durável, não uma thread dormindo. Ao gravar o prazo e soltar o consumer, o sistema retoma após reinício e não ocupa capacidade sem executar trabalho.”

## 8. Segredo do webhook em header e idempotência escopada

Problema: autenticar webhooks sem espalhar o segredo por URLs e diferenciar uma repetição de um novo evento.

Alternativas: segredo na rota; segredo em header; assinatura HMAC; deduplicar somente pelo hash de payload.

Escolha: endpointId público na rota e segredo de alta entropia em X-FlowForge-Webhook-Secret, armazenado como hash. Idempotency-Key opcional por owner/endpoint, associada ao hash da requisição e ExecutionId.

Trade-offs: clientes precisam conseguir enviar o header. Headers também devem ser redigidos em logs/traces. Mesmo payload pode representar dois eventos legítimos; sem chave explícita não há deduplicação heurística. HMAC com proteção de replay pode ser uma evolução, mas fica fora do primeiro contrato.

Em entrevista: “A URL é registrada em muitos lugares, então separei a identidade do endpoint do segredo. A chave de idempotência expressa a identidade da operação, e o hash detecta reutilização da mesma chave com conteúdo diferente.”

## 9. HTTP com allowlist e validação da conexão

Problema: um node HTTP configurável pode alcançar serviços internos, metadados de nuvem e redes privadas.

Alternativas: aceitar qualquer URL; validar só o texto ou hostname; blocklist simples; allowlist mais resolução DNS e conexão controladas.

Escolha: HTTPS/destinos/portas autorizados pelo operador; validar todos os endereços resolvidos e conectar a um endereço aprovado mantendo TLS/SNI. Redirect automático desabilitado. Limites de corpo, timeout e egress de infraestrutura complementam o controle.

Trade-offs: menor flexibilidade e cuidado com proxies, IPv6 e rotação DNS. Uma validação antes de uma segunda resolução livre é vulnerável. Conectores precisam de testes de rede, não apenas mocks de HttpClient. A orientação foi motivada por redirects e DNS pinning discutidos pela [OWASP SSRF Prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html).

Em entrevista: “O problema não é apenas uma URL malformada; é o endereço ao qual o socket realmente conecta. Limitei os destinos e vinculei validação DNS à conexão para fechar a janela de rebinding.”

## 10. Credenciais protegidas e captura limitada de payload

Problema: API e Worker precisam usar segredos sem expor plaintext no banco, logs ou exportação do workflow.

Alternativas: texto no banco; criptografia caseira; chaves no mesmo banco; mecanismo de proteção consolidado com chave externa.

Escolha: ciphertext autenticado via Data Protection, purpose específico e keyring compartilhado/persistente fora do banco. Proteção do keyring usa mecanismo externo apropriado ao ambiente. Logs e snapshots recebem dados limitados/sanitizados; não registram headers secretos ou bodies indiscriminadamente. O contexto operacional de retomada é persistido criptografado e separado de snapshots truncados, com purpose próprio e sem credenciais resolvidas.

Trade-offs: keyring passa a exigir backup, controle de acesso, rotação e configuração idêntica nos dois hosts. Perda das chaves pode tornar as credenciais inutilizáveis. Redação de nomes conhecidos não garante segurança de um payload arbitrário; captura exige política explícita.

Em entrevista: “Criptografar o banco só ajuda se a chave estiver protegida separadamente. Também tratei logs e snapshots como superfícies de vazamento, com limites e retenção próprios.”

## 11. Cancelamento cooperativo e estados explícitos

Problema: um usuário deve interromper execução sem o sistema mentir sobre uma operação que já começou.

Alternativas: apagar a execução; forçar Failed; sinalizar cancelamento e observar pontos de interrupção.

Escolha: CancelRequestedAt, observação entre nodes e CancellationToken durante operações compatíveis. Propor Cancelled em NodeExecution; nodes não iniciados terminam Skipped. WorkflowExecution já contempla Cancelled.

Trade-offs: pedido e conclusão do cancelamento não são simultâneos. Interromper a espera local não cancela necessariamente o efeito remoto; UI e auditoria precisam refletir essa limitação.

Em entrevista: “Cancelamento é uma intenção observada pelo Worker. Não transformei um timeout ou cancelamento em prova de que o destino externo não fez nada.”

## 12. Testes e documentação acompanham os incrementos

Problema: uma arquitetura de portfólio precisa demonstrar comportamento sob falha, não apenas diagramas e código gerado.

Alternativas: implementar tudo e testar no final; testar somente unidades isoladas; verificar regras em unitários e adaptadores/concorrência com serviços reais.

Escolha: smoke do host na Fase 1; invariantes unitárias desde a Fase 2; Testcontainers quando PostgreSQL e RabbitMQ passam a ser usados. Fase 15 consolida CI e automação da suíte existente. Fase 16 fecha apresentação e guia operacional.

Trade-offs: testes de integração custam tempo e exigem Docker; testes de falha precisam de cenários controlados. Não criamos testes que apenas repetem implementação ou atividade de Git artificial. Um ambiente bloqueado é relatado como pendência.

Em entrevista: “Meu critério de entrega inclui testar as janelas de falha que justificaram a arquitetura. Se uso outbox e lease, preciso provar recuperação e duplicação, não só um caminho feliz.”

## Consequências e reavaliação

A primeira entrega terá infraestrutura preparada e hosts mínimos. Nenhuma decisão acima autoriza implementar de uma vez as fases seguintes.

Reavaliar esta ADR quando houver necessidade comprovada de fan-out/joins, isolamento por equipe, escala que exceda os mecanismos existentes, destinos HTTP mais amplos, scheduling recorrente ou operação pública. Cada mudança deve trazer problema observável, alternativas e efeito sobre as garantias atuais.

Documentos relacionados: [arquitetura](../architecture.md), [modelo de dados](../data-model.md) e [roadmap](../roadmap.md).
