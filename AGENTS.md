# Instruções locais do FlowForge

## Objetivo e escopo

- Este é um projeto principal de portfólio e aprendizado de engenharia de backend.
- Construir de forma incremental, mantendo o sistema executável, testável e documentado.
- Implementar apenas a fase ou o incremento solicitado; não iniciar a fase seguinte por iniciativa própria.
- Seguir o MVP e os critérios definidos em [docs/roadmap.md](docs/roadmap.md).
- Consultar [README.md](README.md) e a revisão da fase mais recente ([Fase 7](docs/phase-7-review.md)) para o estado atual. As revisões anteriores preservam as evidências da base.

## Arquitetura e aprendizado

- Manter Domain independente de ASP.NET Core, Entity Framework e infraestrutura.
- Preservar as responsabilidades de API, Application, Domain, Infrastructure e Worker.
- Não antecipar dependências, microsserviços, abstrações genéricas ou infraestrutura sem necessidade concreta.
- Usar [docs/architecture.md](docs/architecture.md) e [docs/data-model.md](docs/data-model.md) como direção técnica.
- Registrar decisões relevantes nas ADRs em [docs/decisions](docs/decisions).
- Explicar problema, alternativas, escolha, trade-offs e como apresentar a decisão em entrevista.
- A ADR inicial é [0001-architecture-and-scope.md](docs/decisions/0001-architecture-and-scope.md).

## Verificação e preservação

- Ao encerrar uma fase, executar build e testes pertinentes e realizar revisão técnica.
- Atualizar a revisão com resultado, riscos e critérios ainda pendentes.
- Impedimentos de ambiente ou verificações não executadas não contam como aprovação.
- Não declarar uma fase concluída quando seu critério de saída estiver pendente.
- Preservar trabalho existente e o arquivo original Novo(a) Documento de Texto.txt.
- Antes de editar ou operar Git, verificar branch, HEAD e alterações locais.

## Git e segurança

- Fazer commits pequenos e claros por fase, após build, testes e revisão.
- Escrever mensagens de commit em português do Brasil, descrevendo diretamente a alteração, sem prefixos como docs:, feat:, fix:, test: ou chore:.
- Aplicar esse formato aos próximos commits; preservar as mensagens do histórico existente.
- Usar a identidade GitHub gabxw, conforme preferência expressa do usuário.
- Verificar a identidade de autor e seu vínculo com a conta; usar e-mail noreply verificado quando disponível.
- Não inventar nome completo, e-mail privado ou associação ao GitHub; não alterar configuração global de Git.
- Não criar commits artificiais para aumentar atividade.
- Nunca expor ou versionar tokens, senhas, chaves ou valores de secrets.
- Aplicar também as regras globais do usuário sobre project-memory, contexto curado e segurança.
