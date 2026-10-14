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
    <aside className="access-context">
      <AccessDiagram />
      <h2>Seu grafo, suas execuções</h2>
      <p>Publique uma versão, receba eventos pelo webhook e acompanhe o caminho que cada execução percorreu.</p>
      <p>Definições e credenciais ficam vinculadas à sua conta.</p>
    </aside>
  </section>
}

// Um workflow de exemplo desenhado com as mesmas peças do canvas: o sinal percorre o ramo true.
function AccessDiagram() {
  const card = (x: number, y: number, title: string, detail: string, tone: string) => <g transform={`translate(${x},${y})`}>
    <rect width="150" height="52" rx="9" className="diagram-card" />
    <rect x="0" y="10" width="3" height="32" rx="1.5" fill={tone} />
    <text x="14" y="22" className="diagram-title">{title}</text>
    <text x="14" y="38" className="diagram-detail">{detail}</text>
  </g>
  const wires = ['M154 96 C176 96 176 96 198 96', 'M348 84 C378 84 372 38 402 38', 'M348 108 C378 108 372 154 402 154']
  return <svg className="access-diagram" viewBox="0 0 556 196" role="img" aria-label="Exemplo de workflow: Webhook Trigger, Condition e dois Logs, um por ramo.">
    {wires.map((d, k) => <path key={k} d={d} className={'diagram-wire' + (k === 2 ? ' is-false' : k === 1 ? ' is-true' : '')} />)}
    {wires.slice(0, 2).map((d, k) => <path key={k} d={d} className="diagram-signal" pathLength={1} style={{ animationDelay: k * .9 + 's' }} />)}
    {card(4, 70, 'Webhook Trigger', 'entrada por webhook', '#ffb547')}
    {card(198, 70, 'Condition', '/total > 100', '#ffd27a')}
    {card(402, 12, 'Log', '“Pedido aprovado”', '#b0b0ba')}
    {card(402, 128, 'Log', '“Revisão manual”', '#b0b0ba')}
    <text x="354" y="72" className="diagram-port is-true">true</text>
    <text x="354" y="128" className="diagram-port is-false">false</text>
  </svg>
}
