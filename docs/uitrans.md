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
| `src/FireGaze/UIText/TranslationChannels.cs` | 翻译通道：Google 免 key / MyMemory / 大模型 / DeepL / 彩云小译 |
| `src/FireGaze/UIText/FFXIVGlossary.cs` | 随插件打包的「英文 → 国服官方中文」术语表（只喂大模型通道，可关）；数据由 `scripts/ffxiv_glossary.py` 生成 |
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

## 界面（2026-10-01 两路盲评后的 v2；页签名与顺序按用户 2026-10-01 决定：插件汉化排第一）

- 主窗口第 1 个页签「**插件汉化**」（顺序：插件汉化 → 简介汉化 → 仓库体检 → 插件安装器；
  「参与翻译」是独立窗口。这里只管插件自己的窗口文字，插件简介在「简介汉化」页）：
  一行一个插件 = 图标 + 标题 + 一行简介 + 状态徽标（行首、固定 x，方便竖扫）+ 「一键汉化」；
  点开一行看详情（作者介绍 + 统计）+ 「抽取界面文本 / 重新抽取」（只列候选、不翻译不写盘）+「编辑校对…」；工具栏 = 刷新 / 搜索 / 状态筛选 / 只看第三方 / 翻译设置…；
  有任务时窗口顶部常驻进度条（滚到哪都看得见）。
- **一键汉化** = 抽取 → 翻译没翻的条目 → 写入插件 DLL → **自动重载插件**，一次点完；
  取消只在翻译阶段可用（已翻的条目会保留），写入阶段写明「此步不能取消」。失败给下一步：重试 / 打开翻译设置。
- **翻译设置**独立窗口（页签工具栏进入），不在翻译流程里：通道、key（DPAPI）、灰名单口径、插件更新后自动重打。
  通道顺序：免费·自动 / **彩云小译** / Google 免 key / MyMemory / 大模型（自填 key）/ DeepL。
  彩云小译是推荐的免费路径（`POST api.interpreter.caiyunai.com/v1/translator`，`X-Authorization: token <token>`，
  **一次最多 50 条**——实测 52 条就 HTTP 413；单条 0.5s、50 条 0.8s；新号送 100 万字 / 一个月）。
- 编辑器窗口（编辑校对）：筛选带计数 + 状态列图例；工具栏 = 翻译未翻 (N) / 应用汉化 / 还原原文 / 更多…
  （重新抽取、导出未翻、导入译文、打开包目录）；应用与还原都自动重载，不再有「重载生效」这一步。
- 编辑 1.5 秒后自动保存，关窗也保存。
- 盲评报告：`docs/hci/review-2026-10-01-uitext-redesign-{heuristic,friction,visual}.md`（第一轮）与 `...-{heuristic,friction,visual}-recheck.md`（v2 定向复评：0 个新增 P1）。
  v2 之后落进实现的复评条目：成功提示 12 秒后自动收起（失败 / 未加载提示常驻）；没有要翻的条目时**跳过写入与重载**；
  同一时刻只有一个写文件 / 写包的任务（`UITextRunLock`，列表与编辑器互相禁用）；行内不再另放「重试」（统一用「一键汉化」）；
  筛选器有「状态」标签；主按钮在「还没汉化好」的行上给强调色；写失败按实际原因显示（备份失败 / 打补丁失败都会带原因）。

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
4. **资源型本地化（.resx / ResourceManager）的插件**：界面文字放在 `Resources` 里、代码只用 key 查表（27 个插件、14,479 key，半数自带官方 zh）。
   资源级打补丁子代理 PoC 已证技术可行（改嵌入资源 + 卫星程序集），但工程量大，**先按「暂不支持」处理**：
   抽取 / 一键汉化的结果行会提示「还有 N 处界面文字放在本地化资源文件里，暂不支持汉化」；
   这些 key 本身按 hardKey 排除（翻了查不到资源）。盘点报告：`docs/top100-2026-10-02/sub-localization.md`。
5. **属性字符串（SimpleTweaks 的名字/描述）**：它们不在 `ldstr` 里（在 CustomAttribute 参数里），抽不到也补不了。
   实测 SimpleTweaks 1.15.0.7：官方 zh-CN 只覆盖 122/181 个名字、约 136/181 个描述，其余会显示英文。
   两条路待定：
   · **A** 写进它自己的 `pluginConfigs/SimpleTweaksPlugin/loc/zh-CN/strings.json`——**会被官方更新覆盖**
     （改语言 / 点更新会重新下载 strings.json），不推荐；
   · **B** 扩展抽取器 + 补丁器去改**属性字符串**（fallback 语义：官方有译文时官方优先，没有才显示我们的），待定。

## 血教训：只有「标签」才当 ID 用（2026-10-01）

