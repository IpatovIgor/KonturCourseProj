// @vitest-environment jsdom
import { StrictMode } from 'react'
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { openNotificationsConnection } from './connection.js'
import NotificationPanel from './NotificationPanel.jsx'
import SummarySettings from './SummarySettings.jsx'
import useNotifications from './useNotifications.js'

vi.mock('./connection.js', async (load) => ({ ...await load(), openNotificationsConnection: vi.fn() }))

const token = 'test-token'
const unread = (id, day = '15') => ({
  id, createdAtUtc: '2026-11-' + day + 'T12:00:00Z', readAtUtc: null,
  localDate: '2026-11-' + day, kind: 'dailySummary', planned: 2, completed: 1, pending: 1,
})
const page = (items = [], unreadCount = items.length, nextCursor = null) => ({ items, unreadCount, nextCursor })
const unauthorized = vi.fn()

function deferred() {
  let resolve
  const promise = new Promise((done) => { resolve = done })
  return { promise, resolve }
}

function connection() {
  let received, reconnect, closed
  return {
    on: vi.fn((_, handler) => { received = handler }),
    off: vi.fn(() => { received = undefined }),
    onreconnected: (handler) => { reconnect = handler },
    onclose: (handler) => { closed = handler },
    start: vi.fn(async () => { expect(received).toBeTypeOf('function') }),
    stop: vi.fn(async () => {}),
    emit: (notification) => received?.(notification),
    reconnect: () => reconnect(),
    close: () => closed(),
  }
}

function strict({ children }) {
  return <StrictMode>{children}</StrictMode>
}

let hub
beforeEach(() => {
  hub = connection()
  openNotificationsConnection.mockReset().mockReturnValue(hub)
  unauthorized.mockReset()
})
afterEach(cleanup)

