# 插件内部文本汉化（UIText）

## 客户端维护与诊断（2026-10-09，本地开发版）

- 收起行的已应用状态只写「已汉化」，内部名与版本保留；展开后显示条数。有意保留的单词、灰名单与「不翻」不构成必须翻满的完成指标。
- 插件列表左侧加 12 个逻辑像素留白，图标与文字间隔 4；展开详情使用标题的实际文字起点，并在同一滚动区内占完整宽度。窄窗口操作列可以换行。
- 行勾选默认可用，不再有「多选」开关。所选插件可加入串行汉化队列；任一插件的主汉化 / 更新按钮也直接加入队列，其他插件正在处理时仍可点击，已排队的显示「已排队」并防重复。写入 / 重载仍串行，不并发改 DLL。「检查汉化更新」有勾选时检查所选，无勾选时检查全部已抽取插件。队列保存到配置目录 `uitrans/queue.json`，只显示「取消队列」一个管理按钮，灰色进度紧接检查按钮，任务说明放在悬停提示。取消停止等待项，翻译阶段可停并保留检查点，写入 / 重载阶段安全完成后停止。重启后中断项回到等待且暂停，下一次汉化 / 检查操作才继续；关闭主窗口时仍会推进已授权队列。
- 翻译会话按 20 个去重原文为一组，在下一组请求前保存已返回译文。占位符检查、玩家译文保护、各通道内部限流仍沿用原逻辑。保存失败会停止并给出错误，不谎报「译文已保留」。写 DLL 和重载阶段仍不可取消。
- 「检查汉化更新」不依赖旧未译条数，显式刷新公共索引和译文包；预览新增文本 / 补入译文 / 更新译文。检查不写 DLL；有真实译文差异且检查成功时，行主按钮变为蓝色「点击更新」，设置 / 打开按钮不再高亮。顶部「批量更新」将所有检查成功且可更新的插件加入独立的 ApplyUpdates 串行任务，不做全量机器重翻。每项写入前核对本机包、主程序集、伴生程序集和涉及的本地化文件是否变化；失败的或已保存的预览会作废，避免重复套用。
- 检查发现新增原文后，没有公共译文可应用时，原主汉化按钮仅翻这次新发现的原文，不纳入旧的缺译单词；旧的人工译文和「不翻」决定仍受保护。公共包合并后会重新按抽取结果分类，避免旧资源 key 混进补丁。
- 插件发现加入已安装状态；未安装的可「去安装」（打开卫月全部插件页并预填内部名），已安装的可「去汉化」（清除搜索与筛选，把目标置顶并展开，滚回第一行）。两个导航按钮使用浅淡的不同色调。安装动作仍由卫月处理，不绕过兼容性 / 测试版 / 安全模式等检查；安装状态未知时按钮只叫「安装器」，不冒充未安装。
- 推荐计数不再给加进来的库内所有插件上报。客户端记录玩家从发现页选择的未安装插件，并重新读取已装索引作为基线，只有之后确实安装成功的该插件才上报现有 plugin-add 接口。意图与上报队列持久化、重试，列表刷新不重复计数；服务端及旧统计不修改，旧数含历史加库记录。
- 详情动作固定顺序：「抽取界面文本」「检查汉化更新」「这句怎么还是英文」「编辑校对」。公共译文库默认收起；更新摘要不再各带一组按钮。「这句怎么还是英文」点击才在按钮附近打开输入弹出框，保持跟随按钮，边缘夹紧，可 Esc / 点击外部关闭。输入原文 → 检查这句 → 显示保留原文 / 功能用字 / 待校对 / 未应用 / 已有补丁 / 未译 / 未找到；可以定位编辑器、写入并重载或准备反馈。反馈只预填不自动发送，判断依据默认收起。
- 离线回归：`dotnet run --project tools/ClientWorkflowTests -c Release`。当前 47 项覆盖持久队列、重启恢复、运行中接受其他行任务、去重与串行推进、取消、独立更新模式、真实更新判定、单插件安装计数、分批保存、原文保护、诊断分类、过期文件签名与安装器衔接；不请求真实翻译服务。

最新审查与布局还原位于本次 Codex 续接聊天的 `outputs/firegaze-client-v8-review-2026-10-09.md` 及 `outputs/firegaze-client-layout-v1.4.0.8.html`。还原图不代表游戏实机验收。本地 DLL 与清单版本为 `1.4.0.8`，直接编译至游戏已启用的开发插件路径；未修改服务端、推送仓库或发布正式包。

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
| `src/FireGaze/UIText/UIStringExtractor.cs` | IL 静态抽取：判 UI 候选 / 灰名单 / 排除；另扫内嵌 `.resources`（资源型本地化）与内嵌 `.json` 本地化表 |
| `src/FireGaze/UIText/UICallSemantics.cs` | 调用语义：谁是 UI 调用、谁拿字符串当 ID、哪些是危险语境（含聊天输出） |
| `src/FireGaze/UIText/UITextModels.cs` | 抽取结果模型（原文 / 上下文 / 判定 / 依据 / 是否保留 ID） |
| `src/FireGaze/UIText/UITextPack.cs` | 配套包模型（原文→译文 + 资源容器/key、来源、不翻名单、库合并优先级） |
| `src/FireGaze/UIText/UITextStore.cs` | 本地包读写（`<配置目录>/uitrans/<内部名>.json`）+ 桌面工具 JSON 兼容 |
| `src/FireGaze/UI/UITextTab.cs` | 「插件汉化」页签：插件列表 + 包状态 + 打开编辑器 |
| `src/FireGaze/UI/UITextEditorWindow.cs` | 编辑器窗口：逐条翻译、筛选、导入导出、打补丁/还原/重载 |
| `src/FireGaze/UIText/UITextPatcher.cs` | 打补丁：dnlib 改写 `ldstr` 字面量 + 重写内嵌 `.resources` 容器 / 内嵌 JSON 本地化表 |
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

## DLL 内嵌 JSON 本地化表（HaselTweaks 这类）（2026-10-03 实现）

- 现象：HaselTweaks / LeveHelper（HaselCommon 系）把全部界面文字放在 **DLL 内嵌资源 `<插件名>.Translations.json`** 里，
  格式 `{ "键": { "en": "…", "ja": "…", "zh": "…" } }`，运行时按键取当前语言、**缺了就回退 `en`**。
  这些字符串既不在 `ldstr`、也不在 `.resources` 里 → 以前整批抽不到（用户报：tweak 名一直英文；
  实测该表 514 键里 **383 键已有社区中文、131 键缺 zh**——缺的就是游戏里看到英文的那些）。
- 现在：
  · 抽取 `ScanJsonResource`：把「有 `en`、缺 `zh`」的键收进 `resources` 段（容器 = `json:<资源名>`、key = JSON 键）；
    值过 `LooksTranslatableResourceValue`；只扫主程序集前缀、排掉第三方库前缀；
  · 打补丁 `PatchJSONResources`：整表重写（JsonNode 保留其余结构语言）、**只补 zh 空的槽，
    上游已有 zh 一律不覆盖**（与磁盘本地化文件同一口径）；同一文件里没改动的键也算「处理过」，不报「没对上」；
  · 反向还原 `RevertJSONResources`：按「当前 zh == 我们的译文」把槽摘掉（不跨 key 猜）。
- **形状嗅探**（`UITextJsonResources.TryParse`）：根是对象 + 每个值都是「语言 → 文本」的对象 + 至少一半条目有 `en`，
  不满足就不认——避免把插件里任意一份数据 JSON 当成翻译表。
- **清账豁免**：同 `file:` 容器——打过补丁后 zh 侧不再缺，但这批条目**不许**按「不在本轮候选里」自动清账
  （`PruneAgainstExtraction` 对 `json:` 容器直接跳过，否则刚打完就被排掉翻不了第二次）。
- 编辑器/翻译上下文里显示为「内嵌 JSON：<资源名> · <键>」。
- fgtest ⑱ 钉住：抽取缺 zh 候选 / 只补 zh 空槽 / 上游 zh 不覆盖 / 反向还原 / 清账豁免（真实样本 HaselTweaks）。
- 配套（2026-10-03）：`uit-rules.json` 给 HaselTweaks / LeveHelper 点名伴生 `HaselCommon.dll`
  （库级 UI：Yes/No/True/False、表格提示、`Open on Garland Tools` 等；它自己的表 22 键里 14 键缺 zh）——
  抽取与打补丁按多程序集流程一起处理。

## 聊天输出（IChatGui）（2026-10-02 用户拍板要翻）

- `IChatGui.Print` / `PrintError` 的方法名 `Print` 以前撞上 `IsLogCall` 被当日志排掉（401 条 / 52 插件），
  `PrintError` 还落在「去向不明」——两条规则口径不一致。
- 现在 `UICallSemantics.IsChatCall` 把它们按 UI 处理（`IsLogCall` 里先短路排除），聊天栏里玩家看得见；
  已实测 BazookaLens 的 `IChatGui.PrintError` 文案进候选（fgtest 钉住）。

## 公共译文库（uit-packs/）（2026-10-02 实现；2026-10-04 多包模型）

- 形态（用户定）：**纯数据包、按插件按需下载**——插件本体不带译文；`一键汉化` 先查库，有现成的就下载合并，
  没有或没覆盖全才用自己的翻译通道；用不到的插件永远不下载。设置页有「从公共译文库下载现成译文」开关（默认开）。
