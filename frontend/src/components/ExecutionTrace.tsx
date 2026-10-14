import type { CSSProperties } from 'react'
import type { NodeExecution, WorkflowExecution } from '../api/contracts'
import type { NodeType } from '../workflows/templates'
import { nodeTypeLabels } from '../workflows/templates'
import { formatDuration, statusLabel } from './format'
import { NodeGlyph } from './Ui'

const time = (value: string | null) => value ? new Date(value).getTime() : null

export interface StepInfo { index: number; type: NodeType; summary: string }

// Cascata no formato de tracing distribuído: a régua começa quando a execução
// foi aceita pela API, então a primeira faixa mostra quanto ela esperou na fila.
export function ExecutionTrace({ execution, nodes, now, step, highlight, onHighlight }: {
  execution: WorkflowExecution; nodes: NodeExecution[]; now: number
  step: (nodeId: string) => StepInfo | null
  highlight: string | null; onHighlight: (nodeId: string | null) => void
}) {
  const active = execution.status === 'pending' || execution.status === 'running'
  const start = time(execution.createdAt) ?? now
  const ends = nodes.map(n => time(n.finishedAt) ?? (n.startedAt && active ? now : time(n.startedAt)) ?? start)
  const end = time(execution.finishedAt) ?? Math.max(active ? now : start, ...ends)
  const span = Math.max(end - start, 1)
  const at = (value: number) => Math.min(100, Math.max(0, (value - start) / span * 100))
  const picked = time(execution.startedAt) ?? (active ? now : start)
  const queued = picked - start

  return <div className="trace" onMouseLeave={() => onHighlight(null)}>
    <div className="trace-axis" aria-hidden="true">
      {[0, .25, .5, .75, 1].map(f => <span key={f} style={{ left: f * 100 + '%' }}>{f === 0 ? '0' : formatDuration(span * f)}</span>)}
    </div>
    <span aria-hidden="true" />
    <div className="trace-row">
      <div className="trace-label"><span className="trace-name"><span className="trace-queue-mark" aria-hidden="true" /><strong>Espera na fila</strong></span><small>até um Worker assumir</small></div>
      <div className="trace-track"><span className="trace-bar queue" role="img" aria-label={'Espera na fila, ' + formatDuration(queued)}
        style={{ left: 0, width: Math.max(at(picked), .4) + '%', '--order': 0 } as CSSProperties} /></div>
      <span className="trace-duration">{formatDuration(queued)}</span>
    </div>
    {nodes.map((node, order) => {
      const info = step(node.nodeId)
      const from = time(node.startedAt)
      const to = time(node.finishedAt) ?? (from !== null && active ? now : from)
      const duration = from !== null && to !== null ? to - from : null
      const title = info ? nodeTypeLabels[info.type] : 'Etapa removida'
      return <div className={'trace-row' + (highlight === node.nodeId ? ' is-highlighted' : '')} key={node.id}
        onMouseEnter={() => onHighlight(node.nodeId)}>
        <div className="trace-label">
          <span className="trace-name">{info && <NodeGlyph type={info.type} size={16} />}<strong>{title}</strong></span>
          <small>{info ? 'Etapa ' + (info.index + 1) + ', ' + info.summary : node.nodeId.slice(0, 8)}</small>
        </div>
        <div className="trace-track">
          {from !== null && to !== null && <span
            className={'trace-bar state-' + node.status}
            role="img"
            aria-label={title + ': ' + statusLabel(node.status) + ', ' + formatDuration(duration ?? 0)}
            style={{ left: at(from) + '%', width: Math.max(at(to) - at(from), .4) + '%', '--order': order + 1 } as CSSProperties}
          />}
          {node.status === 'skipped' && <span className="trace-skip">ignorada</span>}
        </div>
        <span className="trace-duration">{duration === null ? '—' : formatDuration(duration)}</span>
      </div>
    })}
  </div>
}
