# Demonstração do FlowForge

Este roteiro usa apenas dados fictícios e não precisa de uma API externa. Prepare o ambiente e a primeira conta conforme o README. Mantenha API/Worker/PostgreSQL/RabbitMQ ativos, migrations aplicadas e uma sessão autenticada.

## Criar exemplos

Informe FLOWFORGE_SMOKE_EMAIL/FLOWFORGE_SMOKE_PASSWORD em memória, usando Read-Host com password protegido, e execute:

~~~powershell
./scripts/demo-workflow.ps1 -IncludeFailure
~~~

O roteiro autentica com CSRF, cria/publica Pedidos de demonstração e envia total=150 e total=25 por webhook. O primeiro passa por Transform JSON, Delay de dois segundos e Log; o segundo termina pelo ramo alternativo. Repetir o primeiro evento com a mesma Idempotency-Key confirma o mesmo ExecutionId. O secret do webhook permanece em memória e não aparece na saída.

O CI também executa esse roteiro no ambiente Compose. O segundo workflow, Diagnóstico de destino bloqueado, usa https://flowforge-demo.invalid/blocked, origem fora da allowlist padrão. A execução deve falhar com httpDestinationDenied antes de fazer a chamada. A falha é esperada e deixa evidência para investigação; não desabilite SSRF para torná-la um sucesso.

## Apresentar em cinco minutos

1. Abra Workflows e mostre os dois exemplos publicados. Explique rascunho versus versão imutável e dono autenticado.
2. Abra Pedidos de demonstração no editor. Mostre portas true/false e configurações declarativas. Layout não determina ordem de execução.
3. Abra uma execução concluída. Compare nodes Succeeded/Skipped, tipos/tamanhos do input/output e tentativa. Mostre Delay como espera persistida, sem delivery aberta durante dois segundos.
4. Compare os IDs impressos e a duplicata reutilizada. O evento repetido com a mesma chave não cria outro histórico; isso não elimina efeitos externos desconhecidos.
5. Abra o workflow de diagnóstico e sua execução Failed. Verifique errorCode, node HTTP, tentativa e Log pulado.
6. Com profile observability, filtre traces pelos IDs/correlação e acompanhe API, outbox/publicação e Worker. Compare duração ativa, espera e atraso de despacho.

## Investigar uma falha

Comece pelo estado persistido e tentativa, não por um log isolado. HttpDestinationDenied significa que a política recusou o destino; o próximo passo é revisar origem/configuração, não retry indiscriminado. HttpRemoteFailure exige olhar código HTTP e regra de elegibilidade. NodeTimeout não prova que o destino não produziu efeito.

Se uma execução ficar Pending, verifique Worker, outbox disponível e broker. Se ficar Running com ResumeAt, confirme se há espera agendada antes de concluir que travou. Redrive recupera entrega da execução existente; replay cria uma execução nova com efeitos próprios. Leia [confiabilidade](execution-reliability.md) antes de usar essas ações.

Para demonstrar falhas de rede, retry elegível, queda e fencing, use os testes de integração controlados. O comando do README roda PostgreSQL/RabbitMQ descartáveis e servidor TLS de teste. Não provoque falhas em serviços de terceiros.

## Limites da demonstração

Rodar o roteiro novamente cria workflows novos. Não arquiva nem apaga dados anteriores. Conta, JWT e secrets são locais, sem valores versionados. O primeiro cadastro é Admin somente no setup local vazio; não existe cadastro administrativo público em Production. O Compose não é uma implantação pública.
