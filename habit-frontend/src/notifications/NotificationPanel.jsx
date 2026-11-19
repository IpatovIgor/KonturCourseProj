import { useRef, useState } from 'react'
import { errorMessage } from '../api.js'
import { displayDate } from '../habitDisplay.js'

function text(item) {
  if (item.kind === 'habitReminder') return 'Пора выполнить привычку «' + item.habitName + '».'
  return 'Выполнено ' + item.completed + ' из ' + item.planned + '. Осталось ' + item.pending + '.'
}

export default function NotificationPanel({ items, unreadCount, loading, error, retry, markRead, onOpenHabit, locked }) {
  const [limit, setLimit] = useState(20)
  const [readError, setReadError] = useState('')
  const pending = useRef(new Set())
  const [reading, setReading] = useState([])

  async function read(id) {
    if (pending.current.has(id)) return
    pending.current.add(id)
    setReading([...pending.current])
    setReadError('')
    try {
      await markRead(id)
    } catch (requestError) {
      setReadError(errorMessage(requestError))
    } finally {
      pending.current.delete(id)
      setReading([...pending.current])
    }
  }

  return (
    <details className="panel notification-panel">
      <summary>Уведомления <span className="muted">Непрочитанных · {unreadCount}</span></summary>
      {loading && <p className="muted" role="status">Загрузка уведомлений…</p>}
      {error && <div className="feedback">
        <p className="error" role="alert">{errorMessage(error)}</p>
        <button type="button" onClick={retry}>Обновить уведомления</button>
      </div>}
      {readError && <p className="error" role="alert">{readError}</p>}
      {!loading && !error && items.length === 0 && <p className="muted">Уведомлений пока нет.</p>}
      {items.length > 0 && <ul className="habit-list">
        {items.slice(0, limit).map((item) => (
          <li className={'habit-row' + (item.readAtUtc ? '' : ' notification-unread')} key={item.id}>
            <div>
              <p>{text(item)}</p>
              <time className="muted" dateTime={item.localDate}>{displayDate(item.localDate)}</time>
            </div>
            <div className="actions">
              {item.habitId && onOpenHabit && <button type="button" disabled={locked} onClick={() => onOpenHabit(item.habitId)}>Открыть привычку</button>}
              {!item.readAtUtc && <button type="button" disabled={reading.includes(item.id)} onClick={() => read(item.id)}>Прочитано</button>}
            </div>
          </li>
        ))}
      </ul>}
      {items.length > limit && <button type="button" onClick={() => setLimit(limit + 20)}>Загрузить ещё</button>}
    </details>
  )
}