- **多包模型（2026-10-04 用户定）**：
  · **基础包** `uit-packs/<内部名>.json`：**维护者自己翻译/导出的**，云端管线生成、本机 export 更新；
  · **玩家包** `uit-packs/<内部名>@<包ID>.json`：**每次收录的投稿新建一个**（包 ID = `user-<内容指纹前6位>`，匿名）；
    投稿**不再合并进基础包**，也互不覆盖——每个人传的在云端各自留档、各算下载数与 👍；
    **包名可自取**（2026-10-04）：提交确认框里填一个昵称/包名，写进包 `_meta.label`（留空 = 匿名，显示为「玩家包 · 短ID」）；
    **翻译类型**（同日）：确认框里勾「免费翻译 / 大模型翻译 / 人工翻译」（默认按条目 Source 推、可改），写进 `_meta.kinds`，
    索引 `packs[].kinds` 一并带上，插件里显示为「（玩家投稿 · 人工翻译）」；
  · **收稿抽查 issue**（2026-10-04 用户要求）：每收一份直传投稿，机器人另开一条 `[收稿] <插件> · <包名> · N 条`
    issue（由 `inbox.yml` 的 direct 作业在提交之后用 `post_review_issues.py` 发）：正文含包名、包文件链接与
    **原文→译文样本**（均匀抽样，最多 120 行 / 40k 字符——issue 正文有 65536 上限）。入库已自动完成，issue 只供抽查。
  · 索引 `index.json` 里每插件带 `packs` 列表（id/file/label/source/条数/日期/downloads/likes）；
    插件详情「云端译文」逐包一行：`下载译文` + `👍`（本机赞过变灰，每台机器记一次）。
  · 自动合并顺序（2026-10-07 改）：基础包先合 → 玩家包按更新时间**从旧到新**（同级后合 = 更新的包赢）；
    唯一实现 = `scripts/uit_index.py`（三处索引生成器共用，重建时保留下载数与 👍）。
  · 计数：中继 KV，键 `<插件>@<包ID>`（基础包包 ID = `library`）；`library-counts.yml` 每 6 小时写回索引。
- 生成（云端）：`scripts/uit_library_build.py`（工作流 `uit-packs.yml`；**定时已停**，只手动触发）：
  Aetherfeed 找仓库 → 拉仓库文件取 `DownloadLinkInstall` → 下载 zip 取主 DLL → 探针抽取 →
  按「原文 / 容器+key」增量、只翻新增（可带 FF14 术语表）→ 写包 + 索引。
  **不在维护者本机跑**（与简介词表同一条纪律）；本机只允许 `--dry-run` 盘点。
- 合并（插件侧 `UITextPack.MergeLibrary`，2026-10-07 改）：**本机手改过的（user）永不被顶**；其余按
  **人工 3 > 大模型 2 > 基础包 1 > 免费 0** 比大小，同级别覆盖（自动合并从旧到新合，所以同级是更新的包赢）。
  自动合并多个包时用「合并开始前拍的本机手改快照」（`UITextMergeGuard`）当保护名单——
  合并中写进来的玩家手译不在名单里，同级才会按新旧决出。
- 下载线路：raw.githubusercontent + `gh.atmoomen.top` + `gh-proxy.org`（与简介词表同一套）；
  **每个地址的内容先过 JSON 解析校验**（2026-10-07：实测镜像偶尔吐截断的文件），坏了就换下一个地址；
  本地缓存 `<配置目录>/uitrans/library/<内部名>.json`（24h TTL，拉不到就用旧缓存 / 直接跳过）。
- 目标插件清单：`scripts/uit_targets.txt`（首批 = 官方库下载量前 40）。
- **本机导出上传**（`scripts/export_local_library.py`，2026-10-02 用户定，默认合并）：
  · **本机为准**：同一键（条目/属性按原文，资源按容器+key）译文不同 → 用本机的；
  · **其余保留/补齐**：库里已有但本机没有的原样保留，本机独有的追加（`--replace` 才整包覆盖）；
  · **已是中文的不导出**（与插件端 `IsAlreadyChinese` 同口径，含原生「中文###ID」标签）——
    2026-10-02 扫出 AetherBlackbox / ARSR / ArmoireButler 一批「把中文再翻一遍」的噪声，已拦下；
  · 「中文插件」名单（AEAssistV3 / DailyRoutines 等）跳过且不碰库里已有包；
  · 导出时 `PreserveID=false`、`Source=library`、不导出 Review / Skipped（本地决定不外溢）。
- 授权口径（用户定）：默认公开，README 注明「机器/社区翻译，不代表原作者」；作者要求即从库中移除。
- **投稿（玩家把译好的文字交回库）**：插件汉化列表行的「一键上传」把包里已翻好的条目打成
  `type=uit-contribution` 的 JSON，走中继提交（匿名，不带账号）；`inbox.yml` 里 `inbox_uit.py` 体检后
  落成独立玩家包（`<插件>@user-*`）、留档并发手机通知。编辑器里那个「提交人工译文」菜单入口 2026-10-07 已下线。

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
- 编辑器窗口（编辑校对）：筛选带计数 + 状态列图例；工具栏 = 翻译未翻 (N) / 写入并重载 / 全量重翻 (N) / 更多…
  （重新抽取、按术语表重译、导出未翻、导入译文、还原原文、打开包目录）；应用与还原都自动重载，不再有「重载生效」这一步。
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
- **编辑器的抽取合并统一走 `UITextFlow.MergeExtraction`（2026-10-02，1.2.0.86）**：编辑器原先自己抄了一份
  「把抽取并进包」的循环（只并字面量），资源 / 属性 / 本地化文件条目**从来不会被并进去**——
  首次在编辑器里打开一个还没跑过一键汉化的插件（如 AutoDuty），本地化文件的候选根本不出现在行里。
  现在编辑器与列表页共用同一个合并入口，两边口径一致；`MergeResult` 顺带带上「角色依据」（`Reasons`）供行里显示。

## 「中文插件」名单（2026-10-02 用户定）

- 名单（内部名）：`AEAssistV3` / `AEAssist` / `DailyRoutines` / `OmniToolbox` / `XSZToolbox` / `KodakkuAssist` /
  `PromeRotation` / `NyaDraw` / `I-Ching-GL` / `MissFisher` / `LightlessSync` / `LightlessCN` /
  `SillyToolbox` / `pvpauto` / `BOCCHI`。
- 理由：这些是复杂项目，汉化只会增加对方的维护负担；而且本来就是中文插件。
  后两个是 2026-10-02 普查后按同一原则补进名单的——`SillyToolbox` 自述「给自己和亲友用的小功能」、
  `pvpauto` 是中文作者的自动化工具，两者中文文件都完整，本来也翻不出东西。
- 行为：列表里显示「中文插件」，不提供一键汉化 / 抽取 / 编辑 / 上传；
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

### 第三批（1.2.0.78）：v4 定向复评后的收口

- v4 还原图 + 两路定向复评（只判 v3 的 P1 是否消解）：`docs/hci/review-2026-10-02-uitext-v4-{heuristic,friction}-recheck.md`；
  还原图 `Resources/ui-mockup-uitext-v4-{list,windows}.{html,png}`。结论：v3 的 11 条 P1 **9 条完全消解、2 条部分**，本轮无新增 P1；
  复评唯一判为 P1 的是**统计口径**（F1/N5/N6）。
- **统计口径一次收口**：编辑器表头「共 N 条 ｜ 候选 N（文字 A + 资源 B + 属性 C）· 灰名单 G ｜ 已翻译 · 未翻译 · 不翻」——
  「｜」两侧分别是「抽取判定」与「翻译状态」两个维度，不再让人试图相加；列表详情串同口径
  （`RowInfo.Untranslated` 新字段），挪掉了以前用减法算、可能为负的「未翻译」。
- **按钮名跟着真实步骤走**：「有改动待写入」的行主按钮 =「写入并重载」（与编辑器同名，悬停注明不再重翻）；
  翻译失败的行主按钮 =「重试」（与提示语一致），行内重复的「重试」删掉。
- **状态串拆段**：只有「· 有改动尚未写入（点「写入并重载」生效）」这段橙色，通道/已保存与「补丁：…」保持灰——
  以前整串变橙、且「尚未写入」与「已应用」并排像自相矛盾（F2/N3/N4）。
- 其余收口：云端差异改人话（「更新机器译文 N 条 · 你的 N 条人工译文保留不动 · 其余 N 条无变化」）；
  「中文插件 · 不汉化」换 `UiHelpers.Skip` 柔紫（Info 蓝留给「有进度」的状态）；
  图例写明「⚠ 待复核（这条不会写入，悬停看原因）」（占位符校验失败时译文不落库，属实）；
  图例并成两行；key 行输入非空时不再叠加「当前：未保存」；「清除」置灰带悬停说明；上传确认框第一句就点明「公开 issue」。
- 挂账：顶部/行内双取消是否只留一处、工具栏计数可点筛选、「还原原文」按钮（核为还原图伪影，实现本来就统一且仅在「有备份」时出现）、
  以及所有「需实机确认」点（下拉筛选项、窄窗折行、字形覆盖、key 未保存关闭窗口行为等）。

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
- **纯译文补丁也认得出来**（2026-10-02）：字面量正好等于某条唯一译文、且原文已不在 DLL 里，就算作「我们打过的痕迹」；
  配合同一条自救链路（无记录时反向还原出原文再重打）——用户清掉补丁记录后，
  AcquisitionDate / ActionTimelineReborn / AetherBlackbox 就是靠这条恢复的。
