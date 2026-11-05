# 中文导航投稿模块部署合同

状态：本地实现与离线行为检查，不代表已部署。没有创建真实测试Issue或发送真实通知。

新入口为 `chinese-navigation-entry.mjs`；它只接管 `/chinese-navigation-submit`，其余请求与cron仍委托旧 `worker.js`。旧 Dashboard 单文件发布继续可用，但不会提供新的导航投稿业务。部署新功能必须上传模块入口及其依赖，不可只粘贴新入口一份文本。

## Cloudflare 配置

在现有部署配置上追加，保留所有已有KV绑定、变量、secrets、cron和路由；不能用以下片段覆盖整份配置。

```toml
main = "scripts/relay/chinese-navigation-entry.mjs"

[[durable_objects.bindings]]
name = "CHINESE_NAVIGATION"
class_name = "ChineseNavigationSubmission"

[[migrations]]
tag = "chinese-navigation-v1"
new_sqlite_classes = ["ChineseNavigationSubmission"]
```

- `NAVIGATION_REPO` 可选，默认 `REPO`，再默认 `DCritHitFireIV/FireGaze`。配置的目标仓库也必须与客户端允许的Issue仓库一致。
- `NAVIGATION_BOT_LOGIN` 必填：实际中继创建Issue的账户login（GitHub App通常以 `[bot]` 结尾）。丢失回执核对只接受此作者的精确标记。
- 沿用 `APP_ID`、`APP_PRIVATE_KEY`、可选 `INSTALLATION_ID`；兼容原有 `GITHUB_TOKEN`。需要目标仓库 Issues 读写权限。
- 缺少持久对象、明确作者或凭据时返回503 `navigation-unavailable`，不回退KV或内存去重。
- 发布前建立标签：`chinese-navigation`、`chinese-navigation-notify-pending`、`chinese-navigation-needs-attention`。创建Issue时带前两者。

## 通知工作流

将 `.github/workflows/chinese-navigation.yml` 和 `scripts/chinese_navigation_notify.py` 发布到目标Issue仓库默认分支。配置仓库变量 `NAVIGATION_BOT_LOGIN` 与Worker一致，沿用secret `SERVER3_PUSH_URL`。工作流以 `GITHUB_TOKEN` 写回持久状态评论。

Issue打开/加标签触发；每小时及手动运行扫描pending补漏。全局并发组串行发送。Server酱失败只保留并重试通知，不新建Issue。发送成功但保存sent回执前中断可能重复通知；不承诺严格一次。无secret或权限失败保留pending并记needs-attention。

## 客户端协议 schema=1

```json
{"schema":1,"type":"chinese-navigation","action":"propose","identity":{"internalName":"ExamplePlugin","sourceUrl":"https://example.org/index.json","projectUrl":"https://github.com/example/plugin","version":"1.2.3","apiLevel":15,"client":"CN"},"url":"https://github.com/example/plugin-cn","note":"可选说明","clientVersion":"1.5.0.217"}
```

`report-broken` 另需 `entryId` 和 `revision`。来源/项目网址未知可为空字符串，但不会由此获得“已核实”的结论。body最多16 KiB；URL最多2048字符，note最多2000字符，客户端/插件版本最多64字符。

200必须有 `accepted:true`、正确目标仓库的 `issueUrl` 与一致的 `issueNumber` 才算受理。重复投稿返回原Issue；文字备注不改变去重键，版本/来源/URL/失效报告修订改变则是新身份。原始受理时 `notificationState:pending`；读取重复回执时为 `unknown`，因为受理记录不冒充独立通知工作流的当前状态。

202 `outcome-unknown` 表示创建结果未知：持久记录及GitHub精确标记核对，绝不盲目再POST建Issue。搜索无结果仍是未知，不是“确认未创建”。维护者需要用诊断编号检查原Issue、权限/配置；不要删除持久对象或改投稿URL来“重试”，否则会丢失去重保证。

只有网址语法校验，不访问玩家网址，不运行网页内容、不下载插件、不更改中文分类或审核索引。HTTP/业务失败只返回固定阶段、受控原因、HTTP状态和诊断编号，不返回上游正文或凭据。

发布核对仅确认模块版本、DO绑定/migration、权限、labels、工作流/变量存在；未经授权不发公开测试Issue或试验通知。服务端部署与DLL重载分别记账。
