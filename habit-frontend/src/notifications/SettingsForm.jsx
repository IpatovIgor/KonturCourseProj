import { useEffect, useRef, useState } from 'react'
import { errorMessage } from '../api.js'
import QueryState from '../QueryState.jsx'
import useQuery from '../useQuery.js'

const unavailable = 'Настройка уведомлений недоступна.'
const isUnavailable = (error) => error?.response?.status === 404

export default function SettingsForm({ load, save, enabledLabel, saveLabel, readOnly = false, disclosureTitle }) {
  const query = useQuery(load)
  const [draft, setDraft] = useState(null)
  const [feedback, setFeedback] = useState({})
  const [saving, setSaving] = useState(false)
  const request = useRef(null)
  const values = draft ?? query.data

  useEffect(() => () => request.current?.abort(), [])

  function change(value) {
    setDraft({ ...values, ...value })
    setFeedback({})
  }

  async function submit(event) {
    event.preventDefault()
    if (!values || request.current || readOnly) return
    const controller = new AbortController()
    request.current = controller
    setSaving(true)
    setFeedback({})
    try {
      const saved = await save(values, controller.signal)
      if (controller.signal.aborted) return
      setDraft(saved)
      setFeedback({ notice: 'Настройки сохранены.' })
    } catch (error) {
      if (!controller.signal.aborted) setFeedback({ error: isUnavailable(error) ? unavailable : errorMessage(error) })
    } finally {
      if (!controller.signal.aborted) {
        request.current = null
        setSaving(false)
      }
    }
  }

  if (isUnavailable(query.error)) return disclosureTitle ? null : <p className="muted">{unavailable}</p>
  if (!values && disclosureTitle && query.loading) return null
  const content = !values ? <QueryState query={query} /> : (
    <form className="form-stack" onSubmit={submit}>
      <label className="notification-checkbox">
        <input type="checkbox" checked={values.enabled} disabled={saving || readOnly} onChange={(event) => change({ enabled: event.target.checked })} />
        {enabledLabel}
      </label>
      <label>
        Местное время аккаунта
        <input required type="time" value={values.localTime} disabled={saving || readOnly} onChange={(event) => change({ localTime: event.target.value })} />
      </label>
      {feedback.error && <p className="error" role="alert">{feedback.error}</p>}
      {feedback.notice && <p className="notice" role="status">{feedback.notice}</p>}
      {!readOnly && <div className="actions"><button type="submit" disabled={saving}>{saveLabel}</button></div>}
    </form>
  )
  return disclosureTitle ? (
    <details className="panel notification-panel">
      <summary>{disclosureTitle}</summary>
      {content}
    </details>
  ) : content
}
