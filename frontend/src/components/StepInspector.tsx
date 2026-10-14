import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import type { ConditionOperator, HttpRequestMethod, JsonScalar, JsonValue, NodeConfiguration, TransformField, WorkflowConnection } from '../api/contracts'
import { parseConfiguration } from '../workflows/configuration'
import { nodeTypeLabels } from '../workflows/templates'
import type { NodeType } from '../workflows/templates'
import { describeText, operatorLabels } from '../workflows/describe'
import { outputPorts } from '../workflows/graph'
import type { ConnectionCandidate } from '../workflows/graph'
import { NodeGlyph } from './Ui'

export interface InspectorStep { nodeId: string; type: NodeType; configurationText: string; credentialText: string }
export type InspectorMode = 'fields' | 'json'

const methods: HttpRequestMethod[] = ['get', 'post', 'put', 'patch', 'delete', 'head', 'options']
const ordering: ConditionOperator[] = ['greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual']
const safeMethods: HttpRequestMethod[] = ['get', 'head', 'options']

type Parsed = { ok: true; value: unknown } | { ok: false; error: string }

// Texto local enquanto a pessoa digita; só valores válidos chegam à configuração.
// Um valor recusado fica no campo com a explicação ao lado, sem apagar o que foi digitado.
function DraftInput({ id, value, parse, onCommit, disabled, inputMode, placeholder, describedBy }: {
  id: string; value: string; parse: (text: string) => Parsed
  onCommit: (value: unknown) => void; disabled: boolean; inputMode?: 'decimal' | 'text'; placeholder?: string; describedBy?: string
}) {
  const [text, setText] = useState(value)
  const [error, setError] = useState<string | null>(null)
  const [lastValue, setLastValue] = useState(value)
  if (value !== lastValue) { setLastValue(value); setText(value); setError(null) }
  const errorId = id + '-error'
  return <>
    <input id={id} className="field" value={text} disabled={disabled} inputMode={inputMode} placeholder={placeholder}
      aria-invalid={error ? true : undefined} aria-describedby={[error ? errorId : null, describedBy].filter(Boolean).join(' ') || undefined}
      autoComplete="off" spellCheck={false}
      onChange={event => {
        setText(event.target.value)
        const parsed = parse(event.target.value)
        setError(parsed.ok ? null : parsed.error)
        if (parsed.ok) onCommit(parsed.value)
      }} />
    <p className="field-error" id={errorId} aria-live="polite">{error}</p>
  </>
}

function asNumber(text: string): Parsed {
  const value = Number(text.trim().replace(',', '.'))
  return text.trim() !== '' && Number.isFinite(value) ? { ok: true, value } : { ok: false, error: 'Digite um número, como 100 ou 2,5. O valor anterior continua salvo no rascunho.' }
}
// O Domain aceita Delay maior que zero e de até 24 horas.
function asDelaySeconds(text: string): Parsed {
  const parsed = asNumber(text)
  if (!parsed.ok) return parsed
  const seconds = parsed.value as number
  return seconds > 0 && seconds <= 86_400 ? parsed : { ok: false, error: 'Use um valor maior que 0 e de até 86.400 segundos (24 horas).' }
}
// Escalar JSON quando a pessoa usa a sintaxe; texto solto vira string.
function asScalar(text: string): Parsed {
  try {
    const value = JSON.parse(text) as JsonValue
    if (value === null || ['string', 'number', 'boolean'].includes(typeof value)) return { ok: true, value: value as JsonScalar }
  } catch { /* texto solto */ }
  return { ok: true, value: text }
}
function asLiteral(text: string): Parsed {
  try { return { ok: true, value: JSON.parse(text) as JsonValue } } catch { return { ok: true, value: text } }
}
const kindOf = (value: unknown) => value === null ? 'null' : typeof value === 'string' ? 'texto' : typeof value === 'number' ? 'número' : typeof value === 'boolean' ? 'booleano' : 'JSON'

function Field({ label, htmlFor, hint, children }: { label: string; htmlFor: string; hint?: ReactNode; children: ReactNode }) {
  return <div className="inspector-field"><label htmlFor={htmlFor}>{label}</label>{children}{hint && <p className="hint" id={htmlFor + '-hint'}>{hint}</p>}</div>
}

