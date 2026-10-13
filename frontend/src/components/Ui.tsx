import { ApiError } from '../api/client'
import type { NodeType } from '../workflows/templates'
import { nodeTone, statusLabel } from './format'

export function Badge({ status }: { status: string }) {
  return <span className={'badge badge-' + status}>{statusLabel(status)}</span>
}
export function Timestamp({ value }: { value: string | null }) {
  if (!value) return <span className="muted">—</span>
  return <time dateTime={value}>{new Date(value).toLocaleString('pt-BR')}</time>
}

export function ErrorNotice({ error, retry }: { error: unknown; retry?: () => void }) {
  if (!error) return null
  const problem = error instanceof ApiError ? error.problem : null
  return <div className="notice notice-error" role="alert">
    <strong>{problem?.title ?? 'Não foi possível concluir a operação.'}</strong>
    <p>{problem?.detail ?? 'Verifique os dados e tente novamente.'}</p>
    {problem?.errors.length ? <ul>{problem.errors.map((e, index) =>
      <li key={index}>{e.message}{e.nodeId ? ' (node ' + e.nodeId + ')' : ''}{e.connectionId ? ' (conexão ' + e.connectionId + ')' : ''}</li>)}</ul> : null}
    {retry && <button type="button" className="btn" onClick={retry}>Tentar novamente</button>}
  </div>
}
export function Pager({ offset, count, loading, change }: { offset: number; count: number; loading: boolean; change: (offset: number) => void }) {
  return <nav className="pager" aria-label="Paginação">
    <span className="muted">{count ? 'Registros ' + (offset + 1) + '–' + (offset + count) : 'Nenhum registro nesta página'}</span>
    <button className="btn" disabled={loading || offset === 0} onClick={() => change(Math.max(0, offset - 20))}>Anterior</button>
    <button className="btn" disabled={loading || count < 20} onClick={() => change(offset + 20)}>Próxima</button>
  </nav>
}

// Glifos desenhados em grade 20×20; usados no esquemático (SVG aninhado) e no editor.
const glyphPaths: Record<NodeType, string> = {
  webhookTrigger: 'M3 10h9M8.5 6.5 12 10l-3.5 3.5M14.5 4v12',
  httpRequest: 'M10 3a7 7 0 1 0 0 14a7 7 0 1 0 0-14M3 10h14M10 3c-2.4 2-2.4 12 0 14M10 3c2.4 2 2.4 12 0 14',
  delay: 'M6 3h8M6 17h8M7 3c0 4 6 4 6 7s-6 3-6 7M13 3c0 4-6 4-6 7s6 3 6 7',
  condition: 'M10 3l7 7-7 7-7-7zM10 7v6',
  transformJson: 'M7 3.5c-2 0-2 1.5-2 3s-.5 3.5-2 3.5c1.5 0 2 2 2 3.5s0 3 2 3M13 3.5c2 0 2 1.5 2 3s.5 3.5 2 3.5c-1.5 0-2 2-2 3.5s0 3-2 3',
  log: 'M4 5h12M4 10h12M4 15h7',
}
export function NodeGlyph({ type, size = 20, x, y }: { type: NodeType; size?: number; x?: number; y?: number }) {
  return <svg width={size} height={size} x={x} y={y} viewBox="0 0 20 20" fill="none" stroke={nodeTone[type]} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d={glyphPaths[type]} />
  </svg>
}
