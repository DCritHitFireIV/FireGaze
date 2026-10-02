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

## 防滥用（现状）

- 只收 POST + JSON；正文上限 60KB
- 每 IP 每分钟 5 次（实例内存计数，近似限流）
- 反馈额外检查：长度、链接数、垃圾词、几乎只有链接、重复刷屏；**不过关不建 issue**
- 发布后 `scripts/feedback_triage.py`（`feedback.yml`）二次复核：垃圾 → 关 issue +`spam` 标；正常 → `feedback` 标 + 回话 +（可选）Server酱
- 被刷时的处理：吊销 App 私钥（重新生成）或换 PAT、重新部署换个 URL（旧客户端会回退到「打开 issue 页」）

## 国内可达性（重要）

`*.workers.dev` 在国内经常被墙。建议：

- 有域名的话，在 Workers 里绑定 **自定义域**（域名需托管在 Cloudflare），用自定义域填进插件
- 没有域名时先用 workers.dev 测；插件侧设计中继失败会**自动回退**到「打开 GitHub 提交页」流程，不会谎报提交成功