describe('notification history', () => {
  it('retries the initial start and opens one active connection under StrictMode', async () => {
    hub.start.mockRejectedValueOnce(new Error('offline'))
    const api = { history: vi.fn(async () => page()) }
    renderHook(() => useNotifications(api, token, unauthorized), { wrapper: strict })
    await waitFor(() => expect(hub.start).toHaveBeenCalledTimes(2), { timeout: 3000 })
    expect(openNotificationsConnection).toHaveBeenCalledTimes(1)
  })

  it('walks all reconnect pages, merges live messages and keeps a read marker on duplicates', async () => {
    const known = { ...unread('known'), readAtUtc: '2026-11-16T10:00:00Z' }
    const latest = unread('live', '16')
    const recovered = Array.from({ length: 22 }, (_, index) => unread('missed-' + index))
    const old = unread('delayed', '12')
    let recovering = false
    const first = deferred()
    const api = {
      history: vi.fn(async (cursor) => {
        if (!recovering) return page([known], 0)
        if (cursor === 'second') return page([...recovered.slice(19), old], 24)
        return first.promise
      }),
    }
    const { result } = renderHook(() => useNotifications(api, token, unauthorized))
    await waitFor(() => expect(result.current.items.map((item) => item.id)).toEqual(['known']))
    expect(hub.start).toHaveBeenCalledTimes(1)

    recovering = true
    act(() => {
      hub.reconnect()
      hub.emit(latest)
      hub.emit(unread('known'))
      hub.emit(latest)
    })
    await act(async () => first.resolve(page([known, ...recovered.slice(0, 19)], 24, 'second')))
    await waitFor(() => expect(result.current.items).toHaveLength(25))
    expect(result.current.items[0].id).toBe('live')
    expect(result.current.items.at(-1).id).toBe('delayed')
    expect(result.current.items.find((item) => item.id === 'known').readAtUtc).toBe(known.readAtUtc)
    expect(result.current.unreadCount).toBe(24)
  })

  it('coalesces events and refreshes again if an event arrives during a count request', async () => {
    const pending = deferred()
    const api = { history: vi.fn(async () => page()) }
    const { result } = renderHook(() => useNotifications(api, token, unauthorized))
    await waitFor(() => expect(result.current.loading).toBe(false))
    const before = api.history.mock.calls.length
    api.history.mockImplementationOnce(() => pending.promise).mockResolvedValue(page([], 37))
    act(() => {
      hub.emit(unread('one'))
      hub.emit(unread('two'))
    })
    await waitFor(() => expect(api.history).toHaveBeenCalledTimes(before + 1))
    act(() => hub.emit(unread('three')))
    await act(async () => pending.resolve(page([], 36)))
    await waitFor(() => expect(result.current.unreadCount).toBe(37))
    expect(api.history).toHaveBeenCalledTimes(before + 2)
  })

  it('clears on logout and ignores an HTTP response that arrives after cancellation', async () => {
    const late = deferred()
    const api = { history: vi.fn(() => late.promise) }
    const { result, rerender } = renderHook(
      ({ jwt }) => useNotifications(api, jwt, unauthorized), { initialProps: { jwt: token } },
    )
    await waitFor(() => expect(api.history).toHaveBeenCalled())
    act(() => hub.emit(unread('one')))
    expect(result.current.items).toHaveLength(1)
    rerender({ jwt: null })
    expect(result.current.items).toEqual([])
    expect(result.current.unreadCount).toBe(0)
    for (const [, signal] of api.history.mock.calls) expect(signal.aborted).toBe(true)
    await act(async () => late.resolve(page([unread('late')])))
    expect(result.current.items).toEqual([])
    await waitFor(() => expect(hub.stop).toHaveBeenCalledTimes(1))
  })

  it('keeps a failed read visible as unread and refreshes the count after a retry', async () => {
    const api = {
      history: vi.fn().mockResolvedValue(page([unread('one')], 1)),
      markRead: vi.fn().mockRejectedValueOnce(new Error('Сбой отметки')).mockResolvedValue(undefined),
    }
    function Panel() {
      return <NotificationPanel {...useNotifications(api, token, unauthorized)} />
    }
    render(<Panel />)
    fireEvent.click(screen.getByText('Уведомления', { exact: false }))
    fireEvent.click(await screen.findByText('Прочитано'))
    expect(await screen.findByText('Сбой отметки')).toBeTruthy()
    expect(screen.getByText('Прочитано')).toBeTruthy()

    api.history.mockResolvedValue(page([], 0))
    fireEvent.click(screen.getByText('Прочитано'))
    await waitFor(() => expect(screen.queryByText('Прочитано')).toBeNull())
    await waitFor(() => expect(screen.getByText('Непрочитанных · 0')).toBeTruthy())
  })
})

describe('notification settings', () => {
  it('sends local time as entered, keeps the draft after failure and shows the saved values', async () => {
    const api = {
      getSummary: vi.fn(async () => ({ enabled: false, localTime: '21:00' })),
      saveSummary: vi.fn().mockRejectedValueOnce(new Error('Сбой сохранения'))
        .mockResolvedValue({ enabled: true, localTime: '20:05' }),
    }
    render(<SummarySettings api={api} />)
    const time = await screen.findByLabelText('Местное время аккаунта')
    expect(time.value).toBe('21:00')
    fireEvent.click(screen.getByLabelText('Показывать дневную сводку'))
    fireEvent.change(time, { target: { value: '20:00' } })
    fireEvent.click(screen.getByText('Сохранить сводку'))
    expect(await screen.findByText('Сбой сохранения')).toBeTruthy()
    expect(time.value).toBe('20:00')
    expect(api.saveSummary.mock.calls[0][0]).toEqual({ enabled: true, localTime: '20:00' })
    fireEvent.click(screen.getByText('Сохранить сводку'))
    expect(await screen.findByText('Настройки сохранены.')).toBeTruthy()
    expect(time.value).toBe('20:05')
  })

  it('reports settings disabled by the server instead of offering a save', async () => {
    const api = { getSummary: vi.fn().mockRejectedValue({ response: { status: 404 } }) }
    render(<SummarySettings api={api} />)
    expect(await screen.findByText('Настройка уведомлений недоступна.')).toBeTruthy()
    expect(screen.queryByText('Сохранить сводку')).toBeNull()
  })
})
