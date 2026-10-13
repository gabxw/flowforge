import type {
  ApiProblem, CreateWorkflowRequest, ExecutionList, ExecutionLog, GraphErrorCode,
  GraphValidationError, NodeExecution, NodeExecutionAttempt, SaveDraftRequest,
  UpdateWorkflowRequest, WorkflowDetail, WorkflowExecution, WorkflowList,
} from './contracts'
import { JsonPrecisionError, parseExactJson } from './json'
import { authentication, AuthError } from './auth'

const API_BASE = '/api'
const REQUEST_TIMEOUT_MS = 10_000

export class ApiError extends Error {
  readonly problem: ApiProblem

  constructor(problem: ApiProblem) {
    super(problem.detail)
    this.name = 'ApiError'
    this.problem = problem
  }
}

export function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

const graphMessages: Record<GraphErrorCode, string> = {
  emptyGraph: 'Adicione nodes ao workflow antes de publicar.',
  triggerCount: 'O workflow deve ter exatamente um trigger.',
  duplicateNodeId: 'Há nodes com a mesma identidade.',
  duplicateConnectionId: 'Há conexões com a mesma identidade.',
  nodeVersionMismatch: 'Um node pertence a outra versão.',
  connectionVersionMismatch: 'Uma conexão pertence a outra versão.',
  missingSourceNode: 'A origem de uma conexão não existe.',
  missingTargetNode: 'O destino de uma conexão não existe.',
  credentialOwnerMismatch: 'A credencial não está disponível para este proprietário.',
  triggerHasIncomingConnection: 'O trigger não pode receber conexões.',
  invalidSourcePort: 'Uma conexão usa uma saída inválida.',
  duplicateSourcePort: 'Uma saída está conectada a mais de um destino.',
  conditionBranchesIncomplete: 'Conecte os ramos verdadeiro e falso da condição.',
  cycle: 'Remova o ciclo: o grafo deve ser acíclico.',
  unreachableNode: 'Todos os nodes devem ser alcançáveis a partir do trigger.',
  nodeLimitExceeded: 'O workflow aceita no máximo 50 nodes.',
  connectionLimitExceeded: 'O workflow aceita no máximo 100 conexões.',
}

function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function safeId(value: unknown): string | null {
  return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
    ? value : null
}

function graphErrors(body: unknown): GraphValidationError[] {
  if (!record(body) || !Array.isArray(body.errors)) return []
  return body.errors.slice(0, 100).flatMap((entry: unknown) => {
    if (!record(entry) || typeof entry.code !== 'string' ||
        !Object.prototype.hasOwnProperty.call(graphMessages, entry.code)) return []
    const code = entry.code as GraphErrorCode
    return [{ code, message: graphMessages[code], nodeId: safeId(entry.nodeId), connectionId: safeId(entry.connectionId) }]
  })
}

function problem(status: number, body?: unknown): ApiProblem {
  const messages: Record<number, [string, string]> = {
    401: ['Sessão expirada', 'Entre novamente para continuar. A edição local foi preservada.'],
    403: ['Acesso negado', 'Sua conta não tem permissão para esta operação.'],
    400: ['Requisição inválida', 'Revise os campos, configurações e limites do formulário.'],
    404: ['Recurso não encontrado', 'Este recurso não está disponível para o proprietário atual.'],
    409: ['Conflito ao salvar', 'O recurso foi alterado ou seu estado não permite esta operação. Recarregue antes de tentar novamente.'],
    413: ['Conteúdo excede o limite', 'Reduza o tamanho do conteúdo antes de enviar novamente.'],
    415: ['Formato não aceito', 'A operação exige conteúdo JSON.'],
    422: ['Grafo inválido', 'Corrija as regras de publicação indicadas abaixo.'],
    429: ['Limite de requisições', 'Aguarde antes de tentar novamente.'],
    503: ['Serviço indisponível', 'A API, o banco ou a configuração privada estão indisponíveis. Tente novamente mais tarde.'],
  }
  const [title, detail] = messages[status] ?? ['Não foi possível concluir', 'A operação falhou. Tente novamente mais tarde.']
  // Nunca encaminhar detail/title/instance/extensões arbitrários nem mensagens do fetch.
  return { status, title, detail, errors: status === 422 ? graphErrors(body) : [] }
}

