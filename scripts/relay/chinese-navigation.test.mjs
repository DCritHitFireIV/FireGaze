import assert from 'node:assert/strict';
import test from 'node:test';
import { validateNavigationSubmission, navigationSubmissionKey, NavigationSubmissionCoordinator } from './chinese-navigation.mjs';

const draft = (change = {}) => ({ schema: 1, type: 'chinese-navigation', action: 'propose',
  identity: { internalName: 'ExamplePlugin', sourceUrl: 'https://repo.example/index.json',
    projectUrl: 'https://github.com/example/plugin', version: '1.2.3', apiLevel: 15, client: 'CN' },
  url: 'https://github.com/example/plugin-cn?version=1#install', note: '中文支持线索', clientVersion: '1.5.0.217', ...change });
const issue = n => ({ number: n, html_url: `https://github.com/mock/repo/issues/${n}` });
const memory = () => {
  const values = new Map();
  return { get: async key => structuredClone(values.get(key)), put: async (key, value) => values.set(key, structuredClone(value)) };
};
const validated = input => {
  const result = validateNavigationSubmission(input);
  assert.equal(result.ok, true);
  return result.value;
};

test('normalizes only URL-equivalent spelling; retains case, query, fragment and exact identity', async () => {
  const value = validated(draft({url: 'HTTPS://GITHUB.COM:443/Example/Plugin?ref=CN#Install'}));
  assert.equal(value.url, 'https://github.com/Example/Plugin?ref=CN#Install');
  const key = await navigationSubmissionKey(value);
  assert.match(key, /^[a-f0-9]{64}$/);
  assert.equal(key, await navigationSubmissionKey(validated(draft({url:value.url, note: '备注改变'}))));
  assert.notEqual(key, await navigationSubmissionKey(validated(draft({url:value.url.toLowerCase()}))));
  assert.notEqual(key, await navigationSubmissionKey(validated(draft({url:value.url, identity:{...draft().identity, version:'1.2.4'}}))));
});

test('rejects unsafe URLs and invalid identities without treating CN suffix as proof', () => {
  for (const url of ['http://127.0.0.1/a', 'http://0x7f000001', 'http://[::1]', 'http://[::ffff:192.168.1.2]',
    'http://10.0.0.1', 'http://169.254.1.1', 'http://localhost', 'http://host.local', 'file:///C:/x',
    'https://user:secret@github.com/x', 'https://github.com/x\n', 'https://github.com/' + 'a'.repeat(2100)]) {
    assert.equal(validateNavigationSubmission(draft({url})).ok, false, url);
  }
  assert.equal(validateNavigationSubmission(draft({identity:{...draft().identity, apiLevel:0}})).ok, false);
  assert.equal(validateNavigationSubmission(draft({identity:{...draft().identity, client:'INTL'}})).ok, false);
  assert.equal(validateNavigationSubmission(draft({identity:{...draft().identity, sourceUrl:'C:\\private'}})).ok, false);
  const value = validated(draft({identity:{...draft().identity, internalName:'ExampleCN', sourceUrl:'', projectUrl:''}}));
  assert.equal(value.identity.internalName, 'ExampleCN');
  assert.equal(value.verified, undefined);
});

test('broken reports require entry and revision, and form a separate deduplication identity', async () => {
  assert.equal(validateNavigationSubmission(draft({action:'report-broken'})).ok, false);
  const a = validated(draft({action:'report-broken',entryId:'nav-1',revision:'r2'}));
  const b = validated(draft({action:'report-broken',entryId:'nav-1',revision:'r3'}));
  assert.notEqual(await navigationSubmissionKey(a), await navigationSubmissionKey(b));
  assert.notEqual(await navigationSubmissionKey(a), await navigationSubmissionKey(validated(draft())));
});

test('concurrent duplicate submissions create exactly one Issue and receive the same receipt', async () => {
  let created = 0;
  const github = { create: async () => { created++; await new Promise(r => setTimeout(r, 10)); return issue(41); }, find: async () => [] };
  const coordinator = new NavigationSubmissionCoordinator(memory(), github, 'mock/repo');
  const results = await Promise.all(Array.from({length:6}, () => coordinator.submit(validated(draft()))));
  assert.equal(created, 1);
  assert.ok(results.every(x => x.accepted && x.issueNumber === 41 && x.issueUrl === issue(41).html_url));
  assert.equal(results.filter(x => !x.duplicate).length, 1);
  assert.equal(results.find(x => x.duplicate).notificationState, 'unknown', 'a stored Issue receipt does not prove current notification state');
});

