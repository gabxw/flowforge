// Contratos camelCase do transporte HTTP. Datas permanecem strings ISO-8601 UTC.
export type JsonScalar = string | number | boolean | null
export type JsonValue = JsonScalar | JsonValue[] | { [key: string]: JsonValue }

export type HttpRequestMethod = 'get' | 'post' | 'put' | 'patch' | 'delete' | 'head' | 'options'
export type ConditionOperator =
  | 'exists' | 'equals' | 'notEquals' | 'greaterThan'
  | 'greaterThanOrEqual' | 'lessThan' | 'lessThanOrEqual'

export type TransformField = { targetProperty: string } & (
  | { sourcePointer: string; literal?: never }
  | { literal: JsonValue; sourcePointer?: never }
)

export type NodeConfiguration =
  | { type: 'webhookTrigger' }
  | {
      type: 'httpRequest'
      url: string
      method: HttpRequestMethod
      maxAttempts?: number
      supportsIdempotency?: boolean
    }
  | { type: 'delay'; durationTicks: number }
  | { type: 'condition'; sourcePointer: string; operation: 'exists'; expectedValue?: never }
  | { type: 'condition'; sourcePointer: string; operation: 'equals' | 'notEquals'; expectedValue: JsonScalar }
  | {
      type: 'condition'
      sourcePointer: string
      operation: 'greaterThan' | 'greaterThanOrEqual' | 'lessThan' | 'lessThanOrEqual'
      expectedValue: number
    }
  | { type: 'transformJson'; fields: TransformField[] }
  | { type: 'log'; message: string }

export interface WorkflowNode {
  nodeId: string
  configuration: NodeConfiguration
  position: { x: number; y: number }
  credentialId?: string | null
}

export interface WorkflowConnection {
  id: string
  sourceNodeId: string
  targetNodeId: string
  sourcePort: string
}

export interface WorkflowVersion {
  id: string
  versionNumber: number
  status: 'draft' | 'published'
  createdAt: string
  publishedAt: string | null
  revision: number
  nodes: WorkflowNode[]
  connections: WorkflowConnection[]
}

export interface WorkflowSummary {
  id: string
  name: string
  description: string | null
  createdAt: string
  updatedAt: string
  archivedAt: string | null
  revision: number
  currentPublishedVersionId: string | null
}

export interface WorkflowDetail extends WorkflowSummary {
  ownerUserId: string
  draftVersionId: string | null
  versions: WorkflowVersion[]
}

export interface WorkflowList {
  items: WorkflowSummary[]
  offset: number
  limit: number
}

export interface CreateWorkflowRequest {
  name: string
  description?: string | null
}

export interface UpdateWorkflowRequest extends CreateWorkflowRequest {
  expectedRevision: number
}

export interface SaveDraftRequest {
  expectedRevision: number
  nodes: WorkflowNode[]
  connections: WorkflowConnection[]
}

export type WorkflowExecutionStatus = 'pending' | 'running' | 'succeeded' | 'failed' | 'cancelled'
export type NodeExecutionStatus = WorkflowExecutionStatus | 'retrying' | 'skipped'
export type NodeAttemptStatus = 'running' | 'succeeded' | 'failed' | 'cancelled' | 'interrupted'
export type ExternalOutcome = 'notApplicable' | 'notStarted' | 'knownSuccess' | 'knownFailure' | 'unknown'

export type ExecutionFailureCode =
  | 'engineUnavailable' | 'unsupportedNode' | 'nodeFailed' | 'nodeTimeout'
  | 'interruptedNode' | 'invalidExecutorResult' | 'contextLimitExceeded'
  | 'httpDestinationDenied' | 'credentialUnavailable' | 'httpRemoteFailure'
  | 'httpResponseLimitExceeded' | 'httpResponseInvalid' | 'httpTransportFailed'
  | 'httpResponseSensitive' | 'conditionValueNotComparable' | 'transformSourceMissing'
  | 'jsonPointerAmbiguous' | 'transformValueInvalid' | 'executionDeadlineExceeded' | 'attemptLimitExceeded'

export interface WorkflowExecution {
  id: string
  workflowId: string
  workflowVersionId: string
  correlationId: string
  status: WorkflowExecutionStatus
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  errorCode: ExecutionFailureCode | null
  cancelRequestedAt: string | null
  resumeAt: string | null
  deadlineAt: string | null
}

export interface ExecutionList {
  items: WorkflowExecution[]
  offset: number
  limit: number
}

// A API expõe somente metadados dos payloads e dos logs, nunca seus conteúdos.
export interface PayloadSummary {
  byteLength: number
  kind: 'Object' | 'Array' | 'String' | 'Number' | 'True' | 'False' | 'Null'
}

export interface NodeExecution {
  id: string
  executionId: string
  workflowVersionId: string
  nodeId: string
  status: NodeExecutionStatus
  attemptCount: number
  startedAt: string | null
  finishedAt: string | null
  input: PayloadSummary | null
  output: PayloadSummary | null
  errorCode: ExecutionFailureCode | null
  credentialRevisionUsed: number | null
  retryAt: string | null
}

export interface ExecutionLog {
  id: string
  executionId: string
  nodeExecutionId: string
  createdAt: string
  eventCode: 'logRecorded'
  messageByteLength: number
}

export interface NodeExecutionAttempt {
  id: string
  executionId: string
  nodeExecutionId: string
  number: number
  status: NodeAttemptStatus
  startedAt: string
  finishedAt: string | null
  errorCode: ExecutionFailureCode | null
  externalOutcome: ExternalOutcome
  dispatchedAt: string | null
  httpStatusCode: number | null
  credentialRevisionUsed: number | null
  retryAt: string | null
  legacy: boolean
}

export type GraphErrorCode =
  | 'emptyGraph' | 'triggerCount' | 'duplicateNodeId' | 'duplicateConnectionId'
  | 'nodeVersionMismatch' | 'connectionVersionMismatch' | 'missingSourceNode'
  | 'missingTargetNode' | 'credentialOwnerMismatch' | 'triggerHasIncomingConnection'
  | 'invalidSourcePort' | 'duplicateSourcePort' | 'conditionBranchesIncomplete'
  | 'cycle' | 'unreachableNode' | 'nodeLimitExceeded' | 'connectionLimitExceeded'

export interface GraphValidationError {
  code: GraphErrorCode
  message: string
  nodeId: string | null
  connectionId: string | null
}

export interface ApiProblem {
  status: number
  title: string
  detail: string
  errors: GraphValidationError[]
}
