import { useCallback, useEffect, useId, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import * as api from '../api/client'
import type { WorkflowDetail } from '../api/contracts'
import { useResource } from '../hooks/useResource'
import { useCommand } from '../hooks/useCommand'
import { Badge, ErrorNotice, Pager, Timestamp } from '../components/Ui'
import { formatDuration } from '../components/format'
import WorkflowEditor from '../components/WorkflowEditor'

type Tab = 'graph' | 'executions' | 'settings'
const tabs: { id: Tab; label: string }[] = [{ id: 'graph', label: 'Grafo' }, { id: 'executions', label: 'Execuções' }, { id: 'settings', label: 'Configurações' }]

export function WorkflowPage({ id, onDirtyChange }: { id: string; onDirtyChange: (dirty: boolean) => void }) {
  const load = useCallback((signal: AbortSignal) => api.getWorkflow(id, signal), [id])
  const state = useResource(load)
  return <>
    <a className="back-link" href="#/workflows">← Workflows</a>
    {state.loading && !state.data && <p role="status" className="muted">Carregando workflow…</p>}
    <ErrorNotice error={state.error} retry={state.data ? undefined : state.refresh} />
    {state.data && <WorkflowWorkspace workflow={state.data} accept={state.setData} onDirtyChange={onDirtyChange} />}
  </>
}

function WorkflowWorkspace({ workflow, accept, onDirtyChange }: {
  workflow: WorkflowDetail; accept: (value: WorkflowDetail) => void; onDirtyChange: (value: boolean) => void
}) {
  const tabsId = useId()
  const [tab, setTab] = useState<Tab>('graph')
  const [visited, setVisited] = useState<Set<Tab>>(() => new Set(['graph']))
  const [historyGeneration, setHistoryGeneration] = useState(0)
  const [name, setName] = useState(workflow.name)
  const [description, setDescription] = useState(workflow.description ?? '')
  const [graphDirty, setGraphDirty] = useState(false)
  const [metaDirty, setMetaDirty] = useState(false)
  const [editorGeneration, setEditorGeneration] = useState(0)
  const [notice, setNotice] = useState('')
  const dirty = useRef({ graph: false, metadata: false })
  const tabRefs = useRef<Record<Tab, HTMLButtonElement | null>>({ graph: null, executions: null, settings: null })
  const command = useCommand()
  const draft = workflow.versions.find(v => v.id === workflow.draftVersionId)
  const published = workflow.versions.find(v => v.id === workflow.currentPublishedVersionId)
  const [selectedVersion, setSelectedVersion] = useState<string | null>(null)
  const shown = workflow.versions.find(v => v.id === selectedVersion) ?? draft ?? published ?? workflow.versions[0]
  const archived = !!workflow.archivedAt
  const busy = !!command.busy

  const reportGraphDirty = useCallback((value: boolean) => {
    dirty.current.graph = value
    setGraphDirty(value)
    onDirtyChange(value || dirty.current.metadata)
  }, [onDirtyChange])
  function reportMetadata(n: string, d: string) {
    const value = n !== workflow.name || d !== (workflow.description ?? '')
    dirty.current.metadata = value
    setMetaDirty(value)
    onDirtyChange(value || dirty.current.graph)
  }
  useEffect(() => () => onDirtyChange(false), [onDirtyChange])

  function open(next: Tab, focus = false) {
    setTab(next)
    setVisited(current => current.has(next) ? current : new Set(current).add(next))
    if (next === 'executions' && visited.has('executions')) setHistoryGeneration(n => n + 1)
    if (focus) tabRefs.current[next]?.focus()
  }
  function tabKeys(event: KeyboardEvent<HTMLButtonElement>) {
    const index = tabs.findIndex(t => t.id === tab)
    const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0
    if (event.key === 'Home') { event.preventDefault(); open(tabs[0].id, true) }
    else if (event.key === 'End') { event.preventDefault(); open(tabs[tabs.length - 1].id, true) }
    else if (step) { event.preventDefault(); open(tabs[(index + step + tabs.length) % tabs.length].id, true) }
  }

  async function change(label: string, message: string, action: (signal: AbortSignal) => Promise<WorkflowDetail>) {
    setNotice('')
    return command.run(label, action, value => { accept(value); setNotice(message) })
  }
  function reload() {
    if ((graphDirty || metaDirty) && !window.confirm('Recarregar descarta as alterações locais não salvas. Deseja continuar?')) return
    void change('Recarregando…', 'Dados atualizados do servidor.', signal => api.getWorkflow(workflow.id, signal))
      .then(value => {
        setName(value.name); setDescription(value.description ?? '')
        dirty.current = { graph: false, metadata: false }
        setMetaDirty(false); setGraphDirty(false); onDirtyChange(false)
        setSelectedVersion(null); setEditorGeneration(n => n + 1)
      }).catch(() => undefined)
  }
  function chooseVersion(value: string) {
    if (graphDirty && !window.confirm('Trocar de versão descarta a estrutura local não salva. Deseja continuar?')) return
    reportGraphDirty(false)
    setSelectedVersion(value)
  }

  const pending = graphDirty || metaDirty
  const editing = !!shown && shown.id === workflow.draftVersionId && !archived
  const versionCaption = !shown ? '' : archived ? 'Workflow arquivado. As versões ficam disponíveis só para consulta.'
    : shown.status === 'draft' ? (published ? 'Rascunho em edição. Execuções continuam usando a versão ' + published.versionNumber + ' até você publicar.' : 'Rascunho em edição. Publique para que o workflow possa ser executado.')
    : shown.id === published?.id ? 'É a versão que as próximas execuções vão usar. Para mudar algo, abra um novo rascunho.' : 'Versão anterior, mantida para consulta do histórico.'

  return <>
    <header className="workflow-head">
      <div className="workflow-title">
        <h1>{workflow.name}</h1>
        <p className="lead">{workflow.description || 'Automação sem descrição.'}</p>
      </div>
      <div className="workflow-actions">
        <div className="head-meta"><Badge status={archived ? 'archived' : published ? 'published' : 'draft'} /></div>
        {!archived && <div className="action-group">
          {draft ? <button className="btn btn-primary" disabled={busy || pending} onClick={() => {
            void change('Publicando…', 'Versão publicada.', signal => api.publish(workflow.id, workflow.revision, signal))
              .then(() => { setSelectedVersion(null) }).catch(() => undefined)
          }}>Publicar versão</button>
            : <button className="btn" disabled={busy || metaDirty} onClick={() => {
              void change('Abrindo rascunho…', 'Novo rascunho criado.', signal => api.createDraft(workflow.id, workflow.revision, signal))
                .then(() => { setSelectedVersion(null); open('graph') }).catch(() => undefined)
            }}>Abrir novo rascunho</button>}
          <button className={'btn' + (draft ? '' : ' btn-primary')} disabled={busy || !published || pending} title={published ? 'Inicia a versão ' + published.versionNumber + ' com input vazio {}. Eventos com payload entram pelo webhook.' : 'Publique uma versão antes de executar.'} onClick={() => {
            setNotice('')
            void command.run('Solicitando execução…', signal => api.requestExecution(workflow.id, signal), value => { window.location.hash = '/executions/' + value.id }).catch(() => undefined)
          }}>Executar workflow</button>
        </div>}
      </div>
    </header>

    <div className="workspace-feedback" aria-live="polite">
      {command.busy && <p role="status" className="notice">{command.busy}</p>}
      {notice && <p className="notice notice-success" role="status">{notice}</p>}
      {pending && !command.busy && <p className="muted small">Há alterações locais não salvas. Salve antes de publicar ou executar.</p>}
    </div>
    <ErrorNotice error={command.error} />
    {command.error instanceof api.ApiError && command.error.problem.status === 409 && <div className="notice">
      <p>Confira a revisão ou o estado no servidor antes de reaplicar a edição. Sua estrutura local foi preservada.</p>
      <button className="btn" disabled={busy} onClick={reload}>Recarregar versão do servidor</button>
    </div>}

    <div className="tabs" role="tablist" aria-label="Seções do workflow">
      {tabs.map(t => <button key={t.id} ref={el => { tabRefs.current[t.id] = el }} role="tab" type="button" id={tabsId + t.id}
        aria-selected={tab === t.id} aria-controls={tabsId + t.id + '-panel'} tabIndex={tab === t.id ? 0 : -1}
        onClick={() => open(t.id)} onKeyDown={tabKeys}>{t.label}{t.id === 'graph' && graphDirty && <><span className="tab-dot" aria-hidden="true" /><span className="sr-only">, com alterações não salvas</span></>}</button>)}
    </div>

    <section role="tabpanel" id={tabsId + 'graph-panel'} aria-labelledby={tabsId + 'graph'} hidden={tab !== 'graph'} className="tab-panel">
      <div className="version-bar">
        <div><h2>{shown ? 'Versão ' + shown.versionNumber + (shown.status === 'draft' ? ', rascunho' : shown.id === published?.id ? ', em produção' : ', publicada') : 'Sem versões'}</h2><p>{versionCaption}</p></div>
        {workflow.versions.length > 1 && <label className="select-label version-select"><span>Versão</span><select className="field field-compact" value={shown?.id ?? ''} disabled={busy} onChange={e => chooseVersion(e.target.value)}>
          {workflow.versions.map(v => <option key={v.id} value={v.id}>Versão {v.versionNumber}, {v.status === 'draft' ? 'rascunho' : v.id === published?.id ? 'em produção' : 'publicada'}</option>)}
        </select></label>}
      </div>
      {shown && <WorkflowEditor key={shown.id + '-' + editorGeneration} version={shown} disabled={!editing || busy} saving={command.busy === 'Salvando rascunho…'} readOnly={!editing}
        onDirtyChange={reportGraphDirty} onSave={async (nodes, connections) => {
          await change('Salvando rascunho…', 'Rascunho salvo.', signal => api.saveDraft(workflow.id, { expectedRevision: workflow.revision, nodes, connections }, signal))
        }} />}
    </section>

    <section role="tabpanel" id={tabsId + 'executions-panel'} aria-labelledby={tabsId + 'executions'} hidden={tab !== 'executions'} className="tab-panel">
      {visited.has('executions') && <ExecutionHistory key={historyGeneration} workflowId={workflow.id} />}
    </section>

    <section role="tabpanel" id={tabsId + 'settings-panel'} aria-labelledby={tabsId + 'settings'} hidden={tab !== 'settings'} className="tab-panel settings">
      <section className="panel">
        <div className="panel-head"><div><h2>Detalhes</h2><p>Nome e descrição aparecem na lista de workflows.</p></div></div>
        <form onSubmit={e => {
          e.preventDefault()
          void change('Salvando detalhes…', 'Detalhes salvos.', signal => api.updateMetadata(workflow.id, { expectedRevision: workflow.revision, name: name.trim(), description: description.trim() || null }, signal))
            .then(value => { setName(value.name); setDescription(value.description ?? ''); dirty.current.metadata = false; setMetaDirty(false); onDirtyChange(dirty.current.graph) })
            .catch(() => undefined)
        }}>
          <div className="form-grid"><div><label htmlFor="workflow-name">Nome</label><input className="field" id="workflow-name" maxLength={200} required value={name} disabled={archived || busy} onChange={e => { setName(e.target.value); reportMetadata(e.target.value, description) }} /></div>
            <div><label htmlFor="workflow-description">Descrição</label><textarea className="field" id="workflow-description" rows={2} maxLength={2000} value={description} disabled={archived || busy} onChange={e => { setDescription(e.target.value); reportMetadata(name, e.target.value) }} /></div></div>
          {!archived && <div className="form-actions"><button className="btn" disabled={busy || !metaDirty || !name.trim()}>Salvar detalhes</button></div>}
        </form>
      </section>
      <section className="panel">
        <div className="panel-head"><div><h2>Identificação</h2><p>Use o ID para consultar a API ou cruzar com traces.</p></div>
          <button className="btn btn-quiet btn-small" onClick={reload} disabled={busy}>Recarregar workflow</button></div>
        <dl className="metadata"><dt>Workflow ID</dt><dd className="monospace">{workflow.id}</dd><dt>Revisão</dt><dd className="numeric">{workflow.revision}</dd><dt>Criado em</dt><dd><Timestamp value={workflow.createdAt} /></dd><dt>Atualizado em</dt><dd><Timestamp value={workflow.updatedAt} /></dd>{archived && <><dt>Arquivado em</dt><dd><Timestamp value={workflow.archivedAt} /></dd></>}</dl>
      </section>
      {!archived && <section className="panel danger-zone">
        <div className="panel-head"><div><h2>Arquivar</h2><p>Encerra a edição e impede novas execuções. O histórico continua disponível.</p></div>
          <button className="btn btn-danger" disabled={busy || pending} onClick={() => {
            if (!window.confirm('Arquivar encerra a edição e impede novas execuções. O histórico será preservado. Deseja continuar?')) return
            void change('Arquivando…', 'Workflow arquivado. Histórico preservado.', signal => api.archive(workflow.id, workflow.revision, signal)).catch(() => undefined)
          }}>Arquivar workflow</button></div>
      </section>}
    </section>
  </>
}

function ExecutionHistory({ workflowId }: { workflowId: string }) {
  const [offset, setOffset] = useState(0)
  const load = useCallback((signal: AbortSignal) => api.listExecutions(workflowId, offset, signal), [workflowId, offset])
  const state = useResource(load)
  return <section className="panel"><div className="panel-head"><div><h2>Histórico de execuções</h2><p>Abra uma execução para ver o caminho percorrido e o tempo de cada etapa.</p></div><button className="btn btn-quiet btn-small" disabled={state.loading} onClick={state.refresh}>Atualizar histórico</button></div>
    <ErrorNotice error={state.error} retry={state.refresh} />
    {state.loading && !state.data && <p role="status" className="muted">Carregando histórico…</p>}
    {state.data && !state.error && (state.data.items.length ? <table className="history">
      <thead><tr><th>Execução</th><th>Estado</th><th>Duração</th><th>Iniciada em</th></tr></thead>
      <tbody>{state.data.items.map(e => {
        const ran = e.startedAt && e.finishedAt ? new Date(e.finishedAt).getTime() - new Date(e.startedAt).getTime() : null
        return <tr key={e.id}>
          <td><a className="monospace strong-link" href={'#/executions/' + e.id} title={e.id}>{e.id.slice(0, 8)}</a></td>
          <td><Badge status={e.status} /></td>
          <td className="numeric" data-label="Duração">{ran === null ? '—' : formatDuration(ran)}</td>
          <td className="muted" data-label="Iniciada em"><Timestamp value={e.startedAt ?? e.createdAt} /></td>
        </tr>
      })}</tbody></table> : <p className="empty">Nenhuma execução ainda. Publique uma versão e use Executar workflow ou envie um evento ao webhook.</p>)}
    {state.data && !state.error && <Pager offset={offset} count={state.data.items.length} loading={state.loading} change={setOffset} />}
  </section>
}
