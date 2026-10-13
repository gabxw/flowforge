import { useEffect, useId, useState } from 'react'
import type { FormEvent } from 'react'
import { parseExactJson } from '../api/json'
import type { NodeConfiguration, WorkflowConnection, WorkflowNode, WorkflowVersion } from '../api/contracts'
import { createWorkflowTemplate, defaultConfiguration, nodeTypeLabels } from '../workflows/templates'
import type { NodeType } from '../workflows/templates'

interface WorkflowEditorProps {
  version: WorkflowVersion
  disabled: boolean
  saving: boolean
  onSave: (nodes: WorkflowNode[], connections: WorkflowConnection[]) => Promise<void>
  onDirtyChange?: (dirty: boolean) => void
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

const MAX_CONFIGURATION_BYTES = 64 * 1024
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

// JSON.parse verifica a sintaxe. A segunda passagem verifica as chaves antes
// que duplicatas possam ser escondidas pela desserialização do navegador.
function parseConfiguration(text: string, expectedType: NodeType): NodeConfiguration {
  if (new TextEncoder().encode(text).length > MAX_CONFIGURATION_BYTES) {
    throw new Error('A configuração excede o limite do editor de 64 KiB.')
  }

  try {
    JSON.parse(text)
  } catch {
    throw new Error('JSON inválido. Revise aspas, vírgulas e valores da configuração.')
  }

  let cursor = 0
  function whitespace() {
    while (/\s/.test(text[cursor] ?? '') && cursor < text.length) cursor++
  }
  function readString(): string {
    const start = cursor++
    while (text[cursor] !== '"') {
      if (text[cursor] === '\\') cursor++
      cursor++
    }
    cursor++
    return JSON.parse(text.slice(start, cursor)) as string
  }
  function inspect(depth: number) {
    if (depth > 32) throw new Error('A configuração excede 32 níveis de profundidade.')
    whitespace()
    const token = text[cursor]
    if (token === '{') {
      cursor++
      whitespace()
      const keys = new Set<string>()
      while (text[cursor] !== '}') {
        const key = readString()
        if (keys.has(key)) throw new Error('Propriedades JSON duplicadas não são permitidas.')
        keys.add(key)
        whitespace()
        cursor++ // dois-pontos; sintaxe já foi validada
        inspect(depth + 1)
        whitespace()
        if (text[cursor] !== ',') break
        cursor++
        whitespace()
      }
      cursor++
    } else if (token === '[') {
      cursor++
      whitespace()
      while (text[cursor] !== ']') {
        inspect(depth + 1)
        whitespace()
        if (text[cursor] !== ',') break
        cursor++
        whitespace()
      }
      cursor++
    } else if (token === '"') {
      readString()
    } else {
      while (cursor < text.length && !/[\s,\]}]/.test(text[cursor])) cursor++
    }
  }
  inspect(1)

  // A validação compartilhada exige que todo token numérico preserve seu
  // valor decimal ao passar por JSON.parse/JSON.stringify no navegador.
  const value = parseExactJson(text)
  if (value === null || typeof value !== 'object' || Array.isArray(value) ||
    !('type' in value) || value.type !== expectedType) {
    throw new Error('A configuração deve ser um objeto JSON com type igual ao tipo selecionado.')
  }

  // Domínio e API continuam responsáveis pelos campos e regras de cada tipo.
  // Não retiramos membros desconhecidos: a API deve recusá-los explicitamente.
  return value as NodeConfiguration
}

function validIdentity(value: string) {
  return UUID.test(value) && value.toLowerCase() !== '00000000-0000-0000-0000-000000000000'
}

function buildNodes(nodes: EditableNode[]): WorkflowNode[] {
  return nodes.map((entry, index) => {
    try {
      const credentialId = entry.credentialText.trim()
      if (credentialId && (entry.type !== 'httpRequest' || !validIdentity(credentialId))) {
        throw new Error('A referência de credencial deve ser um UUID não vazio em um HTTP Request.')
      }
      const configuration = parseConfiguration(entry.configurationText, entry.type)
      return { ...entry.node, configuration, credentialId: credentialId || null }
    } catch (error) {
      throw new Error('Node ' + (index + 1) + ': ' + (error instanceof Error ? error.message : 'configuração inválida.'))
    }
  })
}

