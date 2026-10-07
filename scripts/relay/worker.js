// FireGaze 中继（Cloudflare Worker）v12
//
// 客户端（插件）POST → 本 Worker 校验/限流/垃圾检测 → 用服务端凭据在仓库里建 issue
// → 现有 GitHub Actions 工作流负责：存档 + 回评 + 用 secret 通知维护者手机。
//
// 通道：
//   · 译文投稿（正文带 ```json + contributions / uit-contribution）——原有通道
//   · 用户反馈（type=feedback，正文带「### FireGaze 反馈」）——2026-10-03 新增
//   · 公共彩云小译代理（POST /translate）——2026-10-03 新增：
//       插件不持有彩云 token；token 只存服务端（机密 CAIYUN_TOKEN）。
//       额度用完 / 密钥失效 / 未配置时回 { ok:false, error:'unavailable' }，插件端自动回退免费通道。
//   · 彩云 token 池（管理：POST /translate/token、POST /translate/token/remove、GET /translate/pool）
//       ——2026-10-07 新增（v10）：
//       多个彩云 token 轮换：某个额度用完（401/403）自动换下一个，冷却到期自动重试
//       （充值 / 月额度重置后自动回归），新 token 加进来即生效；全部逻辑都在本文件里。
//       存储复用 LIBRARY_COUNTS（v11 起整个池子存在单键 `pool:index`，热路径不再 list；旧 tok:* 键自动迁移）；
//       管理端点要机密 ADMIN_KEY（不配 = 管理面关闭）。
//       池子为空时仍用 CAIYUN_TOKEN 兜底（旧配置零改动）；池子用完可推手机提醒（可选，见下）。
//   · KV 免费额度纪律（v11/v12）——免费版每天只有 1,000 次 list / 1,000 次写，必须省着用：
//       聚合计数（/library-counts、/plugin-stats）三层缓存：本实例内存（30 分钟）
//       → Cloudflare Cache API（6 小时，按数据中心共享、跨实例、不占 KV 额度）→ KV 构建。
//       构建时两个端点共用一份「全键列表」缓存（每次构建最多 1 次 list）；计数写入时把数值
//       也放进 KV metadata，读取靠 list 的 metadata 直读，只有旧键（没 metadata）才逐键 get
//       ——并发上限 16，不再串行。token 池用单键，不再 list。
//   · 界面译文直传（POST /uit-submit）——2026-10-04 新增：
//       几千条的投稿不再走 issue 粘贴；Worker 直接把它提交成
//       docs/contributions/inbox/uit-direct-<时间>-<随机>.json，仓库工作流接手并入公共库。
//       需要 GitHub App 有 Contents: Read and write。
//   · 公共库下载量 / 点赞（POST /library-download、POST /library-like 计数，GET /library-counts 读取）——2026-10-04 新增：
//       包本体仍从 raw/镜像下载（CDN 不计下载），由客户端上报一条计数；存 KV（绑定名 LIBRARY_COUNTS）。
//       v6 起按**包**计数（键 `<插件>@<包ID>`，基础包包ID=library）——每个玩家投稿包有独立的下载数与👍。
//   · 准点唤醒译文工作流（Cloudflare Cron Trigger）——2026-10-05 新增（v7）：
//       GitHub 自带 schedule 实测延迟数小时（9/21、9/28 的周一跑晚了 5.8/6.8 小时），
//       所以周一/周五 00:00 UTC（北京 08:00）由本 worker 发 repository_dispatch(translate-now)。
//
// 鉴权（二选一，客户端不持有任何凭据）：
//   ① GitHub App（推荐，「机器人」身份，不用任何个人 token）：
//        APP_ID（变量）、APP_PRIVATE_KEY（机密，PKCS#8 PEM）、
//        INSTALLATION_ID（可选；不填则按 REPO 查安装）
//        （直传还需要 App 的 Contents: Read and write）
//   ② GITHUB_TOKEN（机密）：fine-grained PAT，仅本仓库；投稿要 Issues: R/W，直传要 Contents: R/W
//
// 环境变量：REPO（可选，默认 DCritHitFireIV/FireGaze）
//          FEEDBACK_REPO（可选，默认 = REPO）：玩家反馈的 issue 建到这个仓库；
//          指向私有仓时只有维护者能看（GitHub 公开仓不支持私密 issue）。
// 机密（/translate 用）：CAIYUN_TOKEN——彩云小译访问令牌（池子为空时的兜底）；不配且池子空就只关闭这个端点。
// 机密（token 池管理，可选）：ADMIN_KEY——管理端点鉴权；SERVER3_PUSH_URL——池子用完的手机提醒（可选）。
// 绑定（下载量用）：LIBRARY_COUNTS（KV 命名空间）；不配则计数端点回 counting disabled，其余照常。

const REPO_DEFAULT = 'DCritHitFireIV/FireGaze';
const MAX_BODY = 60000; // GitHub issue 正文上限 65536，留点余量
const PER_IP_LIMIT = 5; // 每个 IP 每分钟最多 5 次投稿/反馈（实例内存计数，近似限流）
const WINDOW_MS = 60_000;
const FEEDBACK_MARKER = '### FireGaze 反馈';

// 公共彩云代理（/translate）
const CAIYUN_URL = 'https://api.interpreter.caiyunai.com/v1/translator';
const TRANSLATE_MAX_ITEMS = 50; // 与插件端同一批上限（实测 52 就 413）
const TRANSLATE_MAX_CHARS = 20000; // 单次请求原文总长上限
const TRANSLATE_PER_IP_LIMIT = 120; // 每分钟每 IP 的翻译请求上限（插件端 0.6s 一批，刚好在上限内）
const TRANSLATE_DAILY_CHARS = 400000; // 每 IP 每天最多翻多少字符（实例内存计数，近似限流，防止单机刷完额度）

