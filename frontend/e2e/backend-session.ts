import { randomUUID } from 'node:crypto'
import { expect } from '@playwright/test'
import type { Page } from '@playwright/test'
export async function signInForBackend(page: Page) {
  await page.goto('/')
  await expect(page.getByLabel('E-mail',{exact:true})).toBeVisible()
  if (!await page.getByRole('heading',{name:'Criar conta',exact:true}).count()) await page.getByRole('button',{name:'Criar uma conta',exact:true}).click()
  await page.getByLabel('E-mail',{exact:true}).fill('browser-'+randomUUID()+'@flowforge.local')
  await page.getByLabel('Senha',{exact:true}).fill(randomUUID())
  const response=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/auth/register'&&r.request().method()==='POST')
  await page.getByRole('button',{name:'Criar conta',exact:true}).click()
  const registered=await response;expect(registered.status()).toBe(201)
  let token=(await registered.json()).accessToken as string
  page.on('request',request=>{
    if(new URL(request.url()).pathname.startsWith('/api/workflows')&&request.headers().authorization)token=request.headers().authorization.slice(7)
  })
  await expect(page.getByRole('link',{name:'Novo workflow',exact:true})).toBeVisible()
  return {
    get:(path:string)=>page.request.get(path,{headers:{Authorization:'Bearer '+token}}),
    post:(path:string,options?:{data:unknown;headers?:Record<string,string>})=>page.request.post(path,{...options,headers:{...options?.headers,Authorization:'Bearer '+token}}),
  }
}