function ConfigurationFields({ prefix, value, locked, change, credential, changeCredential }: {
  prefix: string; value: NodeConfiguration; locked: boolean
  change: (value: NodeConfiguration) => void; credential: string; changeCredential: (value: string) => void
}) {
  switch (value.type) {
    case 'webhookTrigger':
      return <p className="inspector-note">Recebe o evento enviado ao webhook da versão publicada. O payload vira o input da próxima etapa.</p>
    case 'log':
      return <Field label="Mensagem" htmlFor={prefix + 'message'} hint="O histórico guarda só o tamanho da mensagem, não o texto.">
        <textarea id={prefix + 'message'} className="field" rows={3} value={value.message} disabled={locked}
          onChange={e => change({ ...value, message: e.target.value })} />
      </Field>
    case 'delay':
      return <Field label="Duração em segundos" htmlFor={prefix + 'delay'} hint="Espera durável: o Worker libera a fila e retoma no prazo. Máximo de 24 horas.">
        <DraftInput id={prefix + 'delay'} value={String(value.durationTicks / 10_000_000)} parse={asDelaySeconds} inputMode="decimal" disabled={locked}
          describedBy={prefix + 'delay-hint'} onCommit={seconds => change({ ...value, durationTicks: Math.round((seconds as number) * 10_000_000) })} />
      </Field>
    case 'condition': {
      const operation = value.operation
      return <>
        <Field label="Campo do input" htmlFor={prefix + 'pointer'} hint={<>JSON Pointer, como <code>/total</code>. Vazio compara o input inteiro.</>}>
          <input id={prefix + 'pointer'} className="field monospace" value={value.sourcePointer} disabled={locked} spellCheck={false}
            aria-describedby={prefix + 'pointer-hint'} onChange={e => change({ ...value, sourcePointer: e.target.value })} />
        </Field>
        <Field label="Operador" htmlFor={prefix + 'operator'}>
          <select id={prefix + 'operator'} className="field" value={operation} disabled={locked} onChange={e => {
            const next = e.target.value as ConditionOperator
            const current = 'expectedValue' in value ? value.expectedValue : undefined
            if (next === 'exists') change({ type: 'condition', sourcePointer: value.sourcePointer, operation: next })
            else if (ordering.includes(next)) change({ type: 'condition', sourcePointer: value.sourcePointer, operation: next as 'greaterThan', expectedValue: typeof current === 'number' ? current : 0 })
            else change({ type: 'condition', sourcePointer: value.sourcePointer, operation: next as 'equals', expectedValue: current ?? true })
          }}>
            {(Object.keys(operatorLabels) as ConditionOperator[]).map(op => <option key={op} value={op}>{operatorLabels[op]}</option>)}
          </select>
        </Field>
        {value.operation !== 'exists' && <Field label="Valor esperado" htmlFor={prefix + 'expected'}
          hint={ordering.includes(operation) ? 'Comparações de ordem aceitam só números.' : <>Interpretado como {kindOf(value.expectedValue)}. Use aspas para forçar texto, como <code>"100"</code>.</>}>
          <DraftInput id={prefix + 'expected'} value={typeof value.expectedValue === 'string' ? value.expectedValue : JSON.stringify(value.expectedValue)}
            parse={ordering.includes(operation) ? asNumber : asScalar} disabled={locked} describedBy={prefix + 'expected-hint'}
            onCommit={expected => change({ ...value, expectedValue: expected } as NodeConfiguration)} />
        </Field>}
        <p className="inspector-note">Ligue as saídas <span className="port-true">true</span> e <span className="port-false">false</span>. Não há conversão de tipos: <code>"100"</code> e <code>100</code> são diferentes.</p>
      </>
    }
    case 'transformJson':
      return <fieldset className="inspector-fields">
        <legend>Campos do resultado</legend>
        <p className="hint">O resultado substitui o input da próxima etapa.</p>
        {value.fields.map((field, k) => {
          const literal = 'literal' in field && field.literal !== undefined
          const replace = (next: TransformField) => change({ ...value, fields: value.fields.map((f, i) => i === k ? next : f) })
          return <div className="transform-row" key={k}>
            <Field label={'Propriedade ' + (k + 1)} htmlFor={prefix + 'target-' + k}>
              <input id={prefix + 'target-' + k} className="field" value={field.targetProperty} disabled={locked}
                onChange={e => replace({ ...field, targetProperty: e.target.value } as TransformField)} />
            </Field>
            <Field label="Origem" htmlFor={prefix + 'kind-' + k}>
              <select id={prefix + 'kind-' + k} className="field" value={literal ? 'literal' : 'pointer'} disabled={locked}
                onChange={e => replace(e.target.value === 'literal' ? { targetProperty: field.targetProperty, literal: '' } : { targetProperty: field.targetProperty, sourcePointer: '' })}>
                <option value="pointer">Campo do input</option><option value="literal">Valor fixo</option>
              </select>
            </Field>
            <Field label={literal ? 'Valor' : 'JSON Pointer'} htmlFor={prefix + 'value-' + k}>
              {literal
                ? <DraftInput id={prefix + 'value-' + k} value={typeof field.literal === 'string' ? field.literal : JSON.stringify(field.literal)} parse={asLiteral}
                  disabled={locked} onCommit={v => replace({ targetProperty: field.targetProperty, literal: v as JsonValue })} />
                : <input id={prefix + 'value-' + k} className="field monospace" value={field.sourcePointer ?? ''} disabled={locked} spellCheck={false}
                  onChange={e => replace({ targetProperty: field.targetProperty, sourcePointer: e.target.value })} />}
            </Field>
            {!locked && value.fields.length > 1 && <button type="button" className="btn btn-quiet btn-small"
              onClick={() => change({ ...value, fields: value.fields.filter((_, i) => i !== k) })}>Remover propriedade {k + 1}</button>}
          </div>
        })}
        {!locked && <button type="button" className="btn btn-small" onClick={() => change({ ...value, fields: [...value.fields, { targetProperty: 'campo' + (value.fields.length + 1), sourcePointer: '' }] })}>Adicionar propriedade</button>}
      </fieldset>
    case 'httpRequest': {
      const attempts = value.maxAttempts ?? 1
      const needsKey = attempts > 1 && !safeMethods.includes(value.method) && !value.supportsIdempotency
      return <>
        <div className="inspector-pair">
          <Field label="Método" htmlFor={prefix + 'method'}>
            <select id={prefix + 'method'} className="field" value={value.method} disabled={locked} onChange={e => change({ ...value, method: e.target.value as HttpRequestMethod })}>
              {methods.map(m => <option key={m} value={m}>{m.toUpperCase()}</option>)}
            </select>
          </Field>
          <Field label="Tentativas" htmlFor={prefix + 'attempts'}>
            <select id={prefix + 'attempts'} className="field" value={attempts} disabled={locked} onChange={e => change({ ...value, maxAttempts: Number(e.target.value) })}>
              {[1, 2, 3, 4, 5].map(n => <option key={n} value={n}>{n}</option>)}
            </select>
          </Field>
        </div>
        <Field label="URL" htmlFor={prefix + 'url'} hint="Só HTTPS para origens permitidas pela política. Nunca coloque segredos na URL.">
          <input id={prefix + 'url'} className="field monospace" type="url" value={value.url} disabled={locked} spellCheck={false}
            aria-describedby={prefix + 'url-hint'} onChange={e => change({ ...value, url: e.target.value })} />
        </Field>
        <label className="check inspector-check"><input type="checkbox" checked={!!value.supportsIdempotency} disabled={locked}
          onChange={e => change({ ...value, supportsIdempotency: e.target.checked })} /> O destino aceita chave de idempotência</label>
        {needsKey && <p className="inspector-note is-warning">Mais de uma tentativa em {value.method.toUpperCase()} exige um destino idempotente. O backend recusa a publicação assim.</p>}
        <Field label="ID da credencial (opcional)" htmlFor={prefix + 'credential'} hint="Só a referência. O segredo fica cifrado no servidor.">
          <input id={prefix + 'credential'} className="field monospace" value={credential} disabled={locked} autoComplete="off" placeholder="UUID da credencial"
            aria-describedby={prefix + 'credential-hint'} onChange={e => changeCredential(e.target.value)} />
        </Field>
      </>
    }
  }
}

