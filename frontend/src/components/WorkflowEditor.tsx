import { useEffect, useId, useState } from 'react'
import type { FormEvent } from 'react'
import type { WorkflowConnection, WorkflowNode, WorkflowVersion } from '../api/contracts'
import { createWorkflowTemplate, defaultConfiguration, nodeTypeLabels } from '../workflows/templates'
import type { NodeType } from '../workflows/templates'
import { parseConfiguration } from '../workflows/configuration'
import { describeText } from '../workflows/describe'
import { arrangePositions, connectionIssue, outputPorts } from '../workflows/graph'
import type { ConnectionCandidate } from '../workflows/graph'
import { FlowGraph } from './FlowGraph'
import { ConnectionInspector, InspectorOverview, StepInspector } from './StepInspector'
import type { InspectorMode } from './StepInspector'

interface WorkflowEditorProps {
  version: WorkflowVersion
  disabled: boolean
  saving: boolean
  onSave: (nodes: WorkflowNode[], connections: WorkflowConnection[]) => Promise<void>
  onDirtyChange?: (dirty: boolean) => void
  readOnly?: boolean
}

interface EditableNode {
  node: WorkflowNode
  type: NodeType
  configurationText: string
  credentialText: string
}

interface LocalDraft {
  versionId: string
  revision: number
  nodes: EditableNode[]
  connections: WorkflowConnection[]
  dirty: boolean
}

const MAX_DRAFT_BYTES = 1024 * 1024
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

function editableNode(node: WorkflowNode): EditableNode {
  return {
    node,
    type: node.configuration.type,
    configurationText: JSON.stringify(node.configuration, null, 2),
    credentialText: node.credentialId ?? '',
  }
}

function fromVersion(version: WorkflowVersion): LocalDraft {
  return {
    versionId: version.id,
    revision: version.revision,
    nodes: version.nodes.map(editableNode),
    connections: version.connections.map(connection => ({ ...connection })),
    dirty: false,
  }
}

function validIdentity(value: string) {
  return UUID.test(value) && value.toLowerCase() !== '00000000-0000-0000-0000-000000000000'
}

class StepError extends Error {
  constructor(readonly nodeId: string, message: string) { super(message) }
}

function buildNodes(nodes: EditableNode[]): WorkflowNode[] {
  return nodes.map((entry, index) => {
    try {
      const credentialId = entry.credentialText.trim()
      if (credentialId && (entry.type !== 'httpRequest' || !validIdentity(credentialId))) {
        throw new Error('A referência de credencial deve ser um UUID não vazio em um HTTP Request.')
      }
      const configuration = parseConfiguration(entry.configurationText, entry.type)
      return { nodeId: entry.node.nodeId, position: { x: entry.node.position.x, y: entry.node.position.y }, configuration, credentialId: credentialId || null }
    } catch (error) {
      throw new StepError(entry.node.nodeId, 'Etapa ' + (index + 1) + ': ' + (error instanceof Error ? error.message : 'configuração inválida.'))
    }
  })
}

