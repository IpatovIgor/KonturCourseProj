// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App.jsx'

const transport = vi.hoisted(() => vi.fn())
vi.mock('axios', async (load) => {
  const { default: axios } = await load()
  return { default: { ...axios, create: (config) => axios.create({ ...config, adapter: transport }) } }
})

const date = '2026-11-10'
const habit = { id: 'abc', name: 'Чтение', description: 'Десять страниц', daysOfWeek: [2], startDate: '2026-11-01', archivedAt: null, nextSchedule: { effectiveFrom: '2026-11-11', daysOfWeek: [3] } }
const archived = { ...habit, id: 'old', name: 'Прогулка', archivedAt: '2026-11-09T10:00:00Z', archivedDate: '2026-11-09', nextSchedule: null }
const history = { from: '2026-10-12', to: date, days: [{ date: '2026-11-09', status: 'missed' }, { date: '2026-11-10', status: 'notScheduled' }] }
const statistics = { from: history.from, to: date, plannedDays: 0, completedDays: 0, missedDays: 0, pendingDays: 0, completionRatePercent: null, currentStreak: 0, longestStreak: 3 }
const today = { date, timeZoneId: 'Asia/Yekaterinburg', items: [{ ...habit, completed: false }] }
const reply = (data, status = 200) => ({ data, status })
const failure = (status, data) => Object.assign(new Error('HTTP error'), { response: { status, data } })
const click = (name) => fireEvent.click(screen.getByRole('button', { name, exact: true }))
const waitButton = (name) => waitFor(() => {
  const button = screen.getByRole('button', { name, exact: true })
  expect(button.disabled).toBe(false)
  return button
})
const fill = (label, value) => fireEvent.change(screen.getByLabelText(label), { target: { value } })

function route(config) {
  return `${config.method.toUpperCase()} ${config.baseURL}${config.url}`
}

function mockApi(overrides = {}) {
  let completed = false
  const handlers = {
    'POST /api/auth/register': () => reply({ message: 'Пользователь успешно зарегистрирован' }, 201),
    'POST /api/auth/login': () => reply({ token: 'test-token', email: 'reader@example.test' }),
    'GET /api/habits/today': () => reply({ ...today, items: [{ ...habit, completed }] }),
    'GET /api/habits': ({ params }) => reply({ items: params.status === 'archived' ? [archived] : [habit], totalCount: 1, page: params.page, pageSize: 20 }),
    'POST /api/habits': () => reply(habit, 201),
    'GET /api/habits/abc': () => reply(habit),
    'GET /api/habits/old': () => reply(archived),
    'PUT /api/habits/abc': () => reply(habit),
    'DELETE /api/habits/abc': () => reply('', 204),
    'PUT /api/habits/abc/completions/2026-11-10': () => { completed = true; return reply('', 204) },
    'DELETE /api/habits/abc/completions/2026-11-10': () => { completed = false; return reply('', 204) },
    'PUT /api/habits/abc/completions/2026-11-09': () => reply('', 204),
    'GET /api/habits/abc/history': () => reply(history),
    'GET /api/habits/abc/statistics': () => reply(statistics),
    'GET /api/habits/old/history': () => reply(history),
    'GET /api/habits/old/statistics': () => reply(statistics),
    ...overrides,
  }
  transport.mockImplementation(async (config) => {
    const handler = handlers[route(config)]
    if (!handler) throw new Error(`Unexpected request: ${route(config)}`)
    return handler(config)
  })
}

function request(name) {
  return transport.mock.calls.map(([config]) => config).find((config) => route(config) === name)
}

function signedIn() {
  localStorage.setItem('token', 'test-token')
  render(<App />)
}

async function register() {
  click('Создать аккаунт')
  fill('Никнейм', 'Читатель')
  fill('Email', 'reader@example.test')
  fill('Пароль', 'password')
  click('Зарегистрироваться')
}

beforeEach(() => {
  localStorage.clear()
  transport.mockReset()
  mockApi()
})
afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

