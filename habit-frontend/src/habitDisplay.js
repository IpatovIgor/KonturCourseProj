export const weekdays = ['Понедельник', 'Вторник', 'Среда', 'Четверг', 'Пятница', 'Суббота', 'Воскресенье']
const shortDays = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс']

export function displayDate(date) {
  return date.split('-').reverse().join('.')
}

export function displayDays(days) {
  return days.map((day) => shortDays[day - 1]).join(', ')
}

export function isArchived(habit) {
  return Boolean(habit.archivedAt || habit.archivedDate)
}
