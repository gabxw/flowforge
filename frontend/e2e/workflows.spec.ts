import { mockSession } from './session-mock'
import { expect, test } from '@playwright/test'
import { ids, mockApi } from './mock-api'

const editedLog = {type: 'log', message: 'Mensagem fictícia de validação'}

test('cria publica e acompanha execução sem expor conteúdo de logs', async ({ page }) => {
  const api = await mockApi(page)
  await page.goto('/')
  await page.getByRole('link', {name: 'Novo workflow', exact: true}).click()
  await page.getByLabel('Nome', {exact: true}).fill('Pedido de validação')
  await page.getByLabel('Descrição', {exact: true}).fill('Jornada fictícia de frontend')
  await page.getByRole('button', {name: 'Criar workflow', exact: true}).click()
  await expect(page).toHaveURL(new RegExp('#/workflows/' + ids.workflow))
  await page.getByRole('button', {name: 'Usar exemplo Trigger → Log', exact: true}).click()
  await page.getByRole('textbox', {name: 'Configuração JSON do node 2', exact: true}).fill(JSON.stringify(editedLog))
  await page.getByRole('button', {name: 'Salvar rascunho', exact: true}).click()
  await expect.poll(() => api.workflow.revision).toBe(2)
  await page.getByRole('button', {name: 'Publicar versão', exact: true}).click()
  await expect(page.getByRole('button', {name: 'Executar workflow', exact: true})).toBeEnabled()
  await page.getByRole('button', {name: 'Executar workflow', exact: true}).click()
  await expect(page).toHaveURL(new RegExp('#/executions/' + ids.execution))
  await expect(page.getByRole('heading', {name: 'Detalhes da execução', exact: true})).toBeVisible()
  await expect(page.getByRole('heading', {name: 'Nodes', exact: true})).toBeVisible()
  await expect(page.getByRole('heading', {name: 'Logs', exact: true})).toBeVisible()
  await expect(page.getByText('Log registrado', {exact: true})).toBeVisible()
  await expect(page.getByText(editedLog.message, {exact: true})).toHaveCount(0)
  expect(api.commands.map(command => command.path)).toEqual([
    '/api/workflows', '/api/workflows/' + ids.workflow + '/draft',
    '/api/workflows/' + ids.workflow + '/publish', '/api/workflows/' + ids.workflow + '/executions',
  ])
  expect(api.commands[1].body).toMatchObject({expectedRevision: 1, nodes: [{configuration: {type: 'webhookTrigger'}}, {configuration: editedLog}]})
  expect(api.commands[2].body).toEqual({expectedRevision: 2})
  expect(api.commands[3].body).toBeNull()
})

test('conflito de revisão preserva o rascunho local para decisão do usuário', async ({ page }) => {
  await mockApi(page, {exists: true, conflict: true})
  await page.goto('/#/workflows/' + ids.workflow)
  const config = page.getByRole('textbox', {name: 'Configuração JSON do node 2', exact: true})
  const localValue = JSON.stringify({type: 'log', message: 'Edição local preservada'})
  await config.fill(localValue)
  await page.getByRole('button', {name: 'Salvar rascunho', exact: true}).click()
  await expect(page.getByRole('alert').filter({hasText: 'Conflito ao salvar'})).toBeVisible()
  await expect(page.getByText('O rascunho não foi salvo. Suas alterações locais foram preservadas.', {exact: true})).toBeVisible()
  await expect(config).toHaveValue(localValue)
  await expect(page.getByRole('button', {name: 'Salvar rascunho', exact: true})).toBeEnabled()
})

for (const status of [503, 401]) {
  test('trata erro HTTP ' + status + ' sem corpo técnico do servidor', async ({ page }) => {
    await page.route('**/api/workflows*', route => route.fulfill({
      status, contentType: 'application/problem+json',
      body: JSON.stringify({status, title: 'Falha do serviço', detail: 'SENTINELA_INTERNA_SQL_TOKEN_NAO_EXIBIR'}),
    }))
    await page.goto('/')
    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.getByRole('button', {name: 'Tentar novamente', exact: true})).toBeVisible()
    await expect(page.getByText('SENTINELA_INTERNA_SQL_TOKEN_NAO_EXIBIR', {exact: false})).toHaveCount(0)
    await expect(page.getByRole('link', {name: 'Novo workflow', exact: true})).toBeVisible()
  })
}