describe('Авторизация', () => {
  it('получает токен через login после регистрации без токена', async () => {
    render(<App />)
    await register()
    await waitButton('Отметить')
    expect(JSON.parse(request('POST /api/auth/register').data)).toEqual({ nickName: 'Читатель', email: 'reader@example.test', password: 'password' })
    expect(JSON.parse(request('POST /api/auth/login').data)).toEqual({ email: 'reader@example.test', password: 'password' })
    expect(request('GET /api/habits/today').headers.get('Authorization')).toBe('Bearer test-token')
    expect(localStorage.getItem('token')).toBe('test-token')
    click('Выйти')
    expect(localStorage.getItem('token')).toBeNull()
    expect(screen.getByRole('heading', { name: 'Вход' })).toBeTruthy()
  })

  it('сохраняет email в форме входа при сбое после создания аккаунта', async () => {
    mockApi({ 'POST /api/auth/login': () => { throw failure(401, { message: 'Проверьте пароль' }) } })
    localStorage.setItem('token', 'undefined')
    render(<App />)
    await register()
    expect(await screen.findByText(/Аккаунт создан/)).toBeTruthy()
    expect(screen.getByLabelText('Email').value).toBe('reader@example.test')
    expect(screen.getByText('Проверьте пароль')).toBeTruthy()
    expect(localStorage.getItem('token')).toBeNull()
  })

  it('завершает текущую сессию при 401 защищённой ручки', async () => {
    mockApi({ 'GET /api/habits/today': () => { throw failure(401, { title: 'Требуется авторизация' }) } })
    signedIn()
    expect(await screen.findByText(/Сессия завершилась/)).toBeTruthy()
    expect(localStorage.getItem('token')).toBeNull()
  })

  it('игнорирует запоздавший 401 после повторного входа с тем же токеном', async () => {
    let rejectFirst
    const load = vi.fn().mockImplementationOnce(() => new Promise((_, reject) => { rejectFirst = reject })).mockResolvedValue(reply(today))
    mockApi({ 'GET /api/habits/today': load })
    signedIn()
    await waitFor(() => expect(load).toHaveBeenCalledTimes(1))
    click('Выйти')
    fill('Email', 'reader@example.test')
    fill('Пароль', 'password')
    click('Войти')
    await waitButton('Отметить')
    await act(async () => rejectFirst(failure(401, {})))
    expect(localStorage.getItem('token')).toBe('test-token')
    expect(screen.getByRole('button', { name: 'Выйти' })).toBeTruthy()
  })
})

