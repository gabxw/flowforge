import type { Page } from '@playwright/test'

export const ids = {
  workflow: '10000000-0000-4000-8000-000000000001',
  version: '10000000-0000-4000-8000-000000000002',
  trigger: '10000000-0000-4000-8000-000000000003',
  log: '10000000-0000-4000-8000-000000000004',
  execution: '10000000-0000-4000-8000-000000000005',
  secondExecution: '10000000-0000-4000-8000-000000000006',
}
const now = '2026-10-13T12:00:00Z'

export function workflowDetail(name = 'Workflow de validação') {
  return {
    id: ids.workflow, ownerUserId: '10000000-0000-4000-8000-000000000010',
    name, description: null as string | null, createdAt: now, updatedAt: now,
    archivedAt: null as string | null, revision: 1, currentPublishedVersionId: null as string | null,
    draftVersionId: ids.version as string | null,
    versions: [{
      id: ids.version, versionNumber: 1, status: 'draft', createdAt: now,
      publishedAt: null as string | null, revision: 1,
      nodes: [] as {nodeId: string; configuration: {type: string; message?: string}; position: {x: number; y: number}}[],
      connections: [] as {id: string; sourceNodeId: string; targetNodeId: string; sourcePort: string}[],
    }],
  }
}

export function executionDetail(id = ids.execution, status = 'succeeded') {
  return {
    id, workflowId: ids.workflow, workflowVersionId: ids.version,
    correlationId: '10000000-0000-4000-8000-000000000020', status, createdAt: now,
    startedAt: now, finishedAt: status === 'running' ? null : now, errorCode: null,
    cancelRequestedAt: null as string | null, resumeAt: null, deadlineAt: '2026-10-15T12:00:00Z',
  }
}

export const exampleNodes = [
  {nodeId: ids.trigger, configuration: {type: 'webhookTrigger'}, position: {x: 0, y: 0}},
  {nodeId: ids.log, configuration: {type: 'log', message: 'Mensagem fictícia de validação'}, position: {x: 250, y: 0}},
]

export async function mockApi(page: Page, options: {exists?: boolean; status?: string; conflict?: boolean; invalidPublish?: boolean} = {}) {
  let exists = options.exists ?? false
  const workflow = workflowDetail()
  if (exists) {
    workflow.versions[0].nodes = structuredClone(exampleNodes)
    workflow.versions[0].connections = [{id: '10000000-0000-4000-8000-000000000030', sourceNodeId: ids.trigger, targetNodeId: ids.log, sourcePort: 'next'}]
  }
  const execution = executionDetail(ids.execution, options.status ?? 'succeeded')
  const commands: {method: string; path: string; body: unknown}[] = []
  await page.route(url => url.pathname.startsWith('/api/'), async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname.replace(/\/$/, '')
    const method = request.method()
    const body = request.postData() ? request.postDataJSON() : null
    const ok = (value: unknown, status = 200) => route.fulfill({status, contentType: 'application/json', body: JSON.stringify(value)})
    const problem = (status: number, detail: string) => route.fulfill({status, contentType: 'application/problem+json', body: JSON.stringify({status, title: 'Operação recusada', detail})})
    if (method !== 'GET') commands.push({method, path, body})
    if (path === '/api/workflows' && method === 'GET') {
      return ok({items: exists ? [workflow] : [], offset: 0, limit: 20})
    }
    if (path === '/api/workflows' && method === 'POST') {
      exists = true
      workflow.name = body.name
      workflow.description = body.description
      return ok(workflow, 201)
    }
    if (path === '/api/workflows/' + ids.workflow && method === 'GET') return ok(workflow)
    if (path === '/api/workflows/' + ids.workflow && method === 'PUT') {
      workflow.revision++
      workflow.name = body.name
      workflow.description = body.description
      return ok(workflow)
    }
    if (path === '/api/workflows/' + ids.workflow + '/drafts' && method === 'POST') {
      workflow.revision++
      const version = structuredClone(workflow.versions[0])
      version.id = '10000000-0000-4000-8000-000000000007'
      version.versionNumber = 2
      version.status = 'draft'
      version.publishedAt = null
      version.revision = 1
      workflow.draftVersionId = version.id
      workflow.versions.push(version)
      return ok(workflow, 201)
    }
    if (path === '/api/workflows/' + ids.workflow + '/archive' && method === 'POST') {
      workflow.revision++
      workflow.archivedAt = now
      return ok(workflow)
    }
    if (path === '/api/workflows/' + ids.workflow + '/draft' && method === 'PUT') {
      if (options.conflict) return problem(409, 'A revisão foi alterada por outro cliente.')
      workflow.revision++
      workflow.versions[0].nodes = body.nodes
      workflow.versions[0].connections = body.connections
      return ok(workflow)
    }
    if (path === '/api/workflows/' + ids.workflow + '/publish' && method === 'POST') {
      if (options.invalidPublish) {
        return route.fulfill({status: 422, contentType: 'application/problem+json', body: JSON.stringify({
          status: 422, title: 'SENTINELA_INTERNA_NAO_EXIBIR', detail: 'SENTINELA_INTERNA_NAO_EXIBIR',
          errors: [{code: 'cycle', nodeId: ids.trigger, connectionId: null, message: 'SENTINELA_INTERNA_NAO_EXIBIR'}],
        })})
      }
      workflow.revision++
      const draft = workflow.versions.find(version => version.id === workflow.draftVersionId)!
      draft.status = 'published'
      draft.publishedAt = now
      workflow.draftVersionId = null
      workflow.currentPublishedVersionId = draft.id
      return ok(workflow)
    }
    if (path === '/api/workflows/' + ids.workflow + '/executions' && method === 'POST') return ok({...execution, status: 'pending'}, 202)
    if (path === '/api/workflows/' + ids.workflow + '/executions' && method === 'GET') return ok({items: [], offset: 0, limit: 20})
    if (path === '/api/executions/' + ids.execution && method === 'GET') return ok(execution)
    if (path === '/api/executions/' + ids.execution + '/cancel' && method === 'POST') {
      execution.cancelRequestedAt = now
      return ok(execution, 202)
    }
    if (path === '/api/executions/' + ids.execution + '/nodes') return ok(workflow.versions[0].nodes.map((node, index) => ({
      id: '10000000-0000-4000-8000-00000000004' + index, executionId: ids.execution, workflowVersionId: ids.version,
      nodeId: node.nodeId, status: execution.status === 'running' ? 'running' : 'succeeded', attemptCount: 1,
      startedAt: now, finishedAt: execution.finishedAt, input: {byteLength: 2, kind: 'Object'},
      output: {byteLength: 2, kind: 'Object'}, errorCode: null, credentialRevisionUsed: null, retryAt: null,
    })))
    if (path === '/api/executions/' + ids.execution + '/logs') return ok([{
      id: '10000000-0000-4000-8000-000000000050', executionId: ids.execution,
      nodeExecutionId: '10000000-0000-4000-8000-000000000041', createdAt: now, eventCode: 'logRecorded', messageByteLength: 35,
    }])
    if (path === '/api/executions/' + ids.execution + '/attempts') return ok([])
    return problem(404, 'Recurso não encontrado.')
  })
  return {workflow, execution, commands}
}