- **一次点击就自救（2026-10-02，1.2.0.88）**：首次点击时译文包可能**还没有译文**（译文是这一次刚从公共库 / 翻译通道
  补进来的），抽取体检认不出「译文###原文」，于是打到旧补丁上、一条都对不上——用户看到红字，第二次点才好。
  现在「打补丁一条都没对上」（`UITextPatchOutcome.NoMatch`）时，`ApplyCore` 会自动用**当前译文包**反向还原出原文、
  补回记录，再整套重打一次；重打还失败才把红字给用户（红字文案不变，仍适用于真的没招的情况）。

## 持久数据目录（2026-10-02 落地：B + 译文包镜像）

- 位置：`<启动器根>\FireGazeData\`（从 `<配置目录>\..\..` 推出，一般为 `%APPDATA%\XIVLauncherCN\FireGazeData\`），
  与插件配置目录分开——用户重置 / 清理 FireGaze 插件配置时不会连它一起删掉。
  推不出来（非常规目录）时整体退回配置目录，行为与加它之前一致；启动日志会写明用的是哪个。
- `uit-originals\<内部名>\`：**补丁原始备份**（`<文件名>.orig`）+ `manifest.json`（每个文件的 sourceHash / patchedHash）。
  打补丁时先往这里写；写不进去才退回配置目录（老行为）。补丁记录丢了（配置被重置）时，
  只要「盘上文件哈希 == manifest 的 patchedHash」且「备份哈希 == sourceHash」就自动认领、重建记录；
  任一对不上（插件更新过）一律拒绝，**绝不把旧原文盖回去**。
  旧版留在配置目录里的备份会在启动后 20 秒一次性迁进这里（状态改指新路径后才删旧副本）。
  多程序集插件里只要有一份文件没能进持久目录，就不发清单（认领缺一份不完整，宁可退回反向还原）。
- `uit-packs\<内部名>.json`：**译文包镜像**。每次保存包双写；配置目录里的包没了就从镜像恢复回工作目录
  （DLL 原文丢了还能反向还原，译文丢了就真没了，所以镜像一起做）。
- 认领失败有负缓存（按 DLL 写入时间）：清单陈旧时列表每 5 秒刷一次，不重复算大 DLL 的哈希。
- 还原干净后清掉持久目录里这份补丁的 `.orig` 与 manifest（DLL 已回原版，不再需要）。
- fgtest：`持久数据：原始备份 manifest 认领（含防误盖）/ 译文包镜像恢复 / 目录解析`（另含篡改 DLL / 篡改备份必须拒绝、部分备份不发清单）。

## 还没做（按计划）

1. 配套库的**投稿通道**：relay（Cloudflare Worker）匿名投稿 + 撤回（私有 KV 存一次性凭据）；库下载已做，见「公共译文库」。
2. 界面 HCI 评审（先出还原图，按仓库既有流程）；
3. 批量翻译的单位与限流（免费接口有每日额度；大插件建议用自填 key）。
4. **词典式汉化插件的「词典值预填」（2026-10-02 观察到，未做）**：像 Allagan Tools - CN 自带词典
   （`d["Dungeon Chest"] = "副本宝箱"`）里已经有中文，但**显示点不走 Tr** 时仍需补丁；抽取器可以顺手
   把「同 DLL 词典里该 key 的中文值」当默认译文预填——省 AI、且与作者译名一致。

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
  **修订（2026-10-02，1.2.0.96）**：点分标识符（`no_matches.title`）仍保持不翻；**自然语言句子形态**的 key
  （`Tr("These combine into a single list…")` 这类）改判**灰名单**——勾「连灰名单一起翻」后可翻，
  翻了查表 miss 会退回显示译文本身。见「查表 key 的『词典式汉化』与库残留解锁」节。
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
  （31,768 条，约 0.9 MB，包内压缩后 ~433 KB）随插件打包；`translate.yml` 每周重建，产物确定（不写 mtime、不经有版本差异的压缩器）。
  数据源后来加了系统名表与人工补充表（2026-10-02，见「术语表补齐」与「查表 key 的『词典式汉化』」两节）。
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

## 抽取器：继承链上的 UI 属性 getter + 字典键守卫（2026-10-02，1.2.0.80）

用户报 Allagan Tools（InventoryTools）「Can the item be sourced via botany?」等一批没翻。反编译定位到两处抽取器缺口：

- **① 基类 / 继承链上的属性 getter 没被认可**：`ItemInfoRenderer<T>` 声明 `public abstract string SingularName/HelpText`，
  `GenericHasSourceCategoryFilter : BooleanFilter` 覆写 `HelpText`（基类抽象属性），`ItemXxxRenderer` 覆写基类属性——
  旧规则只查**自己直接实现**的接口，于是 `Can the item be sourced via `、`Logging (Hidden)`、
  `Can the item be gathered from a hidden logging node?` 整批落在「没有流向 UI 调用」。
  修法：`IsInterfaceUITextGetter` 改为 ① 沿**继承链**找声明该属性的接口（含基类）；② 覆写的是基类上的**同名抽象 / 虚属性**也算
  （名字白名单不变：SingularName / PluralName / HelpText / Description / …）。
  实测 InventoryTools UI 候选 **65 → 318**。

- **② 字典键的硬键守卫对泛型参数失效**（顺带查出的真 bug）：`dictionary["Dye"] = "染料"` 走
  `Dictionary<string,string>::set_Item`，其参数在 dnlib 里读出来是裸泛型名 **`TKey`/`TValue`**，
  而 `IsStringLikeOrGeneric` 只认 `String` / `!0` 记号 → 「第 0 参是键」的判定整条落空。
  于是插件自带汉化词典的 key（`Dye`/`Stack Size`…）会随着「同文本也出现在某个 UI getter 里」被升成 UI，
  打上补丁就破坏查表。修法：`ParamTypeNames` 把 `GenericSig` 归一成 `"!" + Number`（`!0`）。
  实测：`Dye`/`Stack Size` 回到「当集合/字典的键名用…（翻了会破坏查找）」；UI 候选里的词典键清零。
  fgtest 新增 **Allagan 抽取实证**（本机装着该插件时跑：基类 getter 必须是 UI、`Dye` 必须是硬键；样本找不到自动跳过）。

- **挂账**：`ItemInfoRenderService.GetCategoryName` 的 switch 字面量（`Botany`/`Mining`…）目前仍抽不到——
  它的返回值经**外部** `String.Concat` 拼进 UI getter 的返回，跨外部拼接调用的 UI 传播还没有实现；
  同文本若同时被别的 getter 返回（如 `Mining`）会顺带抽到，纯靠 `GetCategoryName` 的（`Botany`）暂缺。

## 列表筛选：只看已启用 + 还原后立即消失（2026-10-02，1.2.0.80）

用户要求：能只看**已启用**、**已汉化**的插件；在「已汉化」视图里一个个「还原原文」时，行要一个个消失。

- 工具栏新增复选框 **「只看已启用」**（按 `IsLoaded` 过滤，session 级、与「只看第三方」并列；悬停说明：
  在插件管理器里禁用 / 还没加载的不列）。「已汉化」本来就在状态下拉里，两者可叠加。
- `FinishRun` 现在置 `rowsDirty`：一键汉化 / 还原**跑完立刻重算行状态**，不再等下一个 5 秒刷新——
  在「已汉化」筛选下点了「还原原文」，那一行会马上消失（逻辑上它已经不是已汉化）。

## 投稿通道的统一（2026-10-02，1.2.0.82）

- 三条投稿入口——列表行「一键上传」、编辑器「提交人工译文到公共库…」、参与翻译「一键提交」——**统一为同一条通道**：
  **中继优先**（Cloudflare Worker，匿名、不需要 GitHub 账号，一步提交）→ 失败才回退「填好内容打开 GitHub 提交页」
  （参与翻译是两步式：网页按过 Submit 回来点「确认已提交」；行上传没有待提交清单，不走第二步）。
- 修前的不一致：编辑器那条**直接开 GitHub 页**（和列表行不一样），而且标题缺 Worker 校验用的关键词 `contributions`
   ——就算以后接中继也会被线上旧版 Worker 判 400。现在抽成 `ContributeSender.SubmitAsync`，两条入口共用；
  标题统一成 `### FireGaze contributions · 插件界面文字译文贡献`；正文 >6000 时统一导出
  `contributions/uit-<插件>-<时间>.json` 并提示拖进附件。
- 文案：上传确认框与参与翻译窗口都写明「由 FireGaze 中继匿名提交、不需要 GitHub 账号；提交后成为一个 GitHub
  公开 issue」——「公开 issue」是结果（中继用服务端令牌建 issue），不是要求玩家自己去 GitHub 操作。

## 插件自带本地化文件的普查与支持（2026-10-02，1.2.0.84 实现；1.2.0.85 修二次写入）

用户报 AutoDuty 的 `Waits on the specified plugins…` / `Stop Looping @ Item Level` 没翻。定位：这些字符串**不在 DLL 里**，
而在插件自带的本地化文件里（DLL 补丁碰不到）。全量普查本机已装插件（含语言目录/语言文件两种布局；按键级比较）：

