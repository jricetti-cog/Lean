// Validates the v1 message contracts: compiles every JSON Schema with Ajv (draft 2020-12, strict), checks every
// example (envelope + body), negative cases, the inline size limit and the RabbitMQ definitions. Exit 1 on failure.
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const root = dirname(fileURLToPath(import.meta.url));
const dir = join(root, 'messages', 'v1');
const MAX_INLINE_BYTES = 256 * 1024; // topology.md "Limits"
const readJson = (p) => JSON.parse(readFileSync(p, 'utf8'));

const ajv = new Ajv2020({ strict: true, allErrors: true });
addFormats(ajv);
const files = readdirSync(dir).filter((f) => f.endsWith('.schema.json')).sort();
for (const f of files) ajv.addSchema(readJson(join(dir, f)), f);

let failures = 0;
const ok = (msg) => console.log(`  ok    ${msg}`);
const fail = (msg, errors) => {
  failures++;
  console.log(`  FAIL  ${msg}`);
  if (errors) console.log(ajv.errorsText(errors, { separator: '\n        ' }).replace(/^/, '        '));
};

console.log(`schemas (${files.length}, draft 2020-12, strict):`);
for (const f of files) {
  try { ajv.getSchema(f) ? ok(f) : fail(f); } catch (e) { fail(`${f}: ${e.message}`); }
}

// AMQP type -> body schema, routing-key pattern (topology.md, message catalogue).
const K = '(backtest|live|optimization|report|marketdata)';
const U = '[0-9a-f-]{36}';
const ev = (e) => new RegExp(`^job\\.${K}\\.${U}\\.${e}\\.v1$`);
const catalogue = {
  'jobs.v1.BacktestRequested': ['job.submitted.schema.json', /^jobs\.backtest\.v1$/],
  'jobs.v1.LiveRequested': ['job.submitted.schema.json', /^jobs\.live\.v1$/],
  'jobs.v1.OptimizationRequested': ['optimization.requested.schema.json', /^jobs\.optimization\.v1$/],
  'jobs.v1.ReportRequested': ['report.requested.schema.json', /^jobs\.report\.v1$/],
  'jobs.v1.MarketDataFetchRequested': ['marketdata.fetch.requested.schema.json', /^marketdata\.fetch\.v1$/],
  'events.v1.JobStarted': ['job.started.schema.json', ev('status')],
  'events.v1.JobHeartbeat': ['job.heartbeat.schema.json', ev('heartbeat')],
  'events.v1.JobProgress': ['job.progress.schema.json', ev('progress')],
  'events.v1.JobCompleted': ['job.completed.schema.json', ev('status')],
  'events.v1.JobFailed': ['job.failed.schema.json', ev('status')],
  'events.v1.JobCancelled': ['job.cancelled.schema.json', ev('status')],
  'control.v1.CancelJob': ['job.cancel.schema.json', new RegExp(`^job\\.${U}\\.cancel\\.v1$`)],
  'packets.v1.BacktestResult': ['result.packet.schema.json', ev('result')],
  'packets.v1.LiveResult': ['result.packet.schema.json', ev('result')],
  'packets.v1.AlgorithmStatus': ['result.packet.schema.json', ev('result')],
  'packets.v1.PacketRef': ['result.packet.schema.json', ev('result')],
  'packets.v1.Log': ['result.packet.schema.json', ev('log')],
  'packets.v1.Debug': ['result.packet.schema.json', ev('log')],
  'packets.v1.SystemDebug': ['result.packet.schema.json', ev('log')],
  'packets.v1.OrderEvent': ['result.packet.schema.json', ev('order')],
  'packets.v1.RuntimeError': ['result.packet.schema.json', ev('error')],
  'packets.v1.HandledError': ['result.packet.schema.json', ev('error')],
};
const envelopeTypes = readJson(join(dir, 'envelope.schema.json')).properties.type.enum;
for (const t of envelopeTypes) if (!catalogue[t]) fail(`envelope type ${t} missing from catalogue`);

function checkMessage(name, m) {
  const env = ajv.getSchema('envelope.schema.json');
  if (!env(m.envelope)) return fail(`${name}: envelope`, env.errors);
  const [schema, rk] = catalogue[m.envelope.type] ?? [];
  if (!schema) return fail(`${name}: unknown type ${m.envelope.type}`);
  if (`${m.schema}.schema.json` !== schema) return fail(`${name}: type ${m.envelope.type} needs ${schema}`);
  if (!rk.test(m.routingKey)) return fail(`${name}: routing key ${m.routingKey} does not match ${rk}`);
  if (m.envelope.correlationId !== m.envelope.headers['job-id']) return fail(`${name}: correlationId != job-id header`);
  const size = Buffer.byteLength(JSON.stringify(m.body), 'utf8');
  if (size > MAX_INLINE_BYTES) return fail(`${name}: body ${size} B > inline limit ${MAX_INLINE_BYTES} B`);
  const v = ajv.getSchema(schema);
  if (!v(m.body)) return fail(`${name}: body`, v.errors);
  ok(`${name} (${m.envelope.type} -> ${schema}, ${m.routingKey}, ${size} B)`);
}

console.log('examples:');
const exDir = join(dir, 'examples');
const examples = Object.fromEntries(readdirSync(exDir).filter((f) => f.endsWith('.json')).sort()
  .map((f) => [f, readJson(join(exDir, f))]));
