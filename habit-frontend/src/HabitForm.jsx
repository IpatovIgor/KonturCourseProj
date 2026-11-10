import { useState } from 'react'
import { weekdays } from './habitDisplay.js'

export default function HabitForm({ habit, pending, onSave, onCancel }) {
  const [name, setName] = useState(habit?.name ?? '')
  const [description, setDescription] = useState(habit?.description ?? '')
  const [daysOfWeek, setDaysOfWeek] = useState(habit?.nextSchedule?.daysOfWeek ?? habit?.daysOfWeek ?? [1, 2, 3, 4, 5, 6, 7])
  const [error, setError] = useState('')

  function toggleDay(day) {
    setDaysOfWeek((days) => days.includes(day) ? days.filter((value) => value !== day) : [...days, day])
    setError('')
  }

  function submit(event) {
    event.preventDefault()
    const title = name.trim()
    const note = description.trim()
    if (!title || title.length > 100) {
      setError('Название должно содержать от 1 до 100 символов.')
      return
    }
    if (note.length > 1000) {
      setError('Описание должно содержать не более 1000 символов.')
      return
    }
    if (!daysOfWeek.length) {
      setError('Выберите хотя бы один день недели.')
      return
    }
    setError('')
    onSave({ name: title, description: note || null, daysOfWeek: [...daysOfWeek].sort((a, b) => a - b) })
  }

  return (
    <section className="panel">
      <h2>{habit ? 'Редактирование привычки' : 'Новая привычка'}</h2>
      <form className="form-stack" onSubmit={submit}>
        {error && <p className="error" role="alert">{error}</p>}
        <label>Название<input required value={name} onChange={(event) => setName(event.target.value)} disabled={pending} /></label>
        <label>Описание<textarea rows={3} value={description} onChange={(event) => setDescription(event.target.value)} disabled={pending} /></label>
        <fieldset className="day-picker" disabled={pending}>
          <legend>Дни недели</legend>
          {weekdays.map((label, index) => (
            <label key={label}><input type="checkbox" checked={daysOfWeek.includes(index + 1)} onChange={() => toggleDay(index + 1)} />{label}</label>
          ))}
        </fieldset>
        {habit && <p className="muted">Изменение дней вступит в силу со следующего дня.</p>}
        <div className="actions">
          <button className="primary" disabled={pending}>{habit ? 'Сохранить' : 'Создать привычку'}</button>
          <button type="button" onClick={onCancel} disabled={pending}>Отмена</button>
        </div>
      </form>
    </section>
  )
}
