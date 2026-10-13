import type { CSSProperties } from 'react'
import type { NodeExecution, WorkflowExecution } from '../api/contracts'
import { formatDuration, statusLabel } from './format'

const time = (value: string | null) => value ? new Date(value).getTime() : null

// Cascata no formato de tracing distribuído: a régua começa quando a execução
// foi aceita pela API, então a primeira faixa mostra quanto ela esperou na fila.
export function ExecutionTrace({ execution, nodes, now }: { execution: WorkflowExecution; nodes: NodeExecution[]; now: number }) {
  const active = execution.status === 'pending' || execution.status === 'running'
  const start = time(execution.createdAt) ?? now
  const ends = nodes.map(n => time(n.finishedAt) ?? (n.startedAt && active ? now : time(n.startedAt)) ?? start)
  const end = time(execution.finishedAt) ?? Math.max(active ? now : start, ...ends)
  const span = Math.max(end - start, 1)
  const at = (value: number) => Math.min(100, Math.max(0, (value - start) / span * 100))
  const picked = time(execution.startedAt) ?? (active ? now : start)

  const rows = [
    { key: 'queue', title: 'Espera na fila', detail: 'até um Worker assumir', from: start, to: picked, className: 'queue', label: 'Espera na fila' },
    ...nodes.map((node, index) => {
      const from = time(node.startedAt)
      const to = time(node.finishedAt) ?? (from !== null && active ? now : from)
      return {
        key: node.id, title: 'Etapa ' + (index + 1), detail: node.nodeId.slice(0, 8), from, to,
        className: 'state-' + node.status, label: 'Etapa ' + (index + 1) + ': ' + statusLabel(node.status),
      }
    }),
  ]

  return <div className="trace">
    <div className="trace-axis" aria-hidden="true">
      {[0, .25, .5, .75, 1].map(f => <span key={f} style={{ left: f * 100 + '%' }}>{f === 0 ? '0' : formatDuration(span * f)}</span>)}
    </div>
    <span aria-hidden="true" />
    {rows.map((row, order) => {
      const duration = row.from !== null && row.to !== null ? row.to - row.from : null
      return <div className="trace-row" key={row.key}>
        <div className="trace-label"><strong>{row.title}</strong><code>{row.detail}</code></div>
        <div className="trace-track">
          {row.from !== null && row.to !== null && <span
            className={'trace-bar ' + row.className}
            role="img"
            aria-label={row.label + ', ' + formatDuration(duration ?? 0)}
            style={{ left: at(row.from) + '%', width: Math.max(at(row.to) - at(row.from), .4) + '%', '--order': order } as CSSProperties}
          />}
        </div>
        <span className="trace-duration">{duration === null ? '—' : formatDuration(duration)}</span>
      </div>
    })}
  </div>
}