export default function WorkflowEditor({ version, disabled, saving, onSave, onDirtyChange }: WorkflowEditorProps) {
  const editorId = useId()
  const [draft, setDraft] = useState<LocalDraft>(() => fromVersion(version))
  const [newType, setNewType] = useState<NodeType>('log')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
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

  function addNode() {
    if (draft.nodes.length >= 50) return
    const node: WorkflowNode = {
      nodeId: crypto.randomUUID(),
      configuration: defaultConfiguration(newType),
      position: { x: draft.nodes.length * 240, y: 0 },
      credentialId: null,
    }
    edit(current => ({ ...current, nodes: [...current.nodes, editableNode(node)] }))
  }

  function applyTemplate(template: 'simple' | 'branches') {
    if (draft.nodes.length !== 0 || draft.connections.length !== 0) return
    const graph = createWorkflowTemplate(template)
    edit(current => ({ ...current, nodes: graph.nodes.map(editableNode), connections: graph.connections }))
  }

  function removeNode(nodeId: string) {
    edit(current => ({
      ...current,
      nodes: current.nodes.filter(entry => entry.node.nodeId !== nodeId),
      connections: current.connections.filter(connection =>
        connection.sourceNodeId !== nodeId && connection.targetNodeId !== nodeId),
    }))
  }

  function addConnection() {
    if (draft.connections.length >= 100 || draft.nodes.length < 2) return
    const source = draft.nodes[0]
    const connection: WorkflowConnection = {
      id: crypto.randomUUID(),
      sourceNodeId: source.node.nodeId,
      targetNodeId: draft.nodes[1].node.nodeId,
      sourcePort: source.type === 'condition' ? 'true' : 'next',
    }
    edit(current => ({ ...current, connections: [...current.connections, connection] }))
  }

  function changeConnection(id: string, update: Partial<WorkflowConnection>) {
    edit(current => ({
      ...current,
      connections: current.connections.map(connection => connection.id === id ? { ...connection, ...update } : connection),
    }))
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (locked) return
    setError(null)
    let nodes: WorkflowNode[]
    try {
      if (draft.nodes.length > 50 || draft.connections.length > 100) {
        throw new Error('Use até 50 nodes e 100 conexões.')
      }
      nodes = buildNodes(draft.nodes)
      const nodeIds = new Set(nodes.map(node => node.nodeId))
      if (nodeIds.size !== nodes.length || nodes.some(node => !validIdentity(node.nodeId))) {
        throw new Error('Nodes devem ter UUIDs válidos e únicos.')
      }
      const connectionIds = new Set(draft.connections.map(connection => connection.id))
      if (connectionIds.size !== draft.connections.length ||
        draft.connections.some(connection => !validIdentity(connection.id))) {
        throw new Error('Conexões devem ter UUIDs válidos e únicos.')
      }
      if (draft.connections.some(connection =>
        !nodeIds.has(connection.sourceNodeId) || !nodeIds.has(connection.targetNodeId))) {
        throw new Error('Toda conexão deve referenciar nodes existentes.')
      }
      const requestSize = new TextEncoder().encode(JSON.stringify({ nodes, connections: draft.connections })).length
      if (requestSize > MAX_DRAFT_BYTES) throw new Error('O grafo excede o limite do editor de 1 MiB.')
    } catch (validationError) {
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

  return (
    <form onSubmit={event => void save(event)} className="workflow-editor">
      <div className="editor-toolbar">
        <div>
          <h3>Estrutura do workflow</h3>
          <p className="muted">
            {draft.nodes.length}/50 nodes · {draft.connections.length}/100 conexões.
            {' '}As regras do grafo são verificadas pelo backend ao publicar.
          </p>
        </div>
        <span role="status" className="muted">
          {submitting || saving ? 'Salvando…' : draft.dirty ? 'Alterações locais não salvas' : 'Estrutura carregada do backend'}
        </span>
      </div>

      {remoteChanged && (
        <p role="alert" className="notice">
          A versão no servidor mudou. Suas alterações locais continuam aqui; revise o conflito antes de salvar novamente.
        </p>
      )}
      {error && <p role="alert" className="notice notice-error">{error}</p>}

      {!disabled && empty && (
        <div className="editor-toolbar">
          <button type="button" className="btn" disabled={locked} onClick={() => applyTemplate('simple')}>
            Usar exemplo Trigger → Log
          </button>
          <button type="button" className="btn" disabled={locked} onClick={() => applyTemplate('branches')}>
            Usar exemplo com Condition, Transform e Delay
          </button>
          <p className="muted">Os exemplos preenchem o rascunho e não iniciam uma execução.</p>
        </div>
      )}

      <div className="editor-grid">
        {draft.nodes.map((entry, index) => {
          const nodeId = entry.node.nodeId
          const relatedConnections = draft.connections.filter(connection =>
            connection.sourceNodeId === nodeId || connection.targetNodeId === nodeId).length
          return (
            <section key={nodeId} className="card editor-node" aria-labelledby={editorId + '-node-' + nodeId}>
              <h4 id={editorId + '-node-' + nodeId}>{index + 1}. {nodeTypeLabels[entry.type]}</h4>
              <p className="muted"><code>{nodeId}</code></p>
              <label className="field" htmlFor={editorId + '-type-' + nodeId}>
                Tipo do node {index + 1}
                <select
                  id={editorId + '-type-' + nodeId}
                  value={entry.type}
                  disabled={locked}
                  onChange={event => {
                    const type = event.target.value as NodeType
                    changeNode(nodeId, current => ({
                      ...current,
                      type,
                      configurationText: JSON.stringify(defaultConfiguration(type), null, 2),
                      credentialText: type === 'httpRequest' ? current.credentialText : '',
                    }))
                  }}
                >
                  {Object.entries(nodeTypeLabels).map(([type, label]) => <option key={type} value={type}>{label}</option>)}
                </select>
              </label>
              {!disabled && <p className="muted">Trocar o tipo substitui a configuração deste node pelos valores iniciais.</p>}
              <label htmlFor={editorId + '-config-' + nodeId}>
                Configuração JSON do node {index + 1}
              </label>
              <textarea
                className="field"
                id={editorId + '-config-' + nodeId}
                value={entry.configurationText}
                rows={entry.type === 'webhookTrigger' ? 4 : 9}
                spellCheck={false}
                disabled={locked}
                onChange={event => changeNode(nodeId, current => ({ ...current, configurationText: event.target.value }))}
              />
              {entry.type === 'httpRequest' && (
                <>
                  <label htmlFor={editorId + '-credential-' + nodeId}>
                    ID da credencial do node {index + 1} (opcional)
                  </label>
                  <input
                    className="field"
                    id={editorId + '-credential-' + nodeId}
                    type="text"
                    autoComplete="off"
                    value={entry.credentialText}
                    placeholder="UUID da credencial"
                    disabled={locked}
                    onChange={event => changeNode(nodeId, current => ({ ...current, credentialText: event.target.value }))}
                  />
                  <p className="muted">
                    Use somente a referência de Credential. Não coloque segredos na URL ou no JSON.
                    A URL inicial é fictícia; configure um destino HTTPS permitido antes de executar.
                    POST/PUT/PATCH usam o input como corpo.
                  </p>
                </>
              )}
              {entry.type === 'delay' && <p className="muted">10.000.000 ticks = 1 segundo; duração máxima de 24 horas.</p>}
              {entry.type === 'condition' && <p className="muted">Use JSON Pointer e conecte as portas true e false. A comparação aceita dados JSON, sem executar código.</p>}
              {entry.type === 'transformJson' && <p className="muted">Cada field usa exatamente um sourcePointer ou literal. O resultado substitui o input do próximo node.</p>}
              {!disabled && (
                <button type="button" className="btn" disabled={locked} onClick={() => removeNode(nodeId)}>
                  Remover node {index + 1}{relatedConnections > 0 ? ' e ' + relatedConnections + ' conexão(ões)' : ''}
                </button>
              )}
            </section>
          )
        })}
      </div>

      {!disabled && (
        <div className="editor-toolbar">
          <label className="field" htmlFor={editorId + '-new-type'}>
            Tipo do novo node
            <select id={editorId + '-new-type'} value={newType} disabled={locked} onChange={event => setNewType(event.target.value as NodeType)}>
              {Object.entries(nodeTypeLabels).map(([type, label]) => <option key={type} value={type}>{label}</option>)}
            </select>
          </label>
          <button type="button" className="btn" disabled={locked || draft.nodes.length >= 50} onClick={addNode}>Adicionar node</button>
        </div>
      )}

      <h3>Conexões</h3>
      {draft.connections.length === 0 && <p className="muted">Nenhuma conexão definida.</p>}
      {draft.connections.map((connection, index) => (
        <section key={connection.id} className="card editor-connection" aria-label={'Conexão ' + (index + 1)}>
          <h4>Conexão {index + 1}</h4>
          <div className="editor-toolbar">
            <label className="field" htmlFor={editorId + '-source-' + connection.id}>
              Origem da conexão {index + 1}
              <select
                id={editorId + '-source-' + connection.id}
                value={connection.sourceNodeId}
                disabled={locked}
                onChange={event => changeConnection(connection.id, { sourceNodeId: event.target.value })}
              >
                {draft.nodes.map((entry, nodeIndex) =>
                  <option key={entry.node.nodeId} value={entry.node.nodeId}>{nodeIndex + 1}. {nodeTypeLabels[entry.type]}</option>)}
              </select>
            </label>
            <label className="field" htmlFor={editorId + '-port-' + connection.id}>
              Porta da conexão {index + 1}
              <select
                id={editorId + '-port-' + connection.id}
                value={connection.sourcePort}
                disabled={locked}
                onChange={event => changeConnection(connection.id, { sourcePort: event.target.value as WorkflowConnection['sourcePort'] })}
              >
                <option value="next">next</option>
                <option value="true">true</option>
                <option value="false">false</option>
              </select>
            </label>
            <label className="field" htmlFor={editorId + '-target-' + connection.id}>
              Destino da conexão {index + 1}
              <select
                id={editorId + '-target-' + connection.id}
                value={connection.targetNodeId}
                disabled={locked}
                onChange={event => changeConnection(connection.id, { targetNodeId: event.target.value })}
              >
                {draft.nodes.map((entry, nodeIndex) =>
                  <option key={entry.node.nodeId} value={entry.node.nodeId}>{nodeIndex + 1}. {nodeTypeLabels[entry.type]}</option>)}
              </select>
            </label>
          </div>
          {!disabled && (
            <button type="button" className="btn" disabled={locked} onClick={() => edit(current => ({
              ...current, connections: current.connections.filter(item => item.id !== connection.id),
            }))}>Remover conexão {index + 1}</button>
          )}
        </section>
      ))}

      {!disabled && (
        <div className="editor-toolbar">
          <button type="button" className="btn" disabled={locked || draft.nodes.length < 2 || draft.connections.length >= 100} onClick={addConnection}>
            Adicionar conexão
          </button>
          <button type="submit" className="btn btn-primary" disabled={locked || !draft.dirty}>Salvar rascunho</button>
          {draft.dirty && (
            <button type="button" className="btn" disabled={locked} onClick={() => {
              setDraft(fromVersion(version))
              setError(null)
            }}>Descartar alterações locais</button>
          )}
        </div>
      )}
      <p className="muted">Configuração: até 64 KiB por node e grafo de até 1 MiB neste editor. Salvar não publica nem executa o workflow.</p>
    </form>
  )
}
