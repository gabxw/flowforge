import type { NodeConfiguration, WorkflowConnection, WorkflowNode } from '../api/contracts'

export type NodeType = NodeConfiguration['type']

export const nodeTypeLabels: Record<NodeType, string> = {
  webhookTrigger: 'Webhook Trigger',
  httpRequest: 'HTTP Request',
  delay: 'Delay',
  condition: 'Condition',
  transformJson: 'Transform JSON',
  log: 'Log',
}

export function defaultConfiguration(type: NodeType): NodeConfiguration {
  switch (type) {
    case 'webhookTrigger': return { type }
    case 'httpRequest':
      return { type, url: 'https://invalido.example/', method: 'get', maxAttempts: 1, supportsIdempotency: false }
    case 'delay': return { type, durationTicks: 10_000_000 }
    case 'condition': return { type, sourcePointer: '/aprovado', operation: 'equals', expectedValue: true }
    case 'transformJson': return { type, fields: [{ targetProperty: 'recebido', sourcePointer: '' }] }
    case 'log': return { type, message: 'Workflow processado' }
  }
}

function newNode(type: NodeType, index: number): WorkflowNode {
  return {
    nodeId: crypto.randomUUID(),
    position: { x: index * 240, y: 0 },
    configuration: defaultConfiguration(type),
    credentialId: null,
  }
}

function connect(source: WorkflowNode, target: WorkflowNode, port: 'next' | 'true' | 'false' = 'next'): WorkflowConnection {
  return {
    id: crypto.randomUUID(),
    sourceNodeId: source.nodeId,
    targetNodeId: target.nodeId,
    sourcePort: port,
  }
}

export function createWorkflowTemplate(template: 'simple' | 'branches'): {
  nodes: WorkflowNode[]
  connections: WorkflowConnection[]
} {
  const trigger = newNode('webhookTrigger', 0)
  if (template === 'simple') {
    const log = newNode('log', 1)
    return { nodes: [trigger, log], connections: [connect(trigger, log)] }
  }

  const condition = newNode('condition', 1)
  const transform = newNode('transformJson', 2)
  const delay = newNode('delay', 3)
  const accepted = newNode('log', 4)
  const rejected = newNode('log', 2)
  rejected.position.y = 180
  accepted.configuration = { type: 'log', message: 'Ramo aprovado processado' }
  rejected.configuration = { type: 'log', message: 'Ramo alternativo processado' }
  return {
    nodes: [trigger, condition, transform, delay, accepted, rejected],
    connections: [
      connect(trigger, condition),
      connect(condition, transform, 'true'),
      connect(condition, rejected, 'false'),
      connect(transform, delay),
      connect(delay, accepted),
    ],
  }
}
