# 插件内部文本汉化（UIText）

> 状态：**进行中**（抽取 / 编辑 / 翻译 / 打补丁 / 按需译文库都已接；投稿通道与 HCI 复盘未接）
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
| `src/FireGaze/UIText/UIStringExtractor.cs` | IL 静态抽取：判 UI 候选 / 灰名单 / 排除；另扫内嵌 `.resources`（资源型本地化） |
| `src/FireGaze/UIText/UICallSemantics.cs` | 调用语义：谁是 UI 调用、谁拿字符串当 ID、哪些是危险语境（含聊天输出） |
| `src/FireGaze/UIText/UITextModels.cs` | 抽取结果模型（原文 / 上下文 / 判定 / 依据 / 是否保留 ID） |
| `src/FireGaze/UIText/UITextPack.cs` | 配套包模型（原文→译文 + 资源容器/key、来源、不翻名单、库合并优先级） |
| `src/FireGaze/UIText/UITextStore.cs` | 本地包读写（`<配置目录>/uitrans/<内部名>.json`）+ 桌面工具 JSON 兼容 |
| `src/FireGaze/UI/UITextTab.cs` | 「插件汉化」页签：插件列表 + 包状态 + 打开编辑器 |
| `src/FireGaze/UI/UITextEditorWindow.cs` | 编辑器窗口：逐条翻译、筛选、导入导出、打补丁/还原/重载 |
| `src/FireGaze/UIText/UITextPatcher.cs` | 打补丁：dnlib 改写 `ldstr` 字面量 + 重写内嵌 `.resources` 容器 |
| `src/FireGaze/UIText/UITextPatchStore.cs` | 补丁状态 + 原始 DLL 备份（都在配置目录里） |
| `src/FireGaze/UIText/UITextPatchManager.cs` | 打补丁/还原/重载/更新后重打的调度与安全网 |
| `src/FireGaze/UIText/TranslationChannels.cs` | 翻译通道：Google 免 key / MyMemory / 大模型 / DeepL / 彩云小译 |
| `src/FireGaze/UIText/FFXIVGlossary.cs` | 随插件打包的「英文 → 国服官方中文」术语表（只喂大模型通道，可关）；数据由 `scripts/ffxiv_glossary.py` 生成 |
| `src/FireGaze/UIText/UITextLibrary.cs` | 公共译文库客户端：索引 + 按插件按需下载（`uit-packs/`）+ 合并 |
| `src/FireGaze/UIText/UITextRules.cs` | 静态规则：伴生程序集名单 + 哪些自定义特性算界面文本（`uit-rules.json`，随插件打包） |
| `src/FireGaze/UIText/DPAPI.cs` | 用户 API key 的本机加密存储 |
| `tools/UITextProbe/` | 离线探针（与插件同一份源码）：`--all` / `--json` / `--resources` / `--trace` / `--types` |
| `scripts/uit_library_build.py` | CI 用：下载插件 → 探针抽取 → DeepSeek 增量翻译 → 生成 `uit-packs/*.json` + 索引 |
| `.github/workflows/uit-packs.yml` | 每周一云端生成公共译文包（手动触发可指定插件） |

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
- **资源型条目**（2026-10-02 新增）：另有 `resources` 段，身份是「容器 + key」，见下节。

```json
{
  "_meta": { "format": 2, "source": "library" },
  "entries": [ { "Original": "Show on decipher", "Translated": "解读时显示", "PreserveID": true } ],
  "resources": [
    { "Container": "AutoHook.Resources.Localization.UIStrings.resources", "Key": "AboutTab", "Original": "About", "Translated": "关于" }
  ],
  "skippedResources": ["容器\u0001key"]
}
```

## 资源型本地化（.resx / ResourceManager）（2026-10-02 实现）

- 现象：部分插件把界面文字放在内嵌 `.resources` 容器里，代码只用 key 查表（`ResourceManager.GetString`）；
  以前这些 key 里还有 5,461 个被当成字面量翻掉 → 查不到资源、界面变空（盘点见 `top100-2026-10-02/sub-localization.md`）。