test('recurso ausente mostra erro recuperável e mantém navegação', async ({ page }) => {
  await page.route(url => url.pathname.startsWith('/api/') && !url.pathname.startsWith('/api/auth/'), route => route.fulfill({
    status: 404, contentType: 'application/problem+json', body: JSON.stringify({status: 404, title: 'Não encontrado'}),
  }))
  await page.goto('/#/workflows/' + ids.workflow)
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByRole('link', {name: 'Workflows', exact: true})).toBeVisible()
})

test('cancelamento solicitado mantém Running até resposta terminal do servidor', async ({ page }) => {
  const api = await mockApi(page, {exists: true, status: 'running'})
  await page.goto('/#/executions/' + ids.execution)
  await page.getByRole('button', {name: 'Solicitar cancelamento', exact: true}).click()
  await expect.poll(() => api.execution.cancelRequestedAt).not.toBeNull()
  await expect(page.getByRole('button', {name: 'Solicitar cancelamento', exact: true})).toBeDisabled()
  expect(api.execution.status).toBe('running')
  await expect(page.getByText('Em execução', {exact: true}).first()).toBeVisible()
  await expect(page.getByText('Cancelamento solicitado. Aguardando confirmação do Worker.', {exact: true})).toBeVisible()
  await expect(page.getByText('Cancelada', {exact: true})).toHaveCount(0)
})

test('JSON com propriedades duplicadas não é enviado ao servidor', async ({ page }) => {
  const api = await mockApi(page, {exists: true})
  await page.goto('/#/workflows/' + ids.workflow)
  const ambiguous = '{"type":"log","message":"Primeiro","message":"Segundo"}'
  await page.getByRole('textbox', {name: 'Configuração JSON do node 2', exact: true}).fill(ambiguous)
  await page.getByRole('button', {name: 'Salvar rascunho', exact: true}).click()
  await expect(page.getByText('Node 2: Propriedades JSON duplicadas não são permitidas.', {exact: true})).toBeVisible()
  expect(api.commands).toHaveLength(0)
})

test('usuário retoma a leitura após indisponibilidade sem repetir mutações', async ({ page }) => {
  let unavailable = true
  let successfulReads = 0
  await page.route('**/api/workflows*', async route => {
    if (unavailable) {
      await route.fulfill({status: 503, json: {status: 503, title: 'Indisponível'}})
    } else {
      successfulReads++
      await route.fulfill({json: {items: [], offset: 0, limit: 20}})
    }
  })
  await page.goto('/')
  await expect(page.getByRole('alert')).toBeVisible()
  unavailable = false
  await page.getByRole('button', {name: 'Tentar novamente', exact: true}).click()
  await expect(page.getByRole('alert')).toHaveCount(0)
  expect(successfulReads).toBeGreaterThan(0)
})

test('recusar navegação mantém a edição não salva no workflow atual', async ({ page }) => {
  await mockApi(page, {exists: true})
  await page.goto('/#/workflows/' + ids.workflow)
  const localValue = JSON.stringify({type: 'log', message: 'Rascunho local em edição'})
  const config = page.getByRole('textbox', {name: 'Configuração JSON do node 2', exact: true})
  await config.fill(localValue)
  await expect(page.getByText('Alterações locais não salvas', {exact: true})).toBeVisible()
  page.once('dialog', dialog => dialog.dismiss())
  await page.getByRole('link', {name: 'Workflows', exact: true}).click()
  await expect(page).toHaveURL(new RegExp('#/workflows/' + ids.workflow + '$'))
  await expect(config).toHaveValue(localValue)
})

