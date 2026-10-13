import { useCallback, useEffect, useRef, useState } from 'react'
import * as api from '../api/client'
import type { WorkflowDetail } from '../api/contracts'
import { useResource } from '../hooks/useResource'
import { useCommand } from '../hooks/useCommand'
import { Badge, ErrorNotice, Pager, Timestamp } from '../components/Ui'
import WorkflowEditor from '../components/WorkflowEditor'
import { WorkflowSchematic } from '../components/WorkflowSchematic'

export function WorkflowPage({ id, onDirtyChange }: { id: string; onDirtyChange: (dirty: boolean) => void }) {
  const load = useCallback((signal: AbortSignal) => api.getWorkflow(id, signal), [id])
  const state = useResource(load)
  return <>
    <a className="back-link" href="#/workflows">← Voltar aos workflows</a>
    {state.loading && !state.data && <p role="status" className="muted">Carregando workflow…</p>}
    <ErrorNotice error={state.error} retry={state.data ? undefined : state.refresh} />
    {state.data && <WorkflowWorkspace workflow={state.data} accept={state.setData} onDirtyChange={onDirtyChange} />}
  </>
}

function WorkflowWorkspace({ workflow, accept, onDirtyChange }: {
  workflow: WorkflowDetail; accept: (value: WorkflowDetail) => void; onDirtyChange: (value: boolean) => void
}) {
  const [name, setName] = useState(workflow.name)
  const [description, setDescription] = useState(workflow.description ?? '')
  const [graphDirty, setGraphDirty] = useState(false)
  const [metaDirty, setMetaDirty] = useState(false)
  const [editorGeneration, setEditorGeneration] = useState(0)
  const [notice, setNotice] = useState('')
  const dirty = useRef({ graph: false, metadata: false })
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

  return <>
    <header className="page-head">
      <div><h1>{workflow.name}</h1><p className="lead">{workflow.description || 'Automação sem descrição.'}</p></div>
      <div className="head-meta"><span className="muted small numeric">Revisão {workflow.revision}</span><Badge status={archived ? 'archived' : published ? 'published' : 'draft'} /></div>
    </header>

    {shown && <WorkflowSchematic version={shown} />}
    {shown && graphDirty && <p className="muted small" style={{ margin: '-.75rem 0 1.5rem' }}>O esquemático mostra a estrutura salva. As alterações locais aparecem nele depois de salvar o rascunho.</p>}

    <div className="layout-split">
      <div>
        <section className="panel">
          <div className="panel-head"><h2>Detalhes do workflow</h2></div>
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
          <div className="panel-head"><div><h2>Versões e etapas</h2><p>Edite o rascunho. Execuções usam a versão publicada.</p></div>
            <label className="select-label" style={{ minWidth: '13rem' }}><span>Versão</span><select className="field" value={shown?.id ?? ''} disabled={busy} onChange={e => chooseVersion(e.target.value)}>
              {workflow.versions.map(v => <option key={v.id} value={v.id}>Versão {v.versionNumber}, {v.status === 'draft' ? 'rascunho' : 'publicada'}</option>)}
            </select></label></div>
          {shown && <WorkflowEditor key={shown.id + '-' + editorGeneration} version={shown} disabled={archived || shown.id !== workflow.draftVersionId || busy} saving={command.busy === 'Salvando rascunho…'} readOnly={archived || shown.id !== workflow.draftVersionId}
            onDirtyChange={reportGraphDirty} onSave={async (nodes, connections) => {
              await change('Salvando rascunho…', 'Rascunho salvo.', signal => api.saveDraft(workflow.id, { expectedRevision: workflow.revision, nodes, connections }, signal))
            }} />}
        </section>
      </div>

      <aside aria-label="Publicação e execução">
        <section className="panel">
          <div className="panel-head" style={{ marginBottom: '1rem' }}><div><h2>Publicar e executar</h2>
            <p>{published ? 'Em produção: versão ' + published.versionNumber + '.' : 'Nenhuma versão publicada ainda.'}</p></div></div>
          {command.busy && <p role="status" className="notice">{command.busy}</p>}
          {notice && <p className="notice notice-success" role="status">{notice}</p>}
          <ErrorNotice error={command.error} />
          {command.error instanceof api.ApiError && command.error.problem.status === 409 && <div className="notice">
            <p>Confira a revisão ou o estado no servidor antes de reaplicar a edição. Sua estrutura local foi preservada.</p>
            <button className="btn" disabled={busy} onClick={reload}>Recarregar versão do servidor</button>
          </div>}
          <div className="stack-actions">
            {!archived && draft && <button className="btn btn-primary btn-block" disabled={busy || graphDirty || metaDirty} onClick={() => {
              void change('Publicando…', 'Versão publicada.', signal => api.publish(workflow.id, workflow.revision, signal))
                .then(() => { setSelectedVersion(null) }).catch(() => undefined)
            }}>Publicar versão</button>}
            {!archived && !draft && <button className="btn btn-block" disabled={busy || metaDirty} onClick={() => {
              void change('Abrindo rascunho…', 'Novo rascunho criado.', signal => api.createDraft(workflow.id, workflow.revision, signal))
                .then(() => setSelectedVersion(null)).catch(() => undefined)
            }}>Abrir novo rascunho</button>}
            {!archived && <button className={'btn btn-block' + (draft ? '' : ' btn-primary')} disabled={busy || !published || metaDirty || graphDirty} onClick={() => {
              setNotice('')
              void command.run('Solicitando execução…', signal => api.requestExecution(workflow.id, signal), value => { window.location.hash = '/executions/' + value.id }).catch(() => undefined)
            }}>Executar workflow</button>}
            <button className="btn btn-quiet btn-block" onClick={reload} disabled={busy}>Recarregar workflow</button>
          </div>
          {published && <p className="muted small" style={{ marginTop: '1rem' }}>Execução manual inicia a versão {published.versionNumber} com input vazio <code>{'{}'}</code>. Eventos com payload entram pelo webhook.</p>}
          {(graphDirty || metaDirty) && <p className="muted small" style={{ marginTop: '.6rem' }}>Há alterações locais não salvas. Salve antes de publicar ou executar.</p>}
        </section>
        <section className="panel compact">
          <details><summary>Identificação e datas</summary><dl className="metadata" style={{ gridTemplateColumns: '110px minmax(0, 1fr)', marginTop: '.9rem' }}><dt>Workflow ID</dt><dd className="monospace">{workflow.id}</dd><dt>Criado em</dt><dd><Timestamp value={workflow.createdAt} /></dd><dt>Atualizado em</dt><dd><Timestamp value={workflow.updatedAt} /></dd></dl></details>
          {!archived && <div className="form-actions"><button className="btn btn-danger" disabled={busy || graphDirty || metaDirty} onClick={() => {
            if (!window.confirm('Arquivar encerra a edição e impede novas execuções. O histórico será preservado. Deseja continuar?')) return
            void change('Arquivando…', 'Workflow arquivado. Histórico preservado.', signal => api.archive(workflow.id, workflow.revision, signal)).catch(() => undefined)
          }}>Arquivar workflow</button></div>}
        </section>
      </aside>
    </div>

    <ExecutionHistory workflowId={workflow.id} />
  </>
}