- 现在：**翻值、不翻 key**。
  · 抽取：`UIStringExtractor.ScanResources` 读主程序集内嵌容器（容器名以程序集名开头、排除第三方库前缀与
    `ploc` 伪本地化），值过 `LooksTranslatableResourceValue`（JSON / 网址 / 超长文档不要）；
  · 包：`resources` 段，值为原文；与字面量条目同一套「不翻 / 清账 / 恢复 / 库合并」口径；
  · 打补丁：`UITextPatcher.PatchResources` 用 `ResourceReader/Writer` 重写容器、`Remove+Add` 替换嵌入资源；
    非字符串值原样保留，重写失败的容器只跳过它自己。
- **只补中性资源，不碰卫星程序集**：`ResourceManager` 的查找顺序是「卫星 → 缺失才回退中性」，
  所以官方 zh 有的 key 保持官方译文，官方没覆盖的 key（GoodFriend 17/98、AutoHook 478/547）和没有 zh 的插件
  自动用我们的补丁——天然互补，也不顶掉官方翻译。（推翻了早期子代理报告里「改卫星」的建议：
  实测 Dalamud 不按游戏语言设 UI culture，只有 `CultureFixes` 修法语数字分隔符，实际用的是 Windows 用户语言。）
- 探针：`UITextProbe <dll> --resources` 输出 key→值的 JSON（库生成脚本用）。

## 聊天输出（IChatGui）（2026-10-02 用户拍板要翻）

- `IChatGui.Print` / `PrintError` 的方法名 `Print` 以前撞上 `IsLogCall` 被当日志排掉（401 条 / 52 插件），
  `PrintError` 还落在「去向不明」——两条规则口径不一致。
- 现在 `UICallSemantics.IsChatCall` 把它们按 UI 处理（`IsLogCall` 里先短路排除），聊天栏里玩家看得见；
  已实测 BazookaLens 的 `IChatGui.PrintError` 文案进候选（fgtest 钉住）。

## 公共译文库（uit-packs/）（2026-10-02 实现）

- 形态（用户定）：**纯数据包、按插件按需下载**——插件本体不带译文；`一键汉化` 先查库，有现成的就下载合并，
  没有或没覆盖全才用自己的翻译通道；用不到的插件永远不下载。包 = `uit-packs/<内部名>.json`，
  索引 = `uit-packs/index.json`（插件 → 文件 / 条数 / 日期）。设置页有「从公共译文库下载现成译文」开关（默认开）。
- 生成（云端）：`scripts/uit_library_build.py`（工作流 `uit-packs.yml`，每周一 13:30 北京；手动触发可指定插件）：
  Aetherfeed 找仓库 → 拉仓库文件取 `DownloadLinkInstall` → 下载 zip 取主 DLL → 探针抽取 →
  按「原文 / 容器+key」增量、只翻新增（可带 FF14 术语表）→ 写包 + 索引。
  **不在维护者本机跑**（与简介词表同一条纪律）；本机只允许 `--dry-run` 盘点。
- 合并（插件侧 `UITextPack.MergeLibrary`）：**玩家自己改过的（user）永不被顶**；ai / library 条目可被库更新覆盖。
- 下载线路：raw.githubusercontent + `gh.atmoomen.top` + `gh-proxy.org`（与简介词表同一套）；
  本地缓存 `<配置目录>/uitrans/library/<内部名>.json`（24h TTL，拉不到就用旧缓存 / 直接跳过）。
- 目标插件清单：`scripts/uit_targets.txt`（首批 = 官方库下载量前 40）。
- **本机导出上传**（`scripts/export_local_library.py`，2026-10-02 用户定，默认合并）：
  · **本机为准**：同一键（条目/属性按原文，资源按容器+key）译文不同 → 用本机的；
  · **其余保留/补齐**：库里已有但本机没有的原样保留，本机独有的追加（`--replace` 才整包覆盖）；
  · **已是中文的不导出**（与插件端 `IsAlreadyChinese` 同口径，含原生「中文###ID」标签）——
    2026-10-02 扫出 AetherBlackbox / ARSR / ArmoireButler 一批「把中文再翻一遍」的噪声，已拦下；
  · 「不汉化」名单（AEAssistV3 / DailyRoutines 等）跳过且不碰库里已有包；
  · 导出时 `PreserveID=false`、`Source=library`、不导出 Review / Skipped（本地决定不外溢）。