test('publicação rejeitada pelo domínio mantém rascunho e exibe erro sanitizado', async ({ page }) => {
  const api = await mockApi(page, {exists: true, invalidPublish: true})
  await page.goto('/#/workflows/' + ids.workflow)
  const config = page.getByRole('textbox', {name: 'Configuração JSON do node 2', exact: true})
  const before = await config.inputValue()
  await page.getByRole('button', {name: 'Publicar versão', exact: true}).click()
  await expect(page.getByRole('alert').filter({hasText: 'Grafo inválido'})).toBeVisible()
  await expect(page.getByText('SENTINELA_INTERNA_NAO_EXIBIR', {exact: false})).toHaveCount(0)
  await expect(config).toHaveValue(before)
  await expect(page.getByText('Versão publicada.', {exact: true})).toHaveCount(0)
  expect(api.workflow.revision).toBe(1)
  expect(api.workflow.draftVersionId).toBe(ids.version)
  expect(api.workflow.currentPublishedVersionId).toBeNull()
})

test('edita detalhes abre outra versão e arquiva preservando publicação anterior', async ({ page }) => {
  const api = await mockApi(page, {exists: true})
  await page.goto('/#/workflows/' + ids.workflow)
  await page.getByLabel('Nome', {exact: true}).fill('Workflow renomeado')
  await page.getByLabel('Descrição', {exact: true}).fill('Descrição fictícia atualizada')
  await page.getByRole('button', {name: 'Salvar detalhes', exact: true}).click()
  await expect(page.getByText('Detalhes salvos.', {exact: true})).toBeVisible()
  await expect(page.getByRole('heading', {name: 'Workflow renomeado', exact: true})).toBeVisible()
  await page.getByRole('button', {name: 'Publicar versão', exact: true}).click()
  await expect(page.getByText('Versão publicada.', {exact: true})).toBeVisible()
  await page.getByRole('button', {name: 'Abrir novo rascunho', exact: true}).click()
  await expect(page.getByText('Novo rascunho criado.', {exact: true})).toBeVisible()
  await expect(page.getByRole('textbox', {name: 'Configuração JSON do node 2', exact: true})).toBeEnabled()
  expect(api.workflow.versions).toHaveLength(2)
  expect(api.workflow.versions[0].status).toBe('published')
  expect(api.workflow.currentPublishedVersionId).toBe(ids.version)
  expect(api.workflow.draftVersionId).not.toBe(ids.version)
  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', {name: 'Arquivar workflow', exact: true}).click()
  await expect(page.getByText('Workflow arquivado. Histórico preservado.', {exact: true})).toBeVisible()
  await expect(page.getByText('Arquivado', {exact: true})).toBeVisible()
  await expect(page.getByRole('button', {name: 'Executar workflow', exact: true})).toHaveCount(0)
  expect(api.workflow.versions).toHaveLength(2)
  expect(api.commands.map(command => command.body)).toEqual([
    {expectedRevision: 1, name: 'Workflow renomeado', description: 'Descrição fictícia atualizada'},
    {expectedRevision: 2}, {expectedRevision: 3}, {expectedRevision: 4},
  ])
})

test('editor recusa decimal que perderia precisão antes de solicitar gravação', async ({ page }) => {
  const api = await mockApi(page, {exists: true})
  await page.goto('/#/workflows/' + ids.workflow)
  await page.getByRole('combobox', {name: 'Tipo do node 2', exact: true}).selectOption('condition')
  const raw = '{"type":"condition","sourcePointer":"/valor","operation":"equals","expectedValue":0.1234567890123456789012345678}'
  const config = page.getByRole('textbox', {name: 'Configuração JSON do node 2', exact: true})
  await config.fill(raw)
  await page.getByRole('button', {name: 'Salvar rascunho', exact: true}).click()
  await expect(page.getByText('Node 2: O JSON contém um número fora da precisão suportada pelo navegador. Nenhum valor foi convertido.', {exact: true})).toBeVisible()
  await expect(config).toHaveValue(raw)
  expect(api.commands).toHaveLength(0)
})

test.beforeEach(async ({page}) => { await mockSession(page) })
