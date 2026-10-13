import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { authentication } from '../api/auth'
import { Context } from './context'
type State = Omit<import('./context').AuthState, 'initialize' | 'signIn' | 'signOut'>
export function AuthProvider({children}: {children: ReactNode}) {
  const [state, setState] = useState<State>({user: null, required: true, loading: true, canRegister: false, setupRequired: false, error: null})
  async function initialize() {
    setState(s => ({...s, loading: true, error: null}))
    try { const result = await authentication.bootstrap(); setState({...result, required: !result.user, loading: false, error: null}) }
    catch (e) { setState(s => ({...s, loading: false, error: e instanceof Error ? e.message : 'Acesso indisponível.'})) }
  }
  useEffect(() => {
    let active = true
    void authentication.bootstrap().then(result => {if(active)setState({...result, required: !result.user, loading: false, error: null})})
      .catch(e => {if(active)setState(s=>({...s,loading:false,error:e instanceof Error?e.message:'Acesso indisponível.'}))})
    const expired = () => setState(s => ({...s, required: true}))
    window.addEventListener('flowforge-session-expired', expired)
    return () => { active = false; window.removeEventListener('flowforge-session-expired', expired) }
  }, [])
  async function signIn(email: string, password: string, register: boolean) {
    const user = await authentication.signIn(email, password, register)
    setState(s => ({...s, user, required: false, setupRequired: false, error: null}))
  }
  async function signOut() {
    await authentication.logout(); setState(s => ({...s, user: null, required: true}))
  }
  return <Context.Provider value={{...state, initialize, signIn, signOut}}>{children}</Context.Provider>
}
