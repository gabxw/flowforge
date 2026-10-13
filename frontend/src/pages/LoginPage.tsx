import { useState } from 'react'
import type { FormEvent } from 'react'
import { useAuth } from '../auth/context'
export function LoginPage() {
  const auth = useAuth()
  const [register, setRegister] = useState(auth.setupRequired)
  const [email, setEmail] = useState(auth.user?.email ?? '')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function submit(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(null)
    try { await auth.signIn(email.trim(), password, register); setPassword('') }
    catch (e) { setError(e instanceof Error ? e.message : 'Não foi possível entrar.') }
    finally { setBusy(false) }
  }
  if (auth.loading) return <section className="panel" aria-busy="true"><h1>Verificando acesso</h1><p>Aguarde a confirmação da sessão.</p></section>
  if (auth.error) return <section className="panel"><h1>Acesso indisponível</h1><p role="alert">{auth.error}</p><button className="btn" onClick={()=>void auth.initialize()}>Tentar acesso novamente</button></section>
  return <section className="access-layout">
    <form className="panel access-form" onSubmit={event=>void submit(event)}>
      <h1>{auth.user ? 'Entre novamente' : register ? 'Criar conta' : 'Entrar no FlowForge'}</h1>
      <p>{auth.user ? 'Sua edição permanece aberta enquanto você confirma o acesso.' : 'Acesse seus workflows e acompanhe cada execução.'}</p>
      {auth.setupRequired && <p className="notice">A primeira conta configura o administrador deste ambiente local.</p>}
      {error && <p role="alert" className="notice notice-error">{error}</p>}
      <label htmlFor="access-email">E-mail</label>
      <input id="access-email" type="email" autoComplete="username" required maxLength={254} value={email} readOnly={Boolean(auth.user)} disabled={busy} onChange={e=>setEmail(e.target.value)} />
      <label htmlFor="access-password">Senha</label>
      <input id="access-password" type="password" autoComplete={register ? 'new-password' : 'current-password'} required minLength={register ? 15 : 1} maxLength={128} value={password} disabled={busy} onChange={e=>setPassword(e.target.value)} />
      {register && <p className="hint">Use de 15 a 128 caracteres. Uma frase longa funciona bem.</p>}
      <button className="btn btn-primary" disabled={busy} type="submit">{busy ? 'Confirmando acesso…' : register ? 'Criar conta' : 'Entrar'}</button>
      {auth.canRegister && !auth.user && <button className="btn btn-link" type="button" disabled={busy} onClick={()=>{setRegister(!register);setError(null);setPassword('')}}>{register ? 'Já tenho uma conta' : 'Criar uma conta'}</button>}
    </form>
    <aside className="access-context"><h2>Seu grafo, suas execuções</h2><p>Publique uma versão do workflow, receba eventos e investigue cada etapa pelo histórico.</p><p>As definições e credenciais ficam vinculadas à sua conta.</p></aside>
  </section>
}
