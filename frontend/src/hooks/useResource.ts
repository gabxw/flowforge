import { useCallback, useEffect, useRef, useState } from 'react'
import { isAbortError } from '../api/client'

// Uma leitura por vez; cada tela cancela suas leituras ao navegar.
export function useResource<T>(load: (signal: AbortSignal) => Promise<T>, active?: (value: T) => boolean, pollMs = 3000) {
  const [data, replaceData] = useState<T | null>(null)
  const epoch = useRef(0)
  const currentController = useRef<AbortController | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [loading, setLoading] = useState(true)
  const [updatedAt, setUpdatedAt] = useState<number | null>(null)
  const [generation, setGeneration] = useState(0)
  const refresh = useCallback(() => setGeneration(n => n + 1), [])

  const setData = useCallback((value: T) => {
    epoch.current++
    currentController.current?.abort()
    replaceData(value)
    setError(null)
    setLoading(false)
    setUpdatedAt(Date.now())
  }, [])

  useEffect(() => {
    let disposed = false
    let inFlight = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const controller = new AbortController()
    currentController.current = controller
    const requestEpoch = ++epoch.current
    let shouldPoll = false

    async function read() {
      if (disposed || inFlight) return
      inFlight = true
      setLoading(true)
      try {
        const value = await load(controller.signal)
        if (disposed || requestEpoch !== epoch.current) return
        replaceData(value)
        setError(null)
        setUpdatedAt(Date.now())
        shouldPoll = active?.(value) ?? false
      } catch (cause) {
        if (!disposed && requestEpoch === epoch.current && !isAbortError(cause)) {
          setError(cause)
          shouldPoll = false
        }
      } finally {
        inFlight = false
        if (!disposed && requestEpoch === epoch.current) {
          setLoading(false)
          if (shouldPoll) timer = setTimeout(tick, pollMs)
        }
      }
    }
    function tick() {
      if (disposed) return
      if (document.visibilityState === 'hidden') timer = setTimeout(tick, pollMs)
      else void read()
    }
    function visible() {
      if (document.visibilityState === 'visible' && shouldPoll && !inFlight) {
        clearTimeout(timer)
        void read()
      }
    }
    void read()
    document.addEventListener('visibilitychange', visible)
    return () => {
      disposed = true
      clearTimeout(timer)
      controller.abort()
      document.removeEventListener('visibilitychange', visible)
    }
  }, [load, active, generation, pollMs])

  return { data, setData, error, loading, updatedAt, refresh }
}