function ExecutionHistory({ workflowId }: { workflowId: string }) {
  const [offset, setOffset] = useState(0)
  const load = useCallback((signal: AbortSignal) => api.listExecutions(workflowId, offset, signal), [workflowId, offset])
  const state = useResource(load)
  return <section className="panel"><div className="panel-head"><div><h2>Histórico de execuções</h2><p>Abra uma execução para ver o trace de cada etapa.</p></div><button className="btn btn-quiet" disabled={state.loading} onClick={state.refresh}>Atualizar histórico</button></div>
    <ErrorNotice error={state.error} retry={state.refresh} />
    {state.loading && <p role="status" className="muted">Carregando histórico…</p>}
    {state.data && !state.error && (state.data.items.length ? <div className="table-scroll"><table><thead><tr><th>Execução</th><th>Estado</th><th>Criada em</th><th>Finalizada em</th></tr></thead>
      <tbody>{state.data.items.map(e => <tr key={e.id}><td><a className="monospace strong-link" href={'#/executions/' + e.id}>{e.id}</a></td><td><Badge status={e.status} /></td><td className="muted"><Timestamp value={e.createdAt} /></td><td className="muted"><Timestamp value={e.finishedAt} /></td></tr>)}</tbody></table></div> : <p className="empty">Nenhuma execução registrada para este workflow.</p>)}
    {state.data && !state.error && <Pager offset={offset} count={state.data.items.length} loading={state.loading} change={setOffset} />}
  </section>
}