- ImGui 里**只有第 0 个字符串参数是控件标签**（拿它算 ID），后面的字符串参数都是「只显示的」：
  `InputTextWithHint` 的 hint（输入框灰字）、`BeginCombo` 的 preview、`MenuItem` 的快捷键、`InputFloat` 的 format……
  这些字符串的**绘制路径不处理 `##`**，给它们加 `###原文` 会原样显示出来。
  实测：FriendlyFire 的输入框灰字变成「角色名称（例如苹果汽水）###Character Name (e.g., Apple Soda)」。
  修法：`UICallSemantics.UsesStringAsIDForArgument`（抽取器按参数位置决定要不要保 ID）。
- **纯 ID 标签（`##xxx` 开头）不进候选**：界面上一个像素都不显示，翻了只会把 ID 弄坏。
- `UITextFlow.MergeExtraction` 现在会返回 `ReapplyNeeded`（PreserveID 被改对 / 新增条目）——
  「已是最新」的跳过判断必须带上它，否则**修好的写法永远打不上去**。

## 血教训：免费通道的 429（2026-10-01）

- 用户实测「Google/MyMemory 一条要 10 秒」：根因是我们把 **429 当可重试错误**，原地重试 3 次（等 2s → 5s）再换通道；
  而 Google 免 key 端点走代理是**立刻 429**（额度/频率被卡），重试纯属白等。
  现在：**429 直接换下一条通道，并把这条通道冷却 30 分钟**（不用等连续 5 次）；5xx 才退避重试。
- 另一条路是彩云小译：**批量**提交（≤50 条/请求），几百条几秒，比逐条通道快两个数量级。

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

## 血教训：插值与三元表达式的漏抽（2026-10-02）

- **字符串插值**（`$"..."`）编译成 `DefaultInterpolatedStringHandler`：`AppendLiteral / AppendFormatted` 把碎片
  攼进处理器实例，`ToStringAndClear` 才交给最终消费者。以前不认这个模式，凡是插值拼出来的界面文案
  整条都记成「去向不明：DefaultInterpolatedStringHandler.AppendLiteral」（全量 260 个插件里 9k+ 条）。
  修法：`IsStringProducer` 收下 `*InterpolatedStringHandler` 的穿插/取出方法；`ToString` 系列的非 void 调用
  把**实例自身**一起并进结果（写回靠结构体的 `ldloca` + `StoreLocal` 合并）。实测 Accountant 的
  `'Last Visit: '` 等变成候选，260 个插件界面候选 +2.1k。
- **三元 / if 分支**（`cond ? "A" : "B"`）：线性模拟两条路径都跑，栈深夹紧时直接把「多出来的」值丢掉——
  被丢掉恰是另一条分支的取值。实测 AetherDraw 的 `Plan Name (Optional) / Search Keywords (Required)`
  就丢在这里。修法：夹掉的值进 `carried`，并入下一条指令推上来的值（通常正是同一栈槽的另一分支）。
- 探针：`--trace <方法名>` 可以只看一个方法的逐条 IL；stdout 强制 UTF-8（否则批量落盘 JSON 按系统码页写，不是合法 UTF-8）。

## 血教训：容器 / 集合 / 返回值的另一批漏抽（2026-10-02，top-100 专项）

- **集合元素流**：`List/Dictionary/HashSet.Add("显示文本")` 以前一律当「功能语境」，但列表往往就是给用户看的
  （更新日志、选项、提示）。现在：元素并进接收者（局部变量 / 字段），集合被遍历（GetEnumerator / get_Current / ToArray 当字符串加工）
  才能把它带到 UI；字典 / set_Item 的第 0 参仍是 hardKey。
- **`Stelem` 写回**：数组初始化 `dup; ldstr; stelem` 以前会多压一份数组（+1 漂移），
  3 元素 Combo 只抽到第一个（FrenRider `Icon+Text` / `Icon Only`）。改成把合并后的值写回栈上的 dup 副本。
- **简单字段 getter（`get_Main => _main`）**：对象初始化器 `Main = { "…" }` 靠它把元素并回自动属性字段；
  不能让它把 FieldKey 带到所有 getter 调用结果上（会把字段拉进危险语境，AnoMech 场景名一度变灰），
  只在「集合塞元素的接收者」处用 CallKeys 解析回字段。
- **资源查表 key**：`ResourceManager.GetString("Key")` 的参数是 key（翻了查不到、界面空）。
  以前强类型资源类（`UIStrings.get_X`）的 key 会跟着返回值流到 UI 被当成界面文本（全量 5k+ 条）——现已按 hardKey 处理。
- **日志不污染**：日志文本翻译无害，不该把同一字面量拖进灰名单（FrenRider 一批就是这么变灰的）；
  `StringBuilder.Append` 同理不再是危险语境。
- **泛型键归一化**：调用点的类型名带泛型实参（`ConfigRef`1<MovementStrategy>`）、方法定义是开放泛型（`ConfigRef`1`），
  不归一化会让整个方法变「去向不明」（BossMod AddOption 803 条）。`MethodKey` 现在先 `StripGenericArgs`。
