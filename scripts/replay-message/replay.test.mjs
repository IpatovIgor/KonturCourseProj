import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { fileURLToPath } from 'node:url'

const management = process.env.KONTUR_TEST_RABBIT_MANAGEMENT_URI
const user = process.env.KONTUR_TEST_RABBIT_USER
const password = process.env.KONTUR_TEST_RABBIT_PASSWORD
const broker = new URL(process.env.KONTUR_TEST_RABBIT_URI)
const vhost = 'replay-test-' + randomUUID()
const destination = 'notifications-ready-v1'
const source = destination + '_error'
const unrelated = 'unrelated'
const eventId = randomUUID()
const correlationId = randomUUID()
const envelope = { messageId: eventId, correlationId, message: { eventId, correlationId } }
const authorization = 'Basic ' + Buffer.from(user + ':' + password).toString('base64')
const tool = fileURLToPath(new URL('./bin/Release/net8.0/replay-message.dll', import.meta.url))

async function rabbit(path, method = 'GET', body) {
  const response = await fetch(management + '/api' + path, {
    method,
    headers: { Authorization: authorization, 'Content-Type': 'application/json', Connection: 'close' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(15000),
  })
  assert.ok(response.ok, method + ' ' + path + ' returned ' + response.status)
  const contents = await response.text()
  return contents ? JSON.parse(contents) : undefined
}

function replay(flag) {
  return spawnSync('dotnet', [tool, '--source', source, '--destination', destination, '--event-id', eventId, flag], {
    encoding: 'utf8', timeout: 20000,
    env: {
      ...process.env,
      KONTUR_REPLAY_AMQP_URI: 'amqp://' + broker.host,
      RabbitMq__VirtualHost: vhost,
      RabbitMq__Username: user,
      RabbitMq__Password: password,
    },
  })
}

const declareQueue = queue => rabbit('/queues/' + vhost + '/' + queue, 'PUT', { durable: true })
const receive = (queue, ackmode) => rabbit('/queues/' + vhost + '/' + queue + '/get', 'POST', { count: 2, ackmode, encoding: 'auto' })

await rabbit('/vhosts/' + vhost, 'PUT')
try {
  await rabbit('/permissions/' + vhost + '/' + encodeURIComponent(user), 'PUT', { configure: '.*', write: '.*', read: '.*' })
  await declareQueue(source)
  await declareQueue(unrelated)
  await rabbit('/exchanges/' + vhost + '/' + destination, 'PUT', { type: 'fanout', durable: true })
  await rabbit('/bindings/' + vhost + '/e/' + destination + '/q/' + unrelated, 'POST', { routing_key: '', arguments: {} })
  const original = await rabbit('/exchanges/' + vhost + '/amq.default/publish', 'POST', {
    routing_key: source, payload: JSON.stringify(envelope), payload_encoding: 'string',
    properties: { message_id: eventId, correlation_id: correlationId },
  })
  assert.ok(original.routed)

  const missing = replay('--publish')
  assert.equal(missing.status, 1, missing.stderr || missing.stdout || missing.error?.message)
  assert.match(missing.stderr, /unroutable|NO_ROUTE/i)
  assert.deepEqual(await receive(unrelated, 'ack_requeue_false'), [])
  assert.equal((await receive(source, 'ack_requeue_true')).length, 1)

  await declareQueue(destination)
  const dryRun = replay('--dry-run')
  assert.equal(dryRun.status, 0, dryRun.stderr || dryRun.error?.message)
  assert.deepEqual(await receive(destination, 'ack_requeue_false'), [])

  const delivered = replay('--publish')
  assert.equal(delivered.status, 0, delivered.stderr || delivered.error?.message)
  const copies = await receive(destination, 'ack_requeue_false')
  assert.equal(copies.length, 1)
  assert.equal(copies[0].properties.message_id, eventId)
  assert.equal(copies[0].properties.correlation_id, correlationId)
  assert.deepEqual(JSON.parse(copies[0].payload), envelope)
  assert.deepEqual(await receive(unrelated, 'ack_requeue_false'), [])
  assert.equal((await receive(source, 'ack_requeue_true')).length, 1)
  console.log('Replay targeted only the named queue and retained the source when no ACK was confirmed.')
} finally {
  await rabbit('/vhosts/' + vhost, 'DELETE')
}