| 插件 | 布局 | 英文键 | 中文缺 |
|---|---|---|---|
| AutoDuty | `Localization/en-US/*.json` + `zh-CN/*.json` | 654 | **171**（MainTab 90 / ConfigTab 57 / Overlay 11 / LoopActions 10…） |
| PetRenamer | `I18N/en_UK.json` + `zh_CN.json` | 148 | **27**（进候选 21；4 个 `.Raw` 语言名 + 2 个已是中文的值被规则排除） |
| BOCCHI | `Translations/{en,zh}/*.json`（各 23 文件） | 727 | 0（完整） |
| Henchman | `Localization/{de,en,fr,jp,ko,tw,zh}/*.json`（各 16 文件） | 202 | 0（完整） |
| Aetherphone | `Localization/{en,zh,…}.json` | 6739 | **0（完整）**——早先一版人工普查误记成「没有 zh」，实际有完整 `zh.json` |
| SillyToolbox | `Assets/Localization/{en,zh-Hans,zh-Hant}.json` | 323 | 0（完整；2026-10-02 起列入「中文插件」名单） |
| pvpauto | `Assets/Langs/{English,Chinese}.json` | 631 | 0（完整；同上） |
| DailyRoutines / NyaDraw / KodakkuAssist | `Assets/Langs` ×2 / `Module/Langs`（.resx） | — | 不参与（在「中文插件」名单里） |

- 形态不统一：**目录分语言**（`en-US/`、`en/`）与**单文件分语言**（`en.json`、`en_UK.json`）两种；
  语言代码有 `en-US/zh-CN`、`en/zh`、`en_UK/zh_CN` 三种写法。
- **已实现（`UITextLocalizationFiles.cs`）**：从英文侧读出**中文侧缺失的键**→走现有翻译通道→写回中文侧文件（临时文件替换）。
  · 复用包的 **`resources` 段**：容器名 = `file:<相对插件目录的路径>`、键 = JSON 指针（`/A/B/0`）——
    编辑器行、翻译通道、公共库合并、投稿、清账都不用新增概念；编辑器里显示为「文件：Localization/zh-CN/ConfigTab.json · /A/B」。
  · **上游优先**：基线上已有译文的键一律不覆盖（只跳过）——基线的口径见下条；打过补丁后这些键不再缺，抽取自然不再报，
    因此 `PruneAgainstExtraction` 对 `file:` 条目**不做自动清账**，生命周期交给写入规则。
  · **写入口径（1.2.0.85 修）**：与 DLL 补丁同构——盘上是我们的补丁时**从原始备份重新写一遍**再落盘；
    不是我们的补丁时以当前文件为基线并先备份。这样「改了译文再打一次」真的会更新（旧实现直接读当前文件，
    把自己的旧译文当成上游译文而永不覆盖），也**不会把补丁内容备份成原文**（旧实现二次写入会覆盖 `.orig`）。
    产出与盘上内容一致时不写盘（避免无谓重写）；管理器把「还是我们的补丁」的旧记录原样留在状态里，
    免得它从补丁记录/持久清单里消失、还原与更新后重打找不到它。
  · **结构保护**：指针位置上不是字符串（中英文两边结构对不上）时既不进候选、也不写入，绝不把上游结构改坏。
  · **`.Raw` 不翻**：`Language.*.Raw` 是「以原生语言显示语言名」开关专用（English / Deutsch / Nederlands…），
    翻成中文会让这个开关失去意义——键名以 `/Raw` 或 `.Raw` 结尾的一律不进候选（PetRenamer 实测 4 条）。
  · **备份/还原/更新后重打**沿用补丁状态那套：中文文件与 DLL 一起进 `Files`（备份进持久目录 `uit-originals`，
    清单里的文件名是「相对插件目录的路径」）；持久备份名带源路径短哈希（`x.json.<hash>.orig`），
    同名但不同目录的文件互不覆盖。还原时一起回去。
  · 范围：只处理**已经自带中文变体**的插件，不替没有中文文件的插件发明语言
    （当初以为 Aetherphone 没有 zh，实测它其实是完整的）。
  · **扫描器到不了的地方（已知边界）**：只认插件目录**下一层**的语言目录（`Localization/…`、`I18N/…` 等）
    与根目录的语言文件；`Assets/Langs`、`Assets/Localization`、`Module/Langs` 这类**嵌套**布局、以及
    `English.json`/`Chinese.json` 这类**按语言全名命名**的文件不在范围内。受影响的 SillyToolbox / pvpauto
    现已列入「不汉化」名单（中文完整、中文作者维护），不需要为它们扩扫描器。
  · fgtest：`本地化文件：缺键候选 / JSON 指针 / 写入不覆盖上游译文 全过`（含 `.Raw` 排除、二次写入改译文、
    结构保护、同名文件备份不串）。

## 弹窗收尾 / 插件启用 / 状态监听（2026-10-02，1.2.0.89）

- **血教训（弹窗收尾）**：ImGui 的 `CloseCurrentPopup()` 只是「标记关闭」，`EndPopup()` 才是真的结束这一帧的弹窗——
  少了它窗口栈失衡，下一帧会弹一串 assertion failed（IDStack / PopStyleColor / Begin-End 全报，用户看到的「还原原文报错」）。
  1.2.0.89 修掉了 UI 里 9 处「CloseCurrentPopup 后直接 return、漏 EndPopup」（上传确认 / 云端应用 / 免费通道警告 / 还原原文）。
  现在统一走 `UiHelpers.ClosePopupAndEnd()`；fgtest 加了**方法级源码扫描**（只查自己画弹窗（含 EndPopup）的方法：
  `CloseCurrentPopup` 之后同层先碰 `return` 再碰 `EndPopup` 就 FAIL——旧版本代码会被它拦下 9 处）。
- **恢复链路的临时目录**：反向还原出来的原文写在 `%TEMP%iregaze-recover-*`，原来在重建记录的函数里 `finally` 删掉了，
  返回后抽取就报 `Could not open file ...`（2026-10-02 用户实测 ActionTimelineReborn）。现在成功时把临时目录交给调用方，
  抽完 / 重试用完再删。
- **「启用插件」按钮**：打过补丁但插件没在跑的行，主按钮右侧出现「启用插件」——走卫月安装器同一条启用链路
  （`Profile.AddOrUpdateAsync(state: true, apply: false)` + `LocalPlugin.LoadAsync(PluginLoadReason.Installer)`，
  全反射，见 `PluginEnableBridge`）。成功后行状态立刻刷新。
- **插件启停立即刷新**：订阅公开事件 `IDalamudPluginInterface.ActivePluginsChanged`（无钩子）→ 页签下一帧重建已装插件索引；
  平时 30 秒兜底重建（原来只有手动「刷新」）。索引在后台任务里建、绘制线程换新，不打断帧。
  fgtest 另加**卫月反射契约**：启用链路用到的类型 / 成员（ProfileManager、Profile.WantsPlugin、LocalPlugin.LoadAsync、
  Service<T>.Get、ActivePluginsChanged）少一个就 FAIL——卫月改版当场报错，而不是玩家点按钮才发现。

## 多程序集：0 条对得上的伴生程序集要跳过（2026-10-02，1.2.0.90）

- **现象（用户实测）**：AutoHook / Browsingway 点「一键汉化」报「译文有 202 条，但在 DLL 里一条都没对上」——
  其实主程序集已经全写进去了（探针实测：AutoHook.dll 207 处、Browsingway.dll 62 处），**失败的是伴生程序集**：
  `AutoHook.FishSolver.dll` / `Browsingway.Common.dll` 的字符串没被包收录，逐文件打补丁时它们的匹配数是 0。
- **旧逻辑把「某一个文件 0 条」当成整插件失败**；现在：伴生程序集 0 条 → 跳过这个文件（日志记一笔），
  只有**主程序集**对不上才走「反向还原 → 重试 / 报错」那条链路。
- **被跳过的文件不进补丁记录**（`stateFiles` 只收真正写进去的）：否则 `StatusOf` 会把它当「盘上是原始文件」一直催重打，
  持久清单也发不出去。备份阶段顺手给它备的那份，在收尾清理里删掉（成功也做一次清理）。
- 列表行：**开发/本地插件改标「[本地]」**（卫月的 `manifest.IsThirdParty` 对本地插件默认 false，原来会显示成「[官方]」）。
- 行尾按钮**放不下就换行**（`UiHelpers.SameLineOrWrap`）：按钮一多（一键汉化 + 还原原文 + 详情 + 一键上传）
  会顶出单元格，AutoRequeue 的「一键上传」曾被裁掉一截。
- 「启用插件」按钮的显示条件放宽到 **Applied 或 PendingReload**：插件没加载时打上的补丁会一直是「待确认」，
  原来只认 Applied，所以像 Burning Down the House 这种打了补丁但停用的插件看不到启用入口。

## 「旧补丁 + 丢记录」的杂种 DLL 也能自愈（2026-10-02，1.2.0.91）

- **现象（用户实测 AutoHook）**：旧补丁还在盘上、补丁记录丢了。某次「一键汉化」把**已经打过的 DLL 当成了原文**存成基线，
  又在它上面补了没打过的另一半 → 盘上成了「旧补丁 + 新补丁」的杂种；`ExtractWithGuard` 发现「译文###原文」后改读备份，
  而备份本身就是那份杂种 → 一直卡在「找不到可还原的原始备份……请重装插件」。
