// Only persisted transactions get an ingestion reply. Invalid user input has
// its own needs-input response and must never be described as an accepted pack.
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');

const marker = '<!-- firegaze-inbox-needs-input -->';

module.exports = async ({ github, context, core }) => {
  const root = path.join(process.env.RUNNER_TEMP, 'inbox-persisted');
  const read = directory => {
    const target = path.join(root, directory);
    if (!fs.existsSync(target)) return [];
    return fs.readdirSync(target).filter(name => name.endsWith('.json')).sort()
      .map(name => JSON.parse(fs.readFileSync(path.join(target, name), 'utf8')));
  };
  const invalid = read('invalid-issues');
  const persisted = read('issues');
  const resolved = read('resolved-issues');
  const commentsFor = issue_number => github.paginate(github.rest.issues.listComments, {
      ...context.repo, issue_number, per_page: 100,
  });
  const botComment = (comments, tag) => comments.find(comment =>
    comment.user?.login === 'github-actions[bot]' && comment.body?.includes(tag));
  const resolvePending = async comments => {
    const previous = botComment(comments, marker);
    if (!previous) return;
    const body = `已处理更新后的投稿。正文已通过格式校验，已有内容不会重复入库。处理结果请查看本 issue 回复或工作流回执。\n\n${marker}`;
    if (previous.body !== body) {
      await github.rest.issues.updateComment({ ...context.repo, comment_id: previous.id, body });
    }
  };
  let failed = false;
  for (const item of invalid) {
    try {
      if (item.status !== 'needs-input' || !Number.isSafeInteger(item.issue_number)
          || item.issue_number < 1 || !/^[a-f0-9]{64}$/i.test(item.bodySha256)
          || typeof item.comment !== 'string' || typeof item.reason !== 'string') {
        throw new Error('Invalid needs-input outbox record');
      }
      core.warning(`Issue #${item.issue_number} needs input: ${item.reason}`);
      const body = `${item.comment}\n\n${marker}\n<!-- body-sha256: ${item.bodySha256} -->`;
      const previous = botComment(await commentsFor(item.issue_number), marker);
      if (!previous) {
        await github.rest.issues.createComment({ ...context.repo, issue_number: item.issue_number, body });
      } else if (previous.body !== body) {
        await github.rest.issues.updateComment({ ...context.repo, comment_id: previous.id, body });
      }
    } catch (error) {
      failed = true;
      core.error(`Could not report input needed for issue #${item.issue_number}: ${error.message}`);
    }
  }
  if (invalid.length && process.env.GITHUB_STEP_SUMMARY) {
    fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,
      '### 等待投稿者补充（未入库）\n\n'
      + invalid.map(item => `- #${item.issue_number}: ${item.reason}`).join('\n') + '\n');
  }
  for (const item of persisted) {
    try {
      const comments = await commentsFor(item.issue_number);
      const fingerprint = createHash('sha256').update((item.bodySha256 || '') + '\n' + item.comment).digest('hex');
      const resultMarker = `<!-- firegaze-inbox-result: ${fingerprint} -->`;
      if (!botComment(comments, resultMarker)) {
        await github.rest.issues.createComment({ ...context.repo, issue_number: item.issue_number,
          body: item.comment + '\n\n' + resultMarker });
      }
      await resolvePending(comments);
    } catch (error) {
      failed = true;
      core.error(`Could not reply to persisted issue #${item.issue_number}: ${error.message}`);
    }
  }
  const replied = new Set(persisted.map(item => item.issue_number));
  for (const item of resolved.filter(item => !replied.has(item.issue_number))) {
    try {
      if (item.status !== 'processed' || !Number.isSafeInteger(item.issue_number)
          || item.issue_number < 1 || !/^[a-f0-9]{64}$/i.test(item.bodySha256)) {
        throw new Error('Invalid resolved-issue outbox record');
      }
      await resolvePending(await commentsFor(item.issue_number));
    } catch (error) {
      failed = true;
      core.error(`Could not resolve input notice for issue #${item.issue_number}: ${error.message}`);
    }
  }
  if (failed) core.setFailed('Some contribution results could not receive their reply');
};
