import { useCallback, useEffect, useRef, useState } from 'react'
import { WorkflowListPage } from './pages/WorkflowListPage'
import { CreateWorkflowPage } from './pages/CreateWorkflowPage'
import { WorkflowPage } from './pages/WorkflowPage'
import { ExecutionPage } from './pages/ExecutionPage'

const uuid = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i

export default function App() {
  const [hash, setHash] = useState(() => window.location.hash || '#/workflows')
  const current = useRef(hash)
  const dirty = useRef(false)
  const onDirtyChange = useCallback((value: boolean) => { dirty.current = value }, [])
  useEffect(() => {
    function change() {
      if (dirty.current && !window.confirm('Há alterações não salvas. Deseja sair e descartá-las?')) {
        window.history.replaceState(null, '', current.current)
        return
      }
      dirty.current = false
      current.current = window.location.hash || '#/workflows'
      setHash(current.current)
      window.scrollTo(0, 0)
    }
    function unload(event: BeforeUnloadEvent) {
      if (dirty.current) { event.preventDefault(); event.returnValue = '' }
    }
    window.addEventListener('hashchange', change)
    window.addEventListener('beforeunload', unload)
    return () => { window.removeEventListener('hashchange', change); window.removeEventListener('beforeunload', unload) }
  }, [])

  const path = hash.replace(/^#/, '').split('/').filter(Boolean)
  let page
  if (!path.length || (path.length === 1 && path[0] === 'workflows')) page = <WorkflowListPage />
  else if (path.length === 2 && path[0] === 'workflows' && path[1] === 'new') page = <CreateWorkflowPage onDirtyChange={onDirtyChange} />
  else if (path.length === 2 && path[0] === 'workflows' && uuid.test(path[1])) page = <WorkflowPage id={path[1]} onDirtyChange={onDirtyChange} />
  else if (path.length === 2 && path[0] === 'executions' && uuid.test(path[1])) page = <ExecutionPage id={path[1]} />
  else page = <section className="card empty"><h1>Página não encontrada</h1><p>Confira o endereço ou o ID informado.</p><a className="btn" href="#/workflows">Voltar aos workflows</a></section>

  return <div className="app-layout">
    <a href="#main" className="skip-link" onClick={e => { e.preventDefault(); document.getElementById('main')?.focus() }}>Pular para o conteúdo</a>
    <aside className="sidebar"><a href="#/workflows" className="brand"><span className="brand-mark" aria-hidden="true">F</span>FlowForge</a>
      <p className="sidebar-caption">AUTOMAÇÃO DE WORKFLOWS</p>
      <nav aria-label="Navegação principal"><a href="#/workflows" className={path[0] !== 'executions' ? 'nav-active' : ''}>Workflows <span aria-hidden="true">↗</span></a></nav>
      <div className="sidebar-bottom"><span className="environment-dot" /> Ambiente privado</div>
    </aside>
    <div className="workspace"><div className="topbar"><span>Workspace / {path[0] === 'executions' ? 'Execuções' : 'Workflows'}</span><span className="topbar-note">Versões & histórico</span></div>
      <main id="main" tabIndex={-1} key={hash}>{page}</main>
    </div>
  </div>
}
