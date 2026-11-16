export function createNotificationsApi(http) {
  const data = (request) => request.then((response) => response.data)
  const reminder = (id) => '/habits/' + encodeURIComponent(id) + '/reminder'
  const summary = '/profile/summary-settings'

  return {
    history: (cursor, signal) => data(http.get('/notifications', { params: { limit: 20, cursor }, signal })),
    markRead: (id, signal) => http.put('/notifications/' + encodeURIComponent(id) + '/read', null, { signal }),
    getReminder: (id, signal) => data(http.get(reminder(id), { signal })),
    saveReminder: (id, settings, signal) => data(http.put(reminder(id), settings, { signal })),
    getSummary: (signal) => data(http.get(summary, { signal })),
    saveSummary: (settings, signal) => data(http.put(summary, settings, { signal })),
  }
}
