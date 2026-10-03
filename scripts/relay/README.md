# FireGaze 投稿/反馈中继（Cloudflare Worker）

把「一键提交译文」和「反馈…」都做成真正的一步：插件 **POST** → Worker 校验/限流/垃圾检测 →
用**服务端凭据**在仓库里建 issue → GitHub Actions（`inbox.yml` / `feedback.yml`）负责存档、回评、通知维护者手机。

- **客户端不持有任何凭据**（历史上密钥进过 DLL，现已全部移除）
- 玩家**不需要 GitHub 账号**、不需要开浏览器
- 反馈的垃圾内容在**发布前**就会被 Worker 规则拦下；发布后 `feedback.yml` 再复核一遍

## 鉴权：用 GitHub App（推荐，机器人身份）或 PAT

### 方案 ①：GitHub App（2026-10-03 起推荐——不使用任何个人 token）

1. GitHub → **Settings → Developer settings → GitHub Apps → New GitHub App**
   - Homepage 随意；**Webhook 取消勾选**（不需要）
   - Permissions → Repository permissions → **Issues: Read and write**（其余全部 No access）
   - Where can this app be installed? → **Only on this account**
2. 创建后：
   - 记下 **App ID**（General 页）
   - **Generate a private key** → 下载 `.pem`（PKCS#8）
   - **Install App** → 选 `DCritHitFireIV/FireGaze` → 记下 **Installation ID**（安装后的 URL 里 `installations/<id>`）
3. Cloudflare Worker → Settings → Variables and Secrets：
   - 变量：`APP_ID`、`INSTALLATION_ID`（可选，不填则每次按仓库查）、`REPO`
   - 机密：`APP_PRIVATE_KEY` = 整个 `.pem` 内容（含 `-----BEGIN/END-----`）
     · ⚠️ GitHub 下载的私钥是 **PKCS#1**（`BEGIN RSA PRIVATE KEY`），而 Workers 的 WebCrypto 只认 **PKCS#8**（`BEGIN PRIVATE KEY`）——粘贴前先转换，否则 worker 会回 `server auth failed`：
       `openssl pkcs8 -topk8 -inform PEM -outform PEM -nocrypt -in 原始.pem -out 转换后.pem`（值里的空格/换行代码会自动清理，换行符 CRLF/LF 都不影响）

这样 issue 的作者是 `<app 名>[bot]`，轮换/离职都不涉及个人账号。

### 方案 ②：fine-grained PAT（旧部署兼容）

- `GITHUB_TOKEN`（机密）：仅 `DCritHitFireIV/FireGaze`、权限 **Issues: Read and write**
- 泄露时去 GitHub 撤销重发即可；不建议长期使用

> Worker 优先用 App；没有 `APP_ID` 时才回退 `GITHUB_TOKEN`。

## 请求 / 响应

### 译文投稿（原有）

```
POST /
{"title": "翻译贡献 2026-09-23 12:00（3 条）",
 "body": "### FireGaze 翻译贡献\n\n...\n```json\n{...}\n```"}
```

### 用户反馈（2026-10-03 新增）

```
POST /
{"type": "feedback",
 "title": "[反馈] 一键汉化后按钮点不动（48 字摘要）",
 "body": "### FireGaze 反馈\n\n- 分类：问题\n- 版本：FireGaze 1.3.7 · 卫月 …\n\n### 内容\n…\n\n<details>…脱敏日志…</details>"}
```

```
200 {"ok":true,"issue":"https://github.com/.../issues/135","number":135}
422 {"ok":false,"error":"spam-blocked","detail":"too many links"}
```

### 公共彩云小译代理（2026-10-03 新增）

插件「翻译设置」里的「FireGaze 公共彩云」通道走这里：**彩云 token 只存服务端**（Cloudflare 机密 `CAIYUN_TOKEN`），
插件不持有密钥，用户抓不到。额度用完 / 密钥失效 / 未配置时回 `unavailable`，插件端自动回退到免费通道。

```
POST /translate
{"source": ["Enable the plugin", "Open settings"], "trans_type": "auto2zh"}
```

```
200 彩云原样返回：{"rc":0, "target": ["启用插件", "打开设置"]}
503 {"ok":false, "error":"unavailable", "detail":"caiyun 401"}    # 额度用完 / 密钥失效 / 未配置 → 插件回退免费通道
502 {"ok":false, "error":"upstream", "detail":"caiyun 500"}       # 彩云抖动 / 网关错误
429 {"ok":false, "error":"too many requests"}                       # 每 IP 每分钟 120 次
429 {"ok":false, "error":"daily-limit"}                             # 这台机器今天超过 40 万字符（实例内存计数，近似）
```

- 单次最多 `50` 条 / `20000` 字符（与插件端分批一致）；每 IP 每分钟 120 次、每天 40 万字符——服务端再卡两道，保护额度
- 关掉这个通道不用改插件：Cloudflare 里删掉 / 改名 `CAIYUN_TOKEN` 即可（端点随即回 `unavailable`）

## 防滥用（现状）

- 只收 POST + JSON；正文上限 60KB
- 每 IP 每分钟 5 次投稿/反馈（实例内存计数，近似限流）；`/translate` 单独计，每分钟 120 次 + 每天 40 万字符
- 反馈额外检查：长度、链接数、垃圾词、几乎只有链接、重复刷屏；**不过关不建 issue**
- 发布后 `scripts/feedback_triage.py`（`feedback.yml`）二次复核：垃圾 → 关 issue +`spam` 标；正常 → `feedback` 标 + 回话 +（可选）Server酱
- 被刷时的处理：吊销 App 私钥（重新生成）或换 PAT、重新部署换个 URL（旧客户端会回退到「打开 issue 页」）

## 国内可达性（重要）

`*.workers.dev` 在国内经常被墙。建议：

- 有域名的话，在 Workers 里绑定 **自定义域**（域名需托管在 Cloudflare），用自定义域填进插件
- 没有域名时先用 workers.dev 测；插件侧设计中继失败会**自动回退**到「打开 GitHub 提交页」流程，不会谎报提交成功
