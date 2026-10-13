import type { NodeConfiguration, WorkflowNode, WorkflowVersion } from '../api/contracts'
import { nodeTypeLabels } from '../workflows/templates'
import { formatDuration, nodeTone } from './format'
import { NodeGlyph } from './Ui'

const W = 184
const H = 74
const PAD = 30

interface Placed { node: WorkflowNode; index: number; x: number; y: number }

const operators: Record<string, string> = {
  exists: 'existe', equals: '=', notEquals: '≠', greaterThan: '>', greaterThanOrEqual: '≥', lessThan: '<', lessThanOrEqual: '≤',
}

function clip(text: string, max = 22) {
  return text.length > max ? text.slice(0, max - 1) + '…' : text
}

// Resumo de uma linha da configuração. É só leitura: a validação continua no backend.
function describe(configuration: NodeConfiguration) {
  switch (configuration.type) {
    case 'webhookTrigger': return 'entrada por webhook'
    case 'httpRequest': {
      let host = configuration.url
      try { host = new URL(configuration.url).host } catch { /* URL ainda inválida no rascunho */ }
      return configuration.method.toUpperCase() + ' ' + host
    }
    case 'delay': return 'aguarda ' + formatDuration(configuration.durationTicks / 10_000)
    case 'condition': return (configuration.sourcePointer || '(raiz)') + ' ' + operators[configuration.operation] +
      ('expectedValue' in configuration && configuration.expectedValue !== undefined ? ' ' + JSON.stringify(configuration.expectedValue) : '')
    case 'transformJson': return configuration.fields.length + (configuration.fields.length === 1 ? ' campo' : ' campos')
    case 'log': return '“' + configuration.message + '”'
  }
}

// A Fase 12 usa posições persistidas. A translação abaixo apenas enquadra
// coordenadas negativas no SVG; ela não modifica o grafo salvo.
function place(nodes: WorkflowNode[]): Placed[] {
  if (!nodes.length) return []
  const minX = Math.min(...nodes.map(n => n.position.x))
  const minY = Math.min(...nodes.map(n => n.position.y))
  return nodes.map((node, index) => ({ node, index, x: node.position.x - minX, y: node.position.y - minY }))
}

function sourcePort(p: Placed, port: string) {
  const ratio = port === 'true' ? .32 : port === 'false' ? .68 : .5
  return { x: p.x + W, y: p.y + H * ratio }
}

function wire(sx: number, sy: number, tx: number, ty: number) {
  if (tx > sx + 20) {
    const bend = Math.max(36, (tx - sx) / 2)
    return `M${sx},${sy} C${sx + bend},${sy} ${tx - bend},${ty} ${tx},${ty}`
  }
  // Conexão para trás: contorna por baixo para não atravessar os cards.
  const drop = Math.max(sy, ty) + H
  return `M${sx},${sy} C${sx + 70},${sy} ${sx + 70},${drop} ${(sx + tx) / 2},${drop} S${tx - 70},${ty} ${tx},${ty}`
}

export function WorkflowSchematic({ version }: { version: WorkflowVersion }) {
  const placed = place(version.nodes)
  const byId = new Map(placed.map(p => [p.node.nodeId, p]))
  const live = version.status === 'published'
  const hasBranches = version.connections.some(c => c.sourcePort !== 'next')
  const width = placed.length ? Math.max(...placed.map(p => p.x)) + W + PAD * 2 : 0
  const height = placed.length ? Math.max(...placed.map(p => p.y)) + H + PAD * 2 : 0
  const summary = version.nodes.length + (version.nodes.length === 1 ? ' etapa' : ' etapas') + ' e ' +
    version.connections.length + (version.connections.length === 1 ? ' conexão' : ' conexões')

  return <section className="panel schematic" aria-labelledby="schematic-title">
    <div className="schematic-caption">
      <div>
        <h2 id="schematic-title">Versão {version.versionNumber} {live ? 'em produção' : 'em rascunho'}</h2>
        <p>{summary}. {live ? 'É a versão que as próximas execuções vão usar.' : 'Publique para que as execuções passem a usá-la.'}</p>
      </div>
      <div className="schematic-legend" aria-hidden="true">
        {live && <span><i style={{ background: 'var(--sodium)' }} />sinal</span>}
        {hasBranches && <><span><i style={{ background: 'var(--ok)' }} />true</span><span><i style={{ background: 'var(--fail)' }} />false</span></>}
      </div>
    </div>
    {!placed.length ? <div className="schematic-empty"><strong>Nenhuma etapa ainda</strong>Comece por um dos exemplos ou adicione nodes no editor abaixo.</div> :
      <div className="schematic-canvas">
        <svg width={width} height={height} style={{ minWidth: Math.min(width, 640) }} viewBox={`${-PAD} ${-PAD} ${width} ${height}`} role="img" aria-label={'Esquemático da versão ' + version.versionNumber + ': ' + summary}>
          {version.connections.map(c => {
            const source = byId.get(c.sourceNodeId)
            const target = byId.get(c.targetNodeId)
            if (!source || !target) return null
            const from = sourcePort(source, c.sourcePort)
            const d = wire(from.x, from.y, target.x, target.y + H / 2)
            const tone = c.sourcePort === 'true' ? ' wire-true' : c.sourcePort === 'false' ? ' wire-false' : ''
            return <g key={c.id}>
              <path className={'wire' + tone} d={d} strokeDasharray={live ? undefined : '5 5'} />
              {live && <path className="wire-signal" d={d} />}
            </g>
          })}
          {placed.map(p => {
            const type = p.node.configuration.type
            const branches = version.connections.filter(c => c.sourceNodeId === p.node.nodeId && c.sourcePort !== 'next')
            const outgoing = version.connections.some(c => c.sourceNodeId === p.node.nodeId)
            return <g key={p.node.nodeId} transform={`translate(${p.x},${p.y})`}>
              <rect className="node-plate" width={W} height={H} rx="10" />
              <rect x="0" y="12" width="3" height={H - 24} rx="1.5" fill={nodeTone[type]} />
              <NodeGlyph type={type} size={20} x={16} y={14} />
              <text className="node-title" x="44" y="29">{nodeTypeLabels[type]}</text>
              <text className="node-index" x="2" y="-9">{String(p.index + 1).padStart(2, '0')}</text>
              <text className="node-detail" x="16" y="56">{clip(describe(p.node.configuration))}</text>
              {type !== 'webhookTrigger' && <circle className="port" cx="0" cy={H / 2} r="4" />}
              {branches.length > 0
                ? (['true', 'false'] as const).map(port => <g key={port}>
                  <circle className="port" cx={W} cy={H * (port === 'true' ? .32 : .68)} r="4" style={{ stroke: port === 'true' ? 'var(--ok)' : 'var(--fail)' }} />
                  <text className="port-label" x={W + 8} y={H * (port === 'true' ? .32 : .68) - 6} fill={port === 'true' ? 'var(--ok)' : 'var(--fail)'}>{port}</text>
                </g>)
                : outgoing && <circle className="port" cx={W} cy={H / 2} r="4" />}
            </g>
          })}
        </svg>
      </div>}
  </section>
}