- **修复一（抽取体检的最后一条自救）**：备份也认不出时不再直接报错，改拿**当前盘上的文件**反向还原出原文
  （`TryRecoverPatchedDLL(entry, diskPaths, …)`——必须用盘上路径，不是已换成备份的 sources），补回记录后再继续抽取。
- **修复二（Apply 先还原再打）**：记录丢了、或记录里的「原始备份」本身就带我们的补丁痕迹时，先用
  `MayContainOurPatch` 做便宜预检（只扫字节：抽最多 64 条译文，找**译文本身**的 UTF-16 字节，命中 ≥3 条才算——
  不认 `###原文`，那是弱证据，插件原生的 ImGui 标签就可能长那样，拿来判断会把干净插件的原生 `###ID` 删掉）；
  命中就先 `TryRebuildPatchRecord` 还原出干净原文（还原量阈值兜底），再走正常的备份 + 打补丁——**绝不把打过的 DLL 当基线**。
- 实测（AutoHook 真实数据）：杂种 DLL 反向还原 **676 处**（阈值 = 已译条目 604 的一半）→ 从还原后的基线重打成功 **1211 处**。
- fgtest：`MayContainOurPatch` 三种形态（###原文 / 纯译文 / 干净文件）+ 接线源码断言。

## UI 特性文本 / 翻译缩写 / BOCCHI（2026-10-02，1.2.0.93）

- **BossModReborn 选项说明一直缺翻**：它的显示文本在**自定义特性参数**里（`[PropertyDisplay("标签", 0u, "说明", false, null)]`、
  `[ConfigDisplay(Name = "…")]`、`[PropertyCombo("…", "…")]`），旧规则只认 System.ComponentModel 五件套 + `UI*` 前缀 + `uit-rules.json` 名单。
  规则文件加这三种特性后，实测 BMR 抽出 **304 条**（PropertyDisplay 263 / Combo 32 / ConfigDisplay 9），
  用户报的那批 DeepDungeon 选项（`Automatically navigate to Cairn of Passage`、`Open gold coffers`…）全进来了。
  · 新增 `attributeSkipArgs`：`PropertyDisplay` 的第 5 个参数是**搜索标签**（`slidecast` 这类），翻了会让英文搜索失效——跳过。
  · 反例记一下：BMR 的 `GroupPreset` 名字**不能翻**（代码里拿它做 `== "VBM Multibox"` 这类比较），没有加入名单。
- **BOCCHI 的 key 不能翻**：它的查表方法是 `ConfigWindow::T("no_matches.title")`（单字母缩写），
  不在旧的按名兜底名单里 → 整批 key 被当界面文本翻掉，界面变成 `Unknown translation key: [[windows.main.游戏状态]]`。
  按名兜底名单加入 `T` / `Tr`；同时按用户要求把 **BOCCHI 加入「不汉化」名单**（它自带完整中文本地化，
  实测干净 DLL 上也有 8 条与译文重合的原生中文——「自带中文界面」的插件不该走汉化）。
- **旧补丁预检收紧**：`MayContainOurPatch` 改成「译文 ≥3 条 **且** 至少一条 `###原文`」——光看译文会在
  自带中文界面的插件上误触发（BOCCHI 实测）；纯译文形式的旧补丁漏网由 NoMatch 自动重试兜底。
- **启用插件按钮**：改绿色（与主按钮蓝区分）；启用成功后把「待确认」的补丁直接转正并重建索引——
  主按钮立刻从「一键汉化」变「打开」，不用再等 15 秒确认。

## 字典键的灰名单回归（2026-10-02，1.2.0.94）—— Allagan Tools 物品悬浮窗的「Dungeon Chest」

- **现象（用户实测）**：物品悬浮窗里 `来源: Dungeon Chest`、`用途: 套装, Grand Company Expert Delivery, Desynthesis`
  ——后半截是英文，而「原来界面是中文的」。实测当前抽取器对这三个字符串的判定：
  `Grand Company Expert Delivery` / `Desynthesis` = **UI**（纯 getter，1.2.0.80 的规则能抽到，重新抽取就会翻），
  `Dungeon Chest` = **Excluded**（字典键守卫拦下）→ 悬浮窗永远英文。
- **根因**：`Dungeon Chest` 在 InventoryTools 里**两边都沾**——既是 `LocalizationService.BuildTranslations()`
  里 `dictionary["Dungeon Chest"] = "副本宝箱"` 的**键**，又是 `ItemDungeonChestSourceRenderer.SingularName`
  的显示文本。1.2.0.80 的守卫把「字典键」一律排除，于是这类**双身份文本**被整体丢掉。
- **修法（两条清晰的边界）**：
  · **本地化 / 资源查表 key**（`Translate` / `Localize` / `T` / `Tr` / 资源 key）：查的是 **DLL 外面的表**（JSON / 资源），
    翻了必查不到 —— **一律排除**（HaselTweaks、SimpleTweaks 那类）。
  · **字典 / 集合键名**：和键名使用点在**同一份 DLL**里，dnlib 会把所有同字面量一起改，DLL 内部查表自洽；
    但为了防「键被拿去和外部数据比」的隐患：**没有界面用途**的照旧排除；**两边都沾**的 ⇒ **灰名单**
    （默认不翻，编辑器里能看到原因）。这类文本现在会出现在编辑器里，不再无声丢弃。
- **给用户的取用方式**：重新「一键汉化」后，`Grand Company Expert Delivery` / `Desynthesis` 会直接变中文；
  `Dungeon Chest` 在灰名单里——可以在「编辑校对」里单条填译（user 译文不受灰名单开关限制），
  或在「翻译设置」勾上「批量翻译时连灰名单一起翻」再跑。
  （注：实测重新汉化后的译名不准——模型没有官方术语可依，见下一节 1.2.0.95。）

## 术语表补齐：系统名表 + 人工补充（2026-10-02，1.2.0.95）

- **现象（1.2.0.94 之后实测）**：用户重抽 + 一键汉化后，`Grand Company Expert Delivery` / `Desynthesis`
  从英文变成了**错的中文**——「部队理符交付」/「精炼」。补丁链路本身没问题（探针在用户盘上的 DLL 里
  能查到这两条译文），问题出在**翻译时没有官方术语可依**：随插件打包的术语表只覆盖地名/技能/状态/副本
  这类专有名词，`Grand Company`（大国防联军）、`Desynthesis`（分解）、`Expert Delivery`（筹备稀有品）
  不在表里，模型只能凭字面猜（Grand Company → 部队；Desynthesis → 精炼）。
- **为什么不直接接 `Addon` 表**：这些词条其实都在 `Addon`（游戏 UI 文本表）里，但实测整表接入不可行——
  同一英文在不同界面语境下译法不同（`Friends → 允许好友入场`、`Shattered Memory → 修复玻璃画`、
  `Dungeon Clear! → 突破便宜迷宫！！`），自动筛选（多词 / 译法唯一 / 无标点）后仍有约一半噪声。
  `Addon` 是「语境绑定文本」，不是「术语表」——不要因为它里有几个好词就整表并入。
- **修法（两个小表 + 一份人工补充）**：
  · `SHEETS` 加 `GeneralAction`（动作名）与 `AchievementCategory`（成就分类名）——小而准的官方名词表，
    净增 62 条（`desynthesis → 分解`、`grand company → 大国防联军`、`materia → 魔晶石`、`limit break → 极限技`…）。
  · 新增 `scripts/ffxiv_glossary_extra.tsv`（人工补充、最高优先级、覆盖自动表）：
    `Expert Delivery → 筹备稀有品`、`Grand Company Expert Delivery → 筹备稀有品`、`Free Company → 部队`——
    只放「自动表覆盖不到、又确实翻错」的，逐条可查证（FFCAFE 文本检索）。
  · `STOP_SINGLE` 加 `general`：成就分类里是「整体」、插件界面里通常是「常规」，语境不一，不给提示
    （Python 侧过滤 + C# `FFXIVGlossary.StopSingle` 双保险）。
- **产物**：`ffxiv-glossary.tsv` 31,701 → **31,766 条**（纯增量、无覆盖）；`translate.yml` 云端每周重建时
  自动带上（生成脚本会读 extra 文件）。fgtest 的真术语表断言补上 `Desynthesis` / `Grand Company Expert Delivery`
  / `Free Company` 三个关键词，防止将来重新生成时丢条目。
- **已有译文不会被自动重翻**（机器翻译永不覆盖已有译文）——要修正已打上去的错译名：编辑器里右键
  「清除译文」→「翻译未翻」重翻（此时会命中新术语表），或单条手填。
  批量换掉整个译文包（比如下载到的玩家包质量不行）用编辑器工具栏的「全量重翻」：
  把已有译文的条目也算进候选，用当前通道整个重翻（2026-10-07，1.4.0.4 见下文）。

## 查表 key 的「词典式汉化」与库残留解锁（2026-10-02，1.2.0.96）

- **背景（用户实机）**：Allagan Tools 是 QianChangUwU 的**汉化分支**（`Allagan Tools - CN`），界面文本走它自己的
  `LocalizationService.Tr()`：**词典命中显示中文、未命中把键本身显示出来**（实现：`TryGetValue(text) ? translated : text`）。
  用户看到的「These combine into a single list…」等一堆英文 = **汉化作者词典里没收录的说明文字**。
