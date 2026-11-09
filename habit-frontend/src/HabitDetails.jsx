import { useCallback, useEffect, useState } from 'react'
import { displayDate, displayDays, isArchived } from './habitDisplay.js'
import HabitForm from './HabitForm.jsx'
import QueryState from './QueryState.jsx'
import useQuery from './useQuery.js'

const dayLabels = {
  completed: 'Выполнено',
  missed: 'Пропущено',
  pending: 'Ожидает выполнения',
  notScheduled: 'Не запланировано',
}

function Statistics({ data }) {
  const metrics = [
    ['Запланировано', data.plannedDays],
    ['Выполнено', data.completedDays],
    ['Пропущено', data.missedDays],
    ['Ожидают выполнения', data.pendingDays],
    ['Доля выполнения', data.completionRatePercent == null ? '—' : data.completionRatePercent + '%'],
    ['Текущая серия за всю историю', data.currentStreak],
    ['Лучшая серия за всю историю', data.longestStreak],
  ]
  return <dl className="metrics">{metrics.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
}

export default function HabitDetails({ id, api, today, revision, pending, onBack, onMissing, onSave, onArchive, onToggle }) {
  const [editing, setEditing] = useState(false)
  const [range, setRange] = useState(undefined)
  const [draft, setDraft] = useState(null)
  const [rangeError, setRangeError] = useState('')
  const loadHabit = useCallback((signal) => api.get(id, signal), [api, id])
  const habitQuery = useQuery(loadHabit, revision)
  const habit = habitQuery.data
  const loadHistory = useCallback((signal) => api.history(id, range, signal), [api, id, range])
  const loadStatistics = useCallback((signal) => api.statistics(id, range, signal), [api, id, range])
  const serverDate = today.data?.date
  const history = useQuery(habit ? loadHistory : null, revision)
  const statistics = useQuery(habit ? loadStatistics : null, revision)
  const fields = draft ?? {
    from: range?.from ?? history.data?.from ?? statistics.data?.from ?? '',
    to: range?.to ?? history.data?.to ?? statistics.data?.to ?? '',
  }
  const missing = habitQuery.error?.response?.status === 404

  useEffect(() => {
    if (missing) onMissing()
  }, [missing, onMissing])

  function applyRange(event) {
    event.preventDefault()
    const { from, to } = fields
    if (!from || !to) {
      setRangeError('Укажите обе границы периода.')
      return
    }

    // UTC midnight gives an interval between calendar days independently of local DST.
    const days = (Date.parse(to + 'T00:00:00Z') - Date.parse(from + 'T00:00:00Z')) / 86400000 + 1
    if (!serverDate || to > serverDate || !Number.isFinite(days) || days < 1 || days > 366) {
      setRangeError('Период должен завершаться не позже сегодня и содержать до 366 дней.')
      return
    }
    setRangeError('')
    setRange({ from, to })
  }

  function changeField(event) {
    setDraft({ ...fields, [event.target.name]: event.target.value })
    setRangeError('')
  }

  function resetRange() {
    setDraft(null)
    setRangeError('')
    setRange(undefined)
  }

  if (!habit) {
    return (
      <section className="panel">
        <button type="button" onClick={onBack}>К списку</button>
        <QueryState query={habitQuery} />
      </section>
    )
  }

  const archived = isArchived(habit)
  if (editing && !archived) {
    return (
      <HabitForm habit={habit} pending={pending} onCancel={() => setEditing(false)} onSave={async (values) => {
        if (await onSave(values)) setEditing(false)
      }} />
    )
  }

  return (
    <div className="detail-grid">
      <section className="panel">
        <button type="button" className="text-button" onClick={onBack} disabled={pending}>К списку</button>
        <h2>{habit.name}</h2>
        <QueryState query={habitQuery} />
        {habit.description && <p className="description">{habit.description}</p>}
        <p className="muted">Начало · {displayDate(habit.startDate)}</p>
        <p>Расписание · {displayDays(habit.daysOfWeek)}</p>
        {habit.nextSchedule && <p className="notice">С {displayDate(habit.nextSchedule.effectiveFrom)} · {displayDays(habit.nextSchedule.daysOfWeek)}</p>}
        {archived ? <p className="muted">Привычка в архиве.</p> : (
          <div className="actions">
            <button type="button" className="primary" disabled={pending || habitQuery.loading} onClick={() => setEditing(true)}>Редактировать</button>
            <button type="button" className="danger" disabled={pending || habitQuery.loading} onClick={() => {
              if (window.confirm('Переместить в архив?')) onArchive()
            }}>Архивировать</button>
          </div>
        )}
      </section>
      <section className="panel">
        <h2>Период</h2>
        <QueryState query={today} />
        <form onSubmit={applyRange}>
          <div className="range-fields">
            <label>С даты<input type="date" name="from" value={fields.from} onChange={changeField} disabled={pending} /></label>
            <label>По дату<input type="date" name="to" value={fields.to} onChange={changeField} disabled={pending} /></label>
          </div>
          {rangeError && <p className="error" role="alert">{rangeError}</p>}
          <div className="actions">
            <button type="submit" disabled={pending || !serverDate || Boolean(today.error)}>Применить период</button>
            <button type="button" className="text-button" onClick={resetRange} disabled={pending || (!draft && !range)}>Последние 30 дней</button>
          </div>
        </form>
      </section>
      <section className="panel">
        <h2>Статистика</h2>
        <QueryState query={statistics} />
        {statistics.data && <Statistics data={statistics.data} />}
      </section>
      <section className="panel">
        <h2>История</h2>
        <QueryState query={history} />
        {history.data && (
          history.data.days.length === 0 ? <p className="muted">За этот период записей нет.</p> : (
            <ul className="habit-list">
              {history.data.days.map(({ date, status }) => (
                <li className="habit-row" key={date}>
                  <div><time dateTime={date}>{displayDate(date)}</time><p className="muted">{dayLabels[status]}</p></div>
                  {!archived && serverDate && date <= serverDate && ['completed', 'missed', 'pending'].includes(status) && (
                    <button type="button" disabled={pending || history.loading || today.loading || Boolean(history.error || today.error)} onClick={() => onToggle(id, date, status !== 'completed')}>
                      {status === 'completed' ? 'Снять отметку' : 'Отметить'}
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )
        )}
      </section>
    </div>
  )
}
