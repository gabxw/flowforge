# Validação e integração contínua

Execute com Docker ativo, .NET 10 e Node 22:

~~~powershell
dotnet restore FlowForge.slnx --locked-mode
dotnet build FlowForge.slnx -c Release --no-restore
dotnet test FlowForge.slnx -c Release --no-build -m:1
npm --prefix frontend ci
npm --prefix frontend run lint
npm --prefix frontend run build
npm --prefix frontend test
~~~

PostgreSQL e RabbitMQ dos testes vêm de Testcontainers com portas aleatórias e volumes descartáveis. O servidor HTTPS de teste usa CA descartável/socket real; somente o assembly de testes remapeia a conexão. Não há liberação de loopback ou bypass TLS no runtime. Nenhum serviço externo de produção é necessário.

## O que a suíte verifica

| Camada | Comportamentos |
| --- | --- |
| Domain | Grafo acíclico, publicação/versionamento, portas, configurações tipadas, limites e políticas de retry |
| Application | Caminho escolhido, snapshots, cancelamento, perda de autoridade e contratos de executores |
| API | Problem Details, limites, revisão concorrente, JWT/CSRF, refresh/replay/logout e isolamento por dono |
| Integration | Schema real/migrations, outbox/inbox, fencing, queda/duplicação, TLS/SSRF, efeito remoto e continuidade de trace |
| Browser | Criar/editar/publicar/executar, conflito/erro, keyboard/mobile, layout reaberto e reautenticação sem perder rascunho |
| Compose | Serviços reais, migrations idempotentes, proxy após recriação, broker indisponível, Delay sem delivery aberta e shutdown |

A contagem não substitui cobertura de riscos. Testes observam efeito e autoridade de gravação; não há sucesso derivado apenas de mocks ou getters.

## CI e proteção da main

Três jobs obrigatórios: Build e testes do backend; Lint build e jornadas do frontend; Ambiente Docker e encerramento do Worker. Todos executam em checkout limpo Ubuntu e usam os lockfiles. Os jobs são independentes para reduzir tempo total; falha em qualquer um impede integração. Todo PR roda o workflow, inclusive documentação, evitando checks pendentes por filtro de paths.

A proteção da main exige esses checks provenientes do app GitHub Actions, branch atualizada e histórico linear. Aplica-se também ao administrador; force push/deletion estão bloqueados. PR é obrigatório, com zero revisores exigidos para este projeto individual. Conflitos/comentários precisam estar resolvidos. A configuração do GitHub é evidenciada na revisão, não inferida da presença de um YAML.

Execuções anteriores da mesma branch/PR são canceladas quando surge um novo commit. Não há retry automático dos testes. Processos backend usam -m:1 para conter disputa de recursos; testes de timeout mantêm orçamento específico e os de resposta normal usam orçamento separado.

Artifacts por sete dias: TRX, script SQL idempotente, lockfiles e relatório/resultados Playwright. Trace de navegador com backend real fica desligado para não capturar passwords/tokens; trace de mocks usa somente dados fictícios. Logs de secrets não são publicados.

## Dependências e builds

Actions são fixadas por commit; imagens por versão estável e dependências por lockfile. Restore locked e npm ci impedem resolução silenciosa. npm audit bloqueia severidade alta; auditoria NuGet é executada explicitamente no backend. Atualizações devem passar as mesmas verificações, sem atualizar toda a stack para aumentar escopo.

Docker copia csproj/lockfiles antes das fontes, preservando a camada de restore quando muda apenas C#. As duas imagens compartilham a restauração das três bibliotecas. O runtime mantém usuário não root e keyring com diretório restrito. Não foi acrescentado cache distribuído ou registry.

Não há destino de deploy contratado nesta fase. CD permanece uma promoção manual de código revisado/validado; nenhuma implantação pública é presumida. Consulte os limites operacionais antes de escolher um ambiente.