export default function WorkflowEditor({ version, disabled, saving, onSave, onDirtyChange, readOnly = false }: WorkflowEditorProps) {
  const editorId = useId()
  const [draft, setDraft] = useState<LocalDraft>(() => fromVersion(version))
  const [newType, setNewType] = useState<NodeType>('log')
  const [error, setError] = useState<string | null>(null)
  const [issue, setIssue] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [selectedNode, setSelectedNode] = useState<string | null>(null)
  const [selectedConnection, setSelectedConnection] = useState<string | null>(null)
  const [mode, setMode] = useState<InspectorMode>('fields')
  const locked = disabled || saving || submitting

  useEffect(() => {
    // Um conflito/recarregamento da mesma versão não apaga o rascunho local.
    if (draft.versionId !== version.id || (!draft.dirty && draft.revision !== version.revision)) {
      setDraft(fromVersion(version))
      setError(null)
    }
  }, [version, draft.versionId, draft.revision, draft.dirty])

  useEffect(() => {
    onDirtyChange?.(draft.dirty)
  }, [draft.dirty, onDirtyChange])

  function edit(update: (current: LocalDraft) => LocalDraft) {
    setDraft(current => ({ ...update(current), dirty: true }))
    setError(null)
  }

  function changeNode(nodeId: string, update: (entry: EditableNode) => EditableNode) {
    edit(current => ({
      ...current,
      nodes: current.nodes.map(entry => entry.node.nodeId === nodeId ? update(entry) : entry),
    }))
  }

  function select(selection: { nodeId: string | null; connectionId: string | null }) {
    setSelectedNode(selection.nodeId)
    setSelectedConnection(selection.connectionId)
  }

  function addNode() {
    if (draft.nodes.length >= 50) return
    // A nova etapa entra à direita da última coluna, na altura da etapa mais à direita.
    const rightmost = draft.nodes.reduce<EditableNode | null>((best, entry) => !best || entry.node.position.x > best.node.position.x ? entry : best, null)
    const node: WorkflowNode = {
      nodeId: crypto.randomUUID(),
      configuration: defaultConfiguration(newType),
      position: rightmost ? { x: rightmost.node.position.x + 300, y: rightmost.node.position.y } : { x: 0, y: 0 },
      credentialId: null,
    }
    edit(current => ({ ...current, nodes: [...current.nodes, editableNode(node)] }))
    select({ nodeId: node.nodeId, connectionId: null })
  }

  function applyTemplate(template: 'simple' | 'branches') {
    if (draft.nodes.length !== 0 || draft.connections.length !== 0) return
    const graph = createWorkflowTemplate(template)
    const graphNodes = graph.nodes.map(n => ({ nodeId: n.nodeId, type: n.configuration.type }))
    const positions = arrangePositions(graphNodes, graph.connections)
    const nodes = graph.nodes.map(n => ({ ...n, position: positions.find(p => p.nodeId === n.nodeId)!.position }))
    edit(current => ({ ...current, nodes: nodes.map(editableNode), connections: graph.connections }))
  }

  function removeNode(nodeId: string) {
    edit(current => ({
      ...current,
      nodes: current.nodes.filter(entry => entry.node.nodeId !== nodeId),
      connections: current.connections.filter(connection =>
        connection.sourceNodeId !== nodeId && connection.targetNodeId !== nodeId),
    }))
    select({ nodeId: null, connectionId: null })
  }

  const graphNodes = draft.nodes.map(entry => ({ nodeId: entry.node.nodeId, type: entry.type }))

  function connect(candidate: ConnectionCandidate) {
    if (locked) return
    const problem = connectionIssue(graphNodes, draft.connections, candidate)
    if (problem) { setIssue(problem); return }
    setIssue(null)
    edit(current => ({ ...current, connections: [...current.connections, { id: crypto.randomUUID(), ...candidate }] }))
  }

  function removeConnection(id: string) {
    edit(current => ({ ...current, connections: current.connections.filter(c => c.id !== id) }))
    if (selectedConnection === id) setSelectedConnection(null)
  }

  function changeConnection(id: string, update: Partial<WorkflowConnection>) {
    const existing = draft.connections.find(c => c.id === id)
    if (!existing || locked) return
    const candidate = { ...existing, ...update }
    const source = graphNodes.find(n => n.nodeId === candidate.sourceNodeId)
    if (update.sourceNodeId && source && !outputPorts(source.type).includes(candidate.sourcePort)) {
      candidate.sourcePort = outputPorts(source.type).find(port => !draft.connections.some(c =>
        c.id !== id && c.sourceNodeId === source.nodeId && c.sourcePort === port)) ?? outputPorts(source.type)[0]
    }
    const problem = connectionIssue(graphNodes, draft.connections, candidate, id)
    if (problem) { setIssue(problem); return }
    setIssue(null)
    edit(current => ({ ...current, connections: current.connections.map(c => c.id === id ? candidate : c) }))
  }

  function moveNodes(positions: { nodeId: string; position: { x: number; y: number } }[]) {
    if (locked) return
    setDraft(current => {
      let changed = false
      const nodes = current.nodes.map(entry => {
        const moved = positions.find(p => p.nodeId === entry.node.nodeId)
        if (!moved || !Number.isFinite(moved.position.x) || !Number.isFinite(moved.position.y) ||
          (moved.position.x === entry.node.position.x && moved.position.y === entry.node.position.y)) return entry
        changed = true
        return { ...entry, node: { ...entry.node, position: { x: moved.position.x, y: moved.position.y } } }
      })
      return changed ? { ...current, nodes, dirty: true } : current
    })
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (locked) return
    setError(null)
    let nodes: WorkflowNode[]
    try {
      if (draft.nodes.length > 50 || draft.connections.length > 100) {
        throw new Error('Use até 50 etapas e 100 conexões.')
      }
      nodes = buildNodes(draft.nodes)
      const nodeIds = new Set(nodes.map(node => node.nodeId))
      if (nodeIds.size !== nodes.length || nodes.some(node => !validIdentity(node.nodeId))) {
        throw new Error('Etapas devem ter UUIDs válidos e únicos.')
      }
      const connectionIds = new Set(draft.connections.map(connection => connection.id))
      if (connectionIds.size !== draft.connections.length ||
        draft.connections.some(connection => !validIdentity(connection.id))) {
        throw new Error('Conexões devem ter UUIDs válidos e únicos.')
      }
      if (draft.connections.some(connection =>
        !nodeIds.has(connection.sourceNodeId) || !nodeIds.has(connection.targetNodeId))) {
        throw new Error('Toda conexão deve ligar etapas existentes.')
      }
      const requestSize = new TextEncoder().encode(JSON.stringify({ nodes, connections: draft.connections })).length
      if (requestSize > MAX_DRAFT_BYTES) throw new Error('O grafo excede o limite do editor de 1 MiB.')
    } catch (validationError) {
      // Leva a pessoa até a etapa com problema, sem trocar o modo de edição escolhido.
      if (validationError instanceof StepError) select({ nodeId: validationError.nodeId, connectionId: null })
      setError(validationError instanceof Error ? validationError.message : 'Revise o rascunho antes de salvar.')
      return
    }
    setSubmitting(true)
    try {
      await onSave(nodes, draft.connections)
      setDraft(current => ({ ...current, dirty: false }))
    } catch {
      // O pai apresenta o ProblemDetails recebido. A edição fica intacta,
      // inclusive quando o backend retorna 409 ou está indisponível.
      setError('O rascunho não foi salvo. Suas alterações locais foram preservadas.')
    } finally {
      setSubmitting(false)
    }
  }

  const empty = draft.nodes.length === 0 && draft.connections.length === 0
  const remoteChanged = draft.dirty && version.id === draft.versionId && version.revision !== draft.revision
  const steps = draft.nodes.map(entry => ({ nodeId: entry.node.nodeId, type: entry.type, configurationText: entry.configurationText, credentialText: entry.credentialText }))
  const selectedIndex = draft.nodes.findIndex(entry => entry.node.nodeId === selectedNode)
  const connectionIndex = draft.connections.findIndex(c => c.id === selectedConnection)
  const prefix = editorId + '-'
  const status = submitting || saving ? 'Salvando…' : draft.dirty ? 'Alterações locais não salvas' : 'Tudo salvo'

  let side
  if (selectedIndex >= 0) {
    const entry = draft.nodes[selectedIndex]
    const nodeId = entry.node.nodeId
    side = <StepInspector key={nodeId} prefix={prefix + 'step-'} step={steps[selectedIndex]} index={selectedIndex} locked={locked} mode={mode}
      relatedConnections={draft.connections.filter(c => c.sourceNodeId === nodeId || c.targetNodeId === nodeId).length}
      onMode={setMode} onClose={() => select({ nodeId: null, connectionId: null })}
      onType={type => changeNode(nodeId, current => ({ ...current, type, configurationText: JSON.stringify(defaultConfiguration(type), null, 2), credentialText: type === 'httpRequest' ? current.credentialText : '' }))}
      onText={text => changeNode(nodeId, current => ({ ...current, configurationText: text }))}
      onCredential={value => changeNode(nodeId, current => ({ ...current, credentialText: value }))}
      onRemove={() => removeNode(nodeId)} closeLabel={readOnly ? 'Fechar' : undefined} />
  } else if (connectionIndex >= 0) {
    const connection = draft.connections[connectionIndex]
    side = <ConnectionInspector prefix={prefix + 'connection-'} connection={connection} index={connectionIndex} steps={steps} locked={locked}
      onChange={update => changeConnection(connection.id, update)} onRemove={() => removeConnection(connection.id)}
      onClose={() => select({ nodeId: null, connectionId: null })} />
  } else {
    side = <InspectorOverview prefix={prefix + 'overview-'} steps={steps} connections={draft.connections} locked={locked}
      onSelectStep={nodeId => select({ nodeId, connectionId: null })} onSelectConnection={id => select({ nodeId: null, connectionId: id })}
      onRemoveConnection={removeConnection} onConnect={connect} />
  }

  const templates = !disabled && empty ? <div className="canvas-empty">
    <h3>Comece por um exemplo</h3>
    <p>Os exemplos preenchem o rascunho e não iniciam uma execução.</p>
    <div className="canvas-empty-actions">
      <button type="button" className="btn" disabled={locked} onClick={() => applyTemplate('simple')}>Usar exemplo Trigger → Log</button>
      <button type="button" className="btn" disabled={locked} onClick={() => applyTemplate('branches')}>Usar exemplo com Condition, Transform e Delay</button>
    </div>
    <p className="small">Ou adicione etapas pela barra acima do canvas.</p>
  </div> : null

  return (
    <form onSubmit={event => void save(event)} className="graph-editor">
      {!readOnly && <div className="editor-bar">
        {!disabled ? <div className="editor-bar-tools">
          <label className="sr-only" htmlFor={prefix + 'new-type'}>Tipo da nova etapa</label>
          <select className="field field-compact" id={prefix + 'new-type'} value={newType} disabled={locked} onChange={event => setNewType(event.target.value as NodeType)}>
            {Object.entries(nodeTypeLabels).map(([type, label]) => <option key={type} value={type}>{label}</option>)}
          </select>
          <button type="button" className="btn btn-small" disabled={locked || draft.nodes.length >= 50} onClick={addNode}>Adicionar etapa</button>
          <button type="button" className="btn btn-quiet btn-small" disabled={locked || !draft.nodes.length}
            title="Organiza as colunas pela ordem das conexões. Não muda IDs, configurações nem o caminho de execução."
            onClick={() => moveNodes(arrangePositions(graphNodes, draft.connections))}>Organizar etapas</button>
        </div> : <p className="editor-bar-note">Aguarde a operação em andamento.</p>}
        <div className="editor-bar-save">
          <span role="status" className={'editor-status' + (draft.dirty ? ' is-dirty' : '')}>{status}</span>
          {!disabled && draft.dirty && <button type="button" className="btn btn-quiet btn-small" disabled={locked} onClick={() => {
            setDraft(fromVersion(version))
            setError(null)
            select({ nodeId: null, connectionId: null })
          }}>Descartar alterações locais</button>}
          {!disabled && <button type="submit" className={'btn btn-small' + (draft.dirty ? ' btn-primary' : '')} disabled={locked || !draft.dirty}>Salvar rascunho</button>}
        </div>
      </div>}

      {remoteChanged && (
        <p role="alert" className="notice">
          A versão no servidor mudou. Suas alterações locais continuam aqui; revise o conflito antes de salvar novamente.
        </p>
      )}
      {error && <p role="alert" className="notice notice-error">{error}</p>}
      {issue && <p role="alert" className="notice notice-error">{issue}</p>}

      <div className={'editor-body' + (readOnly ? ' is-readonly' : '')}>
        <FlowGraph
          nodes={draft.nodes.map(entry => ({ nodeId: entry.node.nodeId, type: entry.type, position: entry.node.position, summary: describeText(entry.configurationText) }))}
          connections={draft.connections} mode={readOnly || locked ? 'view' : 'edit'} live={readOnly && version.status === 'published'}
          selectedNodeId={selectedNode} selectedConnectionId={selectedConnection} onSelect={select}
          onPositions={moveNodes} onConnect={connect} onIssue={setIssue}
          onOpen={nodeId => { select({ nodeId, connectionId: null }); requestAnimationFrame(() => document.getElementById(prefix + 'step-type')?.focus()) }}
          overlay={templates} />
        {/* Versão publicada: o canvas ocupa toda a largura e a etapa abre como gaveta. */}
        {readOnly ? selectedIndex >= 0 && <aside className="editor-drawer" aria-label="Configuração da etapa"
          onKeyDown={event => { if (event.key === 'Escape') select({ nodeId: null, connectionId: null }) }}>{side}</aside>
          : <aside className="editor-side" aria-label="Configuração das etapas">{side}</aside>}
      </div>
    </form>
  )
}
