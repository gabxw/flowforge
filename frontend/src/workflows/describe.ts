import type { ConditionOperator, ExecutionFailureCode, NodeConfiguration } from '../api/contracts'
import { formatDuration } from '../components/format'

export const operatorLabels: Record<ConditionOperator, string> = {
  exists: 'existe', equals: 'igual a', notEquals: 'diferente de', greaterThan: 'maior que',
  greaterThanOrEqual: 'maior ou igual a', lessThan: 'menor que', lessThanOrEqual: 'menor ou igual a',
}
const operatorSymbols: Record<ConditionOperator, string> = {
  exists: 'existe', equals: '=', notEquals: '≠', greaterThan: '>', greaterThanOrEqual: '≥', lessThan: '<', lessThanOrEqual: '≤',
}

// Resumo de uma linha da configuração, usado nos cards e na linha do tempo.
// É só leitura: a validação continua no Domain/API.
export function describe(configuration: NodeConfiguration): string {
  switch (configuration.type) {
    case 'webhookTrigger': return 'entrada por webhook'
    case 'httpRequest': {
      let host = configuration.url
      try { host = new URL(configuration.url).host } catch { /* URL ainda inválida no rascunho */ }
      return configuration.method.toUpperCase() + ' ' + host
    }
    case 'delay': return 'aguarda ' + formatDuration(configuration.durationTicks / 10_000)
    case 'condition': return (configuration.sourcePointer || '(raiz)') + ' ' + operatorSymbols[configuration.operation] +
      ('expectedValue' in configuration && configuration.expectedValue !== undefined ? ' ' + JSON.stringify(configuration.expectedValue) : '')
    case 'transformJson': return configuration.fields.length + (configuration.fields.length === 1 ? ' campo' : ' campos')
    case 'log': return '“' + configuration.message + '”'
  }
}

// Mesma leitura do editor: um rascunho com JSON inválido ainda precisa de um rótulo.
export function describeText(text: string): string {
  try {
    const value = JSON.parse(text) as NodeConfiguration
    return describe(value)
  } catch {
    return 'configuração com JSON inválido'
  }
}

// O histórico guarda só o código sanitizado; o texto diz o que houve e qual é o próximo passo.
export const failureHelp: Record<ExecutionFailureCode, { title: string; next: string }> = {
  engineUnavailable: { title: 'A engine não estava disponível quando a execução foi registrada.', next: 'Execute de novo; o histórico antigo continua como estava.' },
  unsupportedNode: { title: 'Uma etapa usa um tipo sem executor disponível.', next: 'Troque a etapa por um dos tipos suportados e publique outra versão.' },
  nodeFailed: { title: 'A etapa falhou ao executar.', next: 'Confira a configuração da etapa e o input que ela recebeu.' },
  nodeTimeout: { title: 'A etapa passou do prazo.', next: 'O destino pode ter recebido a chamada. Verifique o efeito antes de repetir.' },
  interruptedNode: { title: 'A chamada HTTP estava em andamento quando o Worker caiu.', next: 'Não há reenvio automático. Confirme no destino se o efeito ocorreu antes de repetir.' },
  invalidExecutorResult: { title: 'O executor devolveu um resultado fora do contrato.', next: 'Isso indica um defeito na engine. Registre a execução para investigação.' },
  contextLimitExceeded: { title: 'O resultado ficou maior que o limite de contexto.', next: 'Reduza o que a etapa projeta ou recebe.' },
  httpDestinationDenied: { title: 'A política de rede recusou o destino antes do envio.', next: 'Revise a origem e a configuração da URL. Repetir não resolve.' },
  credentialUnavailable: { title: 'A credencial referenciada não pôde ser usada.', next: 'Confira se a credencial existe, pertence à sua conta e não foi revogada.' },
  httpRemoteFailure: { title: 'O destino respondeu com status fora de 2xx.', next: 'Veja o código HTTP na tentativa e a regra de nova tentativa da etapa.' },
  httpResponseLimitExceeded: { title: 'A resposta do destino passou do tamanho permitido.', next: 'Peça ao destino uma resposta menor ou use outro endpoint.' },
  httpResponseInvalid: { title: 'A resposta não era JSON UTF-8 válido.', next: 'Confira o tipo de conteúdo e a codificação devolvidos pelo destino.' },
  httpTransportFailed: { title: 'A conexão com o destino falhou (DNS, TCP, TLS ou leitura).', next: 'Verifique se o destino está acessível e tente de novo.' },
  httpResponseSensitive: { title: 'A resposta continha o valor da credencial e foi descartada.', next: 'Corrija o destino para não devolver o segredo.' },
  conditionValueNotComparable: { title: 'A condição comparou valores de tipos incompatíveis.', next: 'Revise o tipo do campo e do valor esperado. Não há conversão implícita.' },
  transformSourceMissing: { title: 'O campo apontado pelo Transform não existe no input.', next: 'Revise o JSON Pointer e o payload recebido.' },
  jsonPointerAmbiguous: { title: 'O JSON Pointer encontrou propriedades duplicadas.', next: 'Corrija o payload para que cada propriedade apareça uma vez.' },
  transformValueInvalid: { title: 'O Transform recebeu um valor com profundidade incompatível.', next: 'Reduza a profundidade ou o tamanho do valor projetado.' },
  executionDeadlineExceeded: { title: 'A execução passou do prazo de 48 horas.', next: 'Revise esperas e novas tentativas longas antes de executar de novo.' },
  attemptLimitExceeded: { title: 'A recuperação esgotou as tentativas de retomar a execução.', next: 'Verifique banco e broker; a execução ficou preservada para investigação.' },
}
