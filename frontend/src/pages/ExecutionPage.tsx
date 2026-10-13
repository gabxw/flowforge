import { useCallback } from 'react'
import * as api from '../api/client'
import type { WorkflowExecution, NodeExecution, ExecutionLog, NodeExecutionAttempt } from '../api/contracts'
import { useResource } from '../hooks/useResource'
import { useCommand } from '../hooks/useCommand'
import { Badge, ErrorNotice, Timestamp } from '../components/Ui'
import { ExecutionTrace } from '../components/ExecutionTrace'
import { formatDuration } from '../components/format'

interface ExecutionView { execution: WorkflowExecution; nodes: NodeExecution[]; logs: ExecutionLog[]; attempts: NodeExecutionAttempt[] }
const isActive = (view: ExecutionView) => view.execution.status === 'pending' || view.execution.status === 'running'
const outcomes: Record<string, string> = { notApplicable: 'Não aplicável', notStarted: 'Não iniciado', knownSuccess: 'Sucesso observado', knownFailure: 'Falha observada', unknown: 'Resultado desconhecido' }

const between = (from: string | null, to: string | null, now: number) =>
  from ? (to ? new Date(to).getTime() : now) - new Date(from).getTime() : null

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
  const now = state.updatedAt ?? Date.now()
  const queued = execution ? between(execution.createdAt, execution.startedAt, now) : null
  const ran = execution ? between(execution.startedAt, execution.finishedAt, now) : null
  return <>
    <a className="back-link" href={execution ? '#/workflows/' + execution.workflowId : '#/workflows'}>← Voltar ao workflow</a>
    <header className="page-head"><div><h1>Detalhes da execução</h1><p className="lead">Estado, etapas e tentativas desta execução.</p></div>{execution && <div className="head-meta"><code className="muted" title="Início do ID da execução">{execution.id.slice(0, 8)}</code><Badge status={execution.status} /></div>}</header>
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

      <section className="panel">
        <div className="panel-head">
          <div><h2>Linha do tempo</h2><p>{state.updatedAt ? 'Última consulta às ' + new Date(state.updatedAt).toLocaleTimeString('pt-BR') + '.' : ''}{state.loading ? ' Atualizando…' : active ? ' A tela se atualiza enquanto a execução estiver ativa.' : ''}</p></div>
          <div className="action-group"><button className="btn" disabled={state.loading || !!command.busy} onClick={state.refresh}>Atualizar execução</button>
            <button className="btn btn-danger" disabled={!active || !!execution.cancelRequestedAt || !!command.busy || state.loading || !!state.error} onClick={() => {
              void command.run('Solicitando cancelamento…', signal => api.cancel(id, signal), value => {
                state.setData({ ...data, execution: value })
                state.refresh()
              }).catch(() => undefined)
            }}>Solicitar cancelamento</button></div>
        </div>
        {active && execution.cancelRequestedAt && <p className="notice" role="status">Cancelamento solicitado. Aguardando confirmação do Worker.</p>}
        {execution.resumeAt && active && <p className="notice" role="status">Espera durável até <Timestamp value={execution.resumeAt} />.</p>}
        {execution.errorCode && <p className="notice notice-error">Código da falha: <code>{execution.errorCode}</code></p>}
        <ExecutionTrace execution={execution} nodes={data.nodes} now={now} />
      </section>

      <section className="panel"><div className="panel-head"><div><h2>Nodes</h2><p>Payloads aparecem só como tipo e tamanho. O conteúdo fica no servidor.</p></div></div>
        {!data.nodes.length && <p className="empty">O Worker ainda não registrou etapas nesta execução.</p>}
        <div className="node-history">{data.nodes.map((node, index) => {
          const attempts = data.attempts.filter(a => a.nodeExecutionId === node.id)
          return <article className="node-result" key={node.id}>
            <header><div><h3>Etapa {index + 1}</h3><code className="muted">{node.nodeId}</code></div><Badge status={node.status} /></header>
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
        {data.logs.length ? <div className="table-scroll"><table><thead><tr><th>Evento</th><th>Node execution ID</th><th>Horário</th><th>Tamanho</th></tr></thead>
          <tbody>{data.logs.map(log => <tr key={log.id}><td>Log registrado</td><td className="monospace">{log.nodeExecutionId}</td><td><Timestamp value={log.createdAt} /></td><td className="numeric">{log.messageByteLength} bytes</td></tr>)}</tbody></table></div> : <p className="empty">Nenhum evento Log registrado.</p>}
      </section>

      <section className="panel compact"><h2 style={{ marginBottom: '1rem' }}>Identificação</h2>
        <dl className="metadata"><dt>Execução ID</dt><dd className="monospace">{execution.id}</dd><dt>Versão fixa</dt><dd className="monospace">{execution.workflowVersionId}</dd><dt>Correlation ID</dt><dd className="monospace">{execution.correlationId}</dd>
          <dt>Criada em</dt><dd><Timestamp value={execution.createdAt} /></dd><dt>Iniciada em</dt><dd><Timestamp value={execution.startedAt} /></dd><dt>Finalizada em</dt><dd><Timestamp value={execution.finishedAt} /></dd><dt>Deadline</dt><dd><Timestamp value={execution.deadlineAt} /></dd></dl>
      </section>
    </>}
  </>
}
