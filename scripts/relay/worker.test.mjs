import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { generateKeyPairSync, webcrypto } from 'node:crypto';
import vm from 'node:vm';
import test from 'node:test';

// Execute the complete, deployable Dashboard file. Only GitHub HTTP is mocked;
// the handler, Response/Request bodies, clocks, timers and abort signals are real.
const source = readFileSync(new URL('./worker.js', import.meta.url), 'utf8');
const token = 'secret-test-token-never-log';
const payloadText = plugin => JSON.stringify({type: 'uit-contribution', plugin,
  entries: [{Original: 'secret-original-never-log', Translated: 'secret-translation-never-log'}]});
const reply = (body, status = 200, headers = {}) => new Response(JSON.stringify(body), {
  status, headers: {'Content-Type': 'application/json', 'X-GitHub-Request-Id': 'ABCD:1234:FACE', ...headers},
});

function gitApi({externalAdvances = 0, alwaysConflict = false, forbiddenStage = '', permanentRef422 = false, latency = 0} = {}) {
  let head = 'base';
  let sequence = 0;
  let outside = 0;
  const trees = new Map([['tree-base', new Map()]]);
  const blobs = new Map();
  const commits = new Map([['base', {tree: {sha: 'tree-base'}, parents: []}]]);
  const calls = [];
  const fetch = async (url, options = {}) => {
    assert.equal(new URL(url).hostname, 'api.github.com', 'tests must not reach any other service');
    const route = new URL(url).pathname.replace('/repos/mock/repo', '');
    const body = options.body ? JSON.parse(options.body) : null;
    const method = options.method || 'GET';
    calls.push({route, method});
    if (latency) await new Promise(resolve => setTimeout(resolve, latency));
    const stage = route === '/git/blobs' ? 'blob' : route === '/git/refs/heads/main' ? 'ref-update' : '';
    if (stage === forbiddenStage && forbiddenStage) {
      return reply({message: `denied ${token} secret-translation-never-log 192.0.2.33`}, 403);
    }
    if (route === '/git/ref/heads/main') return reply({object: {sha: head}});
    if (route.startsWith('/git/commits/')) return reply(commits.get(route.split('/').at(-1)));
    if (route === '/git/blobs') {
      assert.equal(body.encoding, 'utf-8');
      const sha = 'blob-' + ++sequence;
      blobs.set(sha, body.content);
      return reply({sha}, 201);
    }
    if (route === '/git/trees') {
      const tree = new Map(trees.get(body.base_tree));
      for (const entry of body.tree) tree.set(entry.path, blobs.get(entry.sha));
      const sha = 'tree-' + ++sequence;
      trees.set(sha, tree);
      return reply({sha}, 201);
    }
    if (route === '/git/commits') {
      const sha = 'commit-' + ++sequence;
      commits.set(sha, {...body, tree: {sha: body.tree}});
      return reply({sha}, 201);
    }
    if (route === '/git/refs/heads/main') {
      assert.equal(body.force, false, 'contribution writes must never overwrite a competing main commit');
      if (permanentRef422) return reply({message: 'Repository rule blocked this update'}, 422);
      if (alwaysConflict || outside < externalAdvances) {
        const tree = new Map(trees.get(commits.get(head).tree.sha));
        tree.set('external-' + ++outside, 'independent change ' + outside);
        const sha = 'outside-' + outside;
        trees.set('tree-' + sha, tree);
        commits.set(sha, {tree: {sha: 'tree-' + sha}, parents: [head]});
        head = sha;
      }
      if (commits.get(body.sha).parents[0] !== head) return reply({message: 'Update is not a fast forward'}, 422);
      head = body.sha;
      return reply({object: {sha: head}});
    }
    if (route === '/issues') return reply({html_url: 'https://github.com/mock/repo/issues/1', number: 1}, 201);
    throw Error('unexpected GitHub request: ' + method + ' ' + route);
  };
  return {fetch, calls, files: () => new Map(trees.get(commits.get(head).tree.sha))};
}