- **为什么 FireGaze 没翻**：这些文本经 `Paragraph(...)` → `text.Tr()`，被抽取器判成「本地化查表 key」而一律排除
  （1.2.0.94 的保守口径，防的是 BOCCHI / SimpleTweaks 那类「key 查外部表」的插件）。
  两者形态可区分：BOCCHI 的 key 是 `no_matches.title` 这类点分标识符；这里是自然语言句子。
- **修法（三件套）**：
  · `UITextText.LooksLikeSentenceKey`：≥3 词、≥20 字符、无 `_ { } % < > =`、至少一个小写字母。
    命中时 `HardKey && !DictionaryKey` 分支从 Excluded 改判 **Ambiguous（灰名单）**——
    默认不翻，勾「批量翻译时连灰名单一起翻」后才翻；翻了查表 miss 会退回显示译文本身（有效）。
    实测 Allagan Tools：**209 条**从 Excluded 转入灰名单。
  · `IsPatchable` 硬拦 `Excluded`（user 译文例外）：此前「Excluded 不打补丁」只靠清账的 skipped 兜底，
    现在补丁层本身就是不变量（库里来的噪声译文不会再被写进 DLL）。
  · 清账恢复加 `IsLibraryLeftOver`：库合并（v1.2.0.94 之前）只清 Review、没摘 skipped——
    形态 = 无备注 + 有译文 + 来源 library；这类条目重新成为候选时自动解锁（**实测解锁 183 条**）。
    `MergeLibrary` 同步修：库译文进来时若条目是 auto-skip，一并解除（不再产生新残留）。
    用户手动标的「不翻」（ai 来源、无备注）仍绝不碰（fgtest 有断言）。
- **用户操作**：勾上「连灰名单一起翻」→「一键汉化」→ 解锁 + 翻译 + 打补丁一次完成
  （实测可打补丁条目 1235 → **1447**）。

## 架构审计与修复（2026-10-02，1.2.0.97）

- **背景**：对全仓库做一次「bug 隐患」审计。方法 = 8 类静态模式扫描（空 catch / async void / 同步阻塞 /
  Math.Clamp / 反射命名查找 / 事件订阅 / 静态状态 / 文件写入与并发）+ 命中点逐一核实；UIText 链路深审。
- **干净项**（扫过没问题）：空 catch 0、async void 0、事件订阅退订成对、同步等待都在线程池、
  界面侧全是「完成才取 Result」的轮询、补丁状态机（崩溃还原 / 自动重打 / 杂种自愈）与持久化
  （临时文件 + 原子替换）设计完备、包实例并发有 RunLock + Rebase 防护。
- **已修**：
  · `notes` / `enabling` / `uploading` 被后台任务线程与界面线程并发读写（普通 Dictionary/HashSet
    在这种读写下属于未定义行为）→ 改 `ConcurrentDictionary` + `TryAdd` / `TryRemove`；
  · `InstalledPluginsIndex` 索引构建改为**逐条容错**（单个插件反射失败只跳过它，不再让整张表「不可用」）；
    顺带修了 `Plugin.Log` 空解引用警告。
- **待定（未修）**：配置文件类非原子写（可统一原子写助手）、`Plugin.instance` 卸载不置空、
  `RepoAuditTab` 的 lambda 订阅、`LastTableUpdateUTC` 命名误导（本地时间）。
- **流程发现**：增量构建会跳过未变更文件、可能掩盖警告——**交付前用 `dotnet build -t:Rebuild` 全量确认 0 警告**。

## 补丁必须保留内嵌 PDB（2026-10-03 血教训，1.3.12）

- **现象**：Collections「一键汉化」后点「启用插件」，抛
  `NullReferenceException at Collections.Dev.StripDirectoryPath(null)` → `DataGenerator..ctor` 崩。
- **根因**：`dnlib` 重写 DLL 时默认不写 PDB；原程序集的调试目录（`EmbeddedPortablePdb`，现代构建常见）
  被整个丢掉 → 运行时 `StackFrame.GetFileName()` 返回 `null` → 插件自己的 `Dev.Log`
  在 `file.LastIndexOf` 上空引用。
