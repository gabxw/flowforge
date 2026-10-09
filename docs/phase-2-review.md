# Revisão da Fase 2 — Domínio de workflows

Status: implementada e verificada localmente; CI pendente. Data: 2026-10-09. A Fase 3 não foi iniciada.

## Escopo

Definições imutáveis de nodes/conexões, configurações tipadas dos seis tipos iniciais, referências de credencial por proprietário, validação de DAG e agregado Workflow com rascunho, publicação e arquivamento. Políticas puras definem transições iniciais de execução e node, incluindo Cancelled. Não há persistência, transporte HTTP dessas definições ou engine nesta entrega.

O [contrato detalhado](superpowers/specs/2026-10-09-phase-2-domain-design.md), o [plano executado](superpowers/plans/2026-10-09-phase-2-domain.md) e a [ADR 0002](decisions/0002-workflow-domain-and-publication.md) registram requisitos, escolhas, alternativas e trade-offs.

## Verificação local

- Configurações e valores: 109 falhas RED por funcionalidade ausente; 110 testes Domain aprovados após implementação e caso adicional de userinfo vazio na URL. Build Release sem avisos/erros e os três testes API preservados. Revisão independente de aderência e qualidade aprovada.
- Transições: RED com 488 falhas e 12 aprovações; GREEN com 500 testes Domain aprovados. Matriz independente verifica 100 pares Workflow e 144 pares Node em ambos os métodos, além dos valores numéricos dos enums. Revisão independente de aderência e qualidade aprovada.
- Grafo e ciclo de vida: RED com 72 falhas por funcionalidade ausente; GREEN com 36 casos de grafo e 36 de agregado. Cobrem limites, duplicações, ciclos, alcance, escopo, proprietário, convergência, publicação inválida sem efeitos, clonagem, UTC, revisões e arquivamento.

Verificação da solução integrada, repetida pelo agente principal após os três incrementos:

| Comando | Resultado |
| --- | --- |
| `dotnet restore FlowForge.slnx --locked-mode` | Aprovado; manifests compatíveis com os locks |
| `dotnet build FlowForge.slnx -c Release --no-restore` | Aprovado; zero avisos e erros |
| `dotnet test FlowForge.slnx -c Release --no-build` | 682 Domain + 3 API aprovados; zero falhas/ignorados |

Domain continua sem PackageReference, FrameworkReference ou referência a outros projetos. O novo projeto Domain.Tests referencia somente Domain e os três pacotes de teste já adotados. Os locks existentes e as dependências do frontend foram preservados. O CI usa a solução inteira e foi renomeado para “Validação do projeto”, mantendo backend, frontend e containers.

## Revisão e validação remota

Configurações, grafo/agregado e transições aprovados em três revisões independentes de aderência e qualidade. A revisão final do conjunto também aprovou a integração sem findings críticos, importantes ou menores. Verificação de UTF-8 em 73 arquivos e de 37 links locais aprovada; diff sem erros de whitespace.

O CI da Fase 2 ainda não foi executado; sua aprovação continua sendo critério de fechamento. O frontend e os containers não foram revalidados localmente nesta fase e serão verificados pelo CI completo.

## Limites e riscos

- Imutabilidade e atomicidade são garantias do agregado em memória. Concorrência entre processos, constraints e transações serão tratadas na persistência.
- HTTPS sintático não garante segurança contra SSRF. O executor, a política de destinos e a resolução/conexão controlada pertencem à Fase 8.
- Condition/Transform validam definições declarativas; avaliação de JSON e seleção de ramo pertencem à Fase 9. Números Condition usam conversão decimal, com erro futuro para input fora desse domínio.
- Limites de definição: 50 nodes, 100 conexões, 50 campos Transform, JSON Pointer de até 1.024 unidades UTF-16/32 segmentos e Delay de até 24 horas. Quotas e limites operacionais adicionais permanecem futuros.
- A referência de credencial prova consistência do proprietário declarado, sem consultar existência/revogação no armazenamento.
- O aviso de manutenção do ESLint 9.36.0 registrado na Fase 1 permanece; esta fase não alterou dependências do frontend.

## Próximo incremento

Após o fechamento desta fase, a Fase 3 poderá persistir o domínio existente com PostgreSQL, mappings, migrations e testes com banco real. Depende de nova solicitação; não foi antecipada.
