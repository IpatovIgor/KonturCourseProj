import { useCallback, useEffect, useRef, useState } from 'react'
import { errorMessage } from './api.js'
import { displayDate, displayDays } from './habitDisplay.js'
import HabitDetails from './HabitDetails.jsx'
import HabitForm from './HabitForm.jsx'
import QueryState from './QueryState.jsx'
import useQuery from './useQuery.js'

const sections = [
  ['today', 'Сегодня'],
  ['active', 'Все привычки'],
  ['archived', 'Архив'],
]

function HabitList({ habits, onOpen, onToggle, locked, toggleDisabled }) {
  return (
    <ul className="habit-list">
      {habits.map((habit) => (
        <li className="habit-row" key={habit.id}>
          <div>
            <button type="button" className="text-button habit-name" onClick={() => onOpen(habit.id)} disabled={locked}>{habit.name}</button>
            <p className="muted">{displayDays(habit.daysOfWeek)}</p>
          </div>
          {onToggle && (
            <button type="button" className={habit.completed ? 'completed' : 'primary'} onClick={() => onToggle(habit)} disabled={locked || toggleDisabled}>
              {habit.completed ? 'Снять отметку' : 'Отметить'}
            </button>
          )}
        </li>
      ))}
    </ul>
  )
}

function lastPage(list) {
  return Math.max(1, Math.ceil(list.totalCount / list.pageSize))
}

export default function HabitTracker({ api, onLogout }) {
  const [section, setSection] = useState('today')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState(null)
  const [creating, setCreating] = useState(false)
  const [revision, setRevision] = useState(0)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const writing = useRef(false)

  const today = useQuery(api.today, revision)
  const scheduleRevision = revision + ':' + (today.data?.date ?? '')
  const loadPage = useCallback(async (signal) => {
    const list = await api.list(section, page, signal)
    const last = lastPage(list)
    return page > last ? api.list(section, last, signal) : list
  }, [api, section, page])
  const list = useQuery(section === 'today' ? null : loadPage, scheduleRevision)
  const { reload: reloadToday } = today

  useEffect(() => {
    function refreshWhenVisible() {
      if (!document.hidden) reloadToday()
    }
    window.addEventListener('focus', refreshWhenVisible)
    document.addEventListener('visibilitychange', refreshWhenVisible)
    const timer = window.setInterval(refreshWhenVisible, 60000)
    return () => {
      window.removeEventListener('focus', refreshWhenVisible)
      document.removeEventListener('visibilitychange', refreshWhenVisible)
      window.clearInterval(timer)
    }
  }, [reloadToday])

  function clearFeedback() {
    setError('')
    setNotice('')
  }

  function navigate(next) {
    if (section !== next) setPage(1)
    setSection(next)
    setSelected(null)
    setCreating(false)
    clearFeedback()
  }

  function openHabit(id) {
    clearFeedback()
    setSelected(id)
  }

  const habitMissing = useCallback(() => {
    setSelected(null)
    setError('Привычка больше недоступна.')
    setRevision((value) => value + 1)
  }, [])

  async function mutate(action, message, onSuccess) {
    if (writing.current) return false
    writing.current = true
    setPending(true)
    clearFeedback()
    try {
      await action()
    } catch (requestError) {
      setError(errorMessage(requestError))
      return false
    } finally {
      writing.current = false
      setPending(false)
    }
    setNotice(message)
    setRevision((value) => value + 1)
    onSuccess?.()
    return true
  }

  function toggleCompletion(id, date, completed) {
    return mutate(() => api.setCompleted(id, date, completed), completed ? 'Отметка сохранена.' : 'Отметка снята.')
  }

  function createHabit(fields) {
    return mutate(() => api.create(fields), 'Привычка создана.', () => {
      setCreating(false)
      setSection('active')
      setPage(1)
    })
  }

  function archiveHabit() {
    return mutate(() => api.archive(selected), 'Привычка перемещена в архив.', () => {
      setSelected(null)
      if (section === 'active' && list.data) {
        const afterArchive = { ...list.data, totalCount: list.data.totalCount - 1 }
        setPage(Math.min(list.data.page, lastPage(afterArchive)))
      }
    })
  }

  const isToday = section === 'today'
  const current = isToday ? today : list
  const title = sections.find(([key]) => key === section)[1]

  return (
    <main className="workspace">
      <header className="topbar">
        <div>
          <p className="eyebrow">Личный трекер</p>
          <h1>Мои привычки</h1>
        </div>
        <button type="button" onClick={onLogout}>Выйти</button>
      </header>
      <div className="toolbar">
        <nav aria-label="Разделы">
          {sections.map(([key, label]) => (
            <button key={key} type="button" aria-current={key === section ? 'page' : undefined} onClick={() => navigate(key)} disabled={pending}>{label}</button>
          ))}
        </nav>
        {!creating && <button type="button" className="primary" disabled={pending} onClick={() => { clearFeedback(); setSelected(null); setCreating(true) }}>Новая привычка</button>}
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      {notice && <p className="notice" role="status">{notice}</p>}
      {creating ? (
        <HabitForm pending={pending} onSave={createHabit} onCancel={() => { clearFeedback(); setCreating(false) }} />
      ) : selected ? (
        <HabitDetails
          key={selected}
          id={selected}
          api={api}
          today={today}
          revision={scheduleRevision}
          pending={pending}
          onBack={() => { clearFeedback(); setSelected(null) }}
          onMissing={habitMissing}
          onSave={(fields) => mutate(() => api.edit(selected, fields), 'Изменения сохранены.')}
          onArchive={archiveHabit}
          onToggle={toggleCompletion}
        />
      ) : (
        <section className="panel">
          <h2>{title}</h2>
          {isToday && today.data && <p className="muted">{displayDate(today.data.date)} · {today.data.timeZoneId}</p>}
          <QueryState query={current} />
          {current.data && (
            <>
              {current.data.items.length === 0 && <p className="muted">{isToday ? 'На сегодня ничего не запланировано.' : section === 'archived' ? 'В архиве пока нет привычек.' : 'У вас пока нет активных привычек.'}</p>}
              <HabitList
                habits={current.data.items}
                onOpen={openHabit}
                onToggle={isToday ? (habit) => toggleCompletion(habit.id, today.data.date, !habit.completed) : undefined}
                locked={pending}
                toggleDisabled={current.loading || Boolean(current.error)}
              />
              {!isToday && current.data.totalCount > 0 && (
                <div className="pagination">
                  <button type="button" disabled={pending || current.data.page <= 1} onClick={() => setPage(current.data.page - 1)}>Предыдущая</button>
                  <span>Страница {current.data.page} из {lastPage(current.data)}</span>
                  <button type="button" disabled={pending || current.data.page >= lastPage(current.data)} onClick={() => setPage(current.data.page + 1)}>Следующая</button>
                </div>
              )}
              {!isToday && <p className="muted count">Всего привычек: {current.data.totalCount}</p>}
            </>
          )}
        </section>
      )}
    </main>
  )
}
