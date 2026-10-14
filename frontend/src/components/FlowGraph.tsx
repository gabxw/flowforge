import { useEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties, ReactNode } from 'react'
import { Background, BackgroundVariant, BaseEdge, Controls, Handle, Position, ReactFlow, ReactFlowProvider, getBezierPath, useNodesInitialized, useReactFlow, useUpdateNodeInternals } from '@xyflow/react'
import type { AriaLabelConfig, Edge, EdgeProps, Node, NodeChange, NodeProps, OnConnectEnd } from '@xyflow/react'
import type { NodeExecutionStatus, WorkflowConnection } from '../api/contracts'
import type { ConnectionCandidate, GraphNode } from '../workflows/graph'
import { arrangePositions, connectionIssue, outputPorts } from '../workflows/graph'
import { nodeTypeLabels } from '../workflows/templates'
import { nodeTone, statusLabel } from './format'
import { NodeGlyph } from './Ui'

export interface FlowGraphNode extends GraphNode { position: { x: number; y: number }; summary: string }
export type FlowGraphMode = 'edit' | 'view' | 'run'
export interface RunOverlay {
  status: Record<string, NodeExecutionStatus>
  order: Record<string, number>
  replay: number
}
interface FlowGraphProps {
  nodes: FlowGraphNode[]
  connections: WorkflowConnection[]
  mode: FlowGraphMode
  live?: boolean
  run?: RunOverlay
  highlight?: string | null
  selectedNodeId?: string | null
  selectedConnectionId?: string | null
  onSelect?: (selection: { nodeId: string | null; connectionId: string | null }) => void
  onPositions?: (positions: { nodeId: string; position: { x: number; y: number } }[]) => void
  onConnect?: (connection: ConnectionCandidate) => void
  onIssue?: (issue: string | null) => void
  onOpen?: (nodeId: string) => void
  overlay?: ReactNode
  label?: string
}

// Cores literais para o traço SVG; acompanham os tokens --ok, --fail e o cinza dos fios.
const wireColor = { next: '#8b8b95', true: '#6fe3b4', false: '#ff8a91', issue: '#ff8a91' } as const

type CardData = {
  type: GraphNode['type']; index: number; summary: string; locked: boolean
  status?: NodeExecutionStatus; order?: number; replay?: number; highlighted: boolean; vertical: boolean
}
type FlowNode = Node<CardData, 'step'>
type WireData = { port: string; issue: string | null; signal: 'loop' | 'replay' | null; order: number; dim: boolean }
type FlowEdge = Edge<WireData, 'wire'>

const ariaLabels: Partial<AriaLabelConfig> = {
  'controls.zoomIn.ariaLabel': 'Aumentar zoom',
  'controls.zoomOut.ariaLabel': 'Diminuir zoom',
  'controls.fitView.ariaLabel': 'Enquadrar workflow',
  'node.a11yDescription.default': 'Use Tab para focar uma etapa e Enter ou Espaço para selecionar. Use as setas para mover quando estiver editável.',
  'edge.a11yDescription.default': 'Conexão entre etapas. A lista de conexões no painel lateral permite editar ou remover.',
}

function StepCard({ id, data, selected }: NodeProps<FlowNode>) {
  const update = useUpdateNodeInternals()
  useEffect(() => { update(id) }, [id, data.type, data.vertical, update])
  const ports = outputPorts(data.type)
  const vertical = data.vertical
  // Na leitura vertical (telas estreitas) a entrada fica no topo e as saídas embaixo.
  const along = (port: string) => port === 'true' ? '30%' : port === 'false' ? '70%' : '50%'
  const classes = ['step-card', ports.length > 1 && 'has-branches', vertical && 'is-vertical', selected && 'is-selected', data.highlighted && 'is-highlighted',
    data.status && 'run-' + data.status, data.replay !== undefined && 'replay'].filter(Boolean).join(' ')
  return <div className={classes} data-testid={'canvas-node-' + id}
    style={{ '--tone': nodeTone[data.type], '--order': data.order ?? 0 } as CSSProperties} key={data.replay}>
    <Handle type="target" id="input" position={vertical ? Position.Top : Position.Left} isConnectable={!data.locked && data.type !== 'webhookTrigger'}
      aria-label={'Entrada da etapa ' + (data.index + 1)} data-testid={'input-' + id}
      className={data.type === 'webhookTrigger' ? 'handle-hidden' : undefined} />
    <span className="step-index">{String(data.index + 1).padStart(2, '0')}</span>
    <header>
      <NodeGlyph type={data.type} />
      <strong>{nodeTypeLabels[data.type]}</strong>
      {data.status && <span className="step-lamp" title={statusLabel(data.status)} />}
    </header>
    <p className="step-summary">{data.summary}</p>
    {ports.map(port => <div key={port}>
      {port !== 'next' && <span className={'step-port-label port-' + port}>{port}</span>}
      <Handle type="source" id={port} position={vertical ? Position.Bottom : Position.Right} isConnectable={!data.locked}
        aria-label={'Saída ' + port + ' da etapa ' + (data.index + 1)} data-testid={'output-' + id + '-' + port}
        className={'handle-' + port} style={vertical ? { left: along(port) } : { top: port === 'true' ? '44%' : port === 'false' ? '76%' : '50%' }} />
    </div>)}
  </div>
}

