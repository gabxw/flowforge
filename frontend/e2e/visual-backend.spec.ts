import { signInForBackend } from './backend-session'
import { randomUUID } from 'node:crypto'
import { expect, test } from '@playwright/test'
import type { WorkflowDetail, WorkflowExecution, NodeExecution } from '../src/api/contracts'
import { dragNode, connectPorts, stepJson } from './canvas-actions'

test('editor visual persiste layout publica e executa os dois ramos @backend',async({page})=>{
  test.setTimeout(90_000)
  const api = await signInForBackend(page)
  let workflowId:string|undefined
  try {
    await page.goto('/')
    await page.getByRole('link',{name:'Novo workflow',exact:true}).click()
    await page.getByLabel('Nome',{exact:true}).fill('Editor visual '+randomUUID().slice(0,8))
    await page.getByLabel('Descrição',{exact:true}).fill('Jornada fictícia de layout e execução')
    const creating=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/workflows'&&r.request().method()==='POST')
    await page.getByRole('button',{name:'Criar workflow',exact:true}).click()
    workflowId=((await (await creating).json()) as WorkflowDetail).id
    await page.getByRole('button',{name:'Usar exemplo com Condition, Transform e Delay',exact:true}).click()
    await (await stepJson(page,2)).fill(JSON.stringify({type:'condition',sourcePointer:'',operation:'exists'}))
    const nodeIds=await page.locator('.react-flow__node').evaluateAll(els=>els.map(el=>el.getAttribute('data-id')!))
    expect(nodeIds).toHaveLength(6)
    await page.getByRole('button',{name:'Todas as etapas',exact:true}).click()
    await page.getByRole('button',{name:'Remover conexão 3',exact:true}).click()
    await connectPorts(page,nodeIds[1],'false',nodeIds[5])
    await dragNode(page,nodeIds[3],60,60)
    await page.getByRole('button',{name:'Salvar rascunho',exact:true}).click()
    await expect(page.getByText('Rascunho salvo.',{exact:true})).toBeVisible()
    const saved=(await (await api.get('/api/workflows/'+workflowId)).json()) as WorkflowDetail
    const draft=saved.versions.find(v=>v.id===saved.draftVersionId)!
    expect(draft.nodes[3].position).not.toEqual({x:720,y:0})
    expect(draft.nodes[1].configuration).toEqual({type:'condition',sourcePointer:'',operation:'exists'})
    expect(draft.connections).toHaveLength(5)
    await page.reload()
    await expect(page.getByTestId('canvas-node-'+nodeIds[3])).toBeVisible()
    const reopened=(await (await api.get('/api/workflows/'+workflowId)).json()) as WorkflowDetail
    expect(reopened.versions.find(v=>v.id===reopened.draftVersionId)).toEqual(draft)
    await test.info().attach('editor-visual-rascunho',{body:await page.screenshot({fullPage:true}),contentType:'image/png'})
    await page.getByRole('button',{name:'Publicar versão',exact:true}).click()
    await expect(page.getByText('Versão publicada.',{exact:true})).toBeVisible()
    await page.getByRole('button',{name:'Executar workflow',exact:true}).click()
    await expect(page.getByRole('heading',{name:'Detalhes da execução',exact:true})).toBeVisible()
    await expect(page.locator('.page-head').getByText('Concluída',{exact:true})).toBeVisible({timeout:35_000})
    const firstId=page.url().split('/').at(-1)!
    const first=(await (await api.get('/api/executions/'+firstId)).json()) as WorkflowExecution
    expect(first.workflowVersionId).toBe(draft.id)
    const firstNodes=(await (await api.get('/api/executions/'+firstId+'/nodes')).json()) as NodeExecution[]
    expect(firstNodes.filter(n=>n.status==='succeeded')).toHaveLength(5)
    expect(firstNodes.find(n=>n.nodeId===nodeIds[5])?.status).toBe('skipped')
    await page.goto('/#/workflows/'+workflowId)
    await page.getByRole('button',{name:'Abrir novo rascunho',exact:true}).click()
    await expect(page.getByText('Novo rascunho criado.',{exact:true})).toBeVisible()
    const copied=(await (await api.get('/api/workflows/'+workflowId)).json()) as WorkflowDetail
    expect(copied.versions.find(v=>v.id===copied.draftVersionId)?.nodes).toEqual(draft.nodes)
    await (await stepJson(page,2)).fill(JSON.stringify({type:'condition',sourcePointer:'/ausente',operation:'exists'}))
    await page.getByRole('button',{name:'Salvar rascunho',exact:true}).click()
    await expect(page.getByText('Rascunho salvo.',{exact:true})).toBeVisible()
    await page.getByRole('button',{name:'Publicar versão',exact:true}).click()
    await expect(page.getByText('Versão publicada.',{exact:true})).toBeVisible()
    await page.getByRole('button',{name:'Executar workflow',exact:true}).click()
    await expect(page.getByRole('heading',{name:'Detalhes da execução',exact:true})).toBeVisible()
    await expect(page.locator('.page-head').getByText('Concluída',{exact:true})).toBeVisible({timeout:30_000})
    const secondId=page.url().split('/').at(-1)!
    const secondNodes=(await (await api.get('/api/executions/'+secondId+'/nodes')).json()) as NodeExecution[]
    expect(secondNodes.filter(n=>n.status==='succeeded')).toHaveLength(3)
    expect(secondNodes.find(n=>n.nodeId===nodeIds[5])?.status).toBe('succeeded')
    const final=(await (await api.get('/api/workflows/'+workflowId)).json()) as WorkflowDetail
    expect(final.versions.find(v=>v.id===draft.id)?.nodes).toEqual(draft.nodes)
    expect((await (await api.get('/api/executions/'+firstId)).json()).workflowVersionId).toBe(draft.id)
  } finally {
    if(workflowId) {
      const latest=(await (await api.get('/api/workflows/'+workflowId)).json()) as WorkflowDetail
      const archived=await api.post('/api/workflows/'+workflowId+'/archive',{data:{expectedRevision:latest.revision}})
      expect(archived.ok(),'Arquivar somente o workflow fictício da jornada').toBeTruthy()
    }
  }
})
