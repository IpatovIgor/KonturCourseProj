import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { readFileSync } from 'node:fs'

const definitions = JSON.parse(readFileSync(new URL('./definitions.json', import.meta.url)))
const api = process.env.KONTUR_TEST_RABBIT_MANAGEMENT_URI
const credentials = Buffer.from(
  process.env.KONTUR_TEST_RABBIT_USER + ':' + process.env.KONTUR_TEST_RABBIT_PASSWORD,
).toString('base64')
const clean = 'bootstrap-test-' + randomUUID()
const conflict = clean + '-conflict'
const argumentConflict = clean + '-arguments'
const file = '/tmp/' + clean + '.json'
const broker = (...args) => ['compose', '-f', 'compose.webapi-tests.yaml', 'exec', '-T', 'rabbitmq', ...args]

async function management(path, method = 'GET', body) {
  const response = await fetch(api + '/api' + path, {
    method,
    headers: { Authorization: 'Basic ' + credentials, 'Content-Type': 'application/json', Connection: 'close' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(15000),
  })
  if (method === 'DELETE' && response.status === 404) return
  assert.ok(response.ok, method + ' ' + path + ' returned ' + response.status)
  const content = await response.text()
  if (content) return JSON.parse(content)
}

function forVhost(vhost) {
  return {
    vhosts: [{ name: vhost }],
    exchanges: definitions.exchanges.map(exchange => ({ ...exchange, vhost })),
    queues: definitions.queues.map(queue => ({ ...queue, vhost })),
    bindings: definitions.bindings.map(binding => ({ ...binding, vhost })),
  }
}

function prepare(vhost) {
  const copy = spawnSync('docker', broker('sh', '-c', 'cat > "$1"', 'sh', file), {
    input: JSON.stringify(forVhost(vhost)), encoding: 'utf8', timeout: 15000,
  })
  assert.equal(copy.status, 0, copy.stderr || copy.error?.message)
}

function bootstrap(vhost, status) {
  const result = spawnSync('docker', broker('env', 'RABBITMQ_TOPOLOGY_VHOST=' + vhost,
    'RABBITMQ_DEFINITIONS_FILE=' + file, 'sh', '/etc/rabbitmq/bootstrap.sh'), {
    encoding: 'utf8', timeout: 60000,
  })
  assert.equal(result.status, status, result.stderr || result.stdout || result.error?.message)
}

try {
  prepare(clean)
  bootstrap(clean, 0)
  bootstrap(clean, 0)
  const bindings = await management('/bindings/' + clean)
  assert.equal(bindings.filter(binding => binding.source).length, 5)
  const queues = await management('/queues/' + clean)
  assert.equal(queues.length, 2)
  assert.ok(queues.every(queue => queue.type === 'classic' && queue.durable && !queue.auto_delete))
  for (const policyType of ['policies', 'operator-policies']) {
    const policy = '/' + policyType + '/' + clean + '/limit-queue'
    await management(policy, 'PUT', {
      pattern: '^worker-habit-events-v1$', definition: { 'max-length': 1 }, 'apply-to': 'queues',
    })
    bootstrap(clean, 2)
    assert.equal((await management(policy)).definition['max-length'], 1)
    await management(policy, 'DELETE')
  }
  bootstrap(clean, 0)
  await management('/definitions', 'POST', {
    bindings: [{
      source: 'habit-changed-v1', destination: 'notifications-ready-v1', destination_type: 'exchange',
      routing_key: '', arguments: {}, vhost: clean,
    }],
  })
  bootstrap(clean, 2)

  prepare(conflict)
  const incompatible = forVhost(conflict)
  incompatible.queues[0].durable = false
  await management('/definitions', 'POST', incompatible)
  bootstrap(conflict, 2)
  const saved = await management('/queues/' + conflict)
  assert.equal(saved.find(queue => queue.name === incompatible.queues[0].name).durable, false)
  assert.equal((await management('/bindings/' + conflict)).filter(binding => binding.source).length, 5)

  prepare(argumentConflict)
  const unexpectedArguments = forVhost(argumentConflict)
  unexpectedArguments.queues[0].arguments['x-message-ttl'] = 60000
  await management('/definitions', 'POST', unexpectedArguments)
  bootstrap(argumentConflict, 2)
  const retained = await management('/queues/' + argumentConflict + '/' + unexpectedArguments.queues[0].name)
  assert.equal(retained.arguments['x-message-ttl'], 60000)
  console.log('RabbitMQ bootstrap verified imports, bindings, queue declarations and policies.')
} finally {
  spawnSync('docker', broker('rm', '-f', file), { timeout: 15000 })
  await Promise.all([clean, conflict, argumentConflict].map(vhost => management('/vhosts/' + vhost, 'DELETE')))
}