test('a lost create reply is reconciled by marker, never by creating a second Issue', async () => {
  let creates = 0;
  let marker;
  const storage = memory();
  const github = { create: async payload => { creates++; marker=payload.marker; throw new Error('timeout secret-token'); },
    find: async key => [{...issue(42), body: marker}] };
  const first = await new NavigationSubmissionCoordinator(storage, github, 'mock/repo').submit(validated(draft()));
  assert.equal(first.accepted, false);
  assert.equal(first.errorCode, 'outcome-unknown');
  const recovered = await new NavigationSubmissionCoordinator(storage, github, 'mock/repo').submit(validated(draft()));
  assert.equal(recovered.accepted, true);
  assert.equal(recovered.issueNumber, 42);
  assert.equal(creates, 1);
  assert.ok(!JSON.stringify(first).includes('secret-token'));
});

test('empty reconciliation remains unknown and wrong-repository Issue URLs never count as accepted', async () => {
  let creates = 0;
  const github = { create: async () => { creates++; return {...issue(1),html_url:'https://github.com/other/repo/issues/1'}; }, find: async () => [] };
  const coordinator = new NavigationSubmissionCoordinator(memory(), github, 'mock/repo');
  const first = await coordinator.submit(validated(draft()));
  const repeated = await coordinator.submit(validated(draft()));
  assert.equal(first.accepted, false);
  assert.equal(repeated.accepted, false);
  assert.equal(repeated.errorCode, 'outcome-unknown');
  assert.equal(creates, 1);
});

test('receipt storage failure recovers the existing Issue after restart', async () => {
  const storage = memory();
  const put = storage.put;
  let failReceipt = true, marker, creates = 0;
  storage.put = async (key, value) => { if (value.state === 'accepted' && failReceipt) { failReceipt=false; throw new Error('disk'); } return put(key,value); };
  const github = { create: async payload => { creates++; marker=payload.marker; return issue(43); }, find: async () => [{...issue(43),body:marker}] };
  const first = await new NavigationSubmissionCoordinator(storage,github,'mock/repo').submit(validated(draft()));
  assert.equal(first.accepted,false);
  const recovered = await new NavigationSubmissionCoordinator(storage,github,'mock/repo').submit(validated(draft()));
  assert.equal(recovered.accepted,true);
  assert.equal(recovered.issueNumber,43);
  assert.equal(creates,1);
});

test('a creation claim must be durable before any GitHub side effect', async () => {
  let creates=0;
  const storage = { get: async () => undefined, put: async () => { throw Error('unavailable'); } };
  const result = await new NavigationSubmissionCoordinator(storage,{create:async()=>{creates++;return issue(44);}},'mock/repo').submit(validated(draft()));
  assert.equal(result.accepted,false);
  assert.equal(result.errorCode,'storage-unavailable');
  assert.equal(creates,0);
});

test('official source stays exact and distinct from unknown and third-party submission identities', async () => {
  const official = validated(draft({identity:{...draft().identity,sourceUrl:'OFFICIAL'}}));
  assert.equal(official.identity.sourceUrl, 'OFFICIAL');
  assert.equal(official.verified, undefined);
  const officialKey = await navigationSubmissionKey(official);
  const unknownKey = await navigationSubmissionKey(validated(draft({identity:{...draft().identity,sourceUrl:''}})));
  const thirdPartyKey = await navigationSubmissionKey(validated(draft()));
  assert.equal(new Set([officialKey,unknownKey,thirdPartyKey]).size, 3);
  const report = validated(draft({identity:{...draft().identity,sourceUrl:'OFFICIAL'},
    action:'report-broken',entryId:'nav-official',revision:'r2'}));
  assert.equal(report.identity.sourceUrl, 'OFFICIAL');
  assert.notEqual(await navigationSubmissionKey(report), officialKey);
});

test('official marker is rejected in other URL fields and when source spelling is not exact', () => {
  for (const sourceUrl of ['official','Official',' OFFICIAL','OFFICIAL ','OFFICIAL\n','OFFICIAL\t','LOCAL']) {
    assert.equal(validateNavigationSubmission(draft({identity:{...draft().identity,sourceUrl}})).ok, false, sourceUrl);
  }
  assert.equal(validateNavigationSubmission(draft({identity:{...draft().identity,projectUrl:'OFFICIAL'}})).ok, false);
  assert.equal(validateNavigationSubmission(draft({url:'OFFICIAL'})).ok, false);
  assert.equal(validateNavigationSubmission(draft({identity:{...draft().identity,sourceUrl:'OFFICIAL'},
    action:'report-broken'})).ok, false, 'official source does not remove the report identity requirements');
});

