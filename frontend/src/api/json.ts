export class JsonPrecisionError extends Error {
  constructor() {
    super('O JSON contém um número fora da precisão suportada pelo navegador. Nenhum valor foi convertido.')
    this.name = 'JsonPrecisionError'
  }
}

// Compara o valor decimal escrito, sem converter o coeficiente para Number.
// Formas equivalentes, como 1.00e-2 e 0.01, possuem a mesma representação.
function decimalForm(token: string): string {
  const [mantissa, exponent = '0'] = token.toLowerCase().split('e')
  const negative = mantissa.startsWith('-')
  const absolute = negative ? mantissa.slice(1) : mantissa
  const point = absolute.indexOf('.')
  const fractionLength = point === -1 ? 0 : absolute.length - point - 1
  const digits = absolute.replace('.', '').replace(/^0+/, '')
  if (!digits) return '0' // Inclui -0, 0.0 e 0eN.
  const trailingZeros = digits.match(/0+$/)?.[0].length ?? 0
  const coefficient = digits.slice(0, digits.length - trailingZeros)
  const power = BigInt(exponent) - BigInt(fractionLength) + BigInt(trailingZeros)
  return (negative ? '-' : '') + coefficient + 'e' + power
}

function checkNumber(token: string) {
  const number = Number(token)
  if (!Number.isFinite(number) || (Number.isInteger(number) && !Number.isSafeInteger(number))) {
    throw new JsonPrecisionError()
  }
  const serialized = JSON.stringify(number)
  if (decimalForm(token) !== decimalForm(serialized)) throw new JsonPrecisionError()
}

// Percorre tokens antes do JSON.parse. Dígitos dentro de strings, inclusive
// strings com aspas escapadas, são dados e não participam da validação numérica.
export function parseExactJson(text: string): unknown {
  let cursor = 0
  while (cursor < text.length) {
    const character = text[cursor]
    if (character === '"') {
      cursor++
      let closed = false
      while (cursor < text.length) {
        if (text[cursor] === '\\') cursor += 2
        else if (text[cursor] === '"') {
          cursor++
          closed = true
          break
        } else cursor++
      }
      if (!closed) throw new SyntaxError('JSON inválido.')
    } else if (character === '-' || /[0-9]/.test(character)) {
      const match = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(text.slice(cursor))
      if (!match) throw new SyntaxError('JSON inválido.')
      checkNumber(match[0])
      cursor += match[0].length
    } else cursor++
  }
  return JSON.parse(text) as unknown
}
