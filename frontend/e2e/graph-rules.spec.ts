import { expect, test } from '@playwright/test'
import { arrangePositions, connectionIssue } from '../src/workflows/graph'
import type { GraphNode } from '../src/workflows/graph'
import type { WorkflowConnection } from '../src/api/contracts'

const nodes: GraphNode[] = [
  {nodeId:'trigger',type:'webhookTrigger'}, {nodeId:'condition',type:'condition'},
  {nodeId:'log',type:'log'}, {nodeId:'last',type:'log'},
]
const edge = (id:string,sourceNodeId:string,targetNodeId:string,sourcePort='next'): WorkflowConnection => ({id,sourceNodeId,targetNodeId,sourcePort})

test('portas de condição permitem convergência sem criar fan-out da mesma saída', () => {
  const trueBranch=edge('1','condition','log','true')
  expect(connectionIssue(nodes,[trueBranch],edge('2','condition','log','false'))).toBeNull()
  expect(connectionIssue(nodes,[trueBranch],edge('3','condition','last','true'))).toContain('já está conectada')
})
test('gatilho não recebe entrada e nodes comuns não possuem portas booleanas', () => {
  expect(connectionIssue(nodes,[],edge('1','log','trigger'))).toContain('não recebe')
  expect(connectionIssue(nodes,[],edge('2','log','last','true'))).toContain('não pertence')
  expect(connectionIssue(nodes,[],edge('3','condition','log','next'))).toContain('não pertence')
})
test('ciclo indireto e conexão com o próprio node são recusados', () => {
  expect(connectionIssue(nodes,[edge('1','condition','log','true'),edge('2','log','last')],edge('3','last','condition'))).toContain('ciclo')
  expect(connectionIssue(nodes,[],edge('4','log','log'))).toContain('si mesmo')
})
test('edição substitui a conexão existente sem contabilizar sua própria saída', () => {
  const current=edge('1','trigger','condition')
  expect(connectionIssue(nodes,[current],edge('1','trigger','log'),'1')).toBeNull()
  expect(connectionIssue(nodes,[current],edge('2','trigger','log'))).toContain('já está conectada')
})
test('referência ausente e limite de conexões têm feedback explícito', () => {
  expect(connectionIssue(nodes,[],edge('1','missing','log'))).toContain('existentes')
  const many=Array.from({length:100},(_,i)=>edge(String(i),'unrelated','other'))
  expect(connectionIssue(nodes,many,edge('new','trigger','condition'))).toContain('100')
})
test('arranjo organiza ramos explicitamente e conserva a ordem e identidade dos nodes', () => {
  const snapshot=structuredClone(nodes)
  const connections=[edge('1','trigger','condition'),edge('2','condition','log','true'),edge('3','condition','last','false')]
  const positions=arrangePositions(nodes,connections)
  expect(nodes).toEqual(snapshot)
  expect(positions.map(n=>n.nodeId)).toEqual(nodes.map(n=>n.nodeId))
  expect(positions[2].position.x).toBe(positions[3].position.x)
  expect(positions[3].position.y).toBeGreaterThan(positions[2].position.y)
  expect(arrangePositions(nodes,connections)).toEqual(positions)
})
test('arranjo de rascunho com ciclo preexistente continua limitado e finito', () => {
  const positions=arrangePositions(nodes,[edge('1','log','last'),edge('2','last','log')])
  expect(positions).toHaveLength(nodes.length)
  expect(positions.every(p=>Number.isFinite(p.position.x)&&Number.isFinite(p.position.y))).toBeTruthy()
})