- 授权口径（用户定）：默认公开，README 注明「机器/社区翻译，不代表原作者」；作者要求即从库中移除。
- **投稿（玩家把人工译文交回库）**：编辑器「更多… → 提交人工译文到公共库…」把当前包里 `Source=user` 的条目打成
  `type=uit-contribution` 的 JSON、打开填好的 issue 页（匿名，不带账号）；`inbox.yml` 里 `inbox_uit.py` 体检后
  并进对应包（`Source=user`，下次重建保留）、留档并发手机通知。

## 多程序集插件（主 DLL + 伴生程序集）（2026-10-02）

- 有些插件把界面代码拆在伴生程序集里（ARSR：主 DLL 是壳，配置窗口文字在 `RotationSolver.Basic.dll`），
  只扫主 DLL 会漏一大半（实测：ARSR 纯净主 DLL 884 条 UI 候选 + Basic 443 条；装过补丁的主 DLL 只剩 357 条可见英文）。
- 收录规则（`UITextRules`）：同目录 + （文件名为 `<主DLL名>.*.dll` 或 `uit-rules.json` 的 `companions` 里点名）
  且文件存在；主程序集永远排第一。**不能按「主程序集引用了的」来筛**——实测会把
  SixLabors / NAudio / SharpDX / MessagePack 这类第三方库全捞进来。
- 抽取：`UIStringExtractor.ExtractMany` 合并多文件（同原文取更强判定，PreserveID 取或，Context 加 `[文件名]` 前缀）。
- 打补丁：`UITextPatchManager` 一次备份/补丁/还原**全部文件**；先全部打进 `.new`、全成功才替换（不会半套）；
  状态里记 `Files[]`（每文件的哈希 + 备份），老状态（单文件）自动退化兼容。
- 名单文件 `uit-rules.json` 随插件打包（插件端 `UITextRules` 与 CI `scripts/uit_library_build.py` 读同一份）。
- 「没找到」的口径（2026-10-02）：多文件时取**所有文件的交集**才算真缺失——同一个字符串不会同时出现在
  每个 DLL 里，逐文件拼接会把「只属于伴生程序集的条目」也算成主程序集缺（ARSR 实测虚报 2211 条，修后回落；
  纯函数 `UITextPatchManager.IntersectMissing`，fgtest 有断言）。

## 界面（2026-10-01 两路盲评后的 v2；页签名与顺序按用户 2026-10-01 决定：插件汉化排第一）

- 主窗口第 1 个页签「**插件汉化**」（顺序：插件汉化 → 简介汉化 → 仓库体检 → 插件安装器；
  「参与翻译」是独立窗口。这里只管插件自己的窗口文字，插件简介在「简介汉化」页）：
  一行一个插件 = 图标 + 标题 + 一行简介 + 状态徽标（行首、固定 x，方便竖扫）+ 「一键汉化」；
  点开一行看详情（作者介绍 + 统计）+ 「抽取界面文本 / 重新抽取」（只列候选、不翻译不写盘）+「编辑校对…」；工具栏 = 刷新 / 搜索 / 状态筛选 / 只看第三方 / 翻译设置…；
  有任务时窗口顶部常驻进度条（滚到哪都看得见）。
- **汉化完成后按钮变「打开」**（2026-10-02 用户要求）：行上不再一直摆着「一键汉化」，
  而是变成「打开」（只有设置界面的插件显示「设置」）——有主界面开主界面，没有主界面就开设置界面，
  两者都没有就置灰（点了也不会有反应）。
  能力走卫月插件安装器同一条路：**`LocalPlugin.DalamudInterface.LocalUiBuilder`** 上的
  `HasMainUi / HasConfigUi / OpenMain / OpenConfig`（都是 internal，所以 `PluginUiBridge` 用反射；
  反射一律 DeclaredOnly 沿 `BaseType` 找，开发版插件 `LocalDevPlugin : LocalPlugin` 也找得到）。
  ⚠️ 血教训：`InstalledPluginsIndex` 拿到的 `RawPlugin` 是内部 `LocalPlugin`（FireGaze 直接反射 `PluginManager.InstalledPlugins`），
  **不是**给第三方看的 `IExposedPlugin` 包装器——`as IExposedPlugin` 恒为 null，第一版就是这么把按钮弄灰的。
  判定「汉化完成」= 有包且（补丁已应用 或 压根没有可翻文本）；插件更新出新文本时按钮会自动变回「一键汉化」。
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