describe('Привычки', () => {
  it('ставит и снимает сегодняшнюю отметку с датой от API', async () => {
    signedIn()
    fireEvent.click(await waitButton('Отметить'))
    fireEvent.click(await waitButton('Снять отметку'))
    await waitButton('Отметить')
    expect(request('PUT /api/habits/abc/completions/2026-11-10').data).toBeUndefined()
    expect(request('DELETE /api/habits/abc/completions/2026-11-10').data).toBeUndefined()
    expect(screen.getByText(/Asia\/Yekaterinburg/)).toBeTruthy()
  })

  it('создаёт привычку с числовыми днями и проверяет пустое расписание', async () => {
    signedIn()
    click('Новая привычка')
    fill('Название', '  Зарядка  ')
    fill('Описание', '  Утром  ')
    for (const checkbox of screen.getAllByRole('checkbox')) fireEvent.click(checkbox)
    click('Создать привычку')
    expect(screen.getByText('Выберите хотя бы один день недели.')).toBeTruthy()
    expect(request('POST /api/habits')).toBeUndefined()
    fireEvent.click(screen.getByRole('checkbox', { name: 'Понедельник' }))
    click('Создать привычку')
    await waitFor(() => expect(JSON.parse(request('POST /api/habits').data)).toEqual({ name: 'Зарядка', description: 'Утром', daysOfWeek: [1] }))
    await screen.findByText('Привычка создана.')
  })

  it('открывает детали и сохраняет уже запланированное расписание', async () => {
    signedIn()
    click('Все привычки')
    fireEvent.click(await waitButton('Чтение'))
    fireEvent.click(await waitButton('Редактировать'))
    expect(screen.getByRole('checkbox', { name: 'Среда' }).checked).toBe(true)
    expect(screen.getByRole('checkbox', { name: 'Вторник' }).checked).toBe(false)
    click('Сохранить')
    await screen.findByText('Изменения сохранены.')
    expect(request('GET /api/habits').params).toEqual({ status: 'active', page: 1, pageSize: 20 })
    expect(request('GET /api/habits/abc')).toBeTruthy()
    expect(JSON.parse(request('PUT /api/habits/abc').data).daysOfWeek).toEqual([3])
  })

  it('даёт отметить прошедший запланированный день и передаёт обе границы периода', async () => {
    signedIn()
    fireEvent.click(await waitButton('Чтение'))
    const row = await screen.findByText('09.11.2026')
    fireEvent.click(within(row.closest('li')).getByRole('button', { name: 'Отметить' }))
    await waitFor(() => expect(request('PUT /api/habits/abc/completions/2026-11-09')).toBeTruthy())
    expect(within(screen.getByText('Не запланировано').closest('li')).queryByRole('button')).toBeNull()
    expect(screen.getByText('—')).toBeTruthy()
    fill('С даты', '2026-11-01')
    fill('По дату', '2026-11-11')
    click('Применить период')
    expect(screen.getByText('Период должен завершаться не позже сегодня и содержать до 366 дней.')).toBeTruthy()
    fill('По дату', '2026-11-09')
    click('Применить период')
    for (const endpoint of ['history', 'statistics']) {
      await waitFor(() => expect(transport.mock.calls.some(([config]) => route(config) === `GET /api/habits/abc/${endpoint}` && config.params?.from === '2026-11-01' && config.params?.to === '2026-11-09')).toBe(true))
    }
  })

  it('архивирует последнюю запись на странице и показывает архив без редактирования', async () => {
    let moved = false
    mockApi({
      'GET /api/habits': ({ params }) => reply({ page: params.page, pageSize: 20, totalCount: params.status === 'archived' ? 1 : moved ? 20 : 21, items: params.status === 'archived' ? [archived] : params.page === 2 ? (moved ? [] : [habit]) : [{ ...habit, name: 'Зарядка' }] }),
      'DELETE /api/habits/abc': () => { moved = true; return reply('', 204) },
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    signedIn()
    click('Все привычки')
    fireEvent.click(await waitButton('Следующая'))
    fireEvent.click(await waitButton('Чтение'))
    fireEvent.click(await waitButton('Архивировать'))
    await waitButton('Зарядка')
    expect(screen.getByText('Страница 1 из 1')).toBeTruthy()
    click('Архив')
    fireEvent.click(await waitButton('Прогулка'))
    await screen.findByText('Привычка в архиве.')
    expect(screen.queryByRole('button', { name: 'Редактировать' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Отметить' })).toBeNull()
  })

  it('показывает пустой список отдельно от ошибки и позволяет повторить загрузку', async () => {
    const load = vi.fn()
      .mockRejectedValueOnce(Object.assign(new Error('Network Error'), { request: {} }))
      .mockResolvedValue(reply({ ...today, items: [] }))
    mockApi({ 'GET /api/habits/today': load })
    signedIn()
    expect((await screen.findByRole('alert')).textContent).toContain('Не удалось связаться с сервером')
    click('Повторить загрузку')
    expect(await screen.findByText('На сегодня ничего не запланировано.')).toBeTruthy()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('блокирует повторную отправку формы и сохраняет поля при сетевой ошибке', async () => {
    let rejectSave
    const save = vi.fn()
      .mockImplementationOnce(() => new Promise((_, reject) => { rejectSave = reject }))
      .mockResolvedValue(reply(habit, 201))
    mockApi({ 'POST /api/habits': save })
    signedIn()
    click('Новая привычка')
    fill('Название', 'Зарядка')
    click('Создать привычку')
    click('Создать привычку')
    await waitFor(() => expect(save).toHaveBeenCalledTimes(1))
    await act(async () => rejectSave(Object.assign(new Error('Network Error'), { request: {} })))
    expect(screen.getByRole('alert').textContent).toContain('Не удалось связаться с сервером')
    expect(screen.getByLabelText('Название').value).toBe('Зарядка')
    expect(screen.queryByText('Привычка создана.')).toBeNull()
    click('Создать привычку')
    await screen.findByText('Привычка создана.')
  })

  it('обновляет дату отметки и открытый период после смены серверных суток', async () => {
    const load = vi.fn().mockResolvedValue(reply(today))
    const getHabit = vi.fn().mockResolvedValue(reply(habit))
    mockApi({ 'GET /api/habits/today': load, 'GET /api/habits/abc': getHabit })
    signedIn()
    fireEvent.click(await waitButton('Чтение'))
    await screen.findByText('09.11.2026')
    load.mockResolvedValue(reply({ ...today, date: '2026-11-11' }))
    getHabit.mockResolvedValue(reply({ ...habit, daysOfWeek: [3], nextSchedule: null }))
    fireEvent.focus(window)
    await waitFor(() => expect(transport.mock.calls.filter(([config]) => route(config) === 'GET /api/habits/abc/history')).toHaveLength(2))
    expect(await screen.findByText('Расписание · Ср')).toBeTruthy()
    click('К списку')
    expect(await screen.findByText(/11\.11\.2026 · Asia\/Yekaterinburg/)).toBeTruthy()
  })

  it('игнорирует поздний ответ от предыдущей карточки', async () => {
    let finishFirst
    const loadFirst = vi.fn(() => new Promise((resolve) => { finishFirst = resolve }))
    mockApi({
      'GET /api/habits/abc': loadFirst,
      'GET /api/habits': () => reply({ items: [habit, archived], totalCount: 2, page: 1, pageSize: 20 }),
    })
    signedIn()
    await waitButton('Отметить')
    click('Все привычки')
    fireEvent.click(await waitButton('Чтение'))
    await waitFor(() => expect(loadFirst).toHaveBeenCalledTimes(1))
    click('К списку')
    click('Прогулка')
    await screen.findByRole('heading', { name: 'Прогулка' })
    await act(async () => finishFirst(reply(habit)))
    expect(screen.queryByRole('heading', { name: 'Чтение' })).toBeNull()
    expect(screen.getByRole('heading', { name: 'Прогулка' })).toBeTruthy()
  })
})