function Wire({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data, selected }: EdgeProps<FlowEdge>) {
  const [path] = getBezierPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, curvature: .35 })
  const color = data?.issue ? wireColor.issue : wireColor[(data?.port ?? 'next') as keyof typeof wireColor] ?? wireColor.next
  return <>
    <BaseEdge id={id} path={path} interactionWidth={20}
      style={{ stroke: color, strokeWidth: selected ? 3 : 1.6, strokeDasharray: data?.issue ? '6 5' : undefined, opacity: data?.dim ? .35 : 1 }} />
    {data?.signal === 'loop' && <path className="wire-signal" d={path} />}
    {data?.signal === 'replay' && <path className={'wire-replay port-' + data.port} d={path} pathLength={1}
      style={{ '--order': data.order } as CSSProperties} />}
  </>
}

const nodeTypes = { step: StepCard }
const edgeTypes = { wire: Wire }

function FitOnChange({ count, replay, vertical }: { count: number; replay?: number; vertical: boolean }) {
  const initialized = useNodesInitialized()
  const { fitView, getNodes } = useReactFlow()
  const previous = useRef('')
  useEffect(() => {
    const key = count + ':' + (replay ?? 0) + ':' + vertical
    if (initialized && getNodes().length === count && key !== previous.current) {
      previous.current = key
      void fitView({ padding: .18, minZoom: .2, maxZoom: 1 })
    }
  }, [initialized, count, replay, vertical, fitView, getNodes])
  return null
}

