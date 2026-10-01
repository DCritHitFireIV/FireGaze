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
| `src/FireGaze/UI/UITextEditorWindow.cs` | 编辑器窗口：逐条翻译、筛选、导入导出、打补丁/还原/重载 |
| `src/FireGaze/UIText/UITextPatcher.cs` | 打补丁：dnlib 改写 `ldstr` 字面量（只动字符串，不动代码结构） |
| `src/FireGaze/UIText/UITextPatchStore.cs` | 补丁状态 + 原始 DLL 备份（都在配置目录里） |
| `src/FireGaze/UIText/UITextPatchManager.cs` | 打补丁/还原/重载/更新后重打的调度与安全网 |
| `src/FireGaze/UIText/TranslationChannels.cs` | 翻译通道：Google 免 key / MyMemory / 大模型 / DeepL |
| `src/FireGaze/UIText/DPAPI.cs` | 用户 API key 的本机加密存储 |
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

## 界面（2026-10-01 两路盲评后的 v2）

- 主窗口页签「**界面汉化**」（与「简介汉化」区分：这里只管插件自己的窗口文字）：
  一行一个插件 = 图标 + 标题 + 一行简介 + 状态徽标（行首、固定 x，方便竖扫）+ 「一键汉化」；
  点开一行看详情（作者介绍 + 统计）与「编辑校对…」；工具栏 = 刷新 / 搜索 / 状态筛选 / 只看第三方 / 翻译设置…；
  有任务时窗口顶部常驻进度条（滚到哪都看得见）。
- **一键汉化** = 抽取 → 翻译没翻的条目 → 写入插件 DLL → **自动重载插件**，一次点完；
  取消只在翻译阶段可用（已翻的条目会保留），写入阶段写明「此步不能取消」。失败给下一步：重试 / 打开翻译设置。
- **翻译设置**独立窗口（页签工具栏进入），不在翻译流程里：通道、key（DPAPI）、灰名单口径、插件更新后自动重打。
- 编辑器窗口（编辑校对）：筛选带计数 + 状态列图例；工具栏 = 翻译未翻 (N) / 应用汉化 / 还原原文 / 更多…
  （重新抽取、导出未翻、导入译文、打开包目录）；应用与还原都自动重载，不再有「重载生效」这一步。
- 编辑 1.5 秒后自动保存，关窗也保存。
- 盲评报告：`docs/hci/review-2026-10-01-uitext-redesign-{heuristic,friction,visual}.md`（含定向复评）。

## 验证

- `fgtest`（`C:\Users\Fire\fgtest-refactor`）：抽取器 + 包读写/合并/导入导出 + 打补丁 + 清账/本地化 key 防护断言（26 组）。
  另外 `--sim-package <内部名>` 可对真实包跑一遍「从备份抽取 + 清账」的模拟，只改内存不写盘。
- 基准样例：**Globetrotter 1.2.17** —— 抽取器判出 11 条候选，与桌面工具人工勾选结果逐条一致，功能串 0 误入。
- 加壳插件（OmniToolbox / XSZToolbox / PFRadar / OCNFarmer 等）读得动、抽不出串、不崩。

## 重新抽取与清账（两个血教训，2026-10-01）

- **抽取永远对原始 DLL 做**：盘上是我们自己的补丁时（`PatchedHash` 对得上），编辑器自动改读**原始备份**。
  否则抽到的是补丁后的 `译文###原文`，会把包里好好的条目当成「原文没了」整批清掉。
- **清账是双向的**：
  · 又变回候选的条目——只恢复**系统自己排过的**（带 `自动排除：` 备注），用户手动标的「不翻」绝不碰；
  · 没有译文、也没有来源的**空壳条目**直接删掉（多半是抽错产生的噪声）；
  · 用户手动标记「不翻」时要清掉自动备注，从此归用户管。

## 打补丁的安全网（顺序即优先级）