for (const [f, m] of Object.entries(examples)) checkMessage(f, m);

// LEAN packets serialized by the engine's own types (packets.json is produced by
// `JsonConvert.SerializeObject(packet)` against the Release build, see README "Verification").
const engineDir = join(dir, 'examples', 'engine');
let enginePackets = [];
try { enginePackets = readdirSync(engineDir).filter((f) => f.endsWith('.json')).sort(); } catch { /* optional */ }
if (enginePackets.length) console.log('engine-serialized packets:');
for (const f of enginePackets) {
  const body = readJson(join(engineDir, f));
  const v = ajv.getSchema('result.packet.schema.json');
  const size = Buffer.byteLength(JSON.stringify(body), 'utf8');
  const inline = !(body.Type === 'BacktestResult' && body.Progress >= 1);
  if (inline && size > MAX_INLINE_BYTES) { fail(`${f}: ${size} B exceeds inline limit`); continue; }
  const valid = v(body);
  if (inline === valid) ok(`${f} (${body.Type}, ${size} B, ${valid ? 'valid inline' : 'rejected inline: final result must be a PacketRef'})`);
  else fail(`${f}`, v.errors);
}

console.log('negative cases (must be rejected):');
const clone = (o) => structuredClone(o);
const negatives = [];
{
  const m = clone(examples['04-result.packet.partial-inline.json']); m.body.Progress = 1;
  negatives.push(['final BacktestResult inline', 'result.packet.schema.json', m.body]);
}
{
  const m = clone(examples['07-job.completed.json']); delete m.body.result; m.body.results = { Charts: {} };
  negatives.push(['job.completed carrying the result inline', 'job.completed.schema.json', m.body]);
}
{
  const m = clone(examples['06-result.packet.final-ref.json']); delete m.body.object.sha256;
  negatives.push(['ObjectRef without sha256', 'result.packet.schema.json', m.body]);
}
{
  const m = clone(examples['06-result.packet.final-ref.json']); m.body.format = 'lean-packet';
  negatives.push(['final PacketRef not pointing at the result file', 'result.packet.schema.json', m.body]);
}
{
  const m = clone(examples['01-job.submitted.backtest.json']); m.envelope.headers['contract-version'] = '2.0';
  negatives.push(['v2 message on a v1 contract', 'envelope.schema.json', m.envelope]);
}
for (const [name, schema, data] of negatives) {
  const v = ajv.getSchema(schema);
  v(data) ? fail(`${name} was accepted`) : ok(name);
}
{
  const m = clone(examples['03-job.progress.json']);
  m.body.runtimeStatistics = { Pad: 'x'.repeat(MAX_INLINE_BYTES) };
  const size = Buffer.byteLength(JSON.stringify(m.body), 'utf8');
  size > MAX_INLINE_BYTES ? ok(`progress body of ${size} B exceeds inline limit`) : fail('size check');
}

console.log('topology/definitions.json:');
const defs = readJson(join(root, 'topology', 'definitions.json'));
const dispatch = ['jobs.backtest', 'jobs.live', 'jobs.report', 'jobs.optimization', 'marketdata.fetch'];
const exchanges = new Map(defs.exchanges.map((e) => [e.name, e]));
const want = { 'lean.jobs': ['direct', true], 'lean.jobs.dlx': ['direct', true], 'lean.events': ['topic', true],
  'lean.control': ['topic', true], 'lean.marketdata.live': ['topic', false] };
for (const [n, [type, durable]] of Object.entries(want)) {
  const e = exchanges.get(n);
  e && e.type === type && e.durable === durable ? ok(`exchange ${n} ${type} durable=${durable}`) : fail(`exchange ${n}`);
}
const queues = new Map(defs.queues.map((q) => [q.name, q]));
for (const b of defs.bindings) if (!/\.v1$/.test(b.routing_key)) fail(`binding ${b.source}->${b.destination} key ${b.routing_key} lacks .v1`);
for (const q of dispatch) {
  const a = queues.get(q)?.arguments ?? {};
  const bound = defs.bindings.some((b) => b.source === 'lean.jobs' && b.destination === q && b.routing_key === `${q}.v1`);
  const dlq = defs.bindings.some((b) => b.source === 'lean.jobs.dlx' && b.destination === `${q}.dead`
    && b.routing_key === a['x-dead-letter-routing-key']);
  a['x-queue-type'] === 'quorum' && a['x-dead-letter-exchange'] === 'lean.jobs.dlx' && bound && dlq
    ? ok(`${q}: quorum, lean.jobs/${q}.v1, dead-letters to ${q}.dead, delivery-limit ${a['x-delivery-limit']}`)
    : fail(`${q} topology`);
}
for (const [q, keys] of Object.entries({
  'results.packets': ['result', 'order', 'log', 'error', 'status'],
  'orchestrator.status': ['status', 'heartbeat'],
})) {
  const missing = keys.filter((k) => !defs.bindings.some((b) => b.source === 'lean.events' && b.destination === q && b.routing_key === `job.*.*.${k}.v1`));
  missing.length ? fail(`${q} missing ${missing}`) : ok(`${q} bound to ${keys.map((k) => `job.*.*.${k}.v1`).join(', ')}`);
}

console.log(failures ? `\n${failures} failure(s)` : '\nall message contracts valid');
process.exit(failures ? 1 : 0);
