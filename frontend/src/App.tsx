import { useCallback, useEffect, useRef, useState } from 'react'
import { WorkflowListPage } from './pages/WorkflowListPage'
import { CreateWorkflowPage } from './pages/CreateWorkflowPage'
import { WorkflowPage } from './pages/WorkflowPage'
import { ExecutionPage } from './pages/ExecutionPage'
import { LoginPage } from './pages/LoginPage'
import { useAuth } from './auth/context'

const uuid = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i

export default function App() {
  const auth = useAuth()
  const [signOutError, setSignOutError] = useState<string | null>(null)
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

  async function signOut() {
    if (dirty.current && !window.confirm("Há alterações não salvas. Deseja sair e descartá-las?")) return
    try { await auth.signOut(); dirty.current = false; setSignOutError(null) }
    catch { setSignOutError("Não foi possível confirmar a saída. Tente novamente.") }
  }
  const path = hash.replace(/^#/, '').split('/').filter(Boolean)
  let page
  if (!path.length || (path.length === 1 && path[0] === 'workflows')) page = <WorkflowListPage />
  else if (path.length === 2 && path[0] === 'workflows' && path[1] === 'new') page = <CreateWorkflowPage onDirtyChange={onDirtyChange} />
  else if (path.length === 2 && path[0] === 'workflows' && uuid.test(path[1])) page = <WorkflowPage id={path[1]} onDirtyChange={onDirtyChange} />
  else if (path.length === 2 && path[0] === 'executions' && uuid.test(path[1])) page = <ExecutionPage id={path[1]} />
  else page = <section className="panel empty"><h1>Página não encontrada</h1><p>Confira o endereço ou o ID informado.</p><a className="btn" href="#/workflows">Voltar aos workflows</a></section>

  return <>
    <a href="#main" className="skip-link" onClick={e => { e.preventDefault(); document.getElementById('main')?.focus() }}>Pular para o conteúdo</a>
    <header className="masthead">
      <a href="#/workflows" className="brand" aria-label="FlowForge, início"><BrandMark />FlowForge</a>
      <nav aria-label="Navegação principal"><a href="#/workflows" aria-current={path[0] !== 'executions' ? 'page' : undefined}>Workflows</a></nav>
      {auth.user ? <div className="session-actions"><span className="environment">{auth.user.email}</span><button className="btn" onClick={()=>void signOut()}>Sair</button></div> : <span className="environment">Ambiente privado</span>}
    </header>
    <main id="main" tabIndex={-1}>
      {signOutError && <p role="alert" className="notice notice-error">{signOutError}</p>}
      {auth.required && <LoginPage key={auth.loading ? "loading" : "login"} />}
      {auth.user && <div hidden={auth.required} key={auth.user.id + hash}>{page}</div>}
    </main>
  </>
}

// A marca é um workflow mínimo: gatilho, condição e saída ligados por um fio.
function BrandMark() {
  return <svg width="30" height="30" viewBox="0 0 30 30" fill="none" aria-hidden="true">
    <rect x="1" y="1" width="28" height="28" rx="8" fill="#15304c" stroke="#24496d" />
    <path d="M8 15h4.5M17.5 15H22" stroke="#3a6286" strokeWidth="1.5" />
    <path d="M8 15h4.5M17.5 15H22" stroke="#ffb547" strokeWidth="1.5" strokeDasharray="1.5 4" />
    <circle cx="7.5" cy="15" r="2.5" fill="#ffb547" />
    <path d="M15 11.2 18.8 15 15 18.8 11.2 15z" stroke="#ffd27a" strokeWidth="1.4" strokeLinejoin="round" />
    <rect x="20.5" y="12.5" width="5" height="5" rx="1.2" stroke="#6fe3b4" strokeWidth="1.4" />
  </svg>
}
