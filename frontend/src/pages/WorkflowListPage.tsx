import { useCallback, useState } from 'react'
import * as api from '../api/client'
import { useResource } from '../hooks/useResource'
import { Badge, ErrorNotice, Pager, Timestamp } from '../components/Ui'

export function WorkflowListPage() {
  const [offset, setOffset] = useState(0)
  const [archived, setArchived] = useState(false)
  const [executionId, setExecutionId] = useState('')
  const load = useCallback((signal: AbortSignal) => api.listWorkflows({ offset, includeArchived: archived }, signal), [offset, archived])
  const state = useResource(load)
  return <>
    <header className="page-heading"><div><p className="eyebrow">AUTOMAÇÕES</p><h1>Workflows</h1><p className="muted">Defina etapas, publique versões e acompanhe cada execução.</p></div>
      <a className="btn btn-primary" href="#/workflows/new">Novo workflow</a></header>
    <section className="card">
      <div className="toolbar"><label className="check"><input type="checkbox" checked={archived} onChange={e => { setArchived(e.target.checked); setOffset(0) }} /> Incluir arquivados</label>
        <button className="btn" onClick={state.refresh} disabled={state.loading}>Atualizar lista</button></div>
      <ErrorNotice error={state.error} retry={state.refresh} />
      {state.loading && <p role="status" className="muted">Carregando workflows…</p>}
      {state.data && !state.error && (state.data.items.length ? <div className="table-scroll"><table>
        <thead><tr><th>Workflow</th><th>Estado</th><th>Atualizado em</th><th>Revisão</th></tr></thead>
        <tbody>{state.data.items.map(w => <tr key={w.id}>
          <td><a className="strong-link" href={'#/workflows/' + w.id}>{w.name}</a><p className="table-description">{w.description ?? 'Sem descrição'}</p></td>
          <td><Badge status={w.archivedAt ? 'archived' : w.currentPublishedVersionId ? 'published' : 'draft'} /></td>
          <td><Timestamp value={w.updatedAt} /></td><td>{w.revision}</td>
        </tr>)}</tbody>
      </table></div> : <div className="empty"><h2>Nenhum workflow nesta página</h2><p>Comece com um Trigger e um Log para acompanhar sua primeira execução.</p><a className="btn" href="#/workflows/new">Criar primeiro workflow</a></div>)}
      {state.data && !state.error && <Pager offset={offset} count={state.data.items.length} loading={state.loading} change={setOffset} />}
    </section>
    <section className="card compact"><h2>Consultar execução por ID</h2><form className="inline-form" onSubmit={e => { e.preventDefault(); window.location.hash = '/executions/' + executionId.trim() }}>
      <label className="sr-only" htmlFor="execution-search">ID da execução</label><input id="execution-search" className="field monospace" placeholder="UUID da execução" value={executionId} onChange={e => setExecutionId(e.target.value)} required pattern="[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}" />
      <button className="btn">Consultar execução</button></form></section>
  </>
}
