import type { NodeType } from '../workflows/templates'

const labels: Record<string, string> = {
  pending: 'Pendente', running: 'Em execução', succeeded: 'Concluída', failed: 'Falhou',
  cancelled: 'Cancelada', retrying: 'Aguardando nova tentativa', skipped: 'Ignorado',
  interrupted: 'Interrompida', draft: 'Rascunho', published: 'Publicada', archived: 'Arquivado',
}
export function statusLabel(status: string) {
  return labels[status] ?? status
}
// Durações curtas em ms; acima disso, a unidade que mantém o número legível.
export function formatDuration(ms: number) {
  if (!Number.isFinite(ms) || ms < 0) return '—'
  if (ms < 1000) return Math.round(ms) + ' ms'
  if (ms < 60_000) return (ms / 1000).toLocaleString('pt-BR', { maximumFractionDigits: ms < 10_000 ? 2 : 1 }) + ' s'
  const minutes = Math.floor(ms / 60_000)
  if (minutes < 60) return minutes + ' min ' + Math.round((ms % 60_000) / 1000) + ' s'
  return Math.floor(minutes / 60) + ' h ' + (minutes % 60) + ' min'
}

export const nodeTone: Record<NodeType, string> = {
  webhookTrigger: 'var(--sodium)',
  httpRequest: 'var(--run)',
  delay: 'var(--wait)',
  condition: 'var(--retry)',
  transformJson: 'var(--ok)',
  log: 'var(--paper-2)',
}
