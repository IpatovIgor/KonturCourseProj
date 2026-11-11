import axios from 'axios'

const config = {
  baseURL: (import.meta.env.VITE_API_BASE_URL || '/api').replace(/\/+$/, ''),
  timeout: 15000,
}
const auth = axios.create(config)

export async function register(nickName, email, password) {
  await auth.post('/auth/register', { nickName, email, password })
}

export async function login(email, password) {
  const { data } = await auth.post('/auth/login', { email, password })
  if (!isValidToken(data?.token)) throw new Error('Сервер не вернул токен входа.')
  return data.token
}

export function isValidToken(token) {
  return typeof token === 'string' && Boolean(token.trim()) && !['undefined', 'null'].includes(token.trim())
}

export function createHabitsApi(token, onUnauthorized) {
  const http = axios.create({ ...config, headers: { Authorization: 'Bearer ' + token } })
  http.interceptors.response.use(undefined, (error) => {
    if (error.response?.status === 401 && !error.config?.signal?.aborted) onUnauthorized()
    return Promise.reject(error)
  })

  const data = (request) => request.then((response) => response.data)
  const habit = (id) => '/habits/' + encodeURIComponent(id)

  return {
    today: (signal) => data(http.get('/habits/today', { signal })),
    list: (status, page, signal) => data(http.get('/habits', { params: { status, page, pageSize: 20 }, signal })),
    get: (id, signal) => data(http.get(habit(id), { signal })),
    create: (fields) => data(http.post('/habits', fields)),
    edit: (id, fields) => data(http.put(habit(id), fields)),
    archive: async (id) => { await http.delete(habit(id)) },
    setCompleted: async (id, date, completed) => {
      const url = habit(id) + '/completions/' + encodeURIComponent(date)
      await (completed ? http.put(url) : http.delete(url))
    },
    history: (id, params, signal) => data(http.get(habit(id) + '/history', { params, signal })),
    statistics: (id, params, signal) => data(http.get(habit(id) + '/statistics', { params, signal })),
  }
}

export function errorMessage(error) {
  const data = error.response?.data
  const validation = data?.errors && Object.values(data.errors).flat().filter(Boolean).join(' ')
  return validation || data?.message || data?.title || data?.code ||
    (error.request ? 'Не удалось связаться с сервером. Попробуйте ещё раз.' : error.message) ||
    'Не удалось выполнить запрос.'
}