async function request<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
  const controller = new AbortController()
  let timedOut = false
  const onAbort = () => controller.abort()
  signal?.addEventListener('abort', onAbort, { once: true })
  if (signal?.aborted) controller.abort()
  const timeout = window.setTimeout(() => {
    timedOut = true
    controller.abort()
  }, REQUEST_TIMEOUT_MS)

  try {
    const send = () => fetch(API_BASE + path, {
      method,
      headers: {
        Accept: 'application/json, application/problem+json',
        ...(authentication.token() ? { Authorization: 'Bearer ' + authentication.token() } : {}),
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: controller.signal,
      credentials: 'same-origin',
      cache: 'no-store',
    })
    if (!authentication.token()) { try { await authentication.refresh() } catch (e) { throw new ApiError(problem(e instanceof AuthError ? e.status : 503)) } }
    let response = await send()
    if (response.status === 401) {
      try { await authentication.refresh() } catch (e) { throw new ApiError(problem(e instanceof AuthError ? e.status : 503)) }
      response = await send()
    }

    let result: unknown
    const mediaType = response.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase()
    if (mediaType === 'application/json' || mediaType === 'application/problem+json') {
      try {
        result = parseExactJson(await response.text())
      } catch (cause) {
        if (controller.signal.aborted) throw new DOMException('Consulta cancelada.', 'AbortError')
        if (response.ok) throw new ApiError({
          status: cause instanceof JsonPrecisionError ? 0 : response.status,
          title: cause instanceof JsonPrecisionError ? 'Precisão numérica não suportada' : 'Resposta inválida',
          detail: cause instanceof JsonPrecisionError
            ? 'A resposta contém números fora da precisão suportada pelo navegador. Não editaremos valores arredondados.'
            : 'A API retornou uma resposta que não pôde ser interpretada.', errors: [],
        })
      }
    }
    if (!response.ok) throw new ApiError(problem(response.status, result))
    if (result === undefined || (!record(result) && !Array.isArray(result))) throw new ApiError({
      status: response.status, title: 'Resposta inválida',
      detail: 'A API não retornou o JSON esperado.', errors: [],
    })
    return result as T
  } catch (error) {
    if (signal?.aborted) throw new DOMException('Consulta cancelada.', 'AbortError')
    if (timedOut) throw new ApiError({
      status: 0, title: 'Tempo limite atingido',
      detail: method === 'GET'
        ? 'A API não respondeu em 10 segundos. Tente novamente.'
        : 'A API não respondeu em 10 segundos. O comando pode ter sido recebido; consulte o estado antes de repetir.',
      errors: [],
    })
    if (error instanceof ApiError) throw error
    throw new ApiError({
      status: 0, title: 'Sem conexão com a API',
      detail: method === 'GET'
        ? 'Verifique se o ambiente está iniciado e tente novamente.'
        : 'A resposta não chegou. Consulte o estado antes de repetir o comando.',
      errors: [],
    })
  } finally {
    window.clearTimeout(timeout)
    signal?.removeEventListener('abort', onAbort)
  }
}

function resource(id: string): string {
  return encodeURIComponent(id)
}

export interface WorkflowQuery {
  offset?: number
  limit?: number
  includeArchived?: boolean
}

export function listWorkflows(query: WorkflowQuery = {}, signal?: AbortSignal): Promise<WorkflowList> {
  const params = new URLSearchParams({
    offset: String(query.offset ?? 0),
    limit: String(query.limit ?? 20),
    includeArchived: String(query.includeArchived ?? false),
  })
  return request('/workflows?' + params, 'GET', undefined, signal)
}

export function getWorkflow(id: string, signal?: AbortSignal): Promise<WorkflowDetail> {
  return request('/workflows/' + resource(id), 'GET', undefined, signal)
}

export function create(body: CreateWorkflowRequest, signal?: AbortSignal): Promise<WorkflowDetail> {
  return request('/workflows', 'POST', body, signal)
}

export function updateMetadata(id: string, body: UpdateWorkflowRequest, signal?: AbortSignal): Promise<WorkflowDetail> {
  return request('/workflows/' + resource(id), 'PUT', body, signal)
}

export function saveDraft(id: string, body: SaveDraftRequest, signal?: AbortSignal): Promise<WorkflowDetail> {
  return request('/workflows/' + resource(id) + '/draft', 'PUT', body, signal)
}

export function createDraft(id: string, expectedRevision: number, signal?: AbortSignal): Promise<WorkflowDetail> {
  return request('/workflows/' + resource(id) + '/drafts', 'POST', { expectedRevision }, signal)
}

export function publish(id: string, expectedRevision: number, signal?: AbortSignal): Promise<WorkflowDetail> {
  return request('/workflows/' + resource(id) + '/publish', 'POST', { expectedRevision }, signal)
}

export function archive(id: string, expectedRevision: number, signal?: AbortSignal): Promise<WorkflowDetail> {
  return request('/workflows/' + resource(id) + '/archive', 'POST', { expectedRevision }, signal)
}

export function requestExecution(workflowId: string, signal?: AbortSignal): Promise<WorkflowExecution> {
  return request('/workflows/' + resource(workflowId) + '/executions', 'POST', undefined, signal)
}

export function listExecutions(workflowId: string, offset = 0, signal?: AbortSignal): Promise<ExecutionList> {
  const params = new URLSearchParams({ offset: String(offset), limit: '20' })
  return request('/workflows/' + resource(workflowId) + '/executions?' + params, 'GET', undefined, signal)
}

export function getExecution(id: string, signal?: AbortSignal): Promise<WorkflowExecution> {
  return request('/executions/' + resource(id), 'GET', undefined, signal)
}

export function listNodes(id: string, signal?: AbortSignal): Promise<NodeExecution[]> {
  return request('/executions/' + resource(id) + '/nodes', 'GET', undefined, signal)
}

export function listLogs(id: string, signal?: AbortSignal): Promise<ExecutionLog[]> {
  return request('/executions/' + resource(id) + '/logs', 'GET', undefined, signal)
}

export function listAttempts(id: string, signal?: AbortSignal): Promise<NodeExecutionAttempt[]> {
  return request('/executions/' + resource(id) + '/attempts', 'GET', undefined, signal)
}

export function cancel(id: string, signal?: AbortSignal): Promise<WorkflowExecution> {
  return request('/executions/' + resource(id) + '/cancel', 'POST', undefined, signal)
}