const charUsage = new Map(); // `${ip}|${yyyy-mm-dd}` -> 已用字符数

// 彩云 token 池（v10）：多个 token 轮换，额度用完自动换下一个；冷却到期自动回归。
const POOL_PREFIX = 'tok:'; // v10 的旧键前缀（v11 起只用于自动迁移）
const POOL_INDEX_KEY = 'pool:index'; // v11：整个池子存在一把键里（不再 list，省免费额度）
const POOL_CACHE_MS = 60_000; // token 列表内存缓存（每次过期只读 1 把键）
const POOL_MAX_ATTEMPTS = 3; // 单次请求最多试几个 token（控制玩家侧延迟）
const POOL_COOLDOWN_HOURS = 24; // 冷却基准，失败次数递增：24h → 48h → 72h 封顶
const POOL_COOLDOWN_CAP = 3;
const POOL_USAGE_FLUSH_MS = 3600_000; // 月用量写回 KV 的节流（免费版每天只有 1,000 次写）
const POOL_EMPTY_NOTIFY_MS = 6 * 3600 * 1000; // 池子空的提醒节流
const POOL_ADMIN_PER_IP_LIMIT = 30; // 管理端点每分钟每 IP

// 聚合读（/library-counts、/plugin-stats）的缓存：免费版每天只有 1,000 次 list，
// 客户端本来就自带 5–10 分钟节流，服务端再挡两层；两个端点共用一份全键列表。
const AGG_CACHE_MS = 30 * 60_000; // L1：本实例内存
const AGG_SHARED_TTL_MS = 6 * 3600_000; // L2：Cache API，按数据中心共享、跨实例、不占 KV
const AGG_READ_CONCURRENCY = 16; // 旧键逐条 get 的并发上限
const KEY_LIST_CACHE_MS = 30 * 60_000;
let libraryCountsCache = { at: 0, body: null };
let pluginStatsCache = { at: 0, body: null };
let keyListCache = { at: 0, keys: [] };

let poolCache = { at: 0, entries: [] };
let poolEmptyNotifiedAt = 0;

// 界面译文直传（/uit-submit）
const UIT_SUBMIT_MAX_CHARS = 4_000_000; // 投稿 JSON 上限（几千条约几百 KB，留足余量）
const UIT_SUBMIT_PER_IP_LIMIT = 6; // 每分钟每 IP（正常玩家一次「一键提交」= 1 次）

// 公共库下载量 / 点赞（/library-download、/library-like、/library-counts）
const LIBRARY_DOWNLOAD_PER_IP_LIMIT = 60; // 每分钟每 IP（正常一次下载 = 1 次）
const LIBRARY_LIKE_PER_IP_LIMIT = 30; // 每分钟每 IP（点赞；没做去重，先靠限流）
const PLUGIN_LIKE_PER_IP_LIMIT = 60; // 每分钟每 IP（插件发现：给插件点赞）
const PLUGIN_ADD_PER_IP_LIMIT = 120; // 每分钟每 IP（插件发现：加库推荐上报）
const REPO_SUBMIT_PER_IP_LIMIT = 10; // 每分钟每 IP（插件发现：库链投稿）
const PACK_ID_PATTERN = /^[A-Za-z0-9_.\-]+$/;

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

function allow(ip, limit = PER_IP_LIMIT, bucket = 'inbox') {
  const now = Date.now();
  const key = `${bucket}|${ip}`;
  const rec = hits.get(key);
  if (!rec || now - rec.start > WINDOW_MS) {
    hits.set(key, { start: now, n: 1 });
    return true;
  }
  rec.n += 1;
  return rec.n <= limit;
}

/// 每 IP 每日字符预算（实例内存计数，只在单个 isolate 内准确——够挡「一台机器刷完额度」）。
function chargeChars(ip, chars) {
  const day = new Date().toISOString().slice(0, 10);
  const key = `${ip}|${day}`;
  const used = charUsage.get(key) ?? 0;
  if (used + chars > TRANSLATE_DAILY_CHARS) {
    return false;
  }
  charUsage.set(key, used + chars);
  return true;
}