function load(fetch) {
  const logs = [];
  const context = vm.createContext({
    fetch, Response, Request, Headers, URL, TextEncoder, TextDecoder, AbortController,
    crypto: webcrypto, atob, btoa, setTimeout, clearTimeout,
    console: Object.fromEntries(['log', 'warn', 'error'].map(level => [level, (...args) => logs.push({level, args})])),
  });
  vm.runInContext(source.replace(/^export default \{/m, 'globalThis.relay = {'), context);
  const submit = (name, ip = '192.0.2.33', env = {}) => context.relay.fetch(new Request('https://relay.invalid/uit-submit', {
    method: 'POST', headers: {'CF-Connecting-IP': ip}, body: payloadText(name),
  }), {REPO: 'mock/repo', GITHUB_TOKEN: token, ...env});
  return {relay: context.relay, submit, logs};
}

function safeDiagnostic(logs, body) {
  const text = JSON.stringify({logs, body});
  for (const secret of [token, 'secret-original-never-log', 'secret-translation-never-log', '192.0.2.33']) {
    assert.ok(!text.includes(secret), 'diagnostics must not expose credentials, text or IP');
  }
  assert.ok(logs.length > 0, 'the real handler must emit a structured outcome');
  for (const entry of logs) assert.doesNotThrow(() => JSON.parse(entry.args[0]));
}

test('six simultaneous submissions all reach main without losing a file', {timeout: 20_000}, async () => {
  const api = gitApi();
  const {submit, logs} = load(api.fetch);
  const responses = await Promise.all(Array.from({length: 6}, (_, i) => submit('Plugin' + i)));
  const bodies = await Promise.all(responses.map(response => response.json()));
  assert.deepEqual(responses.map(response => response.status), [200, 200, 200, 200, 200, 200]);
  assert.ok(bodies.every(body => body.ok && body.file && body.commit));
  assert.equal(api.files().size, 6);
  assert.deepEqual([...api.files().values()].sort(), Array.from({length: 6}, (_, i) => payloadText('Plugin' + i)).sort());
  safeDiagnostic(logs, bodies);
});

test('rebuilds on three independent main advances and preserves the outside changes', {timeout: 20_000}, async () => {
  const api = gitApi({externalAdvances: 3});
  const {submit} = load(api.fetch);
  const response = await submit('One');
  assert.equal(response.status, 200);
  assert.equal((await response.json()).ok, true);
  assert.equal(api.files().size, 4);
  assert.equal(api.files().get('external-3'), 'independent change 3');
});

test('permanent Contents 403 is not retried and reports only safe stage/status/request ID', async () => {
  const api = gitApi({forbiddenStage: 'blob'});
  let canceled = false;
  const {submit, logs} = load(async (url, options) => {
    const response = await api.fetch(url, options);
    if (!response.ok) {
      const cancel = response.body.cancel.bind(response.body);
      response.body.cancel = () => { canceled = true; return cancel(); };
    }
    return response;
  });
  const response = await submit('Private');
  const body = await response.json();
  assert.equal(response.status, 502);
  assert.equal(body.error, 'commit failed');
  assert.equal(body.stage, 'blob');
  assert.equal(body.status, 403);
  assert.equal(body.requestId, 'ABCD:1234:FACE');
  assert.equal(body.attempts, 1);
  assert.equal(canceled, true, 'release the upstream connection without reading its private error body');
  assert.equal(api.calls.filter(call => call.route === '/git/blobs').length, 1);
  safeDiagnostic(logs, body);
});

test('conflict exhaustion returns a bounded, diagnosable failure instead of a forced update', {timeout: 20_000}, async () => {
  const api = gitApi({alwaysConflict: true});
  const {submit, logs} = load(api.fetch);
  const response = await submit('Busy');
  const body = await response.json();
  assert.equal(response.status, 502);
  assert.equal(body.code, 'retry-exhausted');
  assert.equal(body.stage, 'ref-update');
  assert.equal(body.status, 422);
  assert.ok(body.attempts >= 6 && body.attempts <= 10);
  assert.ok(api.calls.length <= 43, 'leave room for two App authentication calls within the 50-subrequest Free limit');
  assert.equal([...api.files().keys()].some(key => key.startsWith('docs/')), false);
  safeDiagnostic(logs, body);
});

test('a ref-update 422 with unchanged main is permanent and is not blindly retried', async () => {
  const api = gitApi({permanentRef422: true});
  const {submit, logs} = load(api.fetch);
  const response = await submit('Blocked');
  const body = await response.json();
  assert.equal(response.status, 502);
  assert.equal(body.stage, 'ref-update');
  assert.equal(body.status, 422);
  assert.equal(body.attempts, 1);
  assert.equal(api.calls.filter(call => call.route === '/git/refs/heads/main').length, 1);
  safeDiagnostic(logs, body);
});

test('a successful ref update whose response is lost is recovered by checking main', async () => {
  const api = gitApi();
  const {submit} = load(async (url, options) => {
    const response = await api.fetch(url, options);
    if (options?.method === 'PATCH') throw new TypeError('connection lost');
    return response;
  });
  const response = await submit('Accepted');
  assert.equal(response.status, 200);
  assert.equal(api.files().size, 1);
  assert.equal(api.calls.filter(call => call.route === '/git/refs/heads/main').length, 1);
});

test('a legitimate seven-package burst works, further anonymous traffic remains bounded with Retry-After', async () => {
  const api = gitApi();
  const {submit, logs} = load(api.fetch);
  for (let i = 0; i < 7; i++) assert.equal((await submit('Burst' + i)).status, 200);
  let limited;
  for (let i = 7; i < 30; i++) {
    const response = await submit('Burst' + i);
    if (response.status === 429) { limited = response; break; }
    assert.equal(response.status, 200);
  }
  assert.ok(limited, 'anonymous submissions still need a finite per-minute limit');
  assert.ok(Number(limited.headers.get('Retry-After')) >= 1);
  assert.ok(Number(limited.headers.get('Retry-After')) <= 60);
  assert.equal((await limited.json()).error, 'too many requests');
  assert.equal((await submit('OtherIP', '192.0.2.44')).status, 200);
  safeDiagnostic(logs, {});
});

function slowOnce(api, milliseconds) {
  let first = true;
  let aborted = false;
  const fetch = async (url, options = {}) => {
    if (first) {
      first = false;
      await new Promise((resolve, reject) => {
        const timer = setTimeout(resolve, milliseconds);
        options.signal?.addEventListener('abort', () => {
          aborted = true;
          clearTimeout(timer);
          reject(new DOMException('aborted', 'AbortError'));
        }, {once: true});
      });
    }
    return api.fetch(url, options);
  };
  return {fetch, wasAborted: () => aborted};
}

test('a stalled GitHub call is aborted and retried before consuming the client timeout', {timeout: 10_000}, async () => {
  const api = gitApi();
  const slow = slowOnce(api, 5000);
  const {submit} = load(slow.fetch);
  const start = Date.now();
  const response = await submit('Slow');
  assert.equal(response.status, 200);
  assert.equal(slow.wasAborted(), true);
  assert.ok(Date.now() - start < 4500, 'one upstream call must not consume the whole 20-second client timeout');
});

test('the upstream timeout includes reading the GitHub response body', {timeout: 10_000}, async () => {
  const api = gitApi();
  let first = true;
  let aborted = false;
  const {submit} = load(async (url, options) => {
    if (first) {
      first = false;
      options.signal?.addEventListener('abort', () => { aborted = true; }, {once: true});
      return {ok: true, headers: new Headers(), json: () => new Promise(() => {})};
    }
    return api.fetch(url, options);
  });
  const start = Date.now();
  const response = await submit('SlowBody');
  assert.equal(response.status, 200);
  assert.equal(aborted, true);
  assert.ok(Date.now() - start < 4500);
});

test('a stalled incoming body is canceled and returned within the total submission deadline', {timeout: 20_000}, async () => {
  const api = gitApi();
  const {relay, logs} = load(api.fetch);
  let canceled = false;
  const request = new Request('https://relay.invalid/uit-submit', {
    method: 'POST', headers: {'CF-Connecting-IP': '192.0.2.33'}, duplex: 'half',
    body: new ReadableStream({cancel() { canceled = true; }}),
  });
  const start = Date.now();
  const response = await relay.fetch(request, {REPO: 'mock/repo', GITHUB_TOKEN: token});
  const body = await response.json();
  assert.equal(response.status, 408);
  assert.equal(body.stage, 'body');
  assert.equal(body.code, 'deadline-exceeded');
  assert.equal(canceled, true);
  assert.equal(api.calls.length, 0);
  assert.ok(Date.now() - start < 17_000);
  safeDiagnostic(logs, body);
});

test('the entire retry chain stops within the client budget even when GitHub keeps advancing main', {timeout: 22_000}, async () => {
  const api = gitApi({alwaysConflict: true, latency: 450});
  const {submit} = load(api.fetch);
  const start = Date.now();
  const response = await submit('Deadline');
  const body = await response.json();
  assert.equal(response.status, 502);
  assert.equal(body.code, 'deadline-exceeded');
  assert.ok(Date.now() - start < 17_000, 'total handling time must leave room within the old 20-second client limit');
});

test('GitHub App token fetch shares the deadline and emits no private-key or token material', {timeout: 10_000}, async () => {
  const {privateKey} = generateKeyPairSync('rsa', {modulusLength: 2048});
  const pem = privateKey.export({type: 'pkcs8', format: 'pem'});
  const api = gitApi();
  const slow = slowOnce({fetch: async (url, options) => {
    if (new URL(url).pathname === '/app/installations/123/access_tokens') return reply({token});
    return api.fetch(url, options);
  }}, 5000);
  const {submit, logs} = load(slow.fetch);
  const response = await submit('AuthSlow', '192.0.2.33', {APP_ID: '123', INSTALLATION_ID: '123', APP_PRIVATE_KEY: pem});
  const body = await response.json();
  assert.equal(response.status, 500);
  assert.equal(body.stage, 'app-token');
  assert.equal(body.code, 'upstream-timeout');
  assert.equal(slow.wasAborted(), true);
  safeDiagnostic(logs, body);
  assert.ok(!JSON.stringify({logs, body}).includes(pem));
});

test('the legacy issue endpoint keeps its existing response contract', async () => {
  const api = gitApi();
  const {relay} = load(api.fetch);
  const response = await relay.fetch(new Request('https://relay.invalid/', {
    method: 'POST', body: JSON.stringify({title: '[译文贡献] Legacy', body: 'contributions ```json\n{}\n```'}),
  }), {REPO: 'mock/repo', GITHUB_TOKEN: token});
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), {ok: true, issue: 'https://github.com/mock/repo/issues/1', number: 1});
});
