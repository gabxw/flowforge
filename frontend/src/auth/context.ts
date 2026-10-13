import { createContext, useContext } from 'react'
import type { AuthUser } from '../api/auth'
export interface AuthState { user: AuthUser | null; required: boolean; loading: boolean; canRegister: boolean; setupRequired: boolean; error: string | null; initialize: () => Promise<void>; signIn: (email: string, password: string, register: boolean) => Promise<void>; signOut: () => Promise<void> }
export const Context = createContext<AuthState | null>(null)
export function useAuth() { const state = useContext(Context); if (!state) throw new Error('Sessão indisponível.'); return state }
