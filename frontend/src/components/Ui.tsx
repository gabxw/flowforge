import { ApiError } from '../api/client'

const labels: Record<string, string> = {
  pending: 'Pendente', running: 'Em execução', succeeded: 'Concluída', failed: 'Falhou',
  cancelled: 'Cancelada', retrying: 'Aguardando nova tentativa', skipped: 'Ignorado',
  interrupted: 'Interrompida', draft: 'Rascunho', published: 'Publicada', archived: 'Arquivado',
}
export function Badge({ status }: { status: string }) {
  return <span className={'badge badge-' + status}>{labels[status] ?? status}</span>
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
      <li key={index}>{e.message}{e.nodeId ? ' · Node ' + e.nodeId : ''}{e.connectionId ? ' · Conexão ' + e.connectionId : ''}</li>)}</ul> : null}
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
