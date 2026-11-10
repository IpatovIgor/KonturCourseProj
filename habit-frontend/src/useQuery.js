import { useEffect, useReducer, useState } from 'react'

export default function useQuery(load, refreshKey = 0) {
  const [revision, reload] = useReducer((current) => current + 1, 0)
  const [result, setResult] = useState(null)

  useEffect(() => {
    if (!load) return
    const controller = new AbortController()
    const { signal } = controller

    load(signal).then(
      (data) => {
        if (!signal.aborted) setResult({ load, revision, refreshKey, data, error: null })
      },
      (error) => {
        if (signal.aborted) return
        setResult((previous) => ({
          load, revision, refreshKey, error,
          data: previous?.load === load ? previous.data : undefined,
        }))
      },
    )
    return () => controller.abort()
  }, [load, revision, refreshKey])

  const current = result?.load === load ? result : null
  const fresh = current?.revision === revision && current.refreshKey === refreshKey
  return {
    data: current?.data,
    error: fresh ? current.error : null,
    loading: Boolean(load && !fresh),
    reload,
  }
}
