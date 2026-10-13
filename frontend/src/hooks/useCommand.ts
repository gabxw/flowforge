import { useEffect, useRef, useState } from 'react'
import { isAbortError } from '../api/client'

// Comandos não são repetidos automaticamente: timeout pode ocorrer após o aceite.
export function useCommand() {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<unknown>(null)
  const mounted = useRef(false)
  const pending = useRef<AbortController | null>(null)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false; pending.current?.abort() }
  }, [])

  async function run<T>(label: string, command: (signal: AbortSignal) => Promise<T>, accepted: (value: T) => void) {
    if (pending.current) throw new Error('Aguarde o comando em andamento.')
    const controller = new AbortController()
    pending.current = controller
    setBusy(label)
    setError(null)
    try {
      const result = await command(controller.signal)
      if (!mounted.current || controller.signal.aborted) throw new DOMException('Operação interrompida.', 'AbortError')
      accepted(result)
      return result
    } catch (cause) {
      if (mounted.current && !isAbortError(cause)) setError(cause)
      throw cause
    } finally {
      if (mounted.current) setBusy(null)
      pending.current = null
    }
  }

  return { busy, error, run }
}
