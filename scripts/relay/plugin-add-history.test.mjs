import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';

// The fixture is the reviewed historical evidence, independent of the embedded
// production table. Only Cloudflare KV/Cache are replaced; every request runs
// the complete production Worker and its real routing, aggregation and reset.
const history = JSON.parse(readFileSync(process.env.FIREGAZE_HISTORY_FIXTURE
  || new URL('../../discovery/recovery/2026-10-09-counts.json', import.meta.url), 'utf8'));
const source = readFileSync(new URL('./worker.js', import.meta.url), 'utf8');
const marker = `plugin-add-history-reset:${history.id}`;
const admin = 'offline-test-admin';
const sum = values => Object.values(values).reduce((a, b) => a + b, 0);

function memoryKV() {
  const values = new Map();
  const metadata = new Map();
  const failures = {};
  const api = {
    values, failures,
    seed(key, value, withMetadata = true) {
      values.set(key, String(value));
      if (withMetadata) metadata.set(key, { v: Number(value) });
      return api;
    },
    async get(key) {
      if (failures.get === key) throw Error('injected storage read failure');
      return values.get(key) ?? null;
    },
    async put(key, value, options = {}) {
      if (failures.put === key) throw Error('injected storage write failure');
      values.set(key, value);
      metadata.set(key, options.metadata ?? null);
    },
    async delete(key) {
      if (failures.delete === key) throw Error('injected storage delete failure');
      values.delete(key);
      metadata.delete(key);
    },
    async list({ prefix = '', cursor } = {}) {
      if (failures.list) throw Error('injected storage list failure');
      const names = [...values.keys()].filter(key => key.startsWith(prefix)).sort();
      const start = Number(cursor || 0);
      // Deliberately small pages catch incomplete enumeration and deletion races.
      const page = names.slice(start, start + 2);
      const done = start + page.length >= names.length;
      return { keys: page.map(name => ({ name, metadata: metadata.get(name) ?? null })),
        list_complete: done, cursor: done ? '' : String(start + page.length) };
    },
  };
  return api;
}

function memoryCache() {
  const values = new Map();
  return {
    values,
    async match(key) { return values.has(key) ? new Response(values.get(key)) : undefined; },
    async put(key, value) { values.set(key, await value.text()); },
    async delete(key) { return values.delete(key); },
  };
}

function setup(kv = memoryKV(), cache = memoryCache()) {
  const clock = { now: Date.parse('2026-11-12T04:00:00Z') };
  function isolate() {
    class ClockDate extends Date {
      constructor(...args) { super(...(args.length ? args : [clock.now])); }
      static now() { return clock.now; }
    }
    const context = vm.createContext({ Request, Response, Headers, URL, TextEncoder,
      TextDecoder, AbortController, Date: ClockDate, caches: { default: cache },
      fetch: async () => { throw Error('Unexpected network request'); },
      console: { log() {}, warn() {}, error() {} }, setTimeout, clearTimeout });
    vm.runInContext(source.replace(/^export default \{/m, 'globalThis.relay = {')
      .replace(/^export \{[^}]+\};\r?$/gm, ''), context);
    return async (path = '/plugin-stats', body, key = admin, envExtra = {}) => {
      const response = await context.relay.fetch(new Request('https://relay.invalid' + path, {
        method: body === undefined ? 'GET' : 'POST',
        headers: { 'CF-Connecting-IP': '192.0.2.20', 'x-admin-key': key,
          'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      }), { LIBRARY_COUNTS: kv, ADMIN_KEY: admin, ...envExtra });
      return { status: response.status, body: await response.json() };
    };
  }
  return { kv, cache, clock, isolate, request: isolate() };
}

test('adds every recovered plugin once, preserving live-only and overlapping counts', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4).seed('padd:v2:LiveOnly', 3, false)
    .seed('padd:Obsolete', 900));
  const { status, body } = await s.request();
  assert.equal(status, 200);
  assert.equal(Object.keys(history.counts).length, 239);
  assert.equal(sum(history.counts), 578);
  assert.deepEqual(body.adds, { ...history.counts, ADS: 22, LiveOnly: 3 });
  assert.equal(sum(body.adds), 585);
  assert.deepEqual(body.history, { id: history.id, countMapSha256: history.countMapSha256,
    applied: true, count: 239, sum: 578, currentAddsCount: 2, currentAddsSum: 7 });
});

test('empty live data still yields the historical values without writing to KV', async () => {
  const s = setup();
  const result = await s.request();
  assert.deepEqual(result.body.adds, history.counts);
  assert.equal(s.kv.values.size, 0);
});

test('repeated reads and shared-cache reads from fresh isolates never add the baseline twice', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4));
  for (const request of [s.request, s.request, s.isolate(), s.isolate()]) {
    const result = await request();
    assert.equal(result.body.adds.ADS, 22);
    assert.equal(sum(result.body.adds), 582);
  }
  assert.equal(s.kv.values.get('padd:v2:ADS'), '4');
});

