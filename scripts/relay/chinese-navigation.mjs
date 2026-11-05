// Pure navigation rules and durable coordination. No submitted URL is fetched.
const identifier = /^[A-Za-z0-9_.-]{1,128}$/;
const controls = /[\u0000-\u001f\u007f]/;
const text = (value, maximum, required = true) => typeof value === 'string'
  && value.length <= maximum && (!required || value.trim().length > 0) && !controls.test(value);

export function normalizePublicUrl(value) {
  if (!text(value, 2048) || value !== value.trim()) throw new Error('invalid-url');
  const parsed = new URL(value);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password) throw new Error('invalid-url');
  const host = parsed.hostname.toLowerCase().replace(/\.$/, '');
  if (!host.includes('.') && !host.startsWith('[')) throw new Error('nonpublic-url');
  if (host === 'localhost' || /\.(localhost|local|lan|internal|home)$/.test(host)) throw new Error('nonpublic-url');
  if (host.startsWith('[')) {
    const address = host.slice(1,-1);
    // Reject local, unspecified, mapped IPv4 and multicast literals. Do not resolve DNS or visit the URL.
    if (address.startsWith('::') || /^(f[cd]|fe[89ab]|ff)/.test(address)) throw new Error('nonpublic-url');
  } else if (/^\d+\.\d+\.\d+\.\d+$/.test(host)) {
    const [a,b] = host.split('.').map(Number);
    if (a === 0 || a === 10 || a === 127 || a >= 224 || (a === 169 && b === 254)
      || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168)
      || (a === 100 && b >= 64 && b <= 127) || (a === 198 && (b === 18 || b === 19))) throw new Error('nonpublic-url');
  }
  return parsed.href;
}

export function navigationFailure(errorCode, stage, reason, nextAction, submissionId = '') {
  return { accepted: false, duplicate: false, submissionId, notificationState: 'needs-attention',
    errorCode, stage, reason, nextAction, diagnosticId: submissionId.slice(0,16) };
}

export function validateNavigationSubmission(input) {
  try {
    if (!input || input.schema !== 1 || input.type !== 'chinese-navigation'
      || !['propose','report-broken'].includes(input.action)) throw Error('invalid-schema');
    const identity = input.identity;
    if (!identity || !identifier.test(identity.internalName ?? '') || !text(identity.version,64)
      || !Number.isSafeInteger(identity.apiLevel) || identity.apiLevel < 1 || identity.apiLevel > 1000
      || identity.client !== 'CN' || !text(input.clientVersion,64)) throw Error('invalid-identity');
    const sourceUrl = identity.sourceUrl === '' ? '' : normalizePublicUrl(identity.sourceUrl);
    const projectUrl = identity.projectUrl === '' ? '' : normalizePublicUrl(identity.projectUrl);
    const note = input.note ?? '';
    if (typeof note !== 'string' || note.length > 2000 || /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(note)) throw Error('invalid-note');
    if (input.action === 'report-broken' && (!identifier.test(input.entryId ?? '') || !text(input.revision,128))) throw Error('invalid-report');
    return { ok:true, value:{ schema:1, type:'chinese-navigation', action:input.action,
      identity:{internalName:identity.internalName,sourceUrl,projectUrl,version:identity.version,apiLevel:identity.apiLevel,client:'CN'},
      url:normalizePublicUrl(input.url),note,clientVersion:input.clientVersion,
      ...(input.action === 'report-broken' ? {entryId:input.entryId,revision:input.revision} : {}) } };
  } catch {
    return { ok:false, ...navigationFailure('invalid-submission','validation',
      '投稿格式、插件身份或公共网址不符合要求，尚未创建 Issue。','请检查插件版本、HTTP(S)网址和字段长度；不要填写私密地址或访问密钥。') };
  }
}

