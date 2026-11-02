const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const workspace = path.resolve(__dirname, '..');
const marker = '<!-- firegaze-inbox-needs-input -->';
const bot = { login: 'github-actions[bot]' };

function fixture(t, comments = []) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'firegaze-inbox-replies-'));
  const old = { ...process.env };
  process.env.RUNNER_TEMP = root;
  process.env.GITHUB_WORKSPACE = workspace;
  process.env.GITHUB_STEP_SUMMARY = path.join(root, 'summary.md');
  t.after(() => {
    for (const key of ['RUNNER_TEMP', 'GITHUB_WORKSPACE', 'GITHUB_STEP_SUMMARY']) {
      if (old[key] === undefined) delete process.env[key];
      else process.env[key] = old[key];
    }
    fs.rmSync(root, { recursive: true, force: true });
  });
  const writes = [];
  const errors = [];
  const github = {
    rest: { issues: {
      listComments: async ({ issue_number }) => ({ data: comments.filter(c => c.issue_number === issue_number) }),
      createComment: async (args) => {
        writes.push({ kind: 'create', ...args });
        const comment = { id: comments.length + 1, user: bot, ...args };
        comments.push(comment);
        return { data: comment };
      },
      updateComment: async (args) => {
        writes.push({ kind: 'update', ...args });
        Object.assign(comments.find(c => c.id === args.comment_id), { body: args.body });
        return { data: {} };
      },
    } },
    paginate: async (method, args) => (await method(args)).data,
  };
  const core = { warning() {}, error: message => errors.push(message), setFailed: message => errors.push(message) };
  const write = (directory, name, value) => {
    const target = path.join(root, 'inbox-persisted', directory, name + '.json');
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, JSON.stringify(value));
  };
  const invoke = async () => {
    const workflow = fs.readFileSync(path.join(workspace, '.github/workflows/inbox.yml'), 'utf8');
    const source = workflow.match(/          script: \|\n([\s\S]*?)(?=\n      - |\n      #|$)/)[1]
      .split('\n').map(line => line.replace(/^            /, '')).join('\n');
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    await new AsyncFunction('github', 'context', 'core', 'require', source)(
      github, { repo: { owner: 'fixture', repo: 'repository' } }, core, require);
  };
  const invalid = (issue_number = 230, bodySha256 = 'a'.repeat(64)) => write('invalid-issues', issue_number, {
    issue_number, bodySha256, status: 'needs-input', reason: '没有可解析的 JSON 投稿内容',
    comment: '尚未入库。请将导出文件中的 JSON 内容粘贴到本 issue 正文后保存。',
  });
  return { root, writes, comments, errors, github, write, invalid, invoke };
}

test('missing JSON produces an actionable pending reply and summary, never an accepted reply', async t => {
  const f = fixture(t);
  f.invalid();
  await f.invoke();
  assert.equal(f.writes.length, 1);
  assert.match(f.writes[0].body, /尚未入库/);
  assert.match(f.writes[0].body, /<!-- firegaze-inbox-needs-input -->/);
  assert.match(fs.readFileSync(path.join(f.root, 'summary.md'), 'utf8'), /#230/);
  assert.equal(f.errors.length, 0);
});

test('recovery of the same invalid issue does not post duplicate replies', async t => {
  const f = fixture(t);
  f.invalid();
  await f.invoke();
  await f.invoke();
  assert.equal(f.writes.length, 1);
});

test('an edited but still invalid issue updates the existing bot reply', async t => {
  const f = fixture(t);
  f.invalid();
  await f.invoke();
  f.invalid(230, 'b'.repeat(64));
  await f.invoke();
  assert.deepEqual(f.writes.map(w => w.kind), ['create', 'update']);
  assert.equal(f.comments.length, 1);
});

test('a user cannot impersonate the bot validation reply with its marker', async t => {
  const userBody = marker + '\nUser explanation that must stay unchanged';
  const f = fixture(t, [{ id: 8, user: { login: 'contributor' }, issue_number: 230, body: userBody }]);
  f.invalid();
  await f.invoke();
  assert.equal(f.comments[0].body, userBody);
  assert.equal(f.writes.length, 1);
  assert.equal(f.writes[0].kind, 'create');
});

test('a repaired and persisted contribution receives its real result and resolves stale validation', async t => {
  const f = fixture(t, [{ id: 8, user: bot, issue_number: 230, body: marker + '\n尚未入库' }]);
  f.write('issues', 230, { issue_number: 230, comment: '已并入 1 条，拒绝 0 条。' });
  await f.invoke();
  assert.equal(f.writes.filter(w => w.kind === 'create').length, 1);
  assert.equal(f.writes.find(w => w.kind === 'create').body.split('\n\n<!--')[0], '已并入 1 条，拒绝 0 条。');
  assert.match(f.comments.find(c => c.id === 8).body, /已处理更新后的投稿/);
});

test('reply API failures remain visible while independent persisted replies continue', async t => {
  const f = fixture(t);
  f.invalid();
  f.write('issues', 231, { issue_number: 231, comment: '已并入 1 条。' });
  const create = f.github.rest.issues.createComment;
  f.github.rest.issues.createComment = async args => {
    if (args.issue_number === 230) throw new Error('permission denied');
    return create(args);
  };
  await f.invoke();
  assert.equal(f.writes.length, 1);
  assert.equal(f.writes[0].issue_number, 231);
  assert.ok(f.errors.some(message => /230/.test(message)));
});

test('no outbox produces no reply, network request or fabricated success', async t => {
  const f = fixture(t);
  f.github.paginate = () => { throw new Error('unexpected request'); };
  await f.invoke();
  assert.equal(f.writes.length, 0);
  assert.equal(f.errors.length, 0);
});

test('replaying a durable result does not duplicate its successful ingestion reply', async t => {
  const f = fixture(t);
  f.write('issues', 230, { issue_number: 230, bodySha256: 'c'.repeat(64), comment: '已并入 1 条。' });
  await f.invoke();
  await f.invoke();
  assert.equal(f.writes.filter(w => w.kind === 'create').length, 1);
});

test('a corrected duplicate resolves the pending notice without announcing another accepted pack', async t => {
  const f = fixture(t, [{ id: 8, user: bot, issue_number: 230, body: marker + '\n尚未入库' }]);
  f.write('resolved-issues', 230, { issue_number: 230, bodySha256: 'c'.repeat(64), status: 'processed' });
  await f.invoke();
  assert.equal(f.writes.filter(w => w.kind === 'create').length, 0);
  assert.equal(f.writes.filter(w => w.kind === 'update').length, 1);
  assert.match(f.comments[0].body, /已处理更新后的投稿/);
});

test('reconciling an existing pack does not create a new reply when no pending notice exists', async t => {
  const f = fixture(t);
  f.write('resolved-issues', 230, { issue_number: 230, bodySha256: 'c'.repeat(64), status: 'processed' });
  await f.invoke();
  assert.equal(f.writes.length, 0);
});
