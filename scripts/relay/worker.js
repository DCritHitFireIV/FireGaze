// FireGaze 投稿中继（Cloudflare Worker）
//
// 客户端（插件）POST { title, body } → 本 Worker 用服务端令牌在仓库里建 issue
// → 现有 inbox.yml 工作流负责：存档 + 回评 + 用 secret 通知维护者手机。
//
// 密钥（GitHub token）只存在 Worker 的环境变量里；插件客户端不持有任何推送凭据。
//
// 环境变量（Workers → 设置 → 变量和机密）：
//   GITHUB_TOKEN（机密，必填）：fine-grained PAT，仅 DCritHitFireIV/FireGaze，权限 Issues: Read and write
//   REPO（可选）：默认 DCritHitFireIV/FireGaze

const REPO_DEFAULT = 'DCritHitFireIV/FireGaze';
const MAX_BODY = 60000; // GitHub issue 正文上限 65536，留点余量
const PER_IP_LIMIT = 5; // 每个 IP 每分钟最多 5 次（实例内存计数，近似限流）
const WINDOW_MS = 60_000;

const hits = new Map();

function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8' },
  });
}

function allow(ip) {
  const now = Date.now();
  const rec = hits.get(ip);
  if (!rec || now - rec.start > WINDOW_MS) {
    hits.set(ip, { start: now, n: 1 });
    return true;
  }
  rec.n += 1;
  return rec.n <= PER_IP_LIMIT;
}

export default {
  async fetch(request, env) {
    if (request.method !== 'POST') {
      return json({ ok: false, error: 'POST only' }, 405);
    }

    const ip = request.headers.get('CF-Connecting-IP') ?? 'unknown';
    if (!allow(ip)) {
      return json({ ok: false, error: 'too many requests' }, 429);
    }

    let data;
    try {
      data = await request.json();
    } catch {
      return json({ ok: false, error: 'invalid json' }, 400);
    }

    const title = String(data.title ?? '').trim().slice(0, 160);
    const body = String(data.body ?? '');
    if (!title || !body) {
      return json({ ok: false, error: 'title/body required' }, 400);
    }
    if (body.length > MAX_BODY) {
      return json({ ok: false, error: 'body too large' }, 413);
    }
    if (!body.includes('```json') || !body.includes('contributions')) {
      return json({ ok: false, error: 'not a FireGaze contribution payload' }, 400);
    }
    if (!env.GITHUB_TOKEN) {
      return json({ ok: false, error: 'server not configured' }, 500);
    }

    const repo = env.REPO || REPO_DEFAULT;
    const res = await fetch(`https://api.github.com/repos/${repo}/issues`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${env.GITHUB_TOKEN}`,
        Accept: 'application/vnd.github+json',
        'User-Agent': 'firegaze-relay',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ title, body }),
    });

    if (!res.ok) {
      const detail = await res.text();
      return json({ ok: false, error: `github ${res.status}`, detail: detail.slice(0, 300) }, 502);
    }

    const issue = await res.json();
    return json({ ok: true, issue: issue.html_url, number: issue.number });
  },
};
