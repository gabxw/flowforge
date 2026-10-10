# Revisão técnica — Fase 6

Status: implementação e validação local aprovadas; publicação e CI final pendentes. Não iniciar Fase 7 neste incremento.

## Entrega

Engine sequencial em Application, regras de estado/travessia em Domain e checkpoint transacional PostgreSQL em Infrastructure. Worker agora executa Trigger/Log; a API mantém aceite rápido e acrescenta consulta de nodes/logs e pedido idempotente de cancelamento. Não há executor HTTP, Condition, Transform ou Delay nesta entrega.

NodeExecution registra início/fim, estado, contagem de tentativas lógicas, metadados de input/output e código de erro. Contexto operacional e mensagem Log são protegidos por Data Protection com keyring persistente fora do banco. A captura HTTP omite conteúdo arbitrário e ciphertext. Um Log grava evento protegido junto do checkpoint, sem imprimir mensagem livre.

A migration 20261010135849_MotorSequencialEHistorico adiciona duas tabelas, progresso/contexto/cancelamento em execuções e FKs compostas de versão/execução/node. Estados Succeeded/Cancelled e novos erros são admitidos sem apagar o resultado engineUnavailable de execuções antigas. A migration anterior é preservada.

## Critérios de saída

| Critério | Evidência local |
| --- | --- |
| Travessia sequencial e transições | Domain + Application, ordem de grafo diferente da travessia |
| Execução simples e histórico | PostgreSQL/RabbitMQ reais e smoke Compose direto/proxy |
| Retomada do checkpoint | Próximo node usa output cifrado; node concluído preserva horários/AttemptCount |
| Versão fixa após enqueue | Publicação/arquivamento posterior não muda a versão nem mensagem Log executada |
| Falha de node | Executor lança exceção, erro sanitizado; tipo sem executor e timeout também falham |
| Cancelamento antes de iniciar | Execução Cancelled sem StartedAt; nodes Skipped, nenhuma tentativa |
| Cancelamento entre/durante nodes | Anteriores preservados, ativo Cancelled quando interrompido, restantes Skipped |
| Concorrência/recuperação | Heartbeat além da lease original, geração antiga rejeitada, perda durante executor, shutdown recuperável |
| Resultado + inbox + Log atômicos | Falha de commit injetada desfaz evento, output e conclusão; retomada produz um único Log |
| Keyring durável e integridade | Novo provider/Worker, binding ao ExecutionId, ciphertext adulterado recusado, caminho obrigatório |

## Verificação executada

| Verificação | Resultado |
| --- | --- |
| Restore --locked-mode | Aprovado |
| Build Release | Aprovado, 0 warnings e 0 erros |
| Unit tests Domain | 737 aprovados |
| Unit tests Application | 8 aprovados |
| HTTP/contrato API | 80 aprovados |
| Integration PostgreSQL/RabbitMQ/proteção | 90 aprovados |
| Total xUnit | 915 aprovados; 0 falhas/ignorados |
| EF pending model changes | Nenhuma alteração pendente |
| Parser de scripts PowerShell | Aprovado |
| Compose Windows isolado | Aprovado, API/Worker reconstruídos e frontend sem alterações em cache |
| Build completo das imagens no CI | Pendente |
| GitHub Actions / integração na main | Pendentes |

Evidências locais em .local/phase-6-evidence, ignoradas no Git. O roteiro isolado aplicou migrations duas vezes, verificou liveness/CRUD, Trigger → Log direto e pelo proxy, recriação do Worker com mesmo keyring, recriação da API com recuperação do proxy, Redis opcional e encerramento normal do Worker. Apenas containers/volumes descartáveis do projeto flowforge-phase6-validation-20261010 foram removidos; remoção confirmada por labels.

O frontend não sofreu alteração e usou a imagem em cache validada na Fase 5. Não conta como build novo do Dockerfile frontend no Windows: persiste a limitação local de download Docker Hub. Build novo de todas as imagens será verificado no CI. Nenhuma configuração de proxy/rede/Docker Desktop foi alterada nesta fase.

Falhas encontradas e corrigidas: expectativa antiga de duas migrations; teste de ConfigurationBuilder colocado inicialmente no projeto sem essa dependência (movido para API.Tests, que já a possui); injeção de expiração que alterava inbox sem lock da execução (corrigida para a ordem real). Testes afetados repetidos e suíte inteira aprovada. Não foram ignoradas nem suprimidas verificações.

## Revisão

Revisão feita pelo próprio implementador, sem alegar revisão independente.

- Domain mantém apenas BCL, sem EF/ASP.NET/Data Protection.
- Executor não escolhe o próximo node; a porta é validada na versão imutável e novamente no checkpoint.
- O banco controla tempo/lease; token, geração e revisão protegem gravações. Transações seguem execução → inbox → nodes e não aguardam executor/rede.
- O início do node é durável. Checkpoint terminal inclui resultado, evento Log e inbox, protegendo a janela commit → ack.
- Trigger/Log têm replay local seguro; nodes concluídos não repetem. Retomada insegura é recusada pelo contrato.
- Shutdown/perda de autoridade não são persistidos como cancelamento do usuário. Cancelamento terminal preserva o resultado anterior.
- Contexto tem limite explícito, integridade autenticada e binding de identidade. Payload excedido falha; snapshot nunca é usado como input.
- Nenhuma credencial é resolvida nesta fase. API/consumer continuam sem texto de exceções, mensagens livres ou ciphertext no histórico.
- DataProtection.Extensions é a única dependência direta nova de runtime, justificada por criptografia/keyring; não há framework de orquestração, Redis ou abstração genérica extra.
- Keyring fica fora do banco, com diretório não root/0700; o perfil sem wrapping é limitado a Development, e Production é recusado.
- Banco passou a ter dez tabelas. Migrations Up são incrementais. Downgrade apaga histórico/contexto novos e não é procedimento de operação com dados da Fase 6; correções devem ser planejadas por migration posterior.

## Limites e riscos aceitos

- Entrada manual {} somente; webhook/payload e idempotência de requests entram na Fase 7/10.
- HTTP e suas regras de SSRF/credenciais não estão implementados. Condition/Transform/Delay entram na Fase 9; scheduler/retry/DLQ na fase correspondente.
- AttemptCount preserva contagem lógica de retomadas, sem tabela de tentativas ainda. Não há retry de falhas terminais.
- Fencing/checkpoint protegem o banco, sem promessa sobre efeitos remotos. Timeout/cancelamento não provam ausência de efeito externo.
- Volume de keyring Development tem chaves XML sem wrapping; produção requer proteção externa, backup/restauração e rotação. Perder chaves bloqueia contexto antigo.
- Mensagem Log original permanece na configuração JSONB do workflow; nunca usar esse campo para segredos. O histórico grava cópia protegida e expõe metadados.
- Contextos, eventos e inbox/outbox são retidos, sem limpeza automática. Política de retenção continua no roadmap.
- Interface/editor, autenticação/RBAC/rate limiting e observabilidade completa seguem em suas fases. API permanece privada.
- Pendência menor herdada: U+0000 em texto de definição persistido ainda retorna 500 sanitizado em vez de 400, sem gravação.
- Limitação Docker Hub local permanece documentada; cache foi usado apenas para frontend sem alterações.

Decisão para entrevista: [ADR 0006](decisions/0006-sequential-engine-and-checkpoints.md). Contrato/recuperação: [execution-engine.md](execution-engine.md).
