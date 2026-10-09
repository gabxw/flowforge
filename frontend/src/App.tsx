import { useEffect, useState } from 'react'

type ApiStatus = 'verificando' | 'online' | 'indisponível'

export default function App() {
  const [apiStatus, setApiStatus] = useState<ApiStatus>('verificando')

  useEffect(() => {
    const deadline = Date.now() + 30_000
    let disposed = false
    let requestController: AbortController | undefined
    let retryTimer: ReturnType<typeof setTimeout> | undefined

    async function checkApi() {
      requestController = new AbortController()
      const timeout = setTimeout(() => requestController?.abort(), 5000)
      let healthy = false

      try {
        const response = await fetch('/api/health/live', {
          signal: requestController.signal,
        })
        healthy = response.ok && (await response.text()) === 'Healthy'
      } catch {
        // Containers may start before the API starts listening. Retry within the deadline.
      } finally {
        clearTimeout(timeout)
      }

      if (disposed) return

      if (healthy) {
        setApiStatus('online')
      } else if (Date.now() < deadline) {
        retryTimer = setTimeout(() => void checkApi(), 1000)
      } else {
        setApiStatus('indisponível')
      }
    }

    void checkApi()
    return () => {
      disposed = true
      clearTimeout(retryTimer)
      requestController?.abort()
    }
  }, [])

  return (
    <main className="mx-auto max-w-3xl px-6 py-16">
      <p className="mb-3 text-sm font-semibold uppercase tracking-widest text-indigo-700">
        Plataforma de automação
      </p>
      <h1 className="text-4xl font-semibold tracking-tight text-slate-900">FlowForge</h1>
      <p className="mt-4 text-lg leading-relaxed text-slate-600">
        Workflows com execução assíncrona, histórico e tratamento de falhas.
      </p>

      <section className="mt-10 rounded-xl border border-slate-200 bg-white p-6">
        <h2 className="text-lg font-semibold text-slate-900">Fase 1 · Estrutura e ambiente</h2>
        <p className="mt-3 text-slate-600">
          Esta versão verifica a inicialização do ambiente. A criação e a execução
          de workflows serão implementadas nas próximas fases.
        </p>
        <p role="status" className="mt-5 text-sm font-medium text-slate-700">
          API: {apiStatus}
        </p>
      </section>
    </main>
  )
}