- **插件专用适配（top-100）**：BossMod `TextHints/GlobalHints.Add`（提示文本，→ DrawPlayerHints）与
  `ConfigRef`1.AddOption` 第 1 参（选项显示名）直接标 UI；`AddOption` 第 8 参 / `DefineRef.As` 第 0 参
  （预设 JSON 的键）硬键排除；资源型本地化（.resx）暂不支持改值（报告：`top100/analysis/sub-localization.md`）。

## 术语表的数据来源（2026-10-02 改正）

- **不能从游戏数据读**：① 国服客户端的 exd 只有中文（`PlaceName.exh` 的 `Languages=[ChineseSimplified]`，压根没装英文原文）；
  ② 这版 Lumina 会把请求语言覆盖成默认语言（`ExcelModule.GetRawSheetCore` 里 `language = Language;`），
  所以 `GetExcelSheet<T>(ClientLanguage.English)` 拿到的其实是当前语言那份——国际服也组不出英文→中文。两种客户端实测都不行。
- 现在的数据源 = `scripts/ffxiv_glossary.py`：xivapi 的英文 datamining CSV + thewakingsands 的国服 CSV 按行 key 对齐，
  筛成匹配器能吃的形态（词只含 `[A-Za-z0-9'’\-.]`、≤4 个词、去掉首尾标点、值单行化），生成 `ffxiv-glossary.tsv`
  （31,701 条，约 0.9 MB，包内压缩后 ~433 KB）随插件打包；`translate.yml` 每周重建，产物确定（不写 mtime、不经有版本差异的压缩器）。
- 为什么打包而不是运行时下载：与 translations.json 同一套（随插件更新）；运行时零网络依赖，也不去读玩家的客户端。

## 血教训：二级窗口被 ini 记住折叠，看起来像「点不开」（2026-10-02）

- 症状：用户点页签工具栏的「翻译设置…」**看起来没反应**。窗口其实开了，但它在 `dalamudUI.ini` 里被记成
  `Collapsed=1`，画出来只剩一条标题栏；保存的位置又贴着屏幕边缘，更像什么都没发生。
- 修法：三个二级窗口（翻译设置 / 文本编辑器 / 参与翻译）在 `OnOpen()` 里
  `ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always)`——只在打开那一刻强制展开，之后用户想折叠仍可折叠。
- 依据（反编译本机 Dalamud `WindowHost.DrawInternal`）：开窗那帧先调 `Window.OnOpen()`，再 `PreDraw`/`ApplyConditionals`，
  最后才 `ImGui.Begin`；我们只在自己 `OnOpen` 里发一次「展开」，不与卫月自身的窗口条件打架。
- 教训：卫月 `Window` 的 `IsOpen=true` 不等于**看得见**。凡是从按钮打开二级窗口的功能，都要防「被 ini 记住的折叠/异位」。

## 血教训：深度哨兵不能是 -1（2026-10-01，I-Ching-GL / pvpauto 扫描挂死）

- 现象：批量扫描里这两个加壳插件**每次超过 90 秒**只能掐掉（`超时 90 秒没跑完（已抽到一半）`）；
  单独探针跑 28 条指令的方法要 28 秒。根因：`ComputeDepths` 用 `-1` 当「还没算出」的哨兵，
  但 `ret` 这类指令在 void 方法上会合法地算出 `-1`（`depth 0 - pops 1`），与哨兵同值 → 工作队列反复重入。
- 修法：哨兵改 `int.MinValue`（`UnknownDepth`）、深度下限夹到 0、`ret` 按方法返回类型决定弹几个（void 弹 0）、
  再加 `iterations > 指令数 × 4` 的兜底。修后同一批 191 个插件 **失败 0 个**，两个卡死插件各 ~2.6 秒。
- 教训：**「值域里可能出现的数」不能当哨兵**；这类挂起不会崩，只会让用户的一键汉化永远转圈。

## 血教训：Equals / GetHashCode 的宽严（2026-10-01 独立评审 P1-1）

- 修 record 文本时把 `Equals` / `GetHashCode` 从危险名单里摘了，副作用是「被比较的字符串」失去灰名单保护：
  `mode.Equals("Auto")` 的 `"Auto"` 若同时也画在界面上，翻译后比较恒不相等 → 插件设置/分支静默失效。
- 现在两者回名单，只对 `EqualityComparer` 窄豁免（record 自动生成的比较/哈希代码走的就是
  `EqualityComparer<T>.Default.*`，那是机械比较）。fgtest 钉住：`String.Equals` 算危险、`EqualityComparer` 不算。
- 术语表加载失败也不再谎报「已就绪 0 条」：失败状态独立（`Failed` / `FailureReason`），设置页显示「加载失败 · 重试」。
- 独立评审报告全文（含 P1-3 待办与三个实机确认点）：`docs/hci/review-2026-10-01-ui-extractor-independent.md`。
