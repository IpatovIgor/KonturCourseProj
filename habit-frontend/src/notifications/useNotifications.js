import { useEffect, useMemo, useRef, useState } from 'react'
import { jwtExpiration, openNotificationsConnection } from './connection.js'

const receivedEvent = 'NotificationReceived'
const initialState = { items: [], unreadCount: 0, error: null, loading: true }
const noop = () => {}

function newestFirst(a, b) {
  const byTime = Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc)
  return byTime || Number(a.id < b.id) - Number(a.id > b.id)
}

export default function useNotifications(api, token, onUnauthorized) {
  const [state, setState] = useState(null)
  const session = useMemo(() => ({ token, api, onUnauthorized }), [token, api, onUnauthorized])
  const commands = useRef(null)
  const previousStop = useRef(Promise.resolve())

  useEffect(() => {
    if (!token || !api) return
    const controller = new AbortController()
    const { signal } = controller
    const items = new Map()
    const expiresAt = jwtExpiration(token)
    let hub, scan, startTimer, countTimer, expiryTimer
    let retryDelay = 1000
    let countBusy = false
    let countRequested = false
    let sequence = 0
    let appliedCount = 0
    let snapshot = initialState

    function update(values) {
      if (signal.aborted) return
      snapshot = { ...snapshot, ...values }
      setState({ owner: session, data: snapshot })
    }

    function merge(notification) {
      const previous = items.get(notification.id)
      items.set(notification.id, {
        ...notification,
        readAtUtc: notification.readAtUtc ?? previous?.readAtUtc ?? null,
      })
      return !previous
    }

    function publishItems(values) {
      update({ items: [...items.values()].sort(newestFirst), ...values })
    }

    async function fetchPage(cursor, requestSignal) {
      const request = ++sequence
      const page = await api.history(cursor, requestSignal)
      if (signal.aborted || requestSignal.aborted) return null
      page.items.forEach(merge)
      const values = { loading: false }
      if (request > appliedCount) {
        appliedCount = request
        values.unreadCount = page.unreadCount
      }
      publishItems(values)
      return page
    }

    async function synchronize() {
      scan?.abort()
      scan = new AbortController()
      const requestSignal = scan.signal
      update({ error: null })
      try {
        let cursor
        do {
          const page = await fetchPage(cursor, requestSignal)
          if (!page) return
          cursor = page.nextCursor
        } while (cursor)
      } catch (error) {
        if (!requestSignal.aborted) update({ error, loading: false })
      }
    }

    async function refreshCount() {
      countTimer = undefined
      countBusy = true
      countRequested = false
      try {
        await fetchPage(undefined, signal)
      } catch (error) {
        update({ error, loading: false })
      } finally {
        countBusy = false
        if (countRequested) requestCount()
      }
    }

    function requestCount() {
      if (signal.aborted) return
      countRequested = true
      if (!countBusy && countTimer === undefined) countTimer = setTimeout(refreshCount, 150)
    }

    function receive(notification) {
      if (signal.aborted) return
      const isNew = merge(notification)
      publishItems()
      if (isNew) requestCount()
    }

    async function markRead(id) {
      await api.markRead(id, signal)
      if (signal.aborted) return
      const item = items.get(id)
      if (item && !item.readAtUtc) merge({ ...item, readAtUtc: new Date().toISOString() })
      publishItems()
      requestCount()
    }

    function endSession() {
      if (!signal.aborted) onUnauthorized()
    }

    function checkExpiration() {
      if (signal.aborted || expiresAt === null) return
      const remaining = expiresAt - Date.now()
      if (remaining <= 0) endSession()
      else expiryTimer = setTimeout(checkExpiration, Math.min(remaining, 2_147_483_647))
    }

    function retryStart() {
      if (signal.aborted) return
      startTimer = setTimeout(start, retryDelay)
      retryDelay = Math.min(30000, retryDelay * 2)
    }

    async function start() {
      if (signal.aborted) return
      try {
        await hub.start()
        if (signal.aborted) return
        retryDelay = 1000
        void synchronize()
      } catch {
        retryStart()
      }
    }

    commands.current = { retry: synchronize, markRead }
    update({})
    checkExpiration()
    // Wait for any previous connection to stop before starting this session.
    startTimer = setTimeout(async () => {
      await previousStop.current
      if (signal.aborted || (expiresAt !== null && expiresAt <= Date.now())) return
      hub = openNotificationsConnection(token, endSession)
      hub.on(receivedEvent, receive)
      hub.onreconnected(() => { if (!signal.aborted) void synchronize() })
      hub.onclose(retryStart)
      void synchronize()
      void start()
    }, 0)

    return () => {
      controller.abort()
      scan?.abort()
      clearTimeout(startTimer)
      clearTimeout(countTimer)
      clearTimeout(expiryTimer)
      commands.current = null
      if (hub) {
        hub.off(receivedEvent, receive)
        previousStop.current = hub.stop().catch(noop)
      }
    }
  }, [api, token, onUnauthorized, session])

  const current = state?.owner === session ? state.data : initialState
  return {
    ...current,
    retry: () => commands.current?.retry(),
    markRead: (id) => commands.current?.markRead(id),
  }
}
