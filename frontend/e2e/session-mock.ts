import type { Page } from '@playwright/test'
export async function mockSession(page: Page, initiallyAuthenticated = true) {
  let authenticated = initiallyAuthenticated
  let refreshes = 0
  await page.route('**/api/auth/**', async route => {
    const path = new URL(route.request().url()).pathname
    const user = {id: '10000000-0000-4000-8000-000000000010', email: 'owner@flowforge.local', role: 'owner'}
    if (path.endsWith('/csrf')) return route.fulfill({json: {csrfToken: 'mock-antiforgery-token'}})
    if (path.endsWith('/status')) return route.fulfill({json: {canRegister: true, setupRequired: false, hasSession: authenticated}})
    if (path.endsWith('/logout')) { authenticated=false;return route.fulfill({json:{signedOut:true}}) }
    if (path.endsWith('/refresh')) {refreshes++;if(!authenticated)return route.fulfill({status:401,json:{status:401}})}
    if (path.endsWith('/login') || path.endsWith('/register')) authenticated=true
    return route.fulfill({json: {accessToken:'mock-access-token',expiresAt:'2099-01-01T00:00:00Z',user}})
  })
  return {expire:()=>{authenticated=false}, refreshes:()=>refreshes}
}
