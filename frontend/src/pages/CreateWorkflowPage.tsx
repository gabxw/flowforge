import { useState } from 'react'
import * as api from '../api/client'
import { ErrorNotice } from '../components/Ui'
import { useCommand } from '../hooks/useCommand'

export function CreateWorkflowPage({ onDirtyChange }: { onDirtyChange: (dirty: boolean) => void }) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const command = useCommand()
  return <>
    <a className="back-link" href="#/workflows">← Workflows</a>
    <header className="page-head"><div><h1>Criar workflow</h1><p className="lead">O workflow começa como rascunho. Configure as etapas antes de publicar.</p></div></header>
    <section className="panel" style={{ maxWidth: '46rem' }}><ErrorNotice error={command.error} />
      <form onSubmit={e => {
        e.preventDefault()
        void command.run('Criando workflow…', signal => api.create({ name: name.trim(), description: description.trim() || null }, signal), workflow => {
          onDirtyChange(false)
          window.location.hash = '/workflows/' + workflow.id
        }).catch(() => undefined)
      }}>
        <label htmlFor="name">Nome</label><input id="name" className="field" maxLength={200} required value={name} placeholder="Ex.: Pedidos aprovados" onChange={e => { setName(e.target.value); onDirtyChange(Boolean(e.target.value || description)) }} disabled={!!command.busy} autoFocus />
        <label htmlFor="description">Descrição</label><textarea id="description" className="field" maxLength={2000} rows={4} value={description} placeholder="O que dispara este workflow e o que ele entrega" onChange={e => { setDescription(e.target.value); onDirtyChange(Boolean(name || e.target.value)) }} disabled={!!command.busy} />
        <div className="form-actions"><button className="btn btn-primary" disabled={!!command.busy || !name.trim()}>{command.busy ?? 'Criar workflow'}</button></div>
      </form></section>
  </>
}
