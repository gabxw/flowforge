import type { WorkflowConnection } from '../api/contracts'
import type { NodeType } from './templates'

export interface GraphNode { nodeId: string; type: NodeType }
export interface ConnectionCandidate { sourceNodeId: string; targetNodeId: string; sourcePort: string }
export function outputPorts(type: NodeType): readonly string[] { return type === 'condition' ? ['true', 'false'] : ['next'] }

// Feedback do editor; o Domain/API continuam decidindo se o grafo pode ser publicado.
export function connectionIssue(nodes: readonly GraphNode[], connections: readonly WorkflowConnection[], candidate: ConnectionCandidate, replacingId?: string): string | null {
  const source = nodes.find(n => n.nodeId === candidate.sourceNodeId)
  const target = nodes.find(n => n.nodeId === candidate.targetNodeId)
  if (!source || !target) return 'Selecione nodes existentes para a conexão.'
  if (source.nodeId === target.nodeId) return 'Um node não pode se conectar a si mesmo.'
  if (target.type === 'webhookTrigger') return 'Webhook Trigger é uma entrada e não recebe conexões.'
  if (!outputPorts(source.type).includes(candidate.sourcePort)) return 'Essa porta não pertence ao tipo do node de origem.'
  const others = connections.filter(c => c.id !== replacingId)
  if (others.length >= 100) return 'O workflow aceita até 100 conexões.'
  if (others.some(c => c.sourceNodeId === source.nodeId && c.sourcePort === candidate.sourcePort)) return 'Essa saída já está conectada. Remova ou edite a conexão existente.'
  const pending = [target.nodeId]
  const seen = new Set<string>()
  while (pending.length) {
    const id = pending.pop()!
    if (id === source.nodeId) return 'Essa conexão criaria um ciclo. Workflows devem ser acíclicos.'
    if (seen.has(id)) continue
    seen.add(id)
    others.filter(c => c.sourceNodeId === id).forEach(c => pending.push(c.targetNodeId))
  }
  return null
}

// Ação explícita e limitada; não sobrescreve posições ao abrir/recarregar.
export function arrangePositions(nodes: readonly GraphNode[], connections: readonly WorkflowConnection[]) {
  const ids = new Set(nodes.map(n => n.nodeId))
  const edges = connections.filter(c => ids.has(c.sourceNodeId) && ids.has(c.targetNodeId))
  const column = new Map(nodes.map(n => [n.nodeId, 0]))
  for (let pass = 0; pass < nodes.length; pass++) {
    let changed = false
    for (const c of edges) {
      const next = (column.get(c.sourceNodeId) ?? 0) + 1
      if (next > (column.get(c.targetNodeId) ?? 0) && next < nodes.length) { column.set(c.targetNodeId, next); changed = true }
    }
    if (!changed) break
  }
  const rows = new Map<string, number>()
  const occupied = new Set<string>()
  const ordered = [...nodes].sort((a, b) => (column.get(a.nodeId) ?? 0) - (column.get(b.nodeId) ?? 0))
  for (const node of ordered) {
    const incoming = edges.find(c => c.targetNodeId === node.nodeId && rows.has(c.sourceNodeId))
    let row = incoming ? (rows.get(incoming.sourceNodeId) ?? 0) + (incoming.sourcePort === 'false' ? 1 : 0) : 0
    while (occupied.has(column.get(node.nodeId) + ':' + row)) row++
    occupied.add(column.get(node.nodeId) + ':' + row)
    rows.set(node.nodeId, row)
  }
  return nodes.map(n => ({ nodeId: n.nodeId, position: { x: (column.get(n.nodeId) ?? 0) * 320, y: (rows.get(n.nodeId) ?? 0) * 200 } }))
}
