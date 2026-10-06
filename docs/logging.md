# FireGaze 诊断日志与反馈（2026-10-03）

规范来源：`~/.pi/agent/skills/logging-conventions/SKILL.md`（分级、脱敏、不得在渲染线程写 I/O、崩溃取证）。
本文是它在 FireGaze 里的落地说明。

## 组成

| 组件 | 位置 | 说明 |
|---|---|---|
| `ActivityLog` | `src/FireGaze/Diagnostics/ActivityLog.cs` | 内存环形缓冲（1500 条）+ 待写队列 + 后台每 2 秒落盘 |
| 日志文件 | `pluginConfigs/FireGaze/logs/firegaze-YYYYMMDD.log` | 保留最近 10 份；写盘失败静默降级 |
| 卫月日志镜像 | `dalamud.log` | Warning 及以上会同步一份，方便和其他插件对照 |
| 反馈入口 | 插件汉化 → 工具条最右「反馈…」 | 内容 + 可选诊断日志（脱敏）→ 中继 → 机器人建 issue |
| 中继 | `scripts/relay/worker.js` v3 | GitHub App 鉴权 + 反馈/投稿分流 + 垃圾预检 |
| 二次复核 | `.github/workflows/feedback.yml` + `scripts/feedback_triage.py` | issue 创建后再复核、打标、回话、通知 |

## 级别口径（单一含义，见 skill §2）

- **Trace** 逐条细节（默认不导出）
- **Debug** 阶段边界、计数、开关
- **Info** 用户可感知的动作与结果（默认导出的下限）
- **Warning** 已恢复/走了兜底（重载失败已自动还原、翻译部分失败…）
- **Error** 本次动作失败（还原失败、翻译失败、插件加载失败…）
- **Critical** 数据损坏/进程风险

## 已接入的埋点

- 启动：版本 + 配置目录
- 应用汉化 / 还原原文：开始、结果、失败原因（补丁管理器）
- 重载插件：失败时记完整异常链（Collections 这类 ctor 异常下次能拿到真因）
- 启用插件：成功/失败/已在运行
- 翻译：请求/成功/失败条数与失败样本（最多 10 条）
- 上传译文、反馈提交：结果与失败原因
- 一键汉化等任务收尾：成功 Info / 失败 Error
- 插件发现：统计拉取降级（Debug）、点赞/推荐上报失败（Warning）与补报成功（Info）、库链投稿结果（打回 Info / 失败 Warning / 受理 Info / 异常 Error）、本机状态文件读写失败（Warning + 状态行一次性提示）

## 反馈链路（不用个人 token）

```
插件「反馈…」(内容 + 可选日志，Snapshot 已脱敏)
   → POST https://firegaze-relay.yuoonmail.workers.dev/  {type:"feedback", title, body}
   → Worker：限流 + 长度/链接/垃圾词/重复行检查（不过关 422，不建 issue）
   → GitHub App 机器人身份建 issue
   → feedback.yml 二次复核：垃圾 → 关闭 + spam 标；正常 → feedback 标 + 回话 + Server酱通知
```

部署步骤见 `scripts/relay/README.md`（App 创建、Worker 变量、仓库 Action）。

## 注意

- **导出会脱敏**：`C:\Users\<名>` → `C:\Users\%USER%`，`ghp_/github_pat_/sk-` 与 Server酱 URL → `%SECRET%`。
- 本地日志文件保留原始路径（方便本机排障），**上传的只有脱敏后的 Snapshot**。
- 新增日志时先问：读者是谁、级别含义、会不会在渲染线程写 I/O（skill §0）。