/// 界面译文直传：把 POST 正文原样提交成仓库里的一份投稿文件。
/// 只做长度与关键字粗检（几千条的大 JSON 全量解析不值当，真正的校验在仓库工作流里）。
async function handleUitSubmit(request, env, ip) {
  if (!allow(ip, UIT_SUBMIT_PER_IP_LIMIT, 'uitsubmit')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  let text;
  try {
    text = await request.text();
  } catch {
    return json({ ok: false, error: 'body unreadable' }, 400);
  }

  if (!text || text.length < 40) {
    return json({ ok: false, error: 'empty body' }, 400);
  }
  if (text.length > UIT_SUBMIT_MAX_CHARS) {
    return json({ ok: false, error: 'too large', detail: `${text.length} chars` }, 413);
  }
  if (!text.includes('"uit-contribution"') || !text.includes('"plugin"')) {
    return json({ ok: false, error: 'not a uit-contribution payload' }, 400);
  }

  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const path = `docs/contributions/inbox/uit-direct-${stamp}-${randomTag()}.json`;
  const repo = env.REPO || REPO_DEFAULT;

  let token;
  try {
    token = await githubToken(env, repo);
  } catch (e) {
    return json({ ok: false, error: 'server auth failed', detail: String(e).slice(0, 200) }, 500);
  }

  if (!token) {
    return json({ ok: false, error: 'server not configured' }, 500);
  }

  // 直传写仓库需要 App 的 Contents: 写权限；没给会在这里拿到 403，客户端会回退到「打开提交页」
  try {
    const result = await commitInboxFile(env, repo, token, path, text);
    return json({
      ok: true,
      file: `https://github.com/${repo}/blob/main/${path}`,
      commit: `https://github.com/${repo}/commit/${result}`,
    });
  } catch (e) {
    return json({ ok: false, error: 'commit failed', detail: String(e).slice(0, 200) }, 502);
  }
}

function randomTag() {
  const bytes = crypto.getRandomValues(new Uint8Array(4));
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/// 用 Git Data API 把一个新文件提交到 main（不用 base64，省 CPU）：
/// 取 head → 建 blob → 建 tree（base_tree=当前）→ 建 commit → 更新 ref。
/// 并发提交撞车（ref 被推走）时整个流程重试一次。
async function commitInboxFile(env, repo, token, path, content, attempt = 0) {
  const headers = {
    Authorization: `Bearer ${token}`,
    Accept: 'application/vnd.github+json',
    'User-Agent': 'firegaze-relay',
    'Content-Type': 'application/json',
  };
  const api = (suffix) => `https://api.github.com/repos/${repo}${suffix}`;
  const message = `chore(uit-packs): 收录玩家直传投稿（${path.split('/').pop()}）`;

  try {
    const headRes = await fetch(api('/git/ref/heads/main'), { headers });
    if (!headRes.ok) throw new Error(`ref ${headRes.status}`);
    const headSha = (await headRes.json()).object.sha;

    const commitRes = await fetch(api(`/git/commits/${headSha}`), { headers });
    if (!commitRes.ok) throw new Error(`commit ${commitRes.status}`);
    const treeSha = (await commitRes.json()).tree.sha;

    const blobRes = await fetch(api('/git/blobs'), {
      method: 'POST',
      headers,
      body: JSON.stringify({ content, encoding: 'utf-8' }),
    });
    if (!blobRes.ok) throw new Error(`blob ${blobRes.status}`);
    const blobSha = (await blobRes.json()).sha;

    const treeMake = await fetch(api('/git/trees'), {
      method: 'POST',
      headers,
      body: JSON.stringify({
        base_tree: treeSha,
        tree: [{ path, mode: '100644', type: 'blob', sha: blobSha }],
      }),
    });
    if (!treeMake.ok) throw new Error(`tree ${treeMake.status}`);
    const newTree = (await treeMake.json()).sha;

    const commitMake = await fetch(api('/git/commits'), {
      method: 'POST',
      headers,
      body: JSON.stringify({ message, tree: newTree, parents: [headSha] }),
    });
    if (!commitMake.ok) throw new Error(`commit-create ${commitMake.status}`);
    const newCommit = (await commitMake.json()).sha;

    const refUpdate = await fetch(api('/git/refs/heads/main'), {
      method: 'PATCH',
      headers,
      body: JSON.stringify({ sha: newCommit, force: false }),
    });
    if (!refUpdate.ok) throw new Error(`ref-update ${refUpdate.status}`);
    return newCommit;
  } catch (error) {
    if (attempt < 1) {
      return commitInboxFile(env, repo, token, path, content, attempt + 1);
    }
    throw error;
  }
}

/// 公共库计数：客户端下载/点赞后各发一条（发完即忘）→ KV 自增。
/// 键：`dl:<插件>@<包ID>` / `like:<插件>@<包ID>`；基础包的包 ID 是 `library`。
function parsePackTarget(data) {
  const plugin = String(data.plugin ?? '').trim().slice(0, 64);
  if (!PACK_ID_PATTERN.test(plugin)) {
    return null;
  }

  const rawPack = String(data.pack ?? 'library').trim().slice(0, 64) || 'library';
  if (!PACK_ID_PATTERN.test(rawPack)) {
    return null;
  }

  return { plugin, pack: rawPack };
}

async function bumpCounter(store, prefix, target) {
  const key = `${prefix}:${target.plugin}@${target.pack}`;
  const current = Number.parseInt((await store.get(key)) ?? '0', 10) || 0;
  const next = current + 1;
  // v11：数值也放进 metadata——聚合端点靠 list 的 metadata 直读，省掉逐键 get
  await store.put(key, String(next), { metadata: { v: next } });
  return next;
}

/// 北京时间（UTC+8）的 ISO 周标签，如 2026-W41；周界 = 周一 00:00。
/// 周赞不清零：每周单独一把键（plike_w:<周>:<插件>），旧周留在 KV、也会被工作流归档进 git。
function beijingISOWeek(date = new Date()) {
  const shifted = new Date(date.getTime() + 8 * 3600 * 1000);
  const target = new Date(Date.UTC(shifted.getUTCFullYear(), shifted.getUTCMonth(), shifted.getUTCDate()));
  const dayNum = target.getUTCDay() || 7;
  target.setUTCDate(target.getUTCDate() + 4 - dayNum);
  const yearStart = new Date(Date.UTC(target.getUTCFullYear(), 0, 1));
  const week = Math.ceil(((target - yearStart) / 86400000 + 1) / 7);
  return `${target.getUTCFullYear()}-W${String(week).padStart(2, '0')}`;
}

/// 自增一把独立键（插件级计数用）。
async function bumpKey(store, key) {
  const current = Number.parseInt((await store.get(key)) ?? '0', 10) || 0;
  const next = current + 1;
  await store.put(key, String(next), { metadata: { v: next } });
  return next;
}

/// 列全部键（带 metadata）+ 内存缓存（30 分钟）：聚合端点们共用，每个窗口最多 1 次 list。
async function listKeysCached(store) {
  const now = Date.now();
  if (keyListCache.at > 0 && now - keyListCache.at < KEY_LIST_CACHE_MS) {
    return keyListCache.keys;
  }

  const keys = [];
  let cursor;
  for (;;) {
    const listed = await store.list({ limit: 1000, cursor });
    keys.push(...listed.keys.map((k) => ({ name: k.name, metadata: k.metadata ?? null })));
    if (listed.list_complete) break;
    cursor = listed.cursor;
  }

  keyListCache = { at: now, keys };
  return keys;
}

/// 按前缀读全部计数（KV list 单次最多 1000 把键，必须分页——插件 2000 条时会静默截断）。
/// v11：优先读 list 返回的 metadata.v（一次 list 带出计数），只有旧键（没 metadata）才逐键 get。
/// v12：旧键 get 改为并发（上限 AGG_READ_CONCURRENCY）——实测 241 把旧键串行要 55 秒，
///       超出客户端 20 秒超时；并发后 2–3 秒。
async function readAllByPrefix(store, prefix) {
  const values = {};
  const legacy = [];
  for (const entry of await listKeysCached(store)) {
    if (!entry.name.startsWith(prefix)) {
      continue;
    }

    const fromMeta = entry.metadata && typeof entry.metadata.v === 'number' ? entry.metadata.v : null;
    if (fromMeta !== null) {
      values[entry.name.slice(prefix.length)] = fromMeta;
    } else {
      legacy.push(entry.name);
    }
  }

  for (let i = 0; i < legacy.length; i += AGG_READ_CONCURRENCY) {
    const batch = legacy.slice(i, i + AGG_READ_CONCURRENCY);
    const got = await Promise.all(
      batch.map(async (name) => [name, Number.parseInt((await store.get(name)) ?? '0', 10) || 0]),
    );
    for (const [name, value] of got) {
      values[name.slice(prefix.length)] = value;
    }
  }
  return values;
}

/// 聚合结果的共享缓存（L2，Cloudflare Cache API）：按数据中心共享、跨实例、不占 KV 额度。
/// 条目自带 builtAt，超过 AGG_SHARED_TTL_MS 就重建；Cache 不可用时静默直连。
async function readAggregationCache(key, producer) {
  const cache = typeof caches !== 'undefined' && caches.default ? caches.default : null;
  if (cache) {
    try {
      const hit = await cache.match(key);
      if (hit) {
        const entry = await hit.json();
        const fresh = entry && entry.body && typeof entry.builtAt === 'number'
          && Date.now() - entry.builtAt < AGG_SHARED_TTL_MS;
        if (fresh) {
          return entry.body;
        }
      }
    } catch {
      // 缓存读失败：直接重建
    }
  }

  const body = await producer();
  if (cache) {
    try {
      await cache.put(key, new Response(JSON.stringify({ builtAt: Date.now(), body }), {
        headers: { 'Content-Type': 'application/json; charset=utf-8' },
      }));
    } catch {
      // 缓存写失败不影响本次响应
    }
  }
  return body;
}

/// 插件发现：点赞（按插件、按周）。总赞累加；周赞落在当周键上，下周可以再点。
async function handlePluginLike(data, env, ip) {
  if (!allow(ip, PLUGIN_LIKE_PER_IP_LIMIT, 'plike')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  const store = env.LIBRARY_COUNTS;
  if (!store) {
    return json({ ok: false, error: 'counting disabled' }, 503);
  }

  const plugin = String(data.plugin ?? '').trim().slice(0, 64);
  if (!PACK_ID_PATTERN.test(plugin)) {
    return json({ ok: false, error: 'bad plugin' }, 400);
  }

  const week = beijingISOWeek();
  const total = await bumpKey(store, `plike_total:${plugin}`);
  const weekly = await bumpKey(store, `plike_w:${week}:${plugin}`);
  return json({ ok: true, plugin, week, total, weekly });
}

/// 插件发现：加库推荐上报（按插件；一次最多 200 条）。
async function handlePluginAdd(data, env, ip) {
  if (!allow(ip, PLUGIN_ADD_PER_IP_LIMIT, 'padd')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  const store = env.LIBRARY_COUNTS;
  if (!store) {
    return json({ ok: false, error: 'counting disabled' }, 503);
  }

  const list = Array.isArray(data.plugins) ? data.plugins.slice(0, 200) : [];
  const names = list
    .map((x) => String(x ?? '').trim().slice(0, 64))
    .filter((x) => PACK_ID_PATTERN.test(x));
  if (names.length === 0) {
    return json({ ok: false, error: 'bad plugins' }, 400);
  }

  for (const name of names) {
    await bumpKey(store, `padd:${name}`);
  }

  return json({ ok: true, count: names.length });
}

/// 插件发现：统计（周赞 / 总赞 / 加库推荐）。工作流定时拉这个写进仓库归档。
async function handlePluginStats(env) {
  const store = env.LIBRARY_COUNTS;
  if (!store) {
    return json({ ok: false, error: 'counting disabled' }, 503);
  }

  const now = Date.now();
  const week = beijingISOWeek();
  if (pluginStatsCache.body && pluginStatsCache.week === week && now - pluginStatsCache.at < AGG_CACHE_MS) {
    return json(pluginStatsCache.body);
  }

  // 缓存键带周号：跨周时不拿旧周的周赞
  const body = await readAggregationCache(`https://firegaze-relay.invalid/agg/plugin?week=${week}`, async () => {
    const total = await readAllByPrefix(store, 'plike_total:');
    const weekly = await readAllByPrefix(store, `plike_w:${week}:`);
    const adds = await readAllByPrefix(store, 'padd:');
    return { ok: true, week, total, weekly, adds, updatedAt: new Date().toISOString() };
  });
  pluginStatsCache = { at: now, week, body };
  return json(body);
}

/// 插件发现：库链投稿（客户端已做过卫月契约检测；这里只做形状检查，真正的收录由仓库工作流定）。
async function handleRepoSubmit(data, env, ip) {
  if (!allow(ip, REPO_SUBMIT_PER_IP_LIMIT, 'reposubmit')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  const target = String(data.url ?? '').trim().slice(0, 512);
  if (!/^https?:\/\//i.test(target)) {
    return json({ ok: false, error: 'bad url' }, 400);
  }

  const repo = env.REPO || REPO_DEFAULT;
  let token;
  try {
    token = await githubToken(env, repo);
  } catch (e) {
    return json({ ok: false, error: 'server auth failed', detail: String(e).slice(0, 200) }, 500);
  }

  if (!token) {
    return json({ ok: false, error: 'server not configured' }, 500);
  }

  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const path = `docs/contributions/inbox/repo-${stamp}-${randomTag()}.json`;
  const payload = JSON.stringify({ type: 'repo-submission', url: target, submittedAt: new Date().toISOString() });
  try {
    const result = await commitInboxFile(env, repo, token, path, payload);
    return json({
      ok: true,
      file: `https://github.com/${repo}/blob/main/${path}`,
      commit: `https://github.com/${repo}/commit/${result}`,
    });
  } catch (e) {
    return json({ ok: false, error: 'commit failed', detail: String(e).slice(0, 200) }, 502);
  }
}

async function handleLibraryDownload(data, env, ip) {
  if (!allow(ip, LIBRARY_DOWNLOAD_PER_IP_LIMIT, 'libdl')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  const store = env.LIBRARY_COUNTS;
  if (!store) {
    return json({ ok: false, error: 'counting disabled' }, 503);
  }

  const target = parsePackTarget(data);
  if (!target) {
    return json({ ok: false, error: 'bad plugin/pack' }, 400);
  }

  const downloads = await bumpCounter(store, 'dl', target);
  return json({ ok: true, plugin: target.plugin, pack: target.pack, downloads });
}

async function handleLibraryLike(data, env, ip) {
  if (!allow(ip, LIBRARY_LIKE_PER_IP_LIMIT, 'liblike')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  const store = env.LIBRARY_COUNTS;
  if (!store) {
    return json({ ok: false, error: 'counting disabled' }, 503);
  }

  const target = parsePackTarget(data);
  if (!target) {
    return json({ ok: false, error: 'bad plugin/pack' }, 400);
  }

  const likes = await bumpCounter(store, 'like', target);
  return json({ ok: true, plugin: target.plugin, pack: target.pack, likes });
}

/// 读取全部计数（定时工作流写回 uit-packs/index.json 用）：下载数与👍分开返回，键都是 `<插件>@<包ID>`。
async function handleLibraryCounts(env) {
  const store = env.LIBRARY_COUNTS;
  if (!store) {
    return json({ ok: false, error: 'counting disabled' }, 503);
  }

  const now = Date.now();
  if (libraryCountsCache.body && now - libraryCountsCache.at < AGG_CACHE_MS) {
    return json(libraryCountsCache.body);
  }

  const body = await readAggregationCache('https://firegaze-relay.invalid/agg/library', async () => {
    const counts = await readAllByPrefix(store, 'dl:');
    const likes = await readAllByPrefix(store, 'like:');
    return { ok: true, updatedAt: new Date().toISOString(), counts, likes };
  });
  libraryCountsCache = { at: now, body };
  return json(body);
}

// ── 彩云 token 池（v10）─────────────────────────────────────────────
// 目标：一个 token 额度用完就自动用下一个；充值 / 月额度重置后自动回归；新 token 加进来即生效。
// 存储：LIBRARY_COUNTS（前缀 tok:），不需要新绑定；热路径只碰内存，冷事件（冷却 / 新增）才写 KV。

function currentMonth() {
  return new Date().toISOString().slice(0, 7);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function poolTokenId(token) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(token));
  return Array.from(new Uint8Array(digest, 0, 6), (b) => b.toString(16).padStart(2, '0')).join('');
}

function maskToken(token) {
  const value = String(token ?? '');
  return value.length <= 10 ? '****' : `${value.slice(0, 4)}****${value.slice(-4)}`;
}

async function listPoolKeys(store) {
  const keys = [];
  let cursor;
  for (;;) {
    const listed = await store.list({ prefix: POOL_PREFIX, limit: 1000, cursor });
    keys.push(...listed.keys.map((k) => k.name));
    if (listed.list_complete) break;
    cursor = listed.cursor;
  }
  return keys;
}

/// 读池子索引（单键 pool:index；返回 tokens 数组，KV 不可用时返回空数组）。
async function readPoolIndex(store) {
  if (!store) return [];
  try {
    const raw = await store.get(POOL_INDEX_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!parsed || !Array.isArray(parsed.tokens)) return [];
    return parsed.tokens.filter((e) => e && typeof e.token === 'string' && e.token.length > 0);
  } catch {
    return [];
  }
}

/// 写回池子索引：先读一遍再按 id 合并（不同实例并发改池子时尽量不丢对方的改动）。
async function persistPool(env, changedEntries) {
  const store = env.LIBRARY_COUNTS;
  if (!store) return;
  try {
    const tokens = await readPoolIndex(store);
    const byId = new Map(tokens.filter((e) => e && e.id).map((e) => [e.id, e]));
    for (const entry of changedEntries) {
      if (!entry || !entry.id || entry.id === 'env') continue;
      byId.set(entry.id, entry);
    }
    await store.put(POOL_INDEX_KEY, JSON.stringify({ tokens: [...byId.values()], updatedAt: new Date().toISOString() }));
  } catch {
    // 写失败只影响统计/冷却持久化，不影响翻译
  }
}

/// 读池子（内存缓存 60 秒）：单键索引 + 旧的单 token（兼容）；月份翻页 = 额度可能重置 → 自动复活。
/// v11：从 v10 的 tok:<id> 多键自动迁到单键（只迁一次，之后热路径不再 list）。
async function loadPool(env) {
  const now = Date.now();
  if (poolCache.at > 0 && now - poolCache.at < POOL_CACHE_MS) {
    return poolCache.entries;
  }

  const previous = poolCache.entries;
  const store = env.LIBRARY_COUNTS;
  let entries = await readPoolIndex(store);

  // v10 → v11 迁移：索引不存在时扫一次旧 tok:* 键，并进索引后清掉旧键
  if (store && entries.length === 0) {
    try {
      const rawIndex = await store.get(POOL_INDEX_KEY);
      if (!rawIndex) {
        const keys = await listPoolKeys(store);
        const migrated = [];
        for (const key of keys) {
          try {
            const raw = await store.get(key);
            const entry = raw ? JSON.parse(raw) : null;
            if (entry && typeof entry.token === 'string' && entry.token.length > 0) {
              entry.id = entry.id || key.slice(POOL_PREFIX.length);
              migrated.push(entry);
            }
          } catch {
            // 坏记录跳过
          }
        }
        if (migrated.length > 0) {
          await persistPool(env, migrated);
          for (const key of keys) {
            try {
              await store.delete(key);
            } catch {
              // 删不掉就留着（索引在就不会再读它）
            }
          }
        }
        entries = migrated;
      }
    } catch {
      // 迁移失败：按空池跑，下次再试
    }
  }

  // 兼容：旧的单 token 也进池子（不落 KV，冷却只在内存里）
  if (env.CAIYUN_TOKEN && !entries.some((e) => e.token === env.CAIYUN_TOKEN)) {
    entries.push({
      id: 'env', token: env.CAIYUN_TOKEN, label: 'CAIYUN_TOKEN（兼容）', addedAt: 0,
      month: currentMonth(), monthChars: 0, fails: 0, cooldownUntil: 0, lastUsedAt: 0, lastError: null,
    });
  }

  // 刷新时把内存里还没落盘的月用量并回来（KV 里的可能是上一次定时写回的快照）
  for (const entry of entries) {
    const old = previous.find((e) => e.id === entry.id);
    if (old && old.month === entry.month) {
      entry.monthChars = Math.max(entry.monthChars ?? 0, old.monthChars ?? 0);
    }
  }

  // 坏条目（缺 id）补一个稳定 id，免得每次都被当成新条目
  for (const entry of entries) {
    if (!entry.id && entry.token) {
      entry.id = await poolTokenId(entry.token);
    }
  }

  // 月份翻页 = 额度可能重置：清冷却、清计数，自动回归
  const month = currentMonth();
  const revived = [];
  for (const entry of entries) {
    if (entry.month !== month) {
      entry.month = month;
      entry.monthChars = 0;
      entry.fails = 0;
      entry.cooldownUntil = 0;
      revived.push(entry);
    }
  }

  if (revived.length > 0) {
    await persistPool(env, revived);
  }

  poolCache = { at: now, entries };
  return entries;
}

/// 可用 token：不在冷却期内的，按「本月用量最少」优先（摊平额度，新 token 先用）。
function usablePool(entries, now) {
  return entries
    .filter((entry) => !(entry.cooldownUntil > now))
    .sort((a, b) => (a.monthChars ?? 0) - (b.monthChars ?? 0) || (a.addedAt ?? 0) - (b.addedAt ?? 0));
}

function recordPoolSuccess(env, entry, chars) {
  entry.monthChars = (entry.monthChars ?? 0) + chars;
  entry.lastUsedAt = Date.now();
  entry.fails = 0;
  entry.cooldownUntil = 0;
  entry.lastError = null;
  const now = Date.now();
  if (now - (entry.flushedAt ?? 0) >= POOL_USAGE_FLUSH_MS) {
    entry.flushedAt = now;
    void persistPool(env, [entry]);
  }
}

async function markPoolCooling(env, entry, detail) {
  entry.fails = (entry.fails ?? 0) + 1;
  const hours = POOL_COOLDOWN_HOURS * Math.min(entry.fails, POOL_COOLDOWN_CAP);
  entry.cooldownUntil = Date.now() + hours * 3600 * 1000;
  entry.lastError = String(detail ?? '').slice(0, 200);
  await persistPool(env, [entry]);
}

/// 调一次彩云；结果分四类：ok / quota（401·403，额度用完或 token 失效）/ jitter（429·5xx）/ unreachable（网络）。
async function callCaiyun(token, payload) {
  let res;
  try {
    res = await fetch(CAIYUN_URL, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-authorization': `token ${token}`,
      },
      body: JSON.stringify(payload),
    });
  } catch {
    return { kind: 'unreachable' };
  }

  const text = await res.text();
  if (res.ok) {
    return { kind: 'ok', text };
  }

  if (res.status === 401 || res.status === 403) {
    return { kind: 'quota', detail: `caiyun ${res.status}: ${text.slice(0, 120)}` };
  }

  return { kind: 'jitter', detail: `caiyun ${res.status}: ${text.slice(0, 120)}` };
}

/// 池子用完时的手机提醒（可选；配了 SERVER3_PUSH_URL 才发，6 小时最多一条）。
async function notifyPoolEmpty(env, total) {
  if (!env.SERVER3_PUSH_URL) return;
  const now = Date.now();
  if (now - poolEmptyNotifiedAt < POOL_EMPTY_NOTIFY_MS) return;
  poolEmptyNotifiedAt = now;
  try {
    await fetch(env.SERVER3_PUSH_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        title: 'FireGaze 公共彩云额度池用完',
        desp: `池子里 ${total} 个 token 都不可用（冷却中）。加新 token：POST /translate/token（需要 ADMIN_KEY）。`,
      }),
    });
  } catch {
    // 提醒失败不影响请求
  }
}

// ── token 池管理端点（要机密 ADMIN_KEY；不配则管理面关闭）──

function poolAdminAuthorized(request, env) {
  const key = env.ADMIN_KEY;
  return typeof key === 'string' && key.length >= 8 && (request.headers.get('x-admin-key') ?? '') === key;
}

function poolRequestIp(request) {
  return request.headers.get('CF-Connecting-IP') ?? 'unknown';
}

/// 加 / 更新一个 token；同一个 token 重复加 = 重置冷却（充值后立刻复活）。
/// 注意：body 已由路由解析过（request 流只能读一次），这里用 data。
async function handlePoolToken(data, request, env, ip) {
  if (!env.ADMIN_KEY) {
    return json({ ok: false, error: 'admin disabled', detail: 'set ADMIN_KEY secret' }, 503);
  }

  if (!allow(ip, POOL_ADMIN_PER_IP_LIMIT, 'pooladm')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  if (!poolAdminAuthorized(request, env)) {
    return json({ ok: false, error: 'forbidden' }, 403);
  }

  const token = String(data.token ?? '').trim();
  if (token.length < 8 || token.length > 256) {
    return json({ ok: false, error: 'bad token' }, 400);
  }

  const store = env.LIBRARY_COUNTS;
  if (!store) {
    return json({ ok: false, error: 'no kv binding', detail: 'LIBRARY_COUNTS' }, 503);
  }

  const id = await poolTokenId(token);
  const existing = (await readPoolIndex(store)).find((e) => e.id === id);
  let record = existing ?? { addedAt: Date.now(), month: currentMonth(), monthChars: 0 };
  record.id = id;
  record.token = token;
  const label = String(data.label ?? '').trim().slice(0, 40);
  if (label) record.label = label;
  if (!record.label) record.label = `token-${id.slice(0, 4)}`;
  record.fails = 0;
  record.cooldownUntil = 0;
  record.lastError = null;
  await persistPool(env, [record]);

  // 直接进内存缓存（不依赖 KV 的最终一致；缓存未加载时不碰，下次读取会带上）
  poolEmptyNotifiedAt = 0;
  const now = Date.now();
  if (poolCache.at > 0 && now - poolCache.at < POOL_CACHE_MS) {
    const fresh = { ...record, id, flushedAt: now };
    const index = poolCache.entries.findIndex((e) => e.id === id);
    if (index >= 0) poolCache.entries[index] = fresh; else poolCache.entries.push(fresh);
  }

  return json({ ok: true, id, label: record.label, masked: maskToken(token), note: '下一次翻译请求就会用上' });
}

/// 移除一个 token（按 /translate/pool 里的 id；body 已由路由解析）。
async function handlePoolRemove(data, request, env, ip) {
  if (!env.ADMIN_KEY) {
    return json({ ok: false, error: 'admin disabled', detail: 'set ADMIN_KEY secret' }, 503);
  }

  if (!allow(ip, POOL_ADMIN_PER_IP_LIMIT, 'pooladm')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  if (!poolAdminAuthorized(request, env)) {
    return json({ ok: false, error: 'forbidden' }, 403);
  }

  const id = String(data.id ?? '').trim();
  if (!/^[0-9a-f]{12}$/.test(id)) {
    return json({ ok: false, error: 'bad id' }, 400);
  }

  const store = env.LIBRARY_COUNTS;
  if (!store) {
    return json({ ok: false, error: 'no kv binding', detail: 'LIBRARY_COUNTS' }, 503);
  }

  const tokens = (await readPoolIndex(store)).filter((e) => e && e.id !== id);
  await store.put(POOL_INDEX_KEY, JSON.stringify({ tokens, updatedAt: new Date().toISOString() }));
  poolCache = { at: 0, entries: [] };
  return json({ ok: true, removed: id });
}

/// 池子状态（token 一律打码，不返回明文）。
async function handlePoolStatus(request, env) {
  if (!env.ADMIN_KEY) {
    return json({ ok: false, error: 'admin disabled', detail: 'set ADMIN_KEY secret' }, 503);
  }

  const ip = poolRequestIp(request);
  if (!allow(ip, POOL_ADMIN_PER_IP_LIMIT, 'pooladm')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  if (!poolAdminAuthorized(request, env)) {
    return json({ ok: false, error: 'forbidden' }, 403);
  }

  const pool = await loadPool(env);
  const now = Date.now();
  const tokens = pool.map((entry) => ({
    id: entry.id,
    label: entry.label ?? '',
    status: entry.cooldownUntil > now ? 'cooling' : 'active',
    cooldownUntil: entry.cooldownUntil > now ? new Date(entry.cooldownUntil).toISOString() : null,
    month: entry.month ?? '',
    monthChars: entry.monthChars ?? 0,
    fails: entry.fails ?? 0,
    lastUsedAt: entry.lastUsedAt ? new Date(entry.lastUsedAt).toISOString() : null,
    lastError: entry.lastError ?? null,
    masked: maskToken(entry.token),
  }));
  return json({ ok: true, total: pool.length, active: tokens.filter((t) => t.status === 'active').length, tokens });
}

/// 公共彩云代理：插件把原文送过来，本 Worker 用服务端 token 池转发给彩云，原样回 target。
/// 池子里按「本月用量最少」挑，额度用完（401/403）自动换下一个；全池冷却时回 unavailable（插件端回退免费通道）。
async function handleTranslate(data, env, ip) {
  if (!allow(ip, TRANSLATE_PER_IP_LIMIT, 'translate')) {
    return json({ ok: false, error: 'too many requests' }, 429);
  }

  const source = Array.isArray(data.source) ? data.source.map((s) => String(s ?? '')) : [];
  if (source.length === 0 || source.length > TRANSLATE_MAX_ITEMS) {
    return json({ ok: false, error: 'bad source', detail: `1..${TRANSLATE_MAX_ITEMS}` }, 400);
  }

  const total = source.reduce((sum, s) => sum + s.length, 0);
  if (total > TRANSLATE_MAX_CHARS) {
    return json({ ok: false, error: 'too large', detail: `${total} chars` }, 413);
  }

  if (!chargeChars(ip, total)) {
    return json({ ok: false, error: 'daily-limit', detail: `over ${TRANSLATE_DAILY_CHARS} chars today` }, 429);
  }

  const transType = data.trans_type === 'zh2en' ? 'zh2en' : 'auto2zh';
  const payload = { source, trans_type: transType, detect: true, media: 'text', request_id: 'firegaze' };

  const pool = await loadPool(env);
  const candidates = usablePool(pool, Date.now()).slice(0, POOL_MAX_ATTEMPTS);
  if (candidates.length === 0) {
    if (pool.length === 0) {
      return json({ ok: false, error: 'unavailable', detail: 'service disabled' }, 503);
    }

    void notifyPoolEmpty(env, pool.length);
    return json({ ok: false, error: 'unavailable', detail: `pool exhausted (${pool.length})` }, 503);
  }

  let lastKind = 'quota';
  for (const entry of candidates) {
    let outcome = await callCaiyun(entry.token, payload);
    if (outcome.kind === 'jitter') {
      // 429/5xx：同一个 token 挣扎一次，再不行就换下一个
      await sleep(400);
      outcome = await callCaiyun(entry.token, payload);
    }

    if (outcome.kind === 'ok') {
      recordPoolSuccess(env, entry, total);
      return new Response(outcome.text, { status: 200, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
    }

    if (outcome.kind === 'unreachable') {
      return json({ ok: false, error: 'upstream-unreachable' }, 502);
    }

    if (outcome.kind === 'quota') {
      await markPoolCooling(env, entry, outcome.detail);
      lastKind = 'quota';
      continue;
    }

    lastKind = 'jitter';
  }

  if (lastKind === 'quota') {
    void notifyPoolEmpty(env, pool.length);
    return json({ ok: false, error: 'unavailable', detail: 'all tokens exhausted' }, 503);
  }

  return json({ ok: false, error: 'upstream', detail: 'caiyun error' }, 502);
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

// ── 准点唤醒译文工作流（Cloudflare Cron Trigger：0 0 * * 1,5）──
// 用 repository_dispatch 而不是 workflow_dispatch：前者只要 App 已具备的 Contents: R/W，
// 后者要额外开 Actions: R/W。
async function dispatchTranslate(env) {
  const repo = env.REPO || REPO_DEFAULT;
  const token = await githubToken(env, repo);
  if (!token) throw new Error('no server credentials');
  const res = await fetch(`https://api.github.com/repos/${repo}/dispatches`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'User-Agent': 'firegaze-relay',
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ event_type: 'translate-now' }),
  });
  if (!res.ok) throw new Error(`dispatch ${res.status}: ${(await res.text()).slice(0, 200)}`);
  return res.status; // 204
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === 'GET') {
      // 下载量读取（定时工作流用）
      if (url.pathname === '/library-counts') {
        return handleLibraryCounts(env);
      }

      if (url.pathname === '/plugin-stats') {
        return handlePluginStats(env);
      }

      if (url.pathname === '/translate/pool') {
        return handlePoolStatus(request, env);
      }

      return json({ ok: true, service: 'firegaze-relay', version: 12 });
    }

    if (request.method !== 'POST') {
      return json({ ok: false, error: 'POST only' }, 405);
    }

    const ip = request.headers.get('CF-Connecting-IP') ?? 'unknown';

    // ── 界面译文直传（正文就是投稿 JSON，不解析）──
    if (url.pathname === '/uit-submit') {
      return handleUitSubmit(request, env, ip);
    }

    let data;
    try {
      data = await request.json();
    } catch {
      return json({ ok: false, error: 'invalid json' }, 400);
    }

    // ── 公共彩云小译代理 ──
    if (url.pathname === '/translate' || url.pathname === '/v1/translate') {
      return handleTranslate(data, env, ip);
    }

    // ── 彩云 token 池管理（要 ADMIN_KEY；body 已由本函数解析）──
    if (url.pathname === '/translate/token') {
      return handlePoolToken(data, request, env, ip);
    }

    if (url.pathname === '/translate/token/remove') {
      return handlePoolRemove(data, request, env, ip);
    }

    // ── 公共库下载计数 / 点赞 ──
    if (url.pathname === '/library-download') {
      return handleLibraryDownload(data, env, ip);
    }

    if (url.pathname === '/library-like') {
      return handleLibraryLike(data, env, ip);
    }

    // ── 插件发现：点赞 / 加库推荐 / 库链投稿 ──
    if (url.pathname === '/plugin-like') {
      return handlePluginLike(data, env, ip);
    }

    if (url.pathname === '/plugin-add') {
      return handlePluginAdd(data, env, ip);
    }

    if (url.pathname === '/repo-submit') {
      return handleRepoSubmit(data, env, ip);
    }

    if (!allow(ip)) {
      return json({ ok: false, error: 'too many requests' }, 429);
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

    // 反馈可以单独指定一个仓库（用户 2026-10-07：反馈进私有仓，只有维护者能看）；不配就还是主仓
    const repo = isFeedback
      ? (env.FEEDBACK_REPO || env.REPO || REPO_DEFAULT)
      : (env.REPO || REPO_DEFAULT);

    let token;
    try {
      token = await githubToken(env, repo);
    } catch (e) {
      return json({ ok: false, error: 'server auth failed', detail: String(e).slice(0, 200) }, 500);
    }

    if (!token) {
      return json({ ok: false, error: 'server not configured' }, 500);
    }

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

  // Cloudflare Cron Trigger（0 0 * * 1,5 = 北京周一/周五 08:00）：准点唤醒译文工作流。
  // 失败只记日志：工作流里的 schedule 兜底，且 cron 下一轮还会再来。
  async scheduled(controller, env, ctx) {
    try {
      await dispatchTranslate(env);
      console.log(`translate dispatched (cron=${controller.cron})`);
    } catch (error) {
      console.error(`translate dispatch failed (cron=${controller.cron}): ${error}`);
    }
  },
};
