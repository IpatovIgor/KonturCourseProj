import { useCallback } from 'react'
import SettingsForm from './SettingsForm.jsx'

export default function ReminderSettings({ api, id, archived }) {
  const load = useCallback((signal) => api.getReminder(id, signal), [api, id])
  const save = (values, signal) => api.saveReminder(id, values, signal)
  return (
    <section className="panel">
      <h2>Напоминание</h2>
      <SettingsForm load={load} save={save} readOnly={archived}
        enabledLabel="Напоминать о привычке" saveLabel="Сохранить напоминание" />
    </section>
  )
}
