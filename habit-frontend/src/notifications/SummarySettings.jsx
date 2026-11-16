import SettingsForm from './SettingsForm.jsx'

export default function SummarySettings({ api }) {
  return <SettingsForm load={api.getSummary} save={api.saveSummary}
    enabledLabel="Показывать дневную сводку" saveLabel="Сохранить сводку" />
}
