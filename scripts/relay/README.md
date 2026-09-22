# FireGaze 投稿中继（Cloudflare Worker）

把「一键提交」恢复成真正的一步：插件 **POST 投稿** → Worker 用**服务端令牌**在仓库里建 issue → 现有 `inbox.yml` 工作流照常存档、回评、用 secret 通知维护者手机。

- **客户端不持有任何密钥**（上次泄密事件的根因就是密钥进了 DLL）
- Worker 只做三件事：校验、限流、建 issue；其余仍然走现有工作流
- 玩家**不需要 GitHub 账号**、不需要开浏览器

## 部署（网页版，不用命令行）

1. Cloudflare Dashboard → **Workers & Pages** → Create → **Create Worker**
2. 名字填 `firegaze-relay` → Deploy（先用默认 Hello World 代码）
3. **Edit code** → 把 `worker.js` 全文粘贴进去 → Deploy
4. Settings → **Variables and Secrets**：
   - 加一个 **Secret**：`GITHUB_TOKEN` = 下面那个 fine-grained PAT
   - （可选）加一个 Variable：`REPO` = `DCritHitFireIV/FireGaze`
5. 记下地址：`https://firegaze-relay.<你的子域>.workers.dev` —— 填进插件的 `RelayURL` 常量

## fine-grained PAT（只给这一个仓库、只给 issues 权限）

GitHub → Settings → Developer settings → **Fine-grained tokens** → Generate new token：

- Repository access：**Only select repositories** → `DCritHitFireIV/FireGaze`
- Permissions → Repository permissions → **Issues: Read and write**（其余全部 No access）
- Expiration：按需（到期后重发一个，更新 Worker 变量即可）

## 请求 / 响应

```
POST /
Content-Type: application/json
{"title": "翻译贡献 2026-09-23 12:00（3 条）",
 "body": "### FireGaze 翻译贡献\n\n- 条数：3\n\n```json\n{...}\n```"}
```

```
200 {"ok":true,"issue":"https://github.com/DCritHitFireIV/FireGaze/issues/135","number":135}
```

## 防滥用（现状 + 可加强）

- 只收 POST + JSON；正文上限 60KB；必须带 `json` 代码块与 `contributions` 字段
- 每 IP 每分钟 5 次（Worker 实例内存计数，近似限流）
- 被刷时的处理：换 PAT、重新部署换个 URL（旧客户端会回退到「打开 issue 页」的老流程）
- 以后要更强：加 Cloudflare KV 做全局限流（需要建一个 KV 命名空间并绑定）

## 国内可达性（重要）

`*.workers.dev` 在国内经常被墙。建议：

- 有域名的话，在 Workers 里绑定 **自定义域**（域名需托管在 Cloudflare），用自定义域填进插件
- 没有域名时先用 workers.dev 测；插件侧设计中继失败会**自动回退**到原来的「打开 GitHub 提交页」流程，不会谎报提交成功