1. **先备份**：原始 DLL 复制到 `<配置目录>/uitrans/backups/<内部名>-<哈希前12位>.dll`，插件目录只留最终 DLL；
2. **先写临时文件再替换**：`<dll>.fguitext.tmp` → `File.Move(overwrite)`，不会留半截 DLL；
3. **待确认标记**：打完写 `state/<内部名>.json`（PendingVerify），重载后插件加载失败 → **自动还原**；
4. **崩溃守门**：重载后 15 秒内插件活着 → 转正（本会话刚打上、又还没重载过的补丁不算「已验证」，
   等重载或下次启动再看）；游戏如果在这之前崩了/被强杀，下次启动会按「可疑崩溃」自动还原；
5. **更新重打**：盘上 DLL 的 SHA-256 与打补丁时不一致（插件更新换目录了）→ 自动重打（`UITextAutoRepatch` 默认开），
   重打后仍需用户点「重载生效」。

口径：
- 只改**有译文、且没被标「不翻」**的条目，按文本内容匹配、不绑版本号；
- 被当控件 ID 用的字面量写成 `译文###原文`；原文自带 `###` 时只替换显示段；
- 命令名 / 日志这类不在包里的字面量**一个字节都不动**（离线回归测试盯着）。

## 还没做（按计划）

1. 配套库：relay（Cloudflare Worker）匿名投稿 + 撤回（私有 KV 存一次性凭据）+ 库下载；
2. 界面 HCI 评审（先出还原图，按仓库既有流程）；
3. 批量翻译的单位与限流（免费接口有每日额度；大插件建议用自填 key）。
4. **属性字符串（SimpleTweaks 的名字/描述）**：它们不在 `ldstr` 里（在 CustomAttribute 参数里），抽不到也补不了。
   实测 SimpleTweaks 1.15.0.7：官方 zh-CN 只覆盖 122/181 个名字、约 136/181 个描述，其余会显示英文。
   两条路待定：
   · **A** 写进它自己的 `pluginConfigs/SimpleTweaksPlugin/loc/zh-CN/strings.json`——**会被官方更新覆盖**
     （改语言 / 点更新会重新下载 strings.json），不推荐；
   · **B** 扩展抽取器 + 补丁器去改**属性字符串**（fallback 语义：官方有译文时官方优先，没有才显示我们的），待定。

## 血教训：抽取器的栈模型（2026-10-01）

- **属性赋值 / 对象初始化器的 `set_*` 是 void，不能往栈上留返回值**——多留一个会让对象初始化器后面的调用参数整体错位。
  实测：`Loc.Localize` 里 `_localizationStrings[key] = new LocalizedString {...}` 把 `fallbackValue` 顶进了 key 参数槽，
  于是 2 参 `LocString(key, fallback)` 的 **fallback**（真正要显示的文本）被当成键名整条排除。
- **所有返回 void 的调用都不往栈上留值**（不只 set_*）。留幻影值会让后一个调用的参数整体错位；
  配套地，**`ldloca` / `ldarga` 要带着「这是谁的地址」**：
  struct 的 mutating 调用（`ImU8String.AppendLiteral`、`DefaultInterpolatedStringHandler.AppendLiteral`）只有写回局部变量，
  后面的 `ldloc` 才能把字面量带给真正的 UI 调用。
- **`ImU8String` 不是 UI 调用**：它是 Dalamud 新版绑定里的 UTF-8 字符串构造器。
  「要不要保留 `###原文`」必须在**接住它的那个 UI 调用**上判（`Button` 要、`TextColored` 不要）——
  2026-10-01 用户实测：Orbwalker 的段落标题 `Movement` 经 `AppendLiteral` 后在 `TextColored` 里画出，
  却在构造器上被误判成「用 ID」，补丁写成 `移动###Movement`。
- **`initobj` / `stobj` / `cpobj` 也要跟栈对齐**：`default(Vector2)` 会产生 `ldloca + initobj`，
  不弹地址的话后一个调用的参数会错位（靠幻影值刚好碰对是巧合）。
- **同一个字面量既当控件标签又当普通文字 → 灰名单**（加 `###原文` 会在文字处漏出后缀，不加可能撞控件 ID，让用户自己定）。
- 集合/字典的键名调用只把**第一个参数**当键（key 永远是第一个参数），后面的参数不能跟着算键。
- 排查入口：探针 `--trace` 会打 `[danger] scan=<调用方> <目标> key=… params=… text=…` 与 `[keyparam] <方法> param=N`。
