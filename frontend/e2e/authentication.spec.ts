import { test, expect } from '@playwright/test'
import { ids, mockApi } from './mock-api'
import { mockSession } from './session-mock'
import { stepJson } from './canvas-actions'
test('entra usa bearer e sai sem persistir tokens no storage',async({page})=>{
  await mockApi(page)
  await mockSession(page,false)
  await page.goto('/')
  await expect(page.getByRole('heading',{name:'Entrar no FlowForge',exact:true})).toBeVisible()
  await page.getByLabel('E-mail',{exact:true}).fill('owner@flowforge.local')
  await page.getByLabel('Senha',{exact:true}).fill('senha-ficticia-de-teste')
  const listing=page.waitForRequest(r=>new URL(r.url()).pathname==='/api/workflows')
  await page.getByRole('button',{name:'Entrar',exact:true}).click()
  expect((await listing).headers().authorization).toBe('Bearer mock-access-token')
  expect(await page.evaluate(()=>localStorage.length+sessionStorage.length)).toBe(0)
  await page.getByRole('button',{name:'Sair',exact:true}).click()
  await expect(page.getByRole('heading',{name:'Entrar no FlowForge',exact:true})).toBeVisible()
})
test('sessão expirada conserva JSON local durante reautenticação',async({page})=>{
  await mockApi(page,{exists:true})
  const session=await mockSession(page)
  let expired=true
  await page.route('**/api/workflows/'+ids.workflow+'/draft',route=>expired?route.fulfill({status:401,json:{status:401}}):route.fallback())
  await page.goto('/#/workflows/'+ids.workflow)
  const local=JSON.stringify({type:'log',message:'Edição conservada durante login'})
  await (await stepJson(page,2)).fill(local)
  session.expire()
  await page.getByRole('button',{name:'Salvar rascunho',exact:true}).click()
  await expect(page.getByRole('heading',{name:'Entre novamente',exact:true})).toBeVisible()
  await page.getByLabel('Senha',{exact:true}).fill('senha-ficticia-de-teste')
  expired=false
  await page.getByRole('button',{name:'Entrar',exact:true}).click()
  await expect(page.getByLabel('Configuração JSON da etapa 2',{exact:true})).toHaveValue(local)
  await expect(page.getByRole('button',{name:'Salvar rascunho',exact:true})).toBeEnabled()
})
test('login tem labels foco e reflow em 390 e 720px',async({page})=>{
  for(const width of [390,720]){
    await page.setViewportSize({width,height:844})
    await mockSession(page,false)
    await page.goto('/')
    await expect(page.getByLabel('E-mail',{exact:true})).toBeVisible()
    await page.getByLabel('E-mail',{exact:true}).focus()
    await page.keyboard.press('Tab')
    await expect(page.getByLabel('Senha',{exact:true})).toBeFocused()
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy()
  }
})
