import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Background, BackgroundVariant, Controls, Handle, MarkerType, Position, ReactFlow, ReactFlowProvider, useNodesInitialized, useReactFlow, useUpdateNodeInternals } from '@xyflow/react'
import type { AriaLabelConfig, Edge, Node, NodeChange, NodeProps, OnConnectEnd } from '@xyflow/react'
import type { WorkflowConnection } from '../api/contracts'
import type { GraphNode, ConnectionCandidate } from '../workflows/graph'
import { connectionIssue, outputPorts } from '../workflows/graph'
import { nodeTypeLabels } from '../workflows/templates'
import { NodeGlyph } from './Ui'

export interface CanvasNode extends GraphNode { position: { x: number; y: number } }
interface CanvasProps {
  nodes: CanvasNode[]; connections: WorkflowConnection[]; locked: boolean
  onPositions: (positions: { nodeId: string; position: { x: number; y: number } }[]) => void
  onConnect: (connection: ConnectionCandidate) => void
  onRemoveConnection: (id: string) => void
  onConfigure: (id: string) => void
}
type FlowNode = Node<{ type: CanvasNode['type']; index: number; locked: boolean }, 'workflow'>
const ariaLabels: Partial<AriaLabelConfig> = {
  'controls.zoomIn.ariaLabel': 'Aumentar zoom',
  'controls.zoomOut.ariaLabel': 'Diminuir zoom',
  'controls.fitView.ariaLabel': 'Enquadrar workflow',
  'node.a11yDescription.default': 'Use Tab para focar um node e Enter ou Espaço para selecionar. Use as setas para mover quando estiver editável.',
  'edge.a11yDescription.default': 'Conexão entre etapas. Use a lista de conexões para editar ou remover.',
}
function WorkflowCard({ id, data, selected }: NodeProps<FlowNode>) {
  const update = useUpdateNodeInternals()
  useEffect(() => { update(id) }, [id, data.type, update])
  return <div className={'flow-card' + (selected ? ' is-selected' : '')} data-testid={'canvas-node-' + id}>
    <Handle type="target" id="input" position={Position.Left} isConnectable={!data.locked && data.type !== 'webhookTrigger'}
      aria-label={'Entrada do node ' + (data.index + 1)} data-testid={'input-' + id}
      style={data.type === 'webhookTrigger' ? { opacity: 0, pointerEvents: 'none' } : undefined} />
    <div className="flow-card-index">{String(data.index + 1).padStart(2, '0')}</div>
    <header><NodeGlyph type={data.type} /><strong>{nodeTypeLabels[data.type]}</strong></header>
    <code>{id.slice(0, 8)}</code>
    {outputPorts(data.type).map(port => <div key={port}>
      <span className={'flow-port-label flow-port-' + port}>{port}</span>
      <Handle type="source" id={port} position={Position.Right} isConnectable={!data.locked}
        aria-label={'Saída ' + port + ' do node ' + (data.index + 1)} data-testid={'output-' + id + '-' + port}
        style={{ top: port === 'true' ? '35%' : port === 'false' ? '72%' : '50%' }} />
    </div>)}
  </div>
}
const nodeTypes = { workflow: WorkflowCard }

function FitNodes({ count }: { count: number }) {
  const initialized = useNodesInitialized()
  const { fitView, getNodes } = useReactFlow()
  const previousCount = useRef(-1)
  useEffect(() => {
    if (initialized && getNodes().length === count && count !== previousCount.current) {
      previousCount.current = count
      void fitView({ padding: .25, minZoom: .25, maxZoom: 1 })
    }
  }, [initialized, count, fitView, getNodes])
  return null
}

