# Operar a persistência da Fase 3

A Fase 3 disponibilizou adaptadores e migrations. A Fase 4 compõe a API com esses stores; veja [operação e contrato HTTP](api.md). Criar workflow assegura o usuário técnico configurado no servidor, sem senha/login. O Worker ainda não utiliza o banco.

## Verificação completa

Requisitos: SDK .NET 10 e Docker com containers Linux acessível pela conta atual. O PostgreSQL de teste é criado automaticamente com porta dinâmica e credencial aleatória; não usa o volume do Compose nem um banco existente. A fixture aplica MigrateAsync e descarta o container ao terminar. Docker indisponível é falha de ambiente, não teste aprovado ou ignorado.

~~~powershell
dotnet tool restore
dotnet restore FlowForge.slnx --locked-mode
dotnet build FlowForge.slnx -c Release --no-restore
dotnet test FlowForge.slnx -c Release --no-build
~~~

Api.Tests agora também contém integração HTTP com PostgreSQL. Para verificar somente regras sem Docker, execute Domain.Tests e filtre Api.Tests por BootstrapTests, RuntimeContractTests ou WorkflowCommandTests. A suíte inteira exige containers reais; não desabilite esses testes no CI para obter um resultado verde.

## Verificar e gerar migrations

A factory de design exige FLOWFORGE_POSTGRES_CONNECTION_STRING no ambiente e não imprime seu conteúdo. Para verificar o modelo e gerar SQL sem abrir conexão ao banco, use um destino de design sem senha:

~~~powershell
$env:FLOWFORGE_POSTGRES_CONNECTION_STRING = 'Host=127.0.0.1;Database=flowforge_design'
New-Item -ItemType Directory -Force .local | Out-Null
dotnet ef migrations has-pending-model-changes --project src/FlowForge.Infrastructure --startup-project src/FlowForge.Infrastructure --configuration Release --no-build
dotnet ef migrations script --idempotent --project src/FlowForge.Infrastructure --startup-project src/FlowForge.Infrastructure --configuration Release --no-build --output .local/migrations.sql
Remove-Item Env:FLOWFORGE_POSTGRES_CONNECTION_STRING
~~~

Revise o SQL antes de aplicar. O destino acima é apenas de design e não deve ser usado como conexão de operação. O CI guarda migrations.sql junto dos TRX no artefato validacao-backend. A migration inicial cria as cinco tabelas da fase e o histórico do EF; o script idempotente consulta esse histórico.

Para aplicar com tooling, configure uma conexão real em uma sessão própria, sem incluí-la no repositório ou em histórico de comandos com valores secretos:

~~~powershell
$env:FLOWFORGE_POSTGRES_CONNECTION_STRING = [System.Net.NetworkCredential]::new(
  '', (Read-Host 'Conexão do PostgreSQL de desenvolvimento' -AsSecureString)
).Password
dotnet ef database update --project src/FlowForge.Infrastructure --startup-project src/FlowForge.Infrastructure --configuration Release --no-build
Remove-Item Env:FLOWFORGE_POSTGRES_CONNECTION_STRING
~~~

Esse comando exige um banco acessível pelo host. No Compose, PostgreSQL permanece sem porta publicada; scripts/migrate-compose.ps1 gera SQL idempotente e aplica por psql dentro do container, com opção -GenerateOnly para revisão. Não abra portas ou reutilize credenciais de produção para os testes.

## Uso dos adaptadores

A API configura IDbContextFactory<FlowForgeDbContext> com UseNpgsql e injeta PostgresWorkflowStore/PostgresTechnicalUserStore. Cada operação cria e descarta seu próprio DbContext. Não habilite sensitive-data logging.

EnsureExistsAsync cria somente o proprietário técnico, preservando o CreatedAt original quando repetido. Get e List sempre recebem o proprietário; Save recebe o agregado alterado e a revisão originalmente lida. Em WorkflowConcurrencyException, recarregue o estado e trate a decisão de edição no caso de uso. Um novo DbContext é usado após falhas; não tente salvar o mesmo contexto que sofreu erro SQL.

Arquivar mantém as versões. Delete físico, migração automática no startup e auth não estão implementados. Constraints SQL não substituem autenticação nem validação do DAG. Detalhes e trade-offs: [ADR 0003](decisions/0003-postgresql-persistence.md).