export async function navigationSubmissionKey(value) {
  const i = value.identity;
  const stable = JSON.stringify([1,value.action,i.internalName,i.sourceUrl,i.projectUrl,i.version,i.apiLevel,i.client,
    value.url,value.entryId ?? '',value.revision ?? '']);
  const bytes = await crypto.subtle.digest('SHA-256',new TextEncoder().encode(stable));
  return [...new Uint8Array(bytes)].map(x => x.toString(16).padStart(2,'0')).join('');
}
export class NavigationSubmissionCoordinator {
  constructor(storage, github, repo) {
    this.storage = storage; this.github = github; this.repo = repo;
    this.tail = Promise.resolve();
  }

  submit(value) {
    // A Durable Object owns one key. Serialize network awaits, not merely storage reads.
    const work = this.tail.then(() => this.submitOwned(value));
    this.tail = work.catch(() => {});
    return work;
  }

  async submitOwned(input) {
    const validated = validateNavigationSubmission(input);
    if (!validated.ok) return validated;
    const value = validated.value;
    const key = await navigationSubmissionKey(value);
    const marker = `<!-- firegaze:chinese-navigation:v1:${key} -->`;
    let stored;
    try { stored = await this.storage.get('submission'); }
    catch { return this.storageFailure(key); }
    if (stored && (stored.key !== key || stored.repo !== this.repo))
      return navigationFailure('identity-mismatch','deduplication','受理记录与当前投稿或仓库身份不一致，未新建 Issue。','请联系维护者核对持久受理配置。',key);
    if (stored?.state === 'accepted' && validIssue(stored.issue,this.repo)) return receipt(key,stored.issue,true);
    if (stored) {
      try {
        const matches = (await this.github.find(key)).filter(item => validIssue(item,this.repo)
          && typeof item.body === 'string' && item.body.split(/\r?\n/).includes(marker));
        if (matches.length === 1) return await this.accept(key,matches[0],true);
      } catch { /* Unknown is durable; absence or search failure never licenses another POST. */ }
      return unknown(key,'reconciliation');
    }
    try { await this.storage.put('submission',{key,repo:this.repo,state:'creating',createdAt:Date.now()}); }
    catch { return this.storageFailure(key); }
    try {
      const created = await this.github.create({value,key,marker});
      if (!validIssue(created,this.repo)) throw Error('invalid-receipt');
      return await this.accept(key,created,false);
    } catch (error) {
      // Keep a durable uncertainty marker, including after a receipt-save failure.
      try { await this.storage.put('submission',{key,repo:this.repo,state:'outcome-unknown',updatedAt:Date.now()}); } catch {}
      const result = unknown(key,'issue-create');
      if (error?.relay && Number.isInteger(error.status) && error.status >= 400 && error.status <= 599) {
        result.githubStatus = error.status;
        result.reason = `GitHub 返回 HTTP ${error.status}，未取得有效 Issue 受理回执；当前未加入导航索引。`;
      }
      return result;
    }
  }

  async accept(key,issue,duplicate) {
    const saved = {number:issue.number,html_url:issue.html_url};
    await this.storage.put('submission',{key,repo:this.repo,state:'accepted',issue:saved,acceptedAt:Date.now()});
    return receipt(key,saved,duplicate);
  }

  storageFailure(key) {
    return navigationFailure('storage-unavailable','deduplication','无法保存或读取持久受理记录，本次没有发起新建 Issue。','请稍后使用同一投稿重试。',key);
  }
}

export function validIssue(issue,repo) {
  return Number.isSafeInteger(issue?.number) && issue.number > 0 && !issue.pull_request
    && issue.html_url === `https://github.com/${repo}/issues/${issue.number}`;
}

function receipt(key,issue,duplicate) {
  return {accepted:true,duplicate,submissionId:key,issueUrl:issue.html_url,issueNumber:issue.number,
    notificationState:duplicate?'unknown':'pending',diagnosticId:key.slice(0,16)};
}

function unknown(key,stage) {
  return navigationFailure('outcome-unknown',stage,'请求结果尚未确认，尚未加入导航索引；已保留原投稿，不会再新建一条。',
    '可使用同一投稿查询原请求；仍未确认时请将诊断编号交给维护者核对。',key);
}
