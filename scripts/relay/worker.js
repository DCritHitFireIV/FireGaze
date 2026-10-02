// FireGaze 中继（Cloudflare Worker）v3
//
// 客户端（插件）POST → 本 Worker 校验/限流/垃圾检测 → 用服务端凭据在仓库里建 issue
// → 现有 GitHub Actions 工作流负责：存档 + 回评 + 用 secret 通知维护者手机。
//
// 三类投稿：
//   · 译文投稿（正文带 ```json + contributions / uit-contribution）——原有通道
//   · 用户反馈（type=feedback，正文带「### FireGaze 反馈」）——2026-10-03 新增
//
// 鉴权（二选一，客户端不持有任何凭据）：
//   ① GitHub App（推荐，「机器人」身份，不用任何个人 token）：
//        APP_ID（变量）、APP_PRIVATE_KEY（机密，PKCS#8 PEM）、
//        INSTALLATION_ID（可选；不填则按 REPO 查安装）
//   ② GITHUB_TOKEN（机密）：fine-grained PAT，仅本仓库、仅 Issues: Read and write（兼容旧部署）
//
// 环境变量：REPO（可选，默认 DCritHitFireIV/FireGaze）

const REPO_DEFAULT = 'DCritHitFireIV/FireGaze';
const MAX_BODY = 60000; // GitHub issue 正文上限 65536，留点余量
const PER_IP_LIMIT = 5; // 每个 IP 每分钟最多 5 次（实例内存计数，近似限流）
const WINDOW_MS = 60_000;
const FEEDBACK_MARKER = '### FireGaze 反馈';

const hits = new Map();

// 明显垃圾词/模式（只做粗筛，真判由用户举报与后台复核兜底）
const BANNED_PATTERNS = [
  /viagra/i, /casino/i, /porn/i, /escort/i, /博彩/, /赌场/, /代刷/, /加微信/, /加我微信/,
  /贷款/, /开票/, /usdt/i, /telegram/i, /whatsapp/i, /兼职刷单/, /色情/, /成人服务/,
];

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

/// 反馈正文的垃圾检测：命中则返回原因，否则 null。
function feedbackSpamReason(text) {
  if (text.length < 10) return 'too short';
  if (text.length > 20000) return 'too long';

  const links = (text.match(/https?:\/\//g) ?? []).length;
  if (links > 8) return 'too many links';

  for (const re of BANNED_PATTERNS) {
    if (re.test(text)) return 'banned words';
  }

  const letters = (text.match(/\p{L}/gu) ?? []).length;
  if (letters < 10 && links >= 1) return 'link only';

  const lines = text.split('\n').map((s) => s.trim()).filter(Boolean);
  if (lines.length > 30) {
    const uniq = new Set(lines);
    if (uniq.size / lines.length < 0.3) return 'repeated lines';
  }

  return null;
}

// ── GitHub App 鉴权（WebCrypto 签 RS256 JWT） ─────────────────────────────

function b64url(bytes) {
  let bin = '';
  const arr = new Uint8Array(bytes);
  for (let i = 0; i < arr.length; i += 1) bin += String.fromCharCode(arr[i]);
  return btoa(bin).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}

function pemToArrayBuffer(pem) {
  const body = pem.replace(/-----[^-]+-----/g, '').replace(/\s+/g, '');
  const bin = atob(body);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i += 1) bytes[i] = bin.charCodeAt(i);
  return bytes.buffer;
}

async function signAppJwt(appId, privateKeyPem) {
  const now = Math.floor(Date.now() / 1000);
  const enc = new TextEncoder();
  const head = b64url(enc.encode(JSON.stringify({ alg: 'RS256', typ: 'JWT' })));
  const payload = b64url(enc.encode(JSON.stringify({ iat: now - 60, exp: now + 540, iss: appId })));
  const key = await crypto.subtle.importKey(
    'pkcs8',
    pemToArrayBuffer(privateKeyPem),
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const sig = await crypto.subtle.sign('RSASSA-PKCS1-v1_5', key, enc.encode(`${head}.${payload}`));
  return `${head}.${payload}.${b64url(sig)}`;
}

async function githubToken(env, repo) {
  if (env.APP_ID && env.APP_PRIVATE_KEY) {
    const jwt = await signAppJwt(env.APP_ID, env.APP_PRIVATE_KEY);
    const headers = {
      Authorization: `Bearer ${jwt}`,
      Accept: 'application/vnd.github+json',
      'User-Agent': 'firegaze-relay',
    };

    let installationId = env.INSTALLATION_ID;
    if (!installationId) {
      const res = await fetch(`https://api.github.com/repos/${repo}/installation`, { headers });
      if (!res.ok) throw new Error(`app installation lookup ${res.status}`);
      installationId = (await res.json()).id;
    }

    const tokenRes = await fetch(`https://api.github.com/app/installations/${installationId}/access_tokens`, {
      method: 'POST',
      headers,
    });
    if (!tokenRes.ok) throw new Error(`app token ${tokenRes.status}`);
    return (await tokenRes.json()).token;
  }

  return env.GITHUB_TOKEN ?? null; // 兼容旧部署
}

export default {
  async fetch(request, env) {
    if (request.method === 'GET') {
      return json({ ok: true, service: 'firegaze-relay', version: 3 });
    }

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

    // ── 分流与预检 ──
    const isFeedback = data.type === 'feedback' || body.includes(FEEDBACK_MARKER);
    if (isFeedback) {
      // 用户反馈：标题/标记 + 垃圾检测（检测通过才发布，见 logging-conventions §4）
      if (!title.startsWith('[反馈]') || !body.includes(FEEDBACK_MARKER)) {
        return json({ ok: false, error: 'not a feedback payload' }, 400);
      }

      const content = body.slice(body.indexOf(FEEDBACK_MARKER) + FEEDBACK_MARKER.length);
      const spam = feedbackSpamReason(content);
      if (spam) {
        return json({ ok: false, error: 'spam-blocked', detail: spam }, 422);
      }
    } else {
      // 译文投稿：原有校验不变
      if (!body.includes('```json') || (!body.includes('contributions') && !body.includes('uit-contribution'))) {
        return json({ ok: false, error: 'not a FireGaze contribution payload' }, 400);
      }
    }

    let token;
    try {
      token = await githubToken(env, env.REPO || REPO_DEFAULT);
    } catch (e) {
      return json({ ok: false, error: 'server auth failed', detail: String(e).slice(0, 200) }, 500);
    }

    if (!token) {
      return json({ ok: false, error: 'server not configured' }, 500);
    }

    const repo = env.REPO || REPO_DEFAULT;
    const res = await fetch(`https://api.github.com/repos/${repo}/issues`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${token}`,
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
