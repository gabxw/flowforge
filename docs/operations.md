# Operação local, recuperação e limites

O ambiente entregue é local/privado. API, frontend e painel publicam somente em 127.0.0.1; PostgreSQL, RabbitMQ, Redis opcional e OTLP ficam na rede Docker. Não use este Compose como configuração pública de produção.

## Configuração e atualização

Secrets PostgreSQL/RabbitMQ são fornecidos ao Compose pela sessão e montados como arquivos. Não grave valores em Git, scripts, artifacts ou .env versionado. Ao recriar serviços, reutilize os mesmos valores correspondentes aos volumes existentes; trocar uma variável não redefine a password de um banco já inicializado.

Aplique migrations com scripts/migrate-compose.ps1. GenerateOnly permite revisar SQL; a aplicação não altera schema ao iniciar. Atualização deve parar escrita/processamento, preservar backup consistente e aplicar o SQL antes de ativar hosts novos. Down de migration não é estratégia segura depois que novos dados foram gravados; prefira correção para frente ou restauração coordenada.

Liveness responde sem banco/keyring e não comprova prontidão completa. Para readiness operacional, use smoke-compose e smokes autenticados. Não execute scripts que criam dados na conta de um usuário real para medir disponibilidade.

## Backup e recuperação

Preserve os três volumes: postgres_data, rabbitmq_data e execution_keyring. O keyring inclui key-*.xml e jwt-signing.protected; copiar apenas o banco não restaura credentials, contexto e assinatura de sessões. A proteção atual depende das permissões do SO/volume; cópias precisam de acesso restrito e criptografia do operador.

Procedimento recomendado:

1. Pare novas entradas e API/Workers para ter um ponto consistente. Registre versão do código e IDs das migrations aplicadas.
2. Faça dump lógico do PostgreSQL com ferramenta compatível, sem imprimir password. Copie o keyring integral e preserve o estado do broker/topologia conforme a política do ambiente. Não mostre o conteúdo desses arquivos em logs.
3. Armazene backups criptografados fora do diretório versionado, com controle de acesso e retenção. Verifique integridade e recuperação periodicamente; existir um arquivo não prova recuperação.
4. Restaure em um projeto Compose isolado com novos volumes. Reaplique configuração/permissions e valide schema, leitura de contexto protegido e uma nova execução fictícia antes de promover.
5. Não misture snapshots de épocas diferentes. Restaurar banco/broker pode repetir entregas; inbox/fencing protegem gravações locais, mas efeitos remotos já produzidos exigem reconciliação. Restauração também deve considerar refresh sessions/revogação e expiração de JWT.

O CI confirma persistência do keyring e retomada de contextos ao recriar API/Worker. Não substitui ensaio de recuperação do backup escolhido pelo operador. Nenhum backup contendo keys ou dados foi publicado como evidência.

## Retenção e capacidade

As janelas de [confiabilidade](execution-reliability.md#idempotência-e-retenção) são contrato/política proposta; **não há cleaner automático entregue**. Inbox/outbox, chaves de webhook, tentativas, replays/redrives e contexto precisam de limpeza coordenada depois do término e da janela de deduplicação. Não elimine uma inbox isolada enquanto sua mensagem puder voltar.

Refresh families têm validade absoluta de sete dias, mas registros expirados também precisam de retenção operacional. O painel usa memória e perde histórico ao reiniciar. Banco, volumes e logs de containers continuam crescendo sem uma política de limpeza; monitore disco e use dados fictícios na demonstração.

Quotas de grafo/payload/contexto, deadlines, tentativas e concorrência são limites de aplicação, não plano de capacidade medido. Rate limiting HTTP é por instância; PostgreSQL continua coordenando autoridade/ownership. Multi-instância exigirá revisar distribuição da quota e observar capacidade do banco/broker antes de acrescentar Redis.

## Incidentes

| Sintoma | Verificação inicial | Ação |
| --- | --- | --- |
| Pending acumulando | Worker/broker, outbox.ready e oldest.age | Recuperar dependência; usar redrive só conforme contrato |
| Running com ResumeAt | Espera/deadline/cancelamento persistidos | Aguardar retomada ou solicitar cancelamento |
| Lease perdida | Fencing, logs/correlação e geração da entrega | Worker antigo não pode gravar; não forçar update manual |
| HTTP recusado | Origem, DNS aprovado, TLS e código sanitizado | Corrigir configuração; não liberar rede privada |
| Timeout externo | Tentativa/resultado externo e idempotência do destino | Reconciliar efeito antes de repetir |
| Refresh reutilizado | Família revogada e abas concorrentes | Reautenticar; não reaproveitar cookie antigo |
| Banco/keyring indisponível | Volume, permissões e schema | Recuperar dependência sem trocar keyring por um vazio |

## Antes de uma implantação pública

Production bloqueia o provedor criptográfico atual: falta wrapping de chaves por certificado/KMS/secret store externo. É uma barreira explícita. Também faltam destino de deploy, TLS/ingress, processo privado de administração de contas, recuperação de password/MFA, collector autenticado com retenção/alertas, backup ensaiado, limpeza executável e capacidade medida.

JWT aqui é de um realm fechado próprio, não um servidor OAuth/OIDC. Adoção de um provedor externo deve preservar ownership e revogação conforme o contrato. Cadastro/setup públicos ficam desligados em Production. CI aprovado comprova o escopo testado, não disponibilidade/SLA de produção.
