import { expect, test } from '@playwright/test'
import { JsonPrecisionError, parseExactJson } from '../src/api/json'

test.describe('Fidelidade numérica do JSON', () => {
  for (const token of [
    '0.1234567890123456789012345678',
    '-0.1234567890123456789012345678',
    '1.0000000000000001',
    '9007199254740993',
    '9007199254740992',
    '9.007199254740993e15',
    '1e309',
    '1e-324',
  ]) {
    test('recusa perda de precisão para ' + token, () => {
      expect(() => parseExactJson('{"value":' + token + '}')).toThrow(JsonPrecisionError)
    })
  }

  test('aceita decimais e expoentes semanticamente equivalentes', () => {
    expect(parseExactJson('[1.2300e+2,0.1000000000000000000000,0.000000123,1e-7,-12.50,9007199254740991]'))
      .toEqual([123, 0.1, 0.000000123, 1e-7, -12.5, 9007199254740991])
  })

  test('aceita zero e zero negativo sem confundir expoentes', () => {
    expect(parseExactJson('[0,-0,0.00,-0.00e-20,0e999999999999999999999999]'))
      .toEqual([0, -0, 0, -0, 0])
  })

  test('não trata dígitos em strings, propriedades ou escapes como números', () => {
    const document = {
      '9007199254740993': '0.1234567890123456789012345678',
      escaped: '"9007199254740993" \\ caminho \\u1234',
      nested: ['1e999999', { value: 0.5 }],
    }
    expect(parseExactJson(JSON.stringify(document))).toEqual(document)
    expect(parseExactJson('{"escaped":"\\u0031e999999","value":1}'))
      .toEqual({ escaped: '1e999999', value: 1 })
  })

  test('mantém JSON sem números e literais compostos', () => {
    expect(parseExactJson('{"nothing":null,"active":true,"items":[false,{},[]]}'))
      .toEqual({ nothing: null, active: true, items: [false, {}, []] })
  })

  test('recusa sintaxe inválida, inclusive string sem fechamento', () => {
    for (const invalid of ['{"value":01}', '{"value":NaN}', '{"value":}', '{"value":"123', '[1,]']) {
      expect(() => parseExactJson(invalid)).toThrow(SyntaxError)
    }
  })

  test('erros de precisão não incluem o token ou conteúdo do documento', () => {
    const token = '0.1234567890123456789012345678'
    try {
      parseExactJson('{"private":"nao-repassar-conteudo","value":' + token + '}')
      throw new Error('O JSON deveria ser recusado.')
    } catch (error) {
      expect(error).toBeInstanceOf(JsonPrecisionError)
      expect((error as Error).message).not.toContain(token)
      expect((error as Error).message).not.toContain('nao-repassar-conteudo')
    }
  })
})
