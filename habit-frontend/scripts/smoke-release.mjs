import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { setTimeout as delay } from 'node:timers/promises'
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr'

const base = new URL(process.argv[2])
const minute = 60_000
// Scheduler and executor each poll on their own one-minute cycle on k3s.
const reminderGrace = 160_000
const runId = randomUUID()

async function request(path, expected, { method = 'GET', body, token } = {}) {
  let response
  try {
    response = await fetch(new URL(path, base), {
      method,
      headers: {
        ...(token && { Authorization: 'Bearer ' + token }),
        ...(body !== undefined && { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(15_000),
    })
  } catch {
    throw new Error(method + ' ' + path + ' did not respond')
  }
  assert.equal(response.status, expected, method + ' ' + path)
  return expected === 204 ? undefined : response.json()
}

async function createAccount(role) {
  const email = 'ci-' + role + '-' + runId + '@example.test'
  const password = randomUUID() + 'aA1!'
  await request('/api/auth/register', 201, {
    method: 'POST', body: { nickName: 'CI ' + role, email, password },
  })
  const { token } = await request('/api/auth/login', 200, {
    method: 'POST', body: { email, password },
  })
  assert.ok(typeof token === 'string' && token.length > 0, 'Login did not return a token')
  return token
}

function connect(token, received) {
  const hub = new HubConnectionBuilder()
    .withUrl(new URL('/hubs/notifications', base).href, {
      accessTokenFactory: () => token,
      transport: HttpTransportType.WebSockets,
      timeout: 15_000,
    })
    .configureLogging(LogLevel.None)
    .build()
  hub.on('NotificationReceived', message => received.push(message))
  return hub
}

async function startHub(hub) {
  try {
    await hub.start()
  } catch {
    throw new Error('Could not open the SignalR WebSocket')
  }
}

async function until(deadline, check, description) {
  do {
    const value = await check()
    if (value) return value
    await delay(3000)
  } while (Date.now() < deadline)
  throw new Error(description + ' timed out')
}

function nextReminder(localDay) {
  let due = Math.floor(Date.now() / minute) * minute + 2 * minute
  // Worker expires jobs at local midnight. Leave enough time for both polling cycles.
  while (localDay.format(due) !== localDay.format(due + reminderGrace)) {
    due += minute
  }
  return due
}

const fixtures = []
const connections = []
let owner

async function createHabit(due, title, localClock) {
  const name = 'CI smoke ' + title + ' ' + runId
  const reminder = { enabled: true, localTime: localClock.format(due) }
  const created = await request('/api/habits', 201, {
    method: 'POST', token: owner, body: { name, daysOfWeek: [1, 2, 3, 4, 5, 6, 7], reminder },
  })
  assert.ok(typeof created.id === 'string' && created.id.length > 0, 'Habit ID missing')
  const path = '/api/habits/' + encodeURIComponent(created.id)
  fixtures.push(path)
  const habit = await request(path, 200, { token: owner })
  assert.equal(habit.name, name)
  assert.deepEqual(await request(path + '/reminder', 200, { token: owner }), reminder)
  return created.id
}

async function main() {
  owner = await createAccount('owner')
  const observer = await createAccount('observer')
  const { timeZoneId } = await request('/api/habits/today', 200, { token: owner })
  assert.ok(timeZoneId, 'The current calendar did not return a time zone')
  const localClock = new Intl.DateTimeFormat('en-GB', {
    timeZone: timeZoneId, hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  })
  const localDay = new Intl.DateTimeFormat('en-CA', { timeZone: timeZoneId, dateStyle: 'short' })
  const received = []
  const observed = []
  const ownerHub = connect(owner, received)
  const observerHub = connect(observer, observed)
  connections.push(ownerHub, observerHub)
  await Promise.all(connections.map(startHub))

  const firstDue = nextReminder(localDay)
  const onlineHabit = await createHabit(firstDue, 'online', localClock)
  await request('/api/habits/' + encodeURIComponent(onlineHabit), 404, { token: observer })

  const online = await until(firstDue + reminderGrace, () =>
    received.find(item => item.kind === 'habitReminder' && item.habitId === onlineHabit),
  'Online reminder')
  await ownerHub.stop()
  const secondDue = nextReminder(localDay)
  const offlineHabit = await createHabit(secondDue, 'offline', localClock)
  const history = async token => (await request('/api/notifications', 200, { token })).items
  const offline = await until(secondDue + reminderGrace, async () =>
    (await history(owner)).find(item => item.kind === 'habitReminder' && item.habitId === offlineHabit),
  'Offline reminder')

  await startHub(ownerHub)
  const saved = await history(owner)
  assert.equal(saved.filter(item => item.habitId === onlineHabit && item.id === online.id).length, 1)
  assert.equal(saved.filter(item => item.habitId === offlineHabit && item.id === offline.id).length, 1)
  assert.equal(saved.filter(item => [onlineHabit, offlineHabit].includes(item.habitId)).length, 2)
  assert.equal(observed.length, 0, 'Another account received a SignalR notification')
  assert.equal((await history(observer)).length, 0, 'Another account can see notification history')
  const readPath = '/api/notifications/' + encodeURIComponent(offline.id) + '/read'
  await request(readPath, 404, { method: 'PUT', token: observer })
  await request(readPath, 204, { method: 'PUT', token: owner })
  await request(readPath, 204, { method: 'PUT', token: owner })
  assert.ok((await history(owner)).find(item => item.id === offline.id)?.readAtUtc, 'Read status was not saved')
}

try {
  await main()
  console.log('Release smoke passed: JWT, habits, WebSocket delivery, offline history and account isolation.')
} catch (error) {
  console.error('Release smoke failed: ' + error.message)
  process.exitCode = 1
} finally {
  await Promise.allSettled(connections.map(hub => hub.stop()))
  for (const path of fixtures) {
    try {
      await request(path, 204, { method: 'DELETE', token: owner })
    } catch {
      console.error('Could not archive the synthetic habit ' + path)
      process.exitCode = 1
    }
  }
}
