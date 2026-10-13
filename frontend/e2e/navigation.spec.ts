import { expect, test } from '@playwright/test'
import { executionDetail, ids } from './mock-api'

test('resposta atrasada de outra execução não sobrescreve a tela atual', async ({ page }) => {
  let releaseFirst!: () => void
  const firstMayFinish = new Promise<void>(resolve => {releaseFirst = resolve})
  let markStarted!: () => void
  const firstStarted = new Promise<void>(resolve => {markStarted = resolve})
  let markFinished!: () => void
  const firstFinished = new Promise<void>(resolve => {markFinished = resolve})
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/executions/' + ids.execution) {
      markStarted()
      await firstMayFinish
      try {
        await route.fulfill({json: executionDetail(ids.execution, 'failed')})
      } catch {
        // A navegação pode abortar esta request antes da resposta: também é comportamento correto.
      } finally {markFinished()}
      return
    }
    if (path === '/api/executions/' + ids.secondExecution) {
      await route.fulfill({json: executionDetail(ids.secondExecution, 'succeeded')})
      return
    }
    await route.fulfill({json: []})
  })
  await page.goto('/#/executions/' + ids.execution)
  await firstStarted
  await page.goto('/#/executions/' + ids.secondExecution)
  await expect(page.getByText('Concluída', {exact: true})).toBeVisible()
  releaseFirst()
  await firstFinished
  await expect(page.getByText(ids.secondExecution, {exact: true})).toBeVisible()
  await expect(page.getByText('Concluída', {exact: true})).toBeVisible()
  await expect(page.getByText('Falhou', {exact: true})).toHaveCount(0)
})
