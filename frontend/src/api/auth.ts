export interface AuthUser { id: string; email: string; role: 'owner' | 'admin' }
interface Grant { accessToken: string; expiresAt: string; user: AuthUser }
export class AuthError extends Error {
  constructor(readonly status: number) { super(status === 401 ? 'E-mail ou senha inválidos. Entre novamente.' : status === 409 ? 'Não foi possível criar a conta. Tente entrar com uma conta existente.' : status === 429 ? 'Limite de tentativas atingido. Aguarde e tente novamente.' : status === 403 ? 'Reabra a tela e tente novamente.' : 'O serviço de acesso está indisponível. Tente novamente.'); }
}
let grant: Grant | null = null
let csrf: string | null = null
let needsLogin = false
let refreshing: Promise<AuthUser> | null = null
let booting: Promise<{ user: AuthUser | null; canRegister: boolean; setupRequired: boolean }> | null = null
async function call<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const controller = new AbortController()
  const timer = window.setTimeout(() => controller.abort(), 10000)
  try {
    const response = await fetch('/api/auth/' + path, { method, credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
      headers: { Accept: 'application/json', ...(method === 'GET' ? {} : { 'X-CSRF-Token': csrf ?? '' }), ...(body ? { 'Content-Type': 'application/json' } : {}) },
      body: body ? JSON.stringify(body) : undefined })
    if (!response.ok) throw new AuthError(response.status)
    return await response.json() as T
  } catch (e) { throw e instanceof AuthError ? e : new AuthError(503) }
  finally { window.clearTimeout(timer) }
}
function accept(value: Grant): AuthUser {
  if (typeof value.accessToken !== 'string' || !value.accessToken || !Number.isFinite(Date.parse(value.expiresAt)) ||
      !value.user || !/^[0-9a-f-]{36}$/i.test(value.user.id) || typeof value.user.email !== 'string' || !['owner','admin'].includes(value.user.role)) throw new AuthError(503)
  grant = value; needsLogin = false; return value.user
}
async function getCsrf() {
  if (!csrf) csrf = (await call<{csrfToken: string}>('csrf')).csrfToken
}
export const authentication = {
  token: () => grant?.accessToken ?? null,
  user: () => grant?.user ?? null,
  async bootstrap() {
    if (!booting) booting = (async () => {
      await getCsrf()
      const status = await call<{canRegister: boolean; setupRequired: boolean; hasSession: boolean}>('status')
      if (!status.hasSession) return {...status, user: null}
      try { return { ...status, user: await authentication.refresh() } }
      catch (e) { if (!(e instanceof AuthError) || e.status !== 401) throw e; return { ...status, user: null } }
    })().finally(() => { booting = null })
    return booting
  },
  async signIn(email: string, password: string, register: boolean) {
    await getCsrf()
    return accept(await call<Grant>(register ? 'register' : 'login', 'POST', {email, password}))
  },
  refresh(): Promise<AuthUser> {
    if (needsLogin) return Promise.reject(new AuthError(401))
    if (!refreshing) {
      const rotate = async () => {
        try { await getCsrf(); return accept(await call<Grant>('refresh', 'POST')) }
        catch (e) {
          if (e instanceof AuthError && e.status === 401) { grant = null; needsLogin = true; window.dispatchEvent(new Event('flowforge-session-expired')) }
          throw e
        }
      }
      // Cookie compartilhado entre abas; Web Locks serializa a leitura/rotação.
      refreshing = Promise.resolve(navigator.locks ? navigator.locks.request('flowforge-refresh', rotate) : rotate()).finally(() => { refreshing = null })
    }
    return refreshing
  },
  async logout() {
    await getCsrf(); await call('logout', 'POST'); grant = null; needsLogin = true
  },
}
