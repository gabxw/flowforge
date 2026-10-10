
# ADR 0009 — Nodes declarativos e espera durável na outbox

Status: aceita na Fase 9, em 10/10/2026.

## Problema

Condition precisa escolher um único ramo a partir de JSON, e Transform precisa produzir o contexto do próximo node com comportamento previsível. Executar código enviado pelo usuário ampliaria a superfície de segurança e tornaria o contrato difícil de testar.

Delay pode durar até 24 horas. Aguardar dentro do consumer prende sua entrega/prefetch, e perder o processo não pode perder o prazo. Reaproveitamos as garantias transacionais já presentes no PostgreSQL, na outbox e na inbox.

## Alternativas

| Opção | Benefício | Custo ou risco |
| --- | --- | --- |
| Expressões/scripts arbitrários | Flexibilidade | Sandbox, recursos e efeitos imprevisíveis; fora do MVP |
| Biblioteca de linguagem de consulta JSON | Mais operadores | Dependência e semântica adicional sem caso necessário |
| Operadores e projeções tipados | Contrato pequeno, testável | Sem cálculos, templates ou linguagem geral |
| Task.Delay dentro do consumer | Implementação curta | Espera volátil, delivery/lease ocupadas, reinício perde timer |
| Scheduler separado ou plugin RabbitMQ | Recursos específicos de tempo | Outro ciclo operacional e coordenação com o banco |
| Redis para timers/locks | Estruturas rápidas | Segundo estado durável e nova janela de inconsistência |
| AvailableAt na outbox existente | Suspensão e continuação na mesma transação | Polling, atraso sob carga e mais linhas de histórico |

## Escolha

Condition usa os sete operadores existentes no domínio. JSON Pointer segue tokens da RFC 6901: raiz vazia, escapes ~1 e ~0, nomes exatos e índices de array sem zeros à esquerda. Campo ausente retorna false, inclusive NotEquals; null é um valor presente. Membro duplicado no objeto consultado falha por ambiguidade. A RFC define a resolução e os escapes; nossa política de ausência/comparação é uma escolha do produto. [RFC 6901](https://www.rfc-editor.org/rfc/rfc6901)

Igualdade é tipada; strings são ordinais e números usam decimal sem perda de precisão. Um número que TryGetDecimal arredondaria não determina silenciosamente um ramo. Comparação ordenada com tipo incompatível ou valor não representável falha com código estável. Preservamos a configuração publicada: literais antigos aceitos pelo domínio, mas não representáveis exatamente, falham ao executar.

Transform projeta de 1 a 50 propriedades no objeto de saída, a partir de literal ou Pointer. Não executa scripts e não faz chamadas externas. Valores compostos são copiados; caminhos ausentes falham, sem substituir por null. Saída é limitada a 64 KiB e profundidade 32, contando o objeto externo. O input original não é alterado.

Delay retorna uma intenção de suspensão imediatamente. O store, sob autoridade da lease, grava na mesma transação:

1. ResumeAt calculado a partir do primeiro StartedAt do node;
2. a próxima DispatchSequence da execução;
3. uma nova mensagem v1 de continuação, com MessageId distinto e AvailableAt no prazo;
4. conclusão/liberação da inbox atual.

Após o commit, o consumer confirma a entrega antiga. A execução e o Delay continuam Running; ResumeAt informa a espera, sem novo estado no enum. Não há thread, transação, heartbeat ou delivery mantidos durante o prazo.

O dispatcher existente publica somente outbox disponível. O índice parcial de AvailableAt e SKIP LOCKED permitem claims concorrentes sem acrescentar um serviço de scheduler. SKIP LOCKED atende a seleção de trabalho em fila; não substitui integridade ou locks da execução. [PostgreSQL — SELECT](https://www.postgresql.org/docs/current/sql-select.html)

A mensagem continua contendo somente versão/IDs/correlação. A sequência fica no banco. Inbox admite histórico concluído, mas apenas um registro ativo por execução. Claim/checkpoint exigem a sequência atual, token, geração e lease válidos. Mensagens anteriores/duplicadas são no-ops; uma mensagem adiantada não reserva a inbox futura. A outbox permanece responsável por acordar no prazo.

Ao receber a continuação devida, a engine conclui o mesmo Delay e percorre o próximo node, sem chamar de novo seu executor ou incrementar a tentativa pela espera. Reinício antes do commit da suspensão permite replay local seguro; usa o início original, não um novo prazo. Arredondamos o deadline para cima ao microssegundo do PostgreSQL, evitando retomar antes do configurado.

Cancelamento acelera AvailableAt da continuação já existente. O Worker observa o pedido e encerra; a API somente persiste a intenção. Se o pedido vencer a corrida antes da suspensão, não criamos outro dispatch. Indisponibilidade do broker pode atrasar a observação do cancelamento.

## Trade-offs e garantias

O relógio é do PostgreSQL. ResumeAt é o instante mínimo elegível, não uma promessa de execução pontual. O polling de 1 segundo, backlog, banco ou broker podem atrasar a retomada. O scheduler e consumer compartilham o host Worker, mas não uma entrega em espera.

Manter Running evita inventar estados antecipados; clientes precisam usar ResumeAt para distinguir processamento de espera. Tempo total do node inclui a espera, enquanto duração ativa exigirá métrica própria na Fase 14.

Cada Delay acrescenta uma outbox/inbox concluída; retenção exige política futura. Não indexamos ResumeAt porque a consulta real usa AvailableAt. Downgrade não é seguro depois de gerar múltiplos dispatches ou códigos novos; não apagamos histórico para viabilizá-lo.

Condições e transformações são determinísticas e podem ser repetidas após interrupção local. Essa propriedade não se estende a efeitos HTTP: uma chamada com resultado externo desconhecido continua sem replay automático. Retries, jitter, DLQ, tentativas individuais e deadline global pertencem à Fase 10.

## Como explicar em entrevista

“Eu precisava liberar o consumer durante uma espera e sobreviver à queda do Worker. O banco já tinha a outbox para fechar a falha entre commit e publicação. Gravei o prazo e a continuação na mesma transação que conclui a mensagem atual. Quando o prazo chega, uma mensagem nova retoma o mesmo node; sequência e inbox impedem que uma duplicata antiga avance a execução. Isso usa a infraestrutura existente, com atraso de polling explícito, sem prometer execução pontual ou exactly-once externo.”

“Para Condition e Transform, limitei o contrato a operadores e projeções. Defini ausência, null, tipos, precisão e limites antes de executar. O objetivo é que o usuário consiga prever o resultado e investigar um erro sem executar código arbitrário ou expor payloads em logs.”

## Verificação

Unitários cobrem resolução de Pointer, igualdade/ordem, precisão, ausência, duplicatas, profundidade e amplificação. Integração cobre ambos os ramos com convergência, contexto protegido, cancelamento, commit com falha, claims concorrentes, delays consecutivos e os seis tipos com RabbitMQ/HTTPS reais. Compose/CI recriam o Worker durante a suspensão e verificam contexto/keyring e continuidade. Resultados efetivos ficam em [phase-9-review.md](../phase-9-review.md).