## 「不汉化」名单（2026-10-02 用户定）

- 名单（内部名）：`AEAssistV3` / `AEAssist` / `DailyRoutines` / `OmniToolbox` / `XSZToolbox` / `KodakkuAssist` /
  `PromeRotation` / `NyaDraw` / `I-Ching-GL` / `MissFisher` / `LightlessSync` / `LightlessCN`。
- 理由（用户原话口径）：这些是复杂项目、由朋友维护，汉化只会增加对方的维护负担；而且本来就是中文插件。
- 行为：列表里显示「中文插件 · 不汉化」，不提供一键汉化 / 抽取 / 编辑 / 上传；
  管理器层（`Apply` / `ExtractWithGuard`）另有一道拦截，绕过界面也打不进补丁；CI 的 `uit_library_build.py`
  同样过滤，云端库不会给它们出包。有补丁记录的仍可点「还原原文」恢复。

## 中文判定与「没东西可翻」的提示（2026-10-02 用户要求）

- 「已是中文」的口径与简介词表统一：**有汉字、没有假名**（日语要翻）——中文插件里夹的英文专名 / 命令
  （`AutoHunt 悬浮窗###AutoHuntFloat`、`使用 /vnav flyflag…`）不再被当成待翻文本。
- 什么都没得翻时必须说清原因，而不是一句「没找到」：
  · 抽到 0 条 →「没有抽取到可翻译的界面文本」；
  · 抽到的都是中文 →「没有需要翻译的内容：抽到的 N 条文本全是中文」；
  · 抽到但都是命令 / 日志 / 键名 → 第三种说法；
  · 翻出来与原文一致（多半本来就无需翻译）→ 报 Info，不再按翻译失败报。

## 列表/编辑器/设置 v3 盲评与修复（2026-10-02）

- 两路 fresh-context 盲评的完整报告（含逐条严重度与维护者处置）：`docs/hci/review-2026-10-02-uitext-v3-{heuristic,friction}.md`；还原图 `Resources/ui-mockup-uitext-v3-{list,windows}.{html,png}`。
- 本批落地的 P1 修复：
  · **译文包比补丁新 → 行按钮从「打开」换回「一键汉化」**，徽标显示「有改动待写入」（`UITextPatchManager.PackNewerThanPatch`）——云端「下载译文」/ 编辑校对之后不再无路可走；
  · 「下载并应用」改名「下载译文」（它本来就不负责应用）；
  · **上传前确认**：条数、人工/机器占比、公开性、不含账号与 key，默认焦点在「取消」；
  · **还原原文确认**：列表弹窗（默认取消）；编辑器用“5 秒内再点一次确认还原”；
  · 失败提示精简 + 行内「重试」；工具栏「待处理 N」；状态筛选加「不汉化」且名单插件不再算进「未汉化」；
  · 中文插件徽标换 Info 色；名单行补回「还原原文 / 详情」入口；
  · 编辑器图例补齐 6 个符号（✔ 人工 / ⚙ 机器 / ⚠ 待复核 / ? 灰名单 / ⛔ 不翻 / · 未翻译），上下文列加悬停全文，“未写入插件”时状态串变橙提示；
  · 翻译设置：每个通道悬停说明（是否要 key / 额度 / 快慢）；key 保存模型写清（除 key 外自动保存；未保存亮黄字；无 key 时「清除」置灰）。
- 挂账（结构改版）：云端信任模型（来源 / 差异预览 / 覆盖策略合并）、编辑器批量操作、设置信息架构分组、计量口径重整、错误块结构化。

### 第二批（1.2.0.75，继续收口盲评挂账）

