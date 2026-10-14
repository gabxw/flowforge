import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../api/client'
import type { WorkflowExecution, NodeExecution, ExecutionLog, NodeExecutionAttempt, WorkflowDetail, WorkflowVersion } from '../api/contracts'
import { useResource } from '../hooks/useResource'
import { useCommand } from '../hooks/useCommand'
import { Badge, ErrorNotice, NodeGlyph, Timestamp } from '../components/Ui'
import { ExecutionTrace } from '../components/ExecutionTrace'
import type { StepInfo } from '../components/ExecutionTrace'
import { FlowGraph } from '../components/FlowGraph'
import { formatDuration } from '../components/format'
import { describe, failureHelp } from '../workflows/describe'
import { nodeTypeLabels } from '../workflows/templates'

interface ExecutionView { execution: WorkflowExecution; nodes: NodeExecution[]; logs: ExecutionLog[]; attempts: NodeExecutionAttempt[] }
const isActive = (view: ExecutionView) => view.execution.status === 'pending' || view.execution.status === 'running'
const outcomes: Record<string, string> = { notApplicable: 'Não aplicável', notStarted: 'Não iniciado', knownSuccess: 'Sucesso observado', knownFailure: 'Falha observada', unknown: 'Resultado desconhecido' }

const between = (from: string | null, to: string | null, now: number) =>
  from ? (to ? new Date(to).getTime() : now) - new Date(from).getTime() : null

// A versão executada é imutável, então basta lê-la uma vez por execução.
function useExecutedVersion(workflowId: string | undefined, versionId: string | undefined) {
  const [result, setResult] = useState<{ key: string; workflow: WorkflowDetail; version: WorkflowVersion } | null>(null)
  const key = workflowId && versionId ? workflowId + ':' + versionId : null
  useEffect(() => {
    if (!key || !workflowId) return
    const controller = new AbortController()
    api.getWorkflow(workflowId, controller.signal).then(workflow => {
      const version = Array.isArray(workflow?.versions) ? workflow.versions.find(v => v.id === versionId) : undefined
      if (version) setResult({ key, workflow, version })
    }).catch(() => undefined)
    return () => controller.abort()
  }, [key, workflowId, versionId])
  return result && result.key === key ? result : null
}

