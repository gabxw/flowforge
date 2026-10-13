import { useCallback } from 'react'
import * as api from '../api/client'
import type { WorkflowExecution, NodeExecution, ExecutionLog, NodeExecutionAttempt } from '../api/contracts'
import { useResource } from '../hooks/useResource'
import { useCommand } from '../hooks/useCommand'
import { Badge, ErrorNotice, Timestamp } from '../components/Ui'

interface ExecutionView { execution: WorkflowExecution; nodes: NodeExecution[]; logs: ExecutionLog[]; attempts: NodeExecutionAttempt[] }
const isActive = (view: ExecutionView) => view.execution.status === 'pending' || view.execution.status === 'running'
const outcomes: Record<string, string> = { notApplicable: 'Não aplicável', notStarted: 'Não iniciado', knownSuccess: 'Sucesso observado', knownFailure: 'Falha observada', unknown: 'Resultado desconhecido' }

export function ExecutionPage({ id }: { id: string }) {
  const command = useCommand()
  const load = useCallback(async (signal: AbortSignal): Promise<ExecutionView> => {
    // Ler o estado primeiro: um estado terminal precede a leitura de seu histórico confirmado.
    const execution = await api.getExecution(id, signal)
    const [nodes, logs, attempts] = await Promise.all([api.listNodes(id, signal), api.listLogs(id, signal), api.listAttempts(id, signal)])
    return { execution, nodes, logs, attempts }
  }, [id])
  const state = useResource(load, isActive)
  const data = state.data
  const execution = data?.execution
  const active = execution?.status === 'pending' || execution?.status === 'running'
  return <>
    <a className="back-link" href={execution ? '#/workflows/' + execution.workflowId : '#/workflows'}>← Voltar ao workflow</a>
    <header className="page-heading"><div><p className="eyebrow">HISTÓRICO</p><h1>Detalhes da execução</h1><p className="muted">Estado, etapas e tentativas desta execução.</p></div>{execution && <Badge status={execution.status} />}</header>
    <ErrorNotice error={state.error} retry={state.refresh} /><ErrorNotice error={command.error} />
    {state.loading && !data && <p role="status">Carregando execução…</p>}
    {state.error && data && <p className="notice">Os dados abaixo são da última consulta bem-sucedida.</p>}
    {execution && data && <>
      <section className="card">
        <div className="toolbar"><span className="muted">{state.updatedAt ? 'Última consulta: ' + new Date(state.updatedAt).toLocaleTimeString('pt-BR') : ''}{state.loading ? ' · Atualizando…' : ''}</span>
          <div className="action-group"><button className="btn" disabled={state.loading || !!command.busy} onClick={state.refresh}>Atualizar execução</button>
            <button className="btn btn-danger" disabled={!active || !!execution.cancelRequestedAt || !!command.busy || state.loading || !!state.error} onClick={() => {
              void command.run('Solicitando cancelamento…', signal => api.cancel(id, signal), value => {
                state.setData({ ...data, execution: value })
                state.refresh()
              }).catch(() => undefined)
            }}>Solicitar cancelamento</button></div></div>
        {active && execution.cancelRequestedAt && <p className="notice" role="status">Cancelamento solicitado. Aguardando confirmação do Worker.</p>}
        {execution.resumeAt && active && <p className="notice" role="status">Espera durável até <Timestamp value={execution.resumeAt} />.</p>}
        {execution.errorCode && <p className="notice notice-error">Código da falha: <code>{execution.errorCode}</code></p>}
        <dl className="metadata"><dt>Execução ID</dt><dd className="monospace">{execution.id}</dd><dt>Versão fixa</dt><dd className="monospace">{execution.workflowVersionId}</dd><dt>Correlation ID</dt><dd className="monospace">{execution.correlationId}</dd>
          <dt>Criada em</dt><dd><Timestamp value={execution.createdAt} /></dd><dt>Iniciada em</dt><dd><Timestamp value={execution.startedAt} /></dd><dt>Finalizada em</dt><dd><Timestamp value={execution.finishedAt} /></dd><dt>Deadline</dt><dd><Timestamp value={execution.deadlineAt} /></dd></dl>
      </section>
      <section className="card"><h2>Nodes</h2>
        {!data.nodes.length && <p className="empty muted">O Worker ainda não registrou etapas nesta execução.</p>}
        <div className="node-history">{data.nodes.map((node, index) => <article className="node-result" key={node.id}>
          <div className="section-heading"><h3>Etapa {index + 1}</h3><Badge status={node.status} /></div>
          <p className="monospace muted small">{node.nodeId}</p>
          <dl className="metadata"><dt>Tentativas</dt><dd>{node.attemptCount}</dd><dt>Início</dt><dd><Timestamp value={node.startedAt} /></dd><dt>Fim</dt><dd><Timestamp value={node.finishedAt} /></dd>
            <dt>Input</dt><dd>{node.input ? node.input.kind + ' · ' + node.input.byteLength + ' bytes' : '—'}</dd><dt>Output</dt><dd>{node.output ? node.output.kind + ' · ' + node.output.byteLength + ' bytes' : '—'}</dd>
            {node.retryAt && <><dt>Nova tentativa</dt><dd><Timestamp value={node.retryAt} /></dd></>}
            {node.errorCode && <><dt>Falha</dt><dd><code>{node.errorCode}</code></dd></>}
            {node.credentialRevisionUsed !== null && <><dt>Revisão da credencial</dt><dd>{node.credentialRevisionUsed}</dd></>}
          </dl>
          {data.attempts.some(a => a.nodeExecutionId === node.id) && <details><summary>Histórico de tentativas</summary><div className="table-scroll"><table><thead><tr><th>Nº</th><th>Estado</th><th>Início</th><th>Fim</th><th>Efeito externo</th><th>HTTP / erro</th></tr></thead>
            <tbody>{data.attempts.filter(a => a.nodeExecutionId === node.id).map(a => <tr key={a.id}><td>{a.number}{a.legacy ? ' (histórico importado)' : ''}</td><td><Badge status={a.status} /></td><td><Timestamp value={a.startedAt} /></td><td><Timestamp value={a.finishedAt} /></td><td>{outcomes[a.externalOutcome] ?? a.externalOutcome}</td><td>{a.httpStatusCode ?? '—'} {a.errorCode ?? ''}</td></tr>)}</tbody></table></div></details>}
        </article>)}</div>
      </section>
      <section className="card"><h2>Logs</h2><p className="muted">Eventos registrados por etapa. O conteúdo das mensagens permanece protegido.</p>
        {data.logs.length ? <div className="table-scroll"><table><thead><tr><th>Evento</th><th>Node execution ID</th><th>Horário</th><th>Tamanho</th></tr></thead>
          <tbody>{data.logs.map(log => <tr key={log.id}><td>Log registrado</td><td className="monospace">{log.nodeExecutionId}</td><td><Timestamp value={log.createdAt} /></td><td>{log.messageByteLength} bytes</td></tr>)}</tbody></table></div> : <p className="empty muted">Nenhum evento Log registrado.</p>}
      </section>
    </>}
  </>
}