- **云端译文「先看差异再应用」**：点「下载译文」先拉包 + 预检（新增 / 覆盖机器译 / 保留玩家译 / 无变化），弹确认框后才写入本机（`UITextFlow.PreviewMerge`，fgtest 有断言）；人工译永不被顶的口径不变。
- **错误块结构化**：行提示支持「标题 + 说明」两行（如标题「翻译失败：大模型没有按 JSON 回答」+ 说明「已翻好的 N 条不会丢；点「重试」接着翻…」），按钮跟在后面。
- **列表行内取消**：进行中的行直接给「取消」（原来只在顶部状态条）。
- **编辑器计量口径**：表头改为「共 N 条：候选 N（文字 A + 资源 B + 属性 C）· 灰名单 · 已翻译 · 不翻」——每个数字都能对上。
- **编辑器图例**加「右键行：翻译这一条 / 标记不翻 / 清除译文 / 复制」（操作发现性）。
- **翻译设置**：大模型 / 彩云小译 / DeepL 三块各加分组标题；「通用选项」分组保留。
- **重新抽取** tooltip 补一句「不会丢已翻好的译文；原文没了的条目会自动标成「不翻」，可在编辑器里恢复」。
- 仍未做：编辑器批量勾选操作、测试连接结果固定位置、首次使用引导、通道命名全局统一、ImGui 字形集实机确认。

## 投稿中继的 400（2026-10-02 修）

- 症状：插件行「一键上传」全部 `HTTP 400`。根因：线上 Worker 的关键词校验只认 `contributions`，
  而插件界面文字的投稿正文是 `uit-contribution`（单数）——两者都不包含校验词。
- 修法：客户端标题行改成 `### FireGaze contributions · 插件界面文字译文贡献`（兼容线上旧 Worker，**不必等重新部署**）；
  同时更新 `scripts/relay/worker.js`（两类投稿都收 + `GET` 健康检查 `version: 2`），部署后可直接 `curl` 验证。
- 顺带：客户端不再只显示 `HTTP 400`——会读 Worker 返回的 `error/detail`；正文超过 6000 字符时
  不再拼进 GitHub 提交页 URL（会被截断），改为导出 `contributions/uit-<插件>-<时间>.json` 并提示拖进附件。

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

1. 配套库的**投稿通道**：relay（Cloudflare Worker）匿名投稿 + 撤回（私有 KV 存一次性凭据）；库下载已做，见「公共译文库」。
2. 界面 HCI 评审（先出还原图，按仓库既有流程）；
3. 批量翻译的单位与限流（免费接口有每日额度；大插件建议用自填 key）。

## 插件适配：委托工厂与接口文本 getter（2026-10-02，1.2.0.76）

- **自定义委托的 `Invoke`**：从委托定义读参数名，按名字分「给人看的文本」与「键名」——
  Allagan 系的 `YesNoChoiceFilter.Factory(key, name, helpText, …)` 以前整批落在「去向不明：Invoke/4」，
  `Can be HQ? / Is Unique?` 抽不到；现在 `name` / `helpText` 进 UI、`key` 进硬键排除。
  名字表：UI = name/text/label/title/caption/heading/description/desc/tooltip/hint/helptext/help/
  displayname/displaytext/message/msg/summary；键名 = key/id/identifier/command/cmd/path/url/uri/version/hash。
- **接口文本 getter**：`get_SingularName` / `get_PluralName` / `get_HelpText` / `get_Description` / … 这类
  「接口实现 + 强 UI 属性名」的简单 getter，返回值直接算 UI（值会被界面框架跨程序集取走，
  本模块内追不到消费者——Allagan 的 ItemXxxRenderer 就是这样）；只认接口实现，不碰普通 Name / Title。
- **Allagan Tools（InventoryTools）例外**：它的英文串是**自带汉化词典的 key**（`LocalizationService.Translations`，
  值是中文），翻了会破坏它自己的查表——这些 key 保持不翻；它显示英文的地方是词典缺条目，属上游维护范围。
- fgtest 新增真实样本断言：AllaganItemSearch 的 `Can be HQ? / Is Unique? / Airship Exploration` 必须是 UI，
  `canBeHq` 必须是键名排除。

## 属性字符串（自定义特性参数）（2026-10-02 实现）

- 不少插件把界面说明放在**自定义特性（Attribute）参数**里，这些字符串在 UTF-8 的 blob 里、不在 `ldstr` 里，普通抽取看不到：
  ARSR 的 `[UIAttribute("Make /rotation Manual a toggle command.")]`（856 条）、
  SimpleTweaks 的 `[TweakName/TweakDescription/TweakConfigOption]`（466 条）。