test('legacy shared aggregation cannot hide or double the recovered baseline', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4));
  await s.cache.put('https://firegaze-relay.invalid/agg/plugin?week=2026-W46&v=2',
    Response.json({ builtAt: s.clock.now, body: { ok: true, week: '2026-W46', adds: { ADS: 777 } } }));
  const result = await s.request();
  assert.equal(result.body.adds.ADS, 22);
  assert.equal(result.body.history.sum, 578);
});

test('a real recommendation POST increments live state and remains visible after cache expiry', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4));
  assert.equal((await s.request()).body.adds.ADS, 22);
  assert.equal((await s.request('/plugin-add', { plugins: ['ADS', 'NewPlugin'] })).status, 200);
  s.clock.now += 7 * 3600_000;
  const result = await s.request();
  assert.equal(result.body.adds.ADS, 23);
  assert.equal(result.body.adds.NewPlugin, 1);
  assert.equal(result.body.history.currentAddsSum, 6);
  assert.equal(result.body.history.sum, 578);
});

test('failed authentication and disabled administration cannot change counts or history state', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4));
  assert.equal((await s.request('/plugin-add/reset', {}, 'wrong-key')).status, 403);
  assert.equal((await s.request('/plugin-add/reset', {}, admin, { ADMIN_KEY: undefined })).status, 503);
  assert.equal((await s.request()).body.adds.ADS, 22);
  assert.equal(s.kv.values.has(marker), false);
});

test('authorized reset clears paginated live counts and durably suppresses this history', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4).seed('padd:v2:Other', 1)
    .seed('padd:Old', 9).seed('padd:v2:Third', 2).seed('plike_total:ADS', 8));
  await s.request();
  const reset = await s.request('/plugin-add/reset', {});
  assert.equal(reset.status, 200);
  assert.equal(reset.body.deleted, 4);
  assert.equal(reset.body.historySuppressed, true);
  for (const request of [s.request, s.isolate()]) {
    const result = await request();
    assert.deepEqual(result.body.adds, {});
    assert.equal(result.body.history.applied, false);
    assert.equal(result.body.history.sum, 0);
    assert.equal(result.body.total.ADS, 8);
  }
  assert.equal((await s.request('/plugin-add/reset', {})).body.deleted, 0);
  assert.equal((await s.request('/plugin-add', { plugins: ['ADS'] })).status, 200);
  s.clock.now += 7 * 3600_000;
  assert.deepEqual((await s.isolate()()).body.adds, { ADS: 1 });
});

test('history suppression write failure happens before destructive deletion and reports failure', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4));
  await s.request();
  s.kv.failures.put = marker;
  const reset = await s.request('/plugin-add/reset', {});
  assert.equal(reset.status, 500);
  assert.equal(reset.body.ok, false);
  assert.equal(reset.body.deleted, 0);
  assert.equal(reset.body.historySuppressed, false);
  assert.equal(s.kv.values.get('padd:v2:ADS'), '4');
  assert.equal((await s.isolate()()).body.adds.ADS, 22);
});

test('partial delete failure reports the observed state and does not resurrect recovered history', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4).seed('padd:v2:Other', 1));
  await s.request();
  s.kv.failures.delete = 'padd:v2:Other';
  const reset = await s.request('/plugin-add/reset', {});
  assert.equal(reset.status, 500);
  assert.equal(reset.body.historySuppressed, true);
  assert.equal(reset.body.deleted, 1);
  const result = await s.request();
  assert.deepEqual(result.body.adds, { Other: 1 });
  assert.equal(result.body.history.applied, false);
});

test('failure to read suppression state cannot silently revive historical recommendations', async () => {
  const s = setup();
  s.kv.failures.get = marker;
  const result = await s.request();
  assert.equal(result.status, 503);
  assert.equal(result.body.ok, false);
  assert.equal('adds' in result.body, false);
  assert.equal(s.cache.values.size, 0);
});

test('failed KV enumeration does not write a reset marker or delete any live value', async () => {
  const s = setup(memoryKV().seed('padd:v2:ADS', 4));
  s.kv.failures.list = true;
  const reset = await s.request('/plugin-add/reset', {});
  assert.equal(reset.status, 500);
  assert.equal(reset.body.deleted, 0);
  assert.equal(reset.body.historySuppressed, false);
  assert.equal(s.kv.values.has(marker), false);
  assert.equal(s.kv.values.get('padd:v2:ADS'), '4');
});

test('plugin likes, weekly likes and library counts keep their original independent values', async () => {
  const s = setup(memoryKV().seed('plike_total:ADS', 8).seed('plike_w:2026-W46:ADS', 2)
    .seed('dl:ADS@library', 7).seed('like:ADS@library', 6));
  const stats = await s.request();
  assert.deepEqual(stats.body.total, { ADS: 8 });
  assert.deepEqual(stats.body.weekly, { ADS: 2 });
  const library = await s.request('/library-counts');
  assert.deepEqual(library.body.counts, { 'ADS@library': 7 });
  assert.deepEqual(library.body.likes, { 'ADS@library': 6 });
  const like = await s.request('/plugin-like', { plugin: 'ADS' });
  assert.equal(like.body.total, 9);
  assert.equal(like.body.weekly, 3);
});