export function StepInspector({ prefix, step, index, locked, mode, relatedConnections, onMode, onType, onText, onCredential, onRemove, onClose, closeLabel = 'Todas as etapas' }: {
  prefix: string; step: InspectorStep; index: number; locked: boolean; mode: InspectorMode; relatedConnections: number; closeLabel?: string
  onMode: (mode: InspectorMode) => void; onType: (type: NodeType) => void; onText: (text: string) => void
  onCredential: (value: string) => void; onRemove: () => void; onClose: () => void
}) {
  let parsed: NodeConfiguration | null = null
  let problem: string | null = null
  try { parsed = parseConfiguration(step.configurationText, step.type) } catch (error) { problem = error instanceof Error ? error.message : 'Configuração inválida.' }
  const number = index + 1
  return <section className="inspector" aria-labelledby={prefix + 'title'}>
    <header className="inspector-head">
      <button type="button" className="btn btn-quiet btn-small inspector-back" onClick={onClose}><span className="chevron" aria-hidden="true">‹</span>{closeLabel}</button>
      <div className="inspector-title">
        <span className="inspector-glyph"><NodeGlyph type={step.type} size={22} /></span>
        <div><h3 id={prefix + 'title'}>{nodeTypeLabels[step.type]}</h3><p>Etapa {number} <code>{step.nodeId.slice(0, 8)}</code></p></div>
      </div>
    </header>
    <Field label={'Tipo da etapa ' + number} htmlFor={prefix + 'type'} hint={locked ? undefined : 'Trocar o tipo reinicia a configuração desta etapa.'}>
      <select id={prefix + 'type'} className="field" value={step.type} disabled={locked} onChange={e => onType(e.target.value as NodeType)}>
        {(Object.keys(nodeTypeLabels) as NodeType[]).map(type => <option key={type} value={type}>{nodeTypeLabels[type]}</option>)}
      </select>
    </Field>
    <div className="segmented" role="group" aria-label="Forma de editar a configuração">
      <button type="button" aria-pressed={mode === 'fields'} onClick={() => onMode('fields')}>Campos</button>
      <button type="button" aria-pressed={mode === 'json'} onClick={() => onMode('json')}>JSON</button>
    </div>
    {mode === 'json' ? <Field label={'Configuração JSON da etapa ' + number} htmlFor={prefix + 'json'} hint="Limite de 64 KiB. Números são enviados exatamente como escritos.">
      <textarea id={prefix + 'json'} className="field json-field" value={step.configurationText} rows={12} spellCheck={false} disabled={locked}
        aria-describedby={prefix + 'json-hint'} onChange={e => onText(e.target.value)} />
    </Field> : parsed ? <ConfigurationFields prefix={prefix} value={parsed} locked={locked}
      change={value => onText(JSON.stringify(value, null, 2))} credential={step.credentialText} changeCredential={onCredential} />
      : <div className="inspector-note is-warning"><p>{problem}</p><button type="button" className="btn btn-small" onClick={() => onMode('json')}>Corrigir no JSON</button></div>}
    {!locked && <button type="button" className="btn btn-danger btn-small inspector-remove" onClick={onRemove}>
      Remover etapa {number}{relatedConnections > 0 ? (relatedConnections === 1 ? ' e 1 conexão' : ' e ' + relatedConnections + ' conexões') : ''}
    </button>}
  </section>
}

