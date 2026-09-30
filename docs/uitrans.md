# 插件内部文本汉化（UIText）

> 状态：**进行中**（抽取器 + 编辑器已完成；翻译通道、打补丁、配套库未接）
> 起点：2026-10-01。设计口径来自与用户的逐轮讨论（见会话记录），这里是落地版。

## 目标

把第三方插件**界面上的英文**翻成中文：用户自己挑插件 → 抽取 → 翻译/校对 → 打补丁生效；
满意的结果可以贡献进公共配套库（后面接）。

- 只翻**画在界面上的字符串**，命令名 / 日志 / 配置键 / 签名这类功能字符串一律不碰（判错的代价不对称）。
- **官方插件（Dip17）也翻**：它们一样是英文界面（本机 190 个插件里有 65 个官方），只是更新由官方安装器负责、
  补丁同样会被覆盖需要重打；列表里给官方插件标 `[官方]`。
- 打补丁采用**静态改写插件 DLL**（dnlib 改字符串字面量，重载后生效），不用任何钩子。
  卫月加载插件走 `LoadInMemory`（`FileShare.Read` + `LoadFromStream`），所以运行中也能改盘，重载/重启后生效。
- 不做全生态批量机器翻译（token 撑不住）；按需、人驱、可选择贡献。

## 代码位置

| 文件 | 作用 |
|---|---|
| `src/FireGaze/UIText/UIStringExtractor.cs` | IL 静态抽取：判 UI 候选 / 灰名单 / 排除 |
| `src/FireGaze/UIText/UICallSemantics.cs` | 调用语义：谁是 UI 调用、谁拿字符串当 ID、哪些是危险语境 |
| `src/FireGaze/UIText/UITextModels.cs` | 抽取结果模型（原文 / 上下文 / 判定 / 依据 / 是否保留 ID） |
| `src/FireGaze/UIText/UITextPack.cs` | 配套包模型（原文→译文、来源、不翻名单、库合并优先级） |
| `src/FireGaze/UIText/UITextStore.cs` | 本地包读写（`<配置目录>/uitrans/<内部名>.json`）+ 桌面工具 JSON 兼容 |
| `src/FireGaze/UI/UITextTab.cs` | 「插件汉化」页签：插件列表 + 包状态 + 打开编辑器 |
| `src/FireGaze/UI/UITextEditorWindow.cs` | 编辑器窗口：逐条翻译、筛选、导入导出 |
| `tools/UITextProbe/` | 离线探针（与插件同一份源码）：`--all` / `--json` / `--trace` / `--types` |

## 抽取器怎么判

1. 逐方法扫 IL，跟踪字面量去向（`ldstr` → 局部 / 参数 / 调用参数）；
2. 两条不动点：**UI 流**（哪条参数最终进 UI 调用）与**危险流**（哪条参数最终进比较/键名/日志等）；
3. 分类：
   - 只进 UI = **候选**（默认翻）
   - 两边都进 = **灰名单**（默认不翻，编辑器里显示原因）
   - 其余 = **排除**（附原因：功能语境 / 去向不明 / 没有可翻译内容 / 已是中文 / 十六进制签名）

UI 调用识别：类型名含 `ImGui`（`Dalamud.Bindings.ImGui.*` / 旧 `ImGuiNET` / ECommons.ImGuiMethods 等）、
再加对象初始化器里给人看的字段（`CommandInfo.HelpMessage`）。
字符串垫片（`ImU8String.op_Implicit`）按「字符串加工」透传，判定落在真正的 ImGui 调用上。

危险语境：日志 / 字符串比较 / 字典键 / 路径 / 正则 / JSON / 反射 / IPC / 命令名。

## 包格式（本地 / 将来的公共库同一份）

```json
{
  "_meta": { "format": 1, "updatedAt": "2026-10-01T12:00:00", "pluginVersion": "1.2.17", "source": "local" },
  "entries": [
    { "Original": "Show on decipher", "Translated": "解读时显示", "Context": "PluginUi.Draw", "Source": "user", "PreserveID": true }
  ],
  "skipped": ["base_search_popup"]
}
```

- `Source`：`user`（人工，最优先）/ `ai`（机器）/ `library`（公共库）/ 空（按 ai）。
- `PreserveID`：这个字面量被 ImGui 当控件 ID 用，打补丁要写 `译文###原文`（显示译文、ID 留在原文）。
- 合并口径：**人工译永不被机器/库顶掉**；库只能更新 ai / library 的条目。
- 桌面翻译工具（DalamudLocalizer）的 `[{Original, Translation, Context}]` 可以直接导入导出。

## 界面

- 主窗口新增「插件汉化」页签（第 4 个）：插件列表 + 本地包条数 + 「开始汉化 / 打开编辑器」。
- 编辑器窗口：筛选（候选 / 灰名单 / 未翻 / 已翻 / 不翻 / 全部）+ 搜索 + 四列（状态 / 上下文 / 原文 / 译文），
  译文可逐条改；右键：标记不翻、清除译文、复制原文；工具栏：重新抽取 / 导出未翻（给 AI）/ 导入译文 / 打开包目录 / 保存。
- 编辑 1.5 秒后自动保存，关窗也保存。

## 验证

- `fgtest`（`C:\Users\Fire\fgtest-refactor`）：抽取器 + 包读写/合并/导入导出断言（20 组）。
- 基准样例：**Globetrotter 1.2.17** —— 抽取器判出 11 条候选，与桌面工具人工勾选结果逐条一致，功能串 0 误入。
- 加壳插件（OmniToolbox / XSZToolbox / PFRadar / OCNFarmer 等）读得动、抽不出串、不崩。

## 还没做（按计划）

1. 翻译通道：免 key 免费接口（有代理走 Google 端点，否则 MyMemory）+ 用户自填 key（DeepSeek / OpenAI 兼容 / DeepL，DPAPI 存本机）；
2. 打补丁：dnlib 改字面量 + 备份 + 重载生效 + 一键还原 + 加载失败/崩溃自动回滚 + 更新后自动重打；
3. 配套库：relay（Cloudflare Worker）匿名投稿 + 撤回（私有 KV 存一次性凭据）+ 库下载；
4. HCI 评审（改界面前先出还原图，按仓库既有流程）。
