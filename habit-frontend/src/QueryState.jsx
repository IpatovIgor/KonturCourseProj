import { errorMessage } from './api.js'

export default function QueryState({ query }) {
  if (query.error) {
    return (
      <div className="feedback">
        <p className="error" role="alert">{errorMessage(query.error)}</p>
        <button type="button" onClick={query.reload}>Повторить загрузку</button>
      </div>
    )
  }
  if (query.loading && !query.data) return <p className="muted" role="status">Загрузка…</p>
  return null
}
