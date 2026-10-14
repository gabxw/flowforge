import { parseExactJson } from '../api/json'
import type { NodeConfiguration } from '../api/contracts'
import type { NodeType } from './templates'

export const MAX_CONFIGURATION_BYTES = 64 * 1024

// JSON.parse verifica a sintaxe. A segunda passagem verifica as chaves antes
// que duplicatas possam ser escondidas pela desserialização do navegador.
export function parseConfiguration(text: string, expectedType: NodeType): NodeConfiguration {
  if (new TextEncoder().encode(text).length > MAX_CONFIGURATION_BYTES) {
    throw new Error('A configuração excede o limite do editor de 64 KiB.')
  }

  try {
    JSON.parse(text)
  } catch {
    throw new Error('JSON inválido. Revise aspas, vírgulas e valores da configuração.')
  }

  let cursor = 0
  function whitespace() {
    while (/\s/.test(text[cursor] ?? '') && cursor < text.length) cursor++
  }
  function readString(): string {
    const start = cursor++
    while (text[cursor] !== '"') {
      if (text[cursor] === '\\') cursor++
      cursor++
    }
    cursor++
    return JSON.parse(text.slice(start, cursor)) as string
  }
  function inspect(depth: number) {
    if (depth > 32) throw new Error('A configuração excede 32 níveis de profundidade.')
    whitespace()
    const token = text[cursor]
    if (token === '{') {
      cursor++
      whitespace()
      const keys = new Set<string>()
      while (text[cursor] !== '}') {
        const key = readString()
        if (keys.has(key)) throw new Error('Propriedades JSON duplicadas não são permitidas.')
        keys.add(key)
        whitespace()
        cursor++ // dois-pontos; sintaxe já foi validada
        inspect(depth + 1)
        whitespace()
        if (text[cursor] !== ',') break
        cursor++
        whitespace()
      }
      cursor++
    } else if (token === '[') {
      cursor++
      whitespace()
      while (text[cursor] !== ']') {
        inspect(depth + 1)
        whitespace()
        if (text[cursor] !== ',') break
        cursor++
        whitespace()
      }
      cursor++
    } else if (token === '"') {
      readString()
    } else {
      while (cursor < text.length && !/[\s,\]}]/.test(text[cursor])) cursor++
    }
  }
  inspect(1)

  // A validação compartilhada exige que todo token numérico preserve seu
  // valor decimal ao passar por JSON.parse/JSON.stringify no navegador.
  const value = parseExactJson(text)
  if (value === null || typeof value !== 'object' || Array.isArray(value) ||
    !('type' in value) || value.type !== expectedType) {
    throw new Error('A configuração deve ser um objeto JSON com type igual ao tipo selecionado.')
  }

  // Domínio e API continuam responsáveis pelos campos e regras de cada tipo.
  // Não retiramos membros desconhecidos: a API deve recusá-los explicitamente.
  return value as NodeConfiguration
}
