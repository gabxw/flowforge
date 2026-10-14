import { signInForBackend } from './backend-session'
import { randomUUID } from 'node:crypto'
import { expect, test } from '@playwright/test'
import { stepJson } from './canvas-actions'

test('jornada real cria publica executa e arquiva workflow @backend', async ({ page }) => {
  test.setTimeout(60_000)
  const name = 'Jornada navegador ' + randomUUID().slice(0, 8)
  const api = await signInForBackend(page)
  let workflowId: string | undefined
  try {
    await page.goto('/')
    await page.getByRole('link', {name: 'Novo workflow', exact: true}).click()
    await page.getByLabel('Nome', {exact: true}).fill(name)
    await page.getByLabel('Descrição', {exact: true}).fill('Dados fictícios da validação de navegador')
    const created = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname.replaceAll('/', '') === 'apiworkflows')
    await page.getByRole('button', {name: 'Criar workflow', exact: true}).click()
    const response = await created
    expect(response.status()).toBe(201)
    const payload = await response.json()
    expect(typeof payload.id).toBe('string')
    workflowId = payload.id
    await expect(page).toHaveURL(new RegExp('#/workflows/' + workflowId + '$'))
    await page.getByRole('button', {name: 'Usar exemplo Trigger → Log', exact: true}).click()
    await (await stepJson(page, 2)).fill(JSON.stringify({type: 'log', message: 'Mensagem fictícia de navegador'}))
    await page.getByRole('button', {name: 'Salvar rascunho', exact: true}).click()
    await expect(page.getByText('Rascunho salvo.', {exact: true})).toBeVisible()
    await page.getByRole('button', {name: 'Publicar versão', exact: true}).click()
    await expect(page.getByText('Versão publicada.', {exact: true})).toBeVisible()
    await test.info().attach('workflow-publicado', {body: await page.screenshot({fullPage: true}), contentType: 'image/png'})
    await page.getByRole('button', {name: 'Executar workflow', exact: true}).click()
    await expect(page.getByRole('heading', {name: 'Detalhes da execução', exact: true})).toBeVisible()
    await expect(page.locator('.page-head').getByText('Concluída', {exact: true})).toBeVisible({timeout: 30_000})
    await expect(page.getByRole('heading', {name: 'Etapas', exact: true})).toBeVisible()
    await expect(page.getByText('Log registrado', {exact: true})).toBeVisible()
    await expect(page.getByText('Mensagem fictícia de navegador', {exact: true})).toHaveCount(0)
    await test.info().attach('execucao-concluida', {body: await page.screenshot({fullPage: true}), contentType: 'image/png'})
    const executionId = page.url().split('/').at(-1)!
    const execution = await api.get('/api/executions/' + executionId)
    expect(execution.ok()).toBeTruthy()
    expect(await execution.json()).toMatchObject({workflowId, status: 'succeeded'})
    await page.goto('/#/workflows/' + workflowId)
    await page.getByRole('tab', {name: 'Execuções', exact: true}).click()
    await expect(page.locator('a[href="#/executions/' + executionId + '"]')).toBeVisible()
  } finally {
    if (workflowId) {
      const current = await api.get('/api/workflows/' + workflowId)
      expect(current.ok(), 'Ler workflow fictício antes de arquivar').toBeTruthy()
      const detail = await current.json()
      const archive = await api.post('/api/workflows/' + workflowId + '/archive', {data: {expectedRevision: detail.revision}})
      expect(archive.ok(), 'Arquivar apenas o workflow fictício criado nesta jornada').toBeTruthy()
    }
  }
})