function CanvasBody({ nodes, connections, locked, onPositions, onConnect, onRemoveConnection, onConfigure }: CanvasProps) {
  const [dimensions, setDimensions] = useState<Record<string, { width: number; height: number }>>({})
  const [selectedNode, setSelectedNode] = useState<string | null>(null)
  const [selectedEdge, setSelectedEdge] = useState<string | null>(null)
  const [issue, setIssue] = useState<string | null>(null)
  const flowNodes = useMemo<FlowNode[]>(() => nodes.map((n, index) => ({
    id: n.nodeId, type: 'workflow', position: n.position, measured: dimensions[n.nodeId], selected: n.nodeId === selectedNode,
    draggable: !locked, data: { type: n.type, index, locked }, ariaLabel: 'Etapa ' + (index + 1) + ': ' + nodeTypeLabels[n.type],
  })), [nodes, selectedNode, locked, dimensions])
  const edges = useMemo<Edge[]>(() => connections.map(c => {
    const source = nodes.find(n => n.nodeId === c.sourceNodeId)
    const validPort = source && outputPorts(source.type).includes(c.sourcePort)
    const problem = connectionIssue(nodes, connections, c, c.id)
    const color = problem ? '#ff8a91' : c.sourcePort === 'true' ? '#6fe3b4' : c.sourcePort === 'false' ? '#ff8a91' : '#ffb547'
    return {
      id: c.id, source: c.sourceNodeId, target: c.targetNodeId,
      sourceHandle: validPort ? c.sourcePort : source ? outputPorts(source.type)[0] : null, targetHandle: 'input',
      type: 'smoothstep', label: problem ? c.sourcePort + ' · revisar' : c.sourcePort,
      selected: c.id === selectedEdge, reconnectable: false,
      style: { stroke: color, strokeWidth: c.id === selectedEdge ? 3 : 1.6, strokeDasharray: problem ? '5 5' : undefined },
      labelStyle: { fill: color, fontSize: 12 }, labelBgStyle: { fill: '#0a1a2c' },
      markerEnd: { type: MarkerType.ArrowClosed, color },
      data: { issue: problem },
      ariaLabel: 'Conexão ' + c.sourcePort + ': ' + c.sourceNodeId.slice(0, 8) + ' para ' + c.targetNodeId.slice(0, 8),
    }
  }), [nodes, connections, selectedEdge])
  const candidate = useCallback((c: { source: string; target: string; sourceHandle?: string | null }): ConnectionCandidate => ({
    sourceNodeId: c.source, targetNodeId: c.target, sourcePort: c.sourceHandle ?? '',
  }), [])
  function nodeChanges(changes: NodeChange<FlowNode>[]) {
    // Dimensões pertencem à projeção do canvas, não ao DTO salvo.
    const sizes = changes.flatMap(c => c.type === 'dimensions' && c.dimensions ? [{ id: c.id, size: c.dimensions }] : [])
    if (sizes.length) setDimensions(previous => {
      const changed = sizes.filter(s => previous[s.id]?.width !== s.size.width || previous[s.id]?.height !== s.size.height)
      if (!changed.length) return previous
      return { ...previous, ...Object.fromEntries(changed.map(s => [s.id, s.size])) }
    })
    const selection = changes.find(c => c.type === 'select' && c.selected)
    if (selection?.type === 'select') setSelectedNode(selection.id)
    if (locked) return
    const moved = changes.flatMap(c => c.type === 'position' && c.position &&
      Number.isFinite(c.position.x) && Number.isFinite(c.position.y) ? [{ nodeId: c.id, position: c.position }] : [])
    if (moved.length) onPositions(moved)
  }
  const end: OnConnectEnd = (_, state) => {
    if (!state.isValid && state.fromNode && state.toNode && state.fromHandle) {
      setIssue(connectionIssue(nodes, connections, {
        sourceNodeId: state.fromNode.id, targetNodeId: state.toNode.id, sourcePort: state.fromHandle.id ?? '',
      }) ?? 'Conecte uma saída à entrada de outro node.')
    }
  }

  return <div className="visual-editor">
    <div className="visual-caption">
      <div><h3>Editor visual</h3><p>{locked ? 'Versão em modo de leitura. Use os controles para explorar o grafo.' : 'Arraste os cards. Ligue uma saída à entrada de outro node. Selecione um card para configurar.'}</p></div>
      <span className="muted small">{nodes.length} {nodes.length === 1 ? 'node' : 'nodes'} e {connections.length} {connections.length === 1 ? 'conexão' : 'conexões'}</span>
    </div>
    {issue && <p role="alert" className="notice notice-error">{issue}</p>}
    <div className="workflow-canvas" aria-label="Canvas do workflow" data-testid="workflow-canvas">
      <ReactFlow<FlowNode> nodes={flowNodes} edges={edges} nodeTypes={nodeTypes}
        onNodesChange={nodeChanges} onNodeClick={(_, node) => { setSelectedNode(node.id) }}
        onNodeDoubleClick={(_, node) => { if (!locked) onConfigure(node.id) }} zoomOnDoubleClick={false}
        onEdgeClick={(_, edge) => { setSelectedEdge(edge.id); setIssue(typeof edge.data?.issue === 'string' ? edge.data.issue : null) }}
        onPaneClick={() => { setSelectedNode(null); setSelectedEdge(null); setIssue(null) }}
        onConnect={c => {
          const proposed = candidate(c)
          const problem = connectionIssue(nodes, connections, proposed)
          if (locked || problem) { setIssue(problem); return }
          onConnect(proposed); setIssue(null)
        }}
        onConnectEnd={end} isValidConnection={c => !locked && connectionIssue(nodes, connections, candidate(c)) === null}
        nodesDraggable={!locked} nodesConnectable={!locked} edgesReconnectable={false}
        deleteKeyCode={null} snapToGrid snapGrid={[20, 20]} minZoom={.2} maxZoom={1.75} colorMode="dark"
        aria-label="Grafo do workflow" ariaLabelConfig={ariaLabels} fitView fitViewOptions={{ padding: .25, minZoom: .25, maxZoom: 1 }}>
        <Background variant={BackgroundVariant.Dots} color="#24496d" gap={24} />
        <Controls showInteractive={false} />
        <FitNodes count={nodes.length} />
      </ReactFlow>
    </div>
    {!locked && <div className="visual-actions">
      <button type="button" className="btn" disabled={!selectedNode || !nodes.some(n => n.nodeId === selectedNode)}
        onClick={() => { if (selectedNode) onConfigure(selectedNode) }}>Configurar node selecionado</button>
      <button type="button" className="btn btn-danger" disabled={!selectedEdge || !connections.some(c => c.id === selectedEdge)}
        onClick={() => { if (selectedEdge) onRemoveConnection(selectedEdge); setSelectedEdge(null) }}>Remover conexão selecionada</button>
      <p className="hint">Seleção, zoom e pan não alteram a versão. Salvar persiste posições, configurações e conexões.</p>
    </div>}
  </div>
}
export function WorkflowCanvas(props: CanvasProps) {
  return <ReactFlowProvider><CanvasBody {...props} /></ReactFlowProvider>
}