function GraphBody({ nodes, connections, mode, live = false, run, highlight = null, selectedNodeId = null, selectedConnectionId = null,
  onSelect, onPositions, onConnect, onIssue, onOpen, overlay, label = 'Grafo do workflow' }: FlowGraphProps) {
  const locked = mode !== 'edit'
  const [dimensions, setDimensions] = useState<Record<string, { width: number; height: number }>>({})
  const frame = useRef<HTMLDivElement>(null)
  const [narrow, setNarrow] = useState(false)
  useEffect(() => {
    const element = frame.current
    if (!element) return
    const observer = new ResizeObserver(([entry]) => setNarrow(entry.contentRect.width < 600))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])
  // Só leitura e execução trocam de orientação: no rascunho as posições salvas são o que se edita.
  const vertical = narrow && mode !== 'edit'
  const layout = useMemo(() => {
    if (!vertical) return null
    const arranged = arrangePositions(nodes, connections)
    return new Map(arranged.map(p => [p.nodeId, { x: Math.round(p.position.y / 140) * 230, y: Math.round(p.position.x / 260) * 130 }]))
  }, [vertical, nodes, connections])
  const depth = layout ? Math.max(0, ...[...layout.values()].map(p => p.y)) / 130 + 1 : 0
  const flowNodes = useMemo<FlowNode[]>(() => nodes.map((n, index) => {
    const status = run?.status[n.nodeId]
    return {
      id: n.nodeId, type: 'step', position: layout?.get(n.nodeId) ?? n.position, measured: dimensions[n.nodeId], selected: n.nodeId === selectedNodeId, draggable: !locked,
      data: { type: n.type, index, summary: n.summary, locked, status, order: run?.order[n.nodeId], replay: run ? run.replay : undefined, highlighted: highlight === n.nodeId, vertical },
      ariaLabel: 'Etapa ' + (index + 1) + ': ' + nodeTypeLabels[n.type] + (status ? ', ' + statusLabel(status) : ''),
    }
  }), [nodes, selectedNodeId, locked, run, highlight, dimensions, layout, vertical])
  const edges = useMemo<FlowEdge[]>(() => connections.map(c => {
    const source = nodes.find(n => n.nodeId === c.sourceNodeId)
    const validPort = !!source && outputPorts(source.type).includes(c.sourcePort)
    const issue = mode === 'edit' ? connectionIssue(nodes, connections, c, c.id) : null
    // No modo execução, um fio está aceso quando as duas pontas rodaram.
    const sourceRan = run?.order[c.sourceNodeId]
    const targetStatus = run?.status[c.targetNodeId]
    const taken = !!run && sourceRan !== undefined && targetStatus !== undefined && targetStatus !== 'skipped' && targetStatus !== 'pending'
    return {
      id: c.id, type: 'wire', source: c.sourceNodeId, target: c.targetNodeId,
      sourceHandle: validPort ? c.sourcePort : source ? outputPorts(source.type)[0] : null, targetHandle: 'input',
      selected: c.id === selectedConnectionId, reconnectable: false, focusable: !locked,
      data: { port: c.sourcePort, issue, signal: run ? (taken ? 'replay' : null) : live ? 'loop' : null, order: sourceRan ?? 0, dim: !!run && !taken },
      ariaLabel: 'Conexão ' + c.sourcePort + ' de ' + (source ? nodeTypeLabels[source.type] : 'etapa ausente') + ' para ' +
        (nodes.find(n => n.nodeId === c.targetNodeId) ? nodeTypeLabels[nodes.find(n => n.nodeId === c.targetNodeId)!.type] : 'etapa ausente'),
    }
  }), [nodes, connections, selectedConnectionId, mode, live, run, locked])
  const candidate = (c: { source: string; target: string; sourceHandle?: string | null }): ConnectionCandidate =>
    ({ sourceNodeId: c.source, targetNodeId: c.target, sourcePort: c.sourceHandle ?? '' })

  function nodeChanges(changes: NodeChange<FlowNode>[]) {
    // Dimensões pertencem à projeção do canvas, não ao DTO salvo.
    const sizes = changes.flatMap(c => c.type === 'dimensions' && c.dimensions ? [{ id: c.id, size: c.dimensions }] : [])
    if (sizes.length) setDimensions(previous => {
      const changed = sizes.filter(x => previous[x.id]?.width !== x.size.width || previous[x.id]?.height !== x.size.height)
      return changed.length ? { ...previous, ...Object.fromEntries(changed.map(x => [x.id, x.size])) } : previous
    })
    const picked = changes.find(c => c.type === 'select' && c.selected)
    if (picked?.type === 'select') onSelect?.({ nodeId: picked.id, connectionId: null })
    if (locked) return
    const moved = changes.flatMap(c => c.type === 'position' && c.position &&
      Number.isFinite(c.position.x) && Number.isFinite(c.position.y) ? [{ nodeId: c.id, position: c.position }] : [])
    if (moved.length) onPositions?.(moved)
  }
  const end: OnConnectEnd = (_, state) => {
    if (!state.isValid && state.fromNode && state.toNode && state.fromHandle) {
      onIssue?.(connectionIssue(nodes, connections, {
        sourceNodeId: state.fromNode.id, targetNodeId: state.toNode.id, sourcePort: state.fromHandle.id ?? '',
      }) ?? 'Ligue uma saída à entrada de outra etapa.')
    }
  }

  return <div ref={frame} className={'flow-graph mode-' + mode + (vertical ? ' is-vertical' : '')} aria-label="Canvas do workflow" data-testid="workflow-canvas"
    style={vertical ? { height: Math.min(720, Math.max(360, depth * 130 + 140)) } : undefined}>
    <ReactFlow<FlowNode, FlowEdge> nodes={flowNodes} edges={edges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
      onNodesChange={nodeChanges}
      onNodeClick={(_, node) => onSelect?.({ nodeId: node.id, connectionId: null })}
      onNodeDoubleClick={(_, node) => onOpen?.(node.id)} zoomOnDoubleClick={false}
      onEdgeClick={(_, edge) => { if (!locked) { onSelect?.({ nodeId: null, connectionId: edge.id }); onIssue?.(edge.data?.issue ?? null) } }}
      onPaneClick={() => { onSelect?.({ nodeId: null, connectionId: null }); onIssue?.(null) }}
      onConnect={c => {
        const proposed = candidate(c)
        const problem = connectionIssue(nodes, connections, proposed)
        if (locked || problem) { onIssue?.(problem); return }
        onConnect?.(proposed); onIssue?.(null)
      }}
      onConnectEnd={end} isValidConnection={c => !locked && connectionIssue(nodes, connections, candidate(c)) === null}
      nodesDraggable={!locked} nodesConnectable={!locked} elementsSelectable edgesReconnectable={false}
      deleteKeyCode={null} snapToGrid snapGrid={[20, 20]} minZoom={.2} maxZoom={1.6} colorMode="dark"
      proOptions={{ hideAttribution: false }} aria-label={label} ariaLabelConfig={ariaLabels}
      fitView fitViewOptions={{ padding: .18, minZoom: .2, maxZoom: 1 }}>
      <Background variant={BackgroundVariant.Dots} color="#34343b" gap={20} size={1.2} />
      <Controls showInteractive={false} />
      <FitOnChange count={nodes.length} replay={run?.replay} vertical={vertical} />
    </ReactFlow>
    {overlay}
  </div>
}

export function FlowGraph(props: FlowGraphProps) {
  return <ReactFlowProvider><GraphBody {...props} /></ReactFlowProvider>
}