- **取证**：`PEReader.ReadDebugDirectory()` 对比——原件 4 个 entry（CodeView/PdbChecksum/Reproducible/**EmbeddedPortablePdb**），
  补丁后 **0 个**。
- **修复**：
  · `Internal/EmbeddedPdb.TryRead`：从 PE 解出内嵌可移植 PDB（`MPDB` + Int32 长度 + deflate）——
    dnlib 的 PDB 读取只认磁盘 `.pdb`（CodeView 路径），**不认内嵌**，所以要自己解；
  · `UIStringExtractor.LoadModule` 把解出的字节喂给 `ModuleCreationOptions.PdbFileOrData`；
  · `UITextPatcher.WriteModule`：有 `PdbState` 时 `PdbFileKind = EmbeddedPortablePDB` + `WritePdb = true`
    （单文件、不用管旁挂 .pdb 的搬移/还原）；写 PDB 失败退回不带 PDB 写，保证补丁能打上。
- **fgtest 回归**（第 ⑰ 项）：拿 Collections 原件真打一遍补丁，断言输出仍有可解析的 `EmbeddedPortablePdb`。
- **外挂便携 PDB 同样要保**（1.3.17）：很多插件把 PDB 放在 DLL 旁（`XxxPlugin.pdb`，CodeView 指过去）。
  `EmbeddedPdb.TryRead` 在没内嵌时读同目录外挂 PDB，写出时统一转成**内嵌可移植 PDB**
  （单文件，不用管旁挂文件的搬移/还原）；fgtest 双样本（内嵌 Collections / 外挂 TeleporterPlugin）回归。
  只有 Windows PDB（MSF，老 `Xxx.pdb`）转不了——那种保持原样丢弃。
- **对已坏的旧补丁（2026-10-03 实测盘点）**：修复前打过补丁的 DLL 都丢了调试目录。全量扫 37 个状态：
  **15 个原件本来有符号、补丁后丢了**（Aetherphone / AutoHunt / AutoRequeue / BDTHPlugin / Battlevest /
  BigPlayerDebuffs / BlueMageHelper / Browsingway / Cammy / CharacterPanelRefined / ContactsTracker /
  CurrencySpender / DalamudRepoBrowser / Deliveroo / FootIk）。逐个查过：**这 15 个都不引用
  `System.Diagnostics.StackFrame`**（只有 `Path.GetFileName`，不需要 PDB），所以没有实际故障；
  Collections（唯一出事的）在修复后重打过，已带符号。
- **怎么修**：**直接再点一次「一键汉化」或「写入并重载」即可**——补丁器会先按 `PatchedHash`
  把盘上的旧补丁还原成原件（原件连带符号），再在原件基线重打，输出就是带 PDB 的。
  （早期文档写「先还原原文再打」是保守说法；两条路都行，原生重打不需要手动还原。）
  插件将来更新/重打时也会自动带上符号——不必专门处理。

## 发布前独立评审的修复（2026-10-03，1.3.21–1.3.22）+ 包装库文字方法的 ### 泄漏修复（1.3.23）

三路代码核查（抽取器 / 补丁管线 / 投稿与上传）+ 两路 HCI 审查 + 两路交叉验证后的修复批次。只记会影响以后改动的口径与新纪律；逐条证据在会话记录里。

**上传 / 公共库口径（服务端 `inbox_uit.py`，改动最大）**：
- **容器名放宽**：除 `<程序集名>.resources` 外，现在收 `file:<相对路径>`（本地化文件）与 `json:<资源名>.json`（DLL 内嵌语言表）。
  以前只认第一种，带本地化文件 / 内嵌 JSON 译文的投稿会被服务端整批拒掉，而客户端只看到中继成功（假成功）。
- **Source 分级写入**：投稿条目按各自的 `Source` 处理——`user`（人工）才允许覆盖已有译文；机器译**只补空槽**，
  绝不覆盖已有内容。以前服务端无条件覆盖并标 `Source=user`，一份旧模型的机器译会把人写的译文永久顶掉。
- **缺包不再退回**：库里没有该插件的包时**新建空包**再收（客户端只在中继成功时报「已上传」，服务端直接退回等于谎报）；
  新包会随下一次库更新进 `index.json`，可直接被其他玩家下载。
- **失败也留档**：`accepted == 0` 的投稿同样写 `docs/contributions/inbox/`（以前只在成功时留档）。
- 回话与摘要会分开报「补缺 / 覆盖人工译 / 跳过（机器译已有内容）」。

**客户端投稿链路**：
- 行「一键上传」的 payload **不再缩进**（中继上限 60000 字符，压缩能多装条目）；读包 / 体检 / 序列化挪到**后台线程**（渲染线程零重活）。
- 提交页回退按**转义后的 URL 长度**判断（以前按未转义正文长度估，中文正文会膨胀约 9 倍、URL 可能被截断）；
  URL 放不下时导出文件并**教玩家把内容粘贴进正文**（以前让人「拖附件」——工作流从不读附件，投稿等于丢）。
- 打开提交页失败不再谎报「已替你打开」；不可达的 `Info` 死分支删掉。
- 「一键上传」与编辑器提交都跳过**标了「不翻」**的条目（资源 / 属性同步，以前只跳 entries）。
- 上传确认框恢复「去向 + 公开性」说明（会成为一个公开 GitHub issue；中继不可用时需要你在网页按 Submit）。

**补丁管线（性能与安全）**：
- `UITextPatchManager.Tick` 的复核与「更新后自动重打」整段挪到**后台任务**（以前在渲染线程做 dnlib 全量读改写 + 每插件反射重建索引）。
- `Apply` / `Restore` 加互斥锁（后台自动重打与界面操作并发时会互相删对方刚建的备份）；`StatusOf` / 包读取加「写入时间 + 长度」缓存，
  编辑器不再每帧全量 SHA-256、列表不再每 5 秒整批读盘解析。
- **状态保存失败时不再清理本次备份**（`Save` 现在返回 bool）——以前写状态失败后仍按「没人引用」删备份，会留下「打过补丁、无记录、无备份」的 DLL。
- 库合并（`MergeLibrary`）加**占位符闸门**：库译文丢了 `{0}` 之类的不写进包（打进去会让插件运行时抛 FormatException）。
- 机器翻译回写不再顶掉资源 / 属性上已有的译文（同一原文值对应多个 key 时，以前会把人工译文覆盖掉）。
- 清账（`PruneAgainstExtraction`）标出的「不翻」现在会置 `ReapplyNeeded`——否则「已经是最新」短路会把盘上的旧译文留下。
- 反向还原：原文自带 `###` 的条目补登「补丁产物 → 原文」精确映射，且精确映射优先于 `###` 后缀（以前这类字面量还原不回来）。
- 资源译文写回统一用 `Trim` 后的值（与还原时的比较口径一致）；写 PDB 失败会记日志并在 outcome 上留 `PdbDropped` 标记。

**抽取器**：
- 包装前缀（`Dalamud.Interface.Utility.` / `ECommons.ImGuiMethods.` 等）分支也过 `NonTextCalls` 过滤，并补进 `PushId` / `Child` / `BeginTable`
  ——`ImRaii.Child("ScrollingPlugins")` 这类纯 ID 串不再进候选（以前会带 `###原文` 写进界面）。

**界面（HCI 评审的 P1 批次）**：
- 行首加展开指示 `▸/▾`（悬停说明「点这一行展开：重新抽取 / 编辑校对 / 云端译文」）——「编辑校对」只在展开区里，以前没有任何可见入口。
- 「应用云端译文」两处指引改成「主按钮（写入并重载）」（以前写「一键汉化」，而那个状态下按钮显示的是「写入并重载」）。
- 指引里指向已删除「详情」按钮的句子改成「点这一行展开 → 「编辑校对…」」。
- 筛选「待应用」改名「待写入」，并把「有改动待写入」的行补进来（以前这类行会从筛选里漏掉）；「失败」筛选只留补丁失败的行。

**已知未修（留给后续）**（快照：以下六项已于当日 1.3.24 全部处理，见下方「未修项清零」）：
- 全局去重强制的 `###原文` 会给非标签字面量加后缀（B-08，涉及 ID 撞车与显示泄漏的权衡，先不动）。
- `file:` 容器的陈旧指针仍可能往中文文件写孤立键（B-13，P3）。
- 「与原文相同，等于没翻」仍与空译文/超长并列成 problems（C-06：品牌名这类无解条目得靠「不翻」处置；以后再细分一类）。
- 反向还原的极端碰撞：同一条译文同时是「普通条目」与「自带 ### 条目」的补丁产物时仍有歧义（复审 P3-c，极罕见）。
- 手工编辑 / 导入的译文不经占位符检查（B-10 只堵了库合并一条路，补丁层闸门未加）。
- 抽取器灰名单的若干细化（A1/A3/A4/A5）、中继投稿幂等键（C-10）。

**修复批次的独立复审（2026-10-03，针对 `d840e40` 的 diff）**：结论 OK with notes，无 P1。据复审补两处：
① 服务端 `plugin` 字段加字符集/长度校验（缺包建包让「未校验字段拼路径写盘」变得可达，P2）；
② 清账计数不再把「已经是自动排除」的条目重复计入（否则 `ReapplyNeeded` 恒真、「已经是最新」永久失效，P2——本条是本批次自己引入的回归）。

**本机受影响插件（重打一次「一键汉化」即自动清掉 ###）**：LeveHelper（Sponsor）、HaselTweaks（Sponsor/Design/Ambient Lighting/Directional Lighting）、
heliosphere-plugin（49 条：Help/Tooltip/TextUnformattedCentred/通知文本）、InventoryTools（8 条 tooltip + 3 条图标资源名）、Battlevest（5 条 tooltip + 8 条 LineCentered 缓存 id）、
Browsingway（2 条居中错误文字）、Cammy（2 条通知）、CurrencySpender（2 条通知 + 7 条 LineCentered）、DalamudRepoBrowser（2 条通知）、BossModReborn（1 条通知标题）、Brio（1 条通知标题）。
AutoHook 有 10 条 TooltipOnHover 候选但未打补丁，无需处理。重打的原理：`ExtractionSourceOf` 从原始备份重抽 → MergeExtraction 发现 PreserveID 变了 → `ReapplyNeeded` → 写入时自动先还原旧补丁再按新写法写入。

## 未修项清零（2026-10-03，1.3.24）——复审遗留全做掉

**抽取器**：
- **B-08**：去重强制的 `###` 只作用于「可能被当控件标签」的条目（`UITextPatcher.MightBeLabel`）：判据是抽取时记的调用目标（RoleReason）——Text* / tooltip / 包装库画文字方法不加 ###（加了会被原样画出来），其它或没有信息时保守保留。
- **A1**：查表路径（get_Item / TryGetValue / ContainsKey）的键名现在也标记为「字典键」（新增 `ParamsDictionaryKey`，随参数流 / 字段流一起传播）：短键不会再被当成「外部表 key」直接丢掉，长键的理由也不再写成「词典式汉化」。
- **A3**：跨程序集「同文异角色」（一份 UI、一份 Excluded）在合并时降为灰名单，不再按「更强判定」直接翻（否则会在功能语境那份 DLL 里也改字面量）。
- **A4**：日志类型判定收窄（Serilog / *Logger / `.Log` 命名空间段 / 以 Log 收尾；Login、LogHeader 不再误伤）；本程序集内的日志包装方法改走 ⑤ 登记参数流（外部日志照旧快速放行）。
- **A5**：`ResourceKeyCount` 改为按字面量 ID 去重（以前两遍扫描 + 一条多处出现会虚高）。

**管线**：
- **B-10**：补丁层加占位符闸门（entries / resources / attributes 三处都拦），拦下的条数写进完成消息；除库合并那道闸门外，手工编辑 / 导入的译文现在也进不去 DLL。
- **B-13**：本地化文件写入加「陈旧指针」闸门——英文侧已没有的 key 不再往中文文件补孤儿键（英文侧定位不到时回退旧行为）。
- **P3-c**：反向还原遇歧义（补丁产物同时被别的条目的 `###` 后缀认领）时放弃（返回 null），不再把错的原文写回。

**投稿 / 云库**：
- **C-06**：「与原文相同」从「需要修的问题」里拆出来单独归类（品牌名等无需翻译；上传弹窗与编辑器状态分开说，不再让用户去找不存在的修法）。
- **C-10**：`inbox_uit.py` 加内容指纹幂等——同一份内容重复到达（中继超时重交、issue edit 重跑）会被识别并跳过，不再重复并入 / 重复通知。
- **云库不受 `###` 泄漏影响（结论）**：库里存的是干净译文，`###` 是打补丁时按 `PreserveID` 即时拼的；`uit_library_build.py` 的 `PreserveID` **每轮都从当轮 probe 重算**（不沿用旧包），所以只要发布用修好的版本，云库不会带错标记。非目标插件（投稿自动建包）的 `PreserveID` 现在由投稿携带（只置 true、不用缺失值清旧值）。

**界面**：
- **P2-3**：完全汉化的行主按钮由「一键汉化」改「重新汉化」（悬停说明：会重抽，有变化才重打）。
- **P2-4**：翻译进行中显示「已用 X 分 X 秒 · 约剩 Y」（完成 10 条以上才估）；「取消只停翻译」从 tooltip 提到状态条常显。
- **P2-8 + 跨页规则**：新增 `UiHelpers.PushDangerButton`（红系）——插件汉化行内 / 弹窗的「还原原文」、仓库体检的工具条与「确认删除」统一用它；规则：主操作=蓝、启用=绿、破坏=红，橙色只做警示色。
- **V1-07**：徽标带分母（`已汉化 981/1083 条`，全齐时才只写条数）。
- **术语**：本页的共享译文来源统一叫「公共译文库」（上传弹窗、按钮、状态行、来源标签、编辑器菜单同一口径）。

## 离线批量重打 + 两处新修复（2026-10-03，1.3.25）

- **查表 key 分支必须排在 UI 分支之前**（`UiStringExtractor` ③b→③ 前移）：`ImGuiService.GetImageTexture`、`ImGuiEx.LineCentered` 这类长着 ImGui 名字的查表包装器，原来会被 UI 分支先吃掉；InventoryTools 的 3 条图标资源名、Battlevest / CurrencySpender 的居中宽度缓存 id 都是这么被翻掉的。现在按 key 排除；存量条目重打时自动淡出。
- **PDB 里含「本地常量」时不再整份丢弃**（`UITextPatcher.WriteModule`）：dnlib 写 Portable PDB 撞上无法序列化的本地常量（如类类型非空常量）会抛 `Expected a null constant`；现在捕获后——**把常量条目整个移除再试**（只丢常量值，文件名 / 行号保留）。注意不能只把值清成 null：I4 常量会改报 `Expected an Int32 constant`（2026-10-03 实测定型）。HaselTweaks / Browsingway / Cammy / DalamudRepoBrowser 的 PDB 借此恢复。
- **离线重打工具**（`fgtest-refactor/Repatch.cs`，`dotnet run -c Release -- repatch <内部名...>`）：从原始备份抽取 → 合并译文包 → 重打补丁 → 更新状态与清单；替换前另存临时安全副本，全程不写本地化文件、不自动重载（重打后需重启游戏 / 重载插件生效）。用途：批量应用抽取器规则修正。
- **BossMod 候选集核对（排除「抽取回归」疑云）**：新旧抽取器对 BossModReborn 的候选差集只有 **14 条，全是 ID 串**（`FiltersPanel` / `party` / `uiobj` 等，NonTextCalls 的正确排除）；其余从包里淡出的条目在 1.3.20 抽取器里同样不存在——属于历史代抽取器的正常淡出，非本次回归。

## 术语表单字条的误用事故与修复（2026-10-03，1.3.26）

- **现象**：`disable→封技`、`unknown→不明物体`、`refresh→醒神`、`warning→倒计时`、`generate→魔力炼成`、`convert→转魔`、`breaking→霹雳舞`、`threshold→回退预备`、`rotation→转向`、`source→始源湖`、`content→表情：幸福`……（游戏数据里的地名 / 表情 / 技能名撞上界面高频普通词）。本机存量受损：**385 条、跨 27 个插件**（BossModReborn 最重，resolve/tankbuster/rotation 合计 240）。
- **根因**：① 术语表把「单字专有名词」也喂给模型，提示还写着「必须采用」——模型对普通动词 / 形容词照用不误；② `The Source`（地名）这类带冠词条目的「去冠词变体」（→ `source`）绕过了单字门槛。
- **修复（三处）**：
  · 生成脚本 `ffxiv_glossary.py`：单字门槛 + 23 个实测受损词的泛词黑名单；**「去冠词变体」也过同一套过滤**；
  · 运行时 `FFXIVGlossary.StopSingle`：同名单（更老版本插件读旧表时也能兜底）；
  · 提示分级（`TranslationChannels`）：多词条仍「必须采用」；**单字条改成「仅在原文里作为专有名词时采用，普通含义按常规翻」**。
  术语表已重新生成：31,768 → **31,582 条**。
- **修复与上云（2026-10-03 完成）**：三轮重翻共 **557 条**（430 + 33 + 94，含属性 / 资源段；0 拒绝），随后重打受影响插件（SimpleTweaks 属性段 617 条复原、AutoHook 45 条等）。
  公共译文库已重新上线：**44 插件 · 10410 条目 · 326 资源 · 666 属性**（`uit-packs/*.json` + `index.json`，PreserveID 一律 false、Source=library、排除本机「不翻」）；同步修复版管线（inbox_uit.py 容器/Source 分级/幂等、export 排除本机不翻、ffxiv_glossary.py 单字过滤 + 去冠词过滤、ffxiv-glossary.tsv 31582 条、uit-rules.json）。
  公开仓推送提交 `de6e9fc`（main，未发版；CI 自动生成仍按 10-02 决定暂停，手动 dispatch 重建前需先同步插件源码）。

## 格式串 / 内部 ID 不进翻译候选 + 部分失败不再报红（2026-10-07，1.4.0.3）

- **起因**：Ktisis 一键汉化从公共译文库补入 133 条后，剩 26 条送机器翻译；其中 25 条被通道「原样退回」（`%Zone%` / `%d s` / `%.3f L` / `Ktisis.ApiVersion` / `GET##ApiVersion` 这类技术串），1 条未返回；旧判定「写入 0 + 失败 > 0」直接标红「翻译失败：失败 1 条」，连 133 条译文也没写进插件——看起来像「下载坏了」。实际是**任务判定**问题，下载与补入都正常。
- **候选过滤**（`UITextText.LooksUntranslatable`，`TranslationTargets` + `BuildTranslateItems` 双闸）：格式串（百分号占位符拆掉后剩 ≤3 个字母）、含 `##` 的内部 ID、点分内部名（`Ktisis.ApiVersion` / `config.json` / `v1.2.3`）不再选进候选、也不进通道；`%d days` 这类拆完还剩真实单词的照常送翻。
- **状态判定**：只有「通道整条不可用」或「回来的比没回来的少」才报红；绝大多数候选只是「原样退回」、个别没回来时——照常把已翻好的写进插件，状态行改用中性提示（`N 条与原文一致（多为格式串或内部名，无需翻译）；M 条未返回，点「重试」可再试一次`）。
- fgtest 断言：`%d s` / `%.2fy` / `%0.0f deg` / `%.3f L` / `%Zone%` / `GET##ApiVersion` / `Ktisis.ApiVersion` 过滤；`%d days` / `Gaze Gizmo?` / `Save###save_button` 不误伤；`BuildTranslateItems` 去重 + 过滤。

## 编辑器「全量重翻」（2026-10-07，1.4.0.4–1.4.0.5）

- **场景（用户定）**：下载了质量不行的公共库 / 玩家包后，想用自己的大模型通道把它们整个换掉——但旧行为「机器翻不覆盖已有译文」把这些条目挡在候选外。
- **入口**：编辑器工具栏「全量重翻 (N)…」→ 确认框（默认焦点在「取消」，说明覆盖范围）。
  （1.4.0.5 晚调整：从「更多」菜单提到工具栏，顶替原「还原原文」的位置；还原移进「更多」菜单，两段确认照旧。）
- **目标集合** `UITextEditorWindow.RetranslateTargets(includeUser)`：不再要求「没译文」，与「翻译未翻」同一套角色规则（UI + 勾选时的灰名单 + 资源 + 属性），`Skipped`（不翻）的除外。
- **覆盖范围**：`AcceptTranslations` 新增 `overwriteExisting` / `overwriteUser`（默认 false，旧调用一切不变）——
  资源 / 属性条目原来「已有译文就不动」，全量重翻传 `overwriteExisting: true` 才覆盖；
  `user` 源（本机手改 + 下载包里带的手改）默认仍保护，确认框勾选「连手动改过的 N 条一起覆盖」才传 `overwriteUser: true`。
  字面量条目由目标选择控制（旧行为不变：字面量按目标选定后直接写入）。
- **流程**：与普通翻译共用 `StartTranslate` 管线（同一通道 / 进度 / 取消 / 失败汇总），状态行与日志带「全量重翻」标记；
  翻完只改本机译文包，仍要点「写入并重载」才生效。
- fgtest：资源/属性的「默认只补槽 → overwriteExisting 覆盖 → overwriteUser 才盖手改」三层断言。
- **下载计数核实（同日）**：基础包与玩家包的下载量都按包上报（`FetchPackFileAsync` 成功联网拉取时 `ReportLibraryDownload(插件, 包ID)`，
  基础包 ID=library、玩家包 ID=user-xxxxxx），【列表详情 — 下载译文】与【一键汉化的自动合并】两条路径都走它；
  实时值由中继 `/library-counts` 提供（详情面板 5 分钟节流刷新），索引里的数字由 `library-counts.yml` 每 6 小时写回（因此仓库里的 index.json 最多滞后 6 小时）。
  本机 24 小时缓存命中不会重复计数（那不是一次新下载）。

## 合并分级 + 下载健壮性（2026-10-07，1.4.0.6）

- **合并分级（用户定）**：**人工 3 > 大模型 2 > 基础包 1 > 免费 0**；同级别再比新旧（自动合并按「基础包 →
  玩家包从旧到新」，同级后合的赢，即同级新包赢）。实现：`UITextKinds.Rank` + `UITextPack.MergeLibrary`
  （入口 / 资源 / 属性三套一致）；`PreviewMerge` 同步按等级算「更新 / 保留」。效果：
  · 本机用大模型翻过的条目，不会再被下载来的「基础包 / 免费」包顶掉（以前会被顶）；
  · 免费译盖不动基础包；大模型译能盖基础包；人工译依旧最大。
- **保护名单** `UITextMergeGuard`：自动合并开始前把本机「手改过且已翻」的条目拍成快照；合并期间新写入的
  玩家手译不在名单里——否则「同级新包赢」会被旧包的玩家手译挡住（`existing.IsUserSource` 一刀切的问题）。
- **inbox 保留机器分级**：`scripts/inbox_uit.py` 收稿时不再把非 user 一律压成 `library`——白名单保留
  `ai:llm` / `ai:deepseek` / `ai:google` / `ai:mymemory` / `ai:caiyun` / `ai:public-caiyun` / `ai:deepl`
  （其余含本来的 `library` 一律写 `library`）。**已有的包维持粗等级**（机器译 = 基础包级），新投稿才带分级。
- **下载解析校验**：`UITextLibrary.FetchTextAsync` 加 `accept` 回调，索引与包都在换地址前先验证能不能解析出来
  （实测 `gh.atmoomen.top` 对 `RotationSolver@user-ce073e.json` 返回过 473KB 的截断文件，重取才恢复完整 629KB）；
  全部地址都过不了才按「拉不到」处理（15 分钟冷却）。
- fgtest：等级合并（大模型盖免费、基础包盖不动大模型、同级可刷新、人工最大、免费盖不动基础包）、
  保护名单（本机手改被快照挡住、同级按合入顺序判新旧）、预检按等级计数；另做过变异测试验证断言会红。