- 识别规则（`UITextRules.IsUIAttribute`）：`System.ComponentModel` 的 Description / Category / Display / Tooltip / Label；
  短名以 `UI`/`Ui` 开头且以 `Attribute` 结尾（UIAttribute / UiTextAttribute…）；以及 `uit-rules.json` 的
  `uiAttributes` 名单（SimpleTweaks 那套 + ARSR 的 `RotationConfigAttribute`）。
- **构造函数参数**全收（值过 `LooksTranslatable`）；**命名参数**按名字白名单收
  （`UITextRules.IsUINamedArgument`：Name / Text / Label / Title / Caption / Heading / Description / Tooltip / Hint /
  DisplayName / DisplayText）——`[RotationConfig(CombatType.PvE, Name = "…")]` 这种界面标题靠它（2026-10-02 用户实测漏翻）；
  Path / Id / Command / Version 这类名字是键与标识，一律不碰。
- **解析器坑（血教训，勿回退）**：dnlib 无参构造的 `ModuleContext.Resolver` 是 NullResolver——
  跨程序集的参数类型解析不出来，**整个特性 blob 弃读**（`IsRawBlob=true`，fixed / named 全丢）。
  ARSR 主 DLL 的 `CombatType`（定义在 `RotationSolver.Basic.dll`）就是这种：它名下 500+ 条配置名一直没进包。
  修法：`UIStringExtractor.LoadModule` 一律用 `new ModuleContext(new AssemblyResolver())` + `PreSearchPaths` 指向 DLL 目录；
  **备份抽取时**（源在 `uitrans/backups`）还要把原插件目录一起带进来
  （`ExtractionSourceOf` 的 out `searchDirectories` → `ExtractMany` / `UITextPatcher.Patch` 都有这个可选参数）。
- **血教训（2026-10-02，勿回退）：拼接片段不能追加 `###原文`。** ImGui 的 `###` **之后**才是 ID。
  ARSR 的 `text2 = $"##{hash}_{text}.Name"` 里 `.Name` 只是 `string.Concat` 的一个参数（不是完整 label），
  补成 `.名称###.Name` 后 ImGui 只拿 `.Name` 当 ID——**整个窗口所有滑块/按钮同 ID，拉一个滑块全部一起动**。
  修法：抽取器给「参与过拼接 / 格式化」的字符串打 `Joined` 标记（`V.Joined` + `Literal.Joined`，
  `MakeResult` 里识别 `String.Concat / Join / Format`、`DefaultInterpolatedStringHandler`、`StringBuilder.Append/Insert`），
  `MarkUI` / `ClassifyFlow` / `MergeExtraction` 都不给它们 PreserveID；
  **包格式升 v2**，v1 旧包加载时把 PreserveID 全部清零（自动重打路径同样安全；
  原文自带 `###` 的条目不受影响——打补丁走「保留原 ID」那条路）。fgtest 有抽取 + 补丁产物（搜 DLL 字节）两道断言。
- 包格式新增 `attributes` 段（身份 = 字符串值），编辑器里显示为「属性」行；
  打补丁用 dnlib 改特性参数（`CAArgument` 是**结构体**，必须把返回值写回列表——只改参数副本会「打成但文件没变」）。
- SimpleTweaks 的 fallback 语义天然成立：官方 zh 按「类名 / Name」做 key，属性值只是 fallback，
  所以官方译优先、官方没覆盖的才显示我们的。

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

- **血教训（2026-10-02，翻译通道）：请求先直连、连接层失败再走系统代理。**
  系统代理开着（v2rayN 等）时 HttpClient 默认走代理——代理链路可能把响应弄坏：
  用户那儿「大模型没有按 JSON 回答 / JSON 结构不对」连败两轮，而本地用**同样的内容、模型、参数、术语表**
  直连全部成功（9 次）。现在 LLM 通道有两个客户端（`ClientDirect` / `ClientViaProxy`），
  直连抛连接层异常才换代理；批两次尝试失败还会**拆成两半重试**（只拆一层，防请求数爆炸）；
  失败现场（`finish_reason` / content 长度与开头）写进日志，下次不用猜。