interface OverviewProps {
  prefix: string; steps: InspectorStep[]; connections: WorkflowConnection[]; locked: boolean
  onSelectStep: (nodeId: string) => void; onSelectConnection: (id: string) => void
  onRemoveConnection: (id: string) => void; onConnect: (candidate: ConnectionCandidate) => void
}

// Visão sem seleção: a lista é o caminho por teclado para tudo que o canvas faz com o mouse.
export function InspectorOverview({ prefix, steps, connections, locked, onSelectStep, onSelectConnection, onRemoveConnection, onConnect }: OverviewProps) {
  const name = (nodeId: string) => {
    const k = steps.findIndex(s => s.nodeId === nodeId)
    return k < 0 ? 'etapa ausente' : (k + 1) + '. ' + nodeTypeLabels[steps[k].type]
  }
  const outputs = steps.flatMap(s => outputPorts(s.type).map(port => ({ value: s.nodeId + '|' + port, label: name(s.nodeId) + (port === 'next' ? '' : ' (' + port + ')') })))
  const [source, setSource] = useState('')
  const [target, setTarget] = useState('')
  useEffect(() => {
    if (source && !outputs.some(o => o.value === source)) setSource('')
    if (target && !steps.some(s => s.nodeId === target)) setTarget('')
  }, [outputs, steps, source, target])
  return <section className="inspector" aria-labelledby={prefix + 'overview'}>
    <h3 id={prefix + 'overview'} className="inspector-section">Etapas</h3>
    {steps.length === 0 ? <p className="inspector-note">Nenhuma etapa ainda.</p> : <ol className="step-list">
      {steps.map((s, k) => <li key={s.nodeId}><button type="button" onClick={() => onSelectStep(s.nodeId)} aria-label={'Abrir etapa ' + (k + 1) + ': ' + nodeTypeLabels[s.type]}>
        <span className="step-list-index">{String(k + 1).padStart(2, '0')}</span><NodeGlyph type={s.type} size={18} />
        <span><strong>{nodeTypeLabels[s.type]}</strong><small>{describeText(s.configurationText)}</small></span>
      </button></li>)}
    </ol>}
    <h3 className="inspector-section">Conexões</h3>
    {connections.length === 0 ? <p className="inspector-note">Nenhuma conexão. Arraste de uma saída até a entrada de outra etapa.</p> : <ol className="connection-list">
      {connections.map((c, k) => <li key={c.id}>
        <button type="button" className="connection-link" onClick={() => onSelectConnection(c.id)}>
          <span>{name(c.sourceNodeId)}</span>{c.sourcePort !== 'next' && <span className={'port-chip port-' + c.sourcePort}>{c.sourcePort}</span>}<span aria-hidden="true" className="connection-arrow" /><span>{name(c.targetNodeId)}</span>
        </button>
        {!locked && <button type="button" className="btn btn-quiet btn-small" onClick={() => onRemoveConnection(c.id)}>Remover conexão {k + 1}</button>}
      </li>)}
    </ol>}
    {/* Fica dentro do formulário do editor, então não pode ser outro <form>. */}
    {!locked && steps.length > 1 && <div className="connect-form" role="group" aria-label="Nova conexão">
      <Field label="Ligar a saída" htmlFor={prefix + 'connect-from'}>
        <select id={prefix + 'connect-from'} className="field" value={source} onChange={e => setSource(e.target.value)}>
          <option value="">Escolha a origem</option>{outputs.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
      </Field>
      <Field label="à entrada de" htmlFor={prefix + 'connect-to'}>
        <select id={prefix + 'connect-to'} className="field" value={target} onChange={e => setTarget(e.target.value)}>
          <option value="">Escolha o destino</option>{steps.filter(s => s.type !== 'webhookTrigger').map(s => <option key={s.nodeId} value={s.nodeId}>{name(s.nodeId)}</option>)}
        </select>
      </Field>
      <button type="button" className="btn btn-small" disabled={!source || !target} onClick={() => {
        const [sourceNodeId, sourcePort] = source.split('|')
        onConnect({ sourceNodeId, sourcePort, targetNodeId: target })
      }}>Conectar</button>
    </div>}
  </section>
}

export function ConnectionInspector({ prefix, connection, index, steps, locked, onChange, onRemove, onClose }: {
  prefix: string; connection: WorkflowConnection; index: number; steps: InspectorStep[]; locked: boolean
  onChange: (update: Partial<WorkflowConnection>) => void; onRemove: () => void; onClose: () => void
}) {
  const source = steps.find(s => s.nodeId === connection.sourceNodeId)
  const ports = outputPorts(source?.type ?? 'log')
  const number = index + 1
  return <section className="inspector" aria-labelledby={prefix + 'connection'}>
    <header className="inspector-head">
      <button type="button" className="btn btn-quiet btn-small inspector-back" onClick={onClose}><span className="chevron" aria-hidden="true">‹</span>Todas as etapas</button>
      <h3 id={prefix + 'connection'}>Conexão {number}</h3>
    </header>
    <Field label={'Origem da conexão ' + number} htmlFor={prefix + 'source'}>
      <select id={prefix + 'source'} className="field" value={connection.sourceNodeId} disabled={locked} onChange={e => onChange({ sourceNodeId: e.target.value })}>
        {steps.map((s, k) => <option key={s.nodeId} value={s.nodeId}>{k + 1}. {nodeTypeLabels[s.type]}</option>)}
      </select>
    </Field>
    <Field label={'Porta da conexão ' + number} htmlFor={prefix + 'port'}>
      <select id={prefix + 'port'} className="field" value={connection.sourcePort} disabled={locked} onChange={e => onChange({ sourcePort: e.target.value })}>
        {ports.map(port => <option key={port} value={port}>{port}</option>)}
        {!ports.includes(connection.sourcePort) && <option value={connection.sourcePort}>{connection.sourcePort} (porta incompatível; revise)</option>}
      </select>
    </Field>
    <Field label={'Destino da conexão ' + number} htmlFor={prefix + 'target'}>
      <select id={prefix + 'target'} className="field" value={connection.targetNodeId} disabled={locked} onChange={e => onChange({ targetNodeId: e.target.value })}>
        {steps.map((s, k) => <option key={s.nodeId} value={s.nodeId}>{k + 1}. {nodeTypeLabels[s.type]}</option>)}
      </select>
    </Field>
    {!locked && <button type="button" className="btn btn-danger btn-small inspector-remove" onClick={onRemove}>Remover conexão {number}</button>}
  </section>
}