export function ExecutionPage({ id }: { id: string }) {
  const command = useCommand()
  const load = useCallback(async (signal: AbortSignal): Promise<ExecutionView> => {
    // Ler o estado primeiro: um estado terminal precede a leitura de seu histórico confirmado.
    const execution = await api.getExecution(id, signal)
    const [nodes, logs, attempts] = await Promise.all([api.listNodes(id, signal), api.listLogs(id, signal), api.listAttempts(id, signal)])
    return { execution, nodes, logs, attempts }
  }, [id])
  const state = useResource(load, isActive, 1500)
  const data = state.data
  const execution = data?.execution
  const executed = useExecutedVersion(execution?.workflowId, execution?.workflowVersionId)
  const [replay, setReplay] = useState(0)
  const [highlight, setHighlight] = useState<string | null>(null)
  const active = execution?.status === 'pending' || execution?.status === 'running'
  const now = state.updatedAt ?? Date.now()
  const queued = execution ? between(execution.createdAt, execution.startedAt, now) : null
  const ran = execution ? between(execution.startedAt, execution.finishedAt, now) : null

  const step = useCallback((nodeId: string): StepInfo | null => {
    const index = executed?.version.nodes.findIndex(n => n.nodeId === nodeId) ?? -1
    if (!executed || index < 0) return null
    const node = executed.version.nodes[index]
    return { index, type: node.configuration.type, summary: describe(node.configuration) }
  }, [executed])

  const run = useMemo(() => {
    if (!data) return undefined
    const status = Object.fromEntries(data.nodes.map(n => [n.nodeId, n.status]))
    // Durante a execução os fios acendem conforme chegam; depois, o replay segue a ordem real.
    const ordered = [...data.nodes].filter(n => n.startedAt).sort((a, b) => a.startedAt!.localeCompare(b.startedAt!))
    const order = Object.fromEntries(ordered.map((n, k) => [n.nodeId, active ? 0 : k]))
    return { status, order, replay }
  }, [data, active, replay])

  const failedNode = data?.nodes.find(n => n.status === 'failed')
  const help = execution?.errorCode ? failureHelp[execution.errorCode] : null
  const logStep = (nodeExecutionId: string) => {
    const node = data?.nodes.find(n => n.id === nodeExecutionId)
    const info = node ? step(node.nodeId) : null
    return info ? 'Etapa ' + (info.index + 1) + ', ' + nodeTypeLabels[info.type] : nodeExecutionId.slice(0, 8)
  }

  return <>
    <a className="back-link" href={execution ? '#/workflows/' + execution.workflowId : '#/workflows'}>← {executed ? executed.workflow.name : 'Voltar ao workflow'}</a>
    <header className="page-head"><div><h1>Detalhes da execução</h1>
      <p className="lead">{executed ? 'Rodou a versão ' + executed.version.versionNumber + ' de ' + executed.workflow.name + '.' : 'Estado, etapas e tentativas desta execução.'}</p></div>
      {execution && <div className="head-meta" aria-live="polite"><code className="muted" title="Início do ID da execução">{execution.id.slice(0, 8)}</code><Badge status={execution.status} /></div>}</header>
    <ErrorNotice error={state.error} retry={state.refresh} /><ErrorNotice error={command.error} />
    {state.loading && !data && <p role="status" className="muted">Carregando execução…</p>}
    {state.error && data && <p className="notice">Os dados abaixo são da última consulta bem-sucedida.</p>}
    {execution && data && <>
      <dl className="readouts">
        <div><dt>Espera na fila</dt><dd>{queued === null ? (active ? 'aguardando' : '—') : formatDuration(queued)}</dd></div>
        <div><dt>{active ? 'Rodando há' : 'Duração'}</dt><dd>{ran === null ? '—' : formatDuration(ran)}</dd></div>
        <div><dt>Etapas registradas</dt><dd className="numeric">{data.nodes.length}</dd></div>
        <div><dt>Tentativas</dt><dd className="numeric">{data.nodes.reduce((sum, node) => sum + node.attemptCount, 0)}</dd></div>
      </dl>

      {execution.errorCode && <section className="failure" aria-labelledby="failure-title">
        <h2 id="failure-title">{help?.title ?? 'A execução falhou.'}</h2>
        <p>{help?.next ?? 'Confira as tentativas da etapa que falhou.'}</p>
        <p className="failure-meta">Código <code>{execution.errorCode}</code>{failedNode && step(failedNode.nodeId) && <>, na etapa {step(failedNode.nodeId)!.index + 1} ({nodeTypeLabels[step(failedNode.nodeId)!.type]})</>}</p>
      </section>}
      {active && execution.cancelRequestedAt && <p className="notice" role="status">Cancelamento solicitado. Aguardando confirmação do Worker.</p>}
      {execution.resumeAt && active && <p className="notice" role="status">Espera durável até <Timestamp value={execution.resumeAt} />.</p>}

      <section className="panel run-panel">
        <div className="panel-head">
          <div><h2>Caminho percorrido</h2><p>{active ? 'Ao vivo: os fios acendem conforme cada etapa termina.' : 'Etapas que rodaram ficam acesas; as ignoradas ficam apagadas.'}</p></div>
          <div className="action-group">
            {!active && executed && <button className="btn btn-quiet btn-small" onClick={() => setReplay(n => n + 1)}>Repetir o caminho</button>}
            {active && <button className="btn btn-danger btn-small" disabled={!!execution.cancelRequestedAt || !!command.busy || state.loading || !!state.error} onClick={() => {
              void command.run('Solicitando cancelamento…', signal => api.cancel(id, signal), value => {
                state.setData({ ...data, execution: value })
                state.refresh()
              }).catch(() => undefined)
            }}>Solicitar cancelamento</button>}
          </div>
        </div>
        {executed ? <FlowGraph mode="run" label="Caminho percorrido pela execução" highlight={highlight}
          nodes={executed.version.nodes.map(n => ({ nodeId: n.nodeId, type: n.configuration.type, position: n.position, summary: describe(n.configuration) }))}
          connections={executed.version.connections} run={run} onSelect={selection => setHighlight(selection.nodeId)} />
          : <p className="empty">O grafo da versão executada aparece aqui quando o workflow é carregado.</p>}
      </section>

      <section className="panel">
        <div className="panel-head">
          <div><h2>Linha do tempo</h2><p>{active ? <><span className="live-dot" aria-hidden="true" />Atualiza sozinha enquanto a execução estiver ativa.</> : state.updatedAt ? 'Consultada às ' + new Date(state.updatedAt).toLocaleTimeString('pt-BR') + '.' : ''}</p></div>
          <button className="btn btn-quiet btn-small" disabled={state.loading || !!command.busy} onClick={state.refresh}>Atualizar execução</button>
        </div>
        <ExecutionTrace execution={execution} nodes={data.nodes} now={now} step={step} highlight={highlight} onHighlight={setHighlight} />
      </section>

      <section className="panel"><div className="panel-head"><div><h2>Etapas</h2><p>Payloads aparecem só como tipo e tamanho. O conteúdo fica no servidor.</p></div></div>
        {!data.nodes.length && <p className="empty">O Worker ainda não registrou etapas nesta execução.</p>}
        <div className="node-history">{data.nodes.map((node, index) => {
          const attempts = data.attempts.filter(a => a.nodeExecutionId === node.id)
          const info = step(node.nodeId)
          return <article className={'node-result' + (highlight === node.nodeId ? ' is-highlighted' : '')} key={node.id}
            onMouseEnter={() => setHighlight(node.nodeId)} onMouseLeave={() => setHighlight(null)}>
            <header><div className="node-result-title">{info && <NodeGlyph type={info.type} />}<div><h3>{info ? nodeTypeLabels[info.type] : 'Etapa ' + (index + 1)}</h3><p className="muted small">Etapa {(info?.index ?? index) + 1} <code>{node.nodeId.slice(0, 8)}</code></p></div></div><Badge status={node.status} /></header>
            <dl className="metadata"><dt>Tentativas</dt><dd className="numeric">{node.attemptCount}</dd><dt>Início</dt><dd><Timestamp value={node.startedAt} /></dd><dt>Fim</dt><dd><Timestamp value={node.finishedAt} /></dd>
              <dt>Input</dt><dd>{node.input ? node.input.kind + ', ' + node.input.byteLength + ' bytes' : '—'}</dd><dt>Output</dt><dd>{node.output ? node.output.kind + ', ' + node.output.byteLength + ' bytes' : '—'}</dd>
              {node.retryAt && <><dt>Nova tentativa</dt><dd><Timestamp value={node.retryAt} /></dd></>}
              {node.errorCode && <><dt>Falha</dt><dd><code>{node.errorCode}</code></dd></>}
              {node.credentialRevisionUsed !== null && <><dt>Revisão da credencial</dt><dd>{node.credentialRevisionUsed}</dd></>}
            </dl>
            {attempts.length > 0 && <details><summary>Histórico de tentativas</summary><div className="table-scroll"><table><thead><tr><th>Nº</th><th>Estado</th><th>Início</th><th>Fim</th><th>Efeito externo</th><th>HTTP / erro</th></tr></thead>
              <tbody>{attempts.map(a => <tr key={a.id}><td>{a.number}{a.legacy ? ' (histórico importado)' : ''}</td><td><Badge status={a.status} /></td><td><Timestamp value={a.startedAt} /></td><td><Timestamp value={a.finishedAt} /></td><td>{outcomes[a.externalOutcome] ?? a.externalOutcome}</td><td>{a.httpStatusCode ?? '—'} {a.errorCode ?? ''}</td></tr>)}</tbody></table></div></details>}
          </article>
        })}</div>
      </section>

      <section className="panel"><div className="panel-head"><div><h2>Logs</h2><p>Eventos registrados por etapa. O conteúdo das mensagens permanece protegido.</p></div></div>
        {data.logs.length ? <div className="table-scroll"><table><thead><tr><th>Evento</th><th>Etapa</th><th>Horário</th><th>Tamanho</th></tr></thead>
          <tbody>{data.logs.map(log => <tr key={log.id}><td>Log registrado</td><td>{logStep(log.nodeExecutionId)}</td><td><Timestamp value={log.createdAt} /></td><td className="numeric">{log.messageByteLength} bytes</td></tr>)}</tbody></table></div> : <p className="empty">Nenhum evento Log registrado.</p>}
      </section>

      <section className="panel compact"><h2 style={{ marginBottom: '1rem' }}>Identificação</h2>
        <dl className="metadata"><dt>Execução ID</dt><dd className="monospace">{execution.id}</dd><dt>Versão fixa</dt><dd className="monospace">{execution.workflowVersionId}</dd><dt>Correlation ID</dt><dd className="monospace">{execution.correlationId}</dd>
          <dt>Criada em</dt><dd><Timestamp value={execution.createdAt} /></dd><dt>Iniciada em</dt><dd><Timestamp value={execution.startedAt} /></dd><dt>Finalizada em</dt><dd><Timestamp value={execution.finishedAt} /></dd><dt>Deadline</dt><dd><Timestamp value={execution.deadlineAt} /></dd></dl>
      </section>
    </>}
  </>
}
