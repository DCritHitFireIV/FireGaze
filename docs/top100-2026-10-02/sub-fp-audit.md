# 假阳性审计：UI 候选里“像功能串”的 2931 条

> 任务 D 产出 · 只读分析 · 2026-10-02
> 输入：`top100/analysis/fp-suspects.tsv`（列 `internal / text / reason / context`；`reason` 实为抽取器判定的 UI 去向，如 `→ ImGui.TextUnformatted`）
> 参照：`top100/extract/*.json`（抽取器逐条判定）、UITextProbe 的 `--trace`（IL）、ilspycmd 反编译（26 个插件，输出在 `%TEMP%/fpaudit/decomp/`，报告内以 `<plugin>.decompiled.cs:行` 引用）
> 结论证据等级：**[验] = 反编译/IL 已验证**，**[抽] = 抽取条目原文**，**[推] = 样本推断（未见 IL）**

---

## 0. 结论摘要

2931 条 “UI 候选” 里，**绝大多数不是可翻译的界面文字**，而是被抽取器的三处近似判定误收进来的：

1. **本地化/资源 key**：`Languages.get_Xxx` 这类强类型资源属性返回 `ResourceManager.GetString("Xxx")`，因为“返回值流向 UI”，抽取器就把 `"Xxx"` 这个 **key** 记成了 UI 文案（Reason 直接写 `→ ImGui.Checkbox`）。共 **1455 条 / 16 个插件**（TidyChat 502、ChatTwo 422、WrathCombo 119、AutoHook 69、HoardFarm 62、Wholist 54…）。
2. **本地 wrapper 的 key 参数**：LightlessSync 的 `CharaDataHubLocale.R/RF(key)` 实现是 `ResourceManager.GetString(key, Culture) ?? key`，同样因返回值进 UI 而把 key 记成 UI 文案。LightlessSync **801 条**里有 **796 条是标识符形态**，抽样 7 条 UI 去向的全部是 key/格式串。
3. **“类型名含 ImGui 就算 UI 调用”**：`ImGui.SetDragDropPayload`、`PushID`、`BeginChild`、`TableSetupColumn`、`SetClipboardText`、`GetID`、`GetProcAddress` 等也被算作 UI，导致 **110 条**根本不会画到屏幕上的字符串进入候选。

分类汇总（类间有重叠，明细见 §5）：

| 类别 | 条数 | 说明 |
|---|---:|---|
| 强类型资源 key（`get_X` 且 text==X） | 1455 | 必须剔除 |
| LightlessSync 标识符（R/RF key 等） | 796（该插件共 801） | 必须剔除 |
| 非绘制 sink（ID/DnD/剪贴板/原生符号/贴图） | 110 | 必须剔除 |
| 文件对话框过滤器（`{.ext}` / `Name{.ext}`） | 51 | 必须剔除 |
| `CommandInfo.HelpMessage` | 23 | 混合：key 剔除；含命令原文的走灰名单 |
| `Notification.Content` | 19 | 全部是 ChatTwo 资源 key |
| 日期/时间格式串 | 37 | 必须剔除（当格式符用） |
| 含 `{0}` 等占位符 | 193 | 分两类：纯格式模板剔除；自然语言句可翻但必须保占位符 |
| URL / `://` | 97 | 基本剔除（多为“标签==跳转目标”） |
| 路径 / 资产扩展名 | 64 + 18 | 必须剔除 |
| 全大写常量（多为 DnD payload） | 19 | 必须剔除 |
| 已是中文/韩文/日文等 | 79（中文 42） | 跳过或交给插件本地化系统 |
| 窗口标题（`→ 窗口标题`） | 12 | 标识符形态的走灰名单 |
| 散文型（≥25 字符、有句子的空格、无括号/路径/URL） | **仅 30 条** | 其中最可能真该翻的就是这撮 |

**抽样与验证**：从 2931 条中分层随机抽 **78 条 / 58 个插件**（种子 20261002，覆盖全部特征类与大簇）；其中 26 个插件做过反编译核对，前 44 条样本中 40 条在反编译源码里找到了使用点。已验证据可复核：反编译命令
`ilspycmd -o <out> --ignore-decompilation-errors <plugin.dll>`（26 个插件产物保留在 `%TEMP%/fpaudit/decomp/`）。

---

## 1. 必须从候选中剔除的模式（规则 + 真实例子）

### R1 强类型资源属性 / 本地化查表 key（最大一类，1455 + ~800 条）

判定规则：
- `context` 匹配 `\.get_(\w+)$` 且 `text == $1` → **剔除**（16 插件命中 1455 条）；
- `text` 作为参数传给已知查表 API（`ResourceManager.GetString`、`…Locale.R/RF`、`.Tr()/.TrId()`、`Loc.Localize(key, …)`）的第 0 个参数 → **剔除**；
- 证据共性：key 被查表后 fallback 回显，翻 key = 查不到 → 界面反而显示译文 key。

已验证例子：

| # | 插件 | 条目 | 证据 |
|---|---|---|---|
| 1 | TidyChat | `EconomyTab_ShowGilWithdrawnMessage`（`→ ImGui.Checkbox`，`Languages.get_…`） | `TidyChat.decompiled.cs:15850`：`internal static string EconomyTab_ShowGilWithdrawnMessage => ResourceManager.GetString("EconomyTab_ShowGilWithdrawnMessage", resourceCulture);` **[验]** |
| 2 | ChatTwo | `ChatType_Alarm`（`→ ImGui.Checkbox`） | `ChatTwo.decompiled.cs:15800` 同样的 `ResourceManager.GetString("ChatType_Alarm", …)`；使用点 `:19841` `ChatType.Alarm => Language.ChatType_Alarm` **[验]** |
| 3 | WrathCombo | `Info_searchHintText` | `WrathCombo.decompiled.cs:16558` `… => ResourceManager.GetString("Info_searchHintText", …)`；`:9899` `ImGui.InputTextWithHint("##settingsSearch", Info_searchHintText, …)` **[验]** |
| 4 | ChatTranslated | `Wizard_Engine_Header` | `ChatTranslated.decompiled.cs:5197` `… => ResourceManager.GetString("Wizard_Engine_Header", …)`；`:2678` `ImGui.TextUnformatted(Resources.Wizard_Engine_Header)` **[验]** |
| 5 | EngageTimer | `Settings_FWTab_TextAlign_Right` | `EngageTimer.decompiled.cs:3665` `… => ResourceManager.GetString(…)`；`:2502` Combo 项由 `"Settings_FWTab_TextAlign_Right".Tr() + "###Right\0"` 拼出（key 查表 + `###ID` 分离） **[验]** |
| 6 | Wholist | `UserInterface_Settings_NearbyPlayers_Job_RedMage` | `Wholist.decompiled.cs:1349` resx 属性；`:506` `ColourEdit.Draw(Strings.UserInterface_…, …)` **[验]** |
| 7 | VanillaPlus | `GameConfigCommand_CommandHelp`（`→ CommandInfo.HelpMessage`） | `VanillaPlus.decompiled.cs:595` resx 属性；`:15602` `HelpMessage = Strings.GameConfigCommand_CommandHelp` **[验]** |
| 8 | LightlessSync | `Mcd_ColumnContents`、`Mcd_AccessRestrictionsHelp`、`IdDisplay_GroupEditHint` | `LightlessSync.decompiled.cs:171429-171441`：`R(key) => ResourceManager.GetString(key, Culture) ?? key`；`:28418` `TableSetupColumn(CharaDataHubLocale.R("Mcd_ColumnContents"),…)`；`:91219` `InputTextWithHint("", IdDisplayLocale.R("IdDisplay_GroupEditHint"), …)`；IL 侧 `--trace`：`[il] 00EA Ldstr … Mcd_AccessRestrictionsHelp → Call CharaDataHubLocale::RF` **[验]** |
| 9 | PlayerTags / HoardFarm / Dresser / DailyDuty / CharacterPanelRefined … | 同形态 `Strings.get_Xxx` / `UIStrings.get_Xxx` | 未逐条反编译；形态与 #1-#7 完全一致 **[抽]** |

> 注意与 R5（灰名单）区分：**查表 key 参数**必须剔除；而 `Loc.Localize(key, fallback)` 的 **fallback 参数**、`Questionable._L("文本")` 的文本参数是“显示兜底”，属于灰名单（见 §3 G5）。

### R2 sink 本身不绘制文字（ID / 拖放 / 剪贴板 / 原生 / 贴图）——110 条

判定规则：`reason` 末段 ∈ {`SetDragDropPayload`,`AcceptDragDropPayload`,`PushID`,`PushId`,`BeginChild`,`BeginTable`,`BeginTabBar`,`TableSetupColumn`,`GetID`,`OpenPopup`,`SetKeyboardFocusHere`,`SetClipboardText`,`GetProcAddress`,`GetModuleHandle`,`TryGetTextureWrap`} → **剔除**。
这些字符串即使被抽取器标了 `PreserveID=True` 也不能翻：补 `###原文` 会破坏匹配，不补会破坏 ID。

分布：TableSetupColumn 23、BeginChild 14、SetDragDropPayload 13、PushID 12、PushId 11、SetClipboardText 9、BeginTable 7、TryGetTextureWrap 6、GetProcAddress 4、AcceptDragDropPayload 3、GetID 3、OpenPopup 3、BeginTabBar/GetModuleHandle 各 1。

已验证例子：

| # | 插件 | 条目 | 证据 |
|---|---|---|---|
| 10 | AetherDraw | `AETHERDRAW_PAGE_DRAG`（`→ ImGui.SetDragDropPayload`） | `AetherDraw.decompiled.cs:2489` `SetDragDropPayload("AETHERDRAW_PAGE_DRAG",…)`；`:2498` `AcceptDragDropPayload` 同一常量；`:2599` 又一处。翻它 = 拖拽对不上 **[验]** |
| 11 | Glamaholic | `TREE_NODE`（`→ ImGui.AcceptDragDropPayload`） | `Glamaholic.decompiled.cs:3421` `SetDragDropPayload("TREE_NODE",…)`，`:3215`/`:3431` `AcceptDragDropPayload("TREE_NODE")` **[验]** |
| 12 | ReSanctuary | `ReSanctuary_CreatureMap_`（`→ ImGui.PushID`） | `ReSanctuary.decompiled.cs:820` `ImGui.PushID("ReSanctuary_CreatureMap_" + creatureId)`——纯 ID **[验]** |
| 13 | ActionTimelineReborn | `ui/uld/icona_frame_hr1.tex`（`→ ThreadLoadImageHandler.TryGetTextureWrap`） | `ActionTimelineReborn.decompiled.cs:2128`/`:2145` 贴图加载路径，不是文字 **[验]** |
| 14 | BossModReborn | ``] G{q.Unknown11} ``（`→ ImGui.GetID`） | 抽取条目：Sink=GetID，Context=`DebugQuests.Draw`；文本还是 C# 插值产物（`{q.Unknown11}` 是反编译出的插值占位） **[抽]** |
| 15 | PenumbraPreviewManager | `igSelectable_BoolPtr`、`cimgui.dll` | 抽取条目 Sink=`GetProcAddress`/`GetModuleHandle`，Context=`ImGuiHookManager.Initialize`——原生导出名/模块名 **[抽]** |
| 16 | UntarnishedHeart | `STEP_REORDER_`（`→ ImGui.SetDragDropPayload`） | 抽取条目；与 #10 同类（payload 前缀 + 序号拼接） **[抽]** |
| 17 | AutoHook | `FOLDER_ORDER`/`PRESET_ORDER`/`PRESET_IN_FOLDER`（`→ ImGuiDragDrop.SetDragDropPayload/AcceptDragDropPayload`） | 抽取条目，DnD 协议常量 **[抽]** |

### R3 文件对话框过滤器 / 扩展名——51 条

判定规则：`text` 含 `{.`（`\{\.\w+`），或 `text` 以 `.ext` 结尾且 `reason` 是 `FileDialogManager.Open/SaveFileDialog` → **剔除**。
理由：`"Label{.ext1,.ext2}"` 是给它解析的过滤器语法；翻 Label 有风险、翻后缀必坏。

已验证例子：

| # | 插件 | 条目 | 证据 |
|---|---|---|---|
| 18 | Ktisis | `{.ktlight}` | `Ktisis.decompiled.cs:21965`：`Filters = Ktisis.Locale.Translate("file.dialog.light.filter") + "{.ktlight}"`。**这条同时说明正确的做法**：可翻的是 key `file.dialog.light.filter`，`{.ktlight}` 必须原样保留 **[验]** |
| 19 | Penumbra | `Shader Program Blobs{.o,.cso,.dxbc,.dxil}` | `Penumbra.decompiled.cs:39914` `FileDialog.OpenFilePicker("Replace …", "Shader Program Blobs{.o,.cso,.dxbc,.dxil}", …)` **[验]** |
| 20 | SonarPlugin | `.json` | `SonarPlugin.decompiled.cs:8667` `public const string LanguageExtension = ".json";`；`:1336` 正则 `"^.*\\.lang\\.json$"`；`:8891` 过滤器 `".json{.json}"`——**const 还会被编译器内联**，只补一处 `ldstr` 会导致同一常量在不同位置值不一致 **[验]** |
| 21 | Aetherphone | `{.png,.jpg,…}.*` / `{.mp3,.wav},.*` / `{.mp4,…},.*` | 抽取条目（FilePicker），纯过滤器 **[抽]** |
| 22 | Brio / MareSynchronos / DisPlacePlugin / AetherDraw | `{.chara}`、`.png`、`.json`、`PNG Image{.png,.PNG}` | 抽取条目，同形态 **[抽]** |

### R4 格式串 / 日期时间格式——37 条 + 纯格式模板

判定规则：
- `text` 仅由日期时间记号构成（`y/M/d/H/h/m/s/f/t` + 空格/`/`/`:`/`,`/`-`/`\`，如 `hh\:mm\:ss`、` yyyy/MM/dd HH:mm`）→ **剔除**；
- `%d/%s/%f`、`{0:F1}` 这类**没有自然语言单词**的模板 → **剔除**；
- 有自然语言单词 + `{0}` 的（如 `Bite time: {0}`）→ 保留给翻译，但**必须校验占位符完全一致**（见 §2 P1）。

已验证例子：

| # | 插件 | 条目 | 证据 |
|---|---|---|---|
| 23 | Altoholic | ` yyyy/MM/dd HH:mm`、` MM/dd/yyyy HH:ss tt` 等 9 条 | `Altoholic.decompiled.cs:13116-13131` `FormatDate(…) => format switch { 2 => $"{date: yyyy/MM/dd HH:mm}", … }`；选中的格式串既当选项标签画出来、又当 `DateTime` 格式符 **[验]**——这就是典型灰名单，绝不能进 UI 翻译 |
| 24 | Accountant | `({0:F1}, {1:F1}, {2:F1})` | 抽取条目 `→ ImGui.Text`；纯格式模板，翻掉 `F1`/括号后 `string.Format` 结果不可控 **[抽]** |
| 25 | AutoDuty / Autofate / BOCCHI / ChilledLeves / ContactsTracker / KangasTweaks / KeitaToolbox / MapPartyAssist / Mappy / Moodles / HoardFarm | `hh\:mm\:ss`、`hh\:mm\:ss\.FFFF`、`dd\:hh\:mm\:ss`、`mm\:ss`、`%d ms`、`<se.%d>`、`Item Level %d`、`{0:D2}h:{1:D2}m:{2:D2}s` | 抽取条目；sink 是 Text/SliderInt/CalcTextSize，但文本是 `.ToString(format)` 的**格式参数**，不是最终显示值 **[抽]**（共 37 条，25 插件） |

### R5 URL / 路径 / 资产串——97 URL + 82 路径/扩展名

判定规则：`^https?://`、含 `://`、`^[/\\]`、含 `\`、以资产扩展名结尾（`.png/.tex/.uld/.json/.scd/.atex/.dds/...`）→ **剔除**。
其中“超链接按钮”的坑：`Button/Hyperlink` 的标签就是 URL，**点它打开/复制的是同一串**，翻标签 = 改行为。

已验证例子：

| # | 插件 | 条目 | 证据 |
|---|---|---|---|
| 26 | ActionTimelineReborn | 见 #13 | 资产路径 **[验]** |
| 27 | SonarPlugin / Ktisis / Penumbra | 见 R3 | 过滤器同源 **[验]** |
| 28 | Glamaholic / EurekaTrackerAutoPopper / ChilledLeves | `https://ffxiv-eureka.com/`、`https://puni.sh/api/repository/veyn` 等（`→ ImGui.SetClipboardText`） | 抽取条目：Sink 就是剪贴板；URL 本身即数据 **[抽]** |
| 29 | AcquisitionDate | `/lodestone/character/?q=`、`/lodestone/character/` | 抽取条目 Context=`LodestoneIDRequest.GetURL`/`CharacterRequest.GetURL`——URL 拼接片段 **[抽]** |
| 30 | FCNameColor / FrenRider / ProxyPlugin | Lodestone 示例 URL、`https://www.google.com/generate_204`（InputTextWithHint / SetTooltip） | 抽取条目；#30 的 `generate_204` 是探针目标，翻不得 **[抽]** |
| 31 | Messenger | `%appdata%\XIVLauncher\pluginConfigs\Messenger\` | 抽取条目；路径提示，翻了用户没法照抄 **[抽]** |

### R6 标识符 / 全大写常量 / `##` ID

判定规则：`^[A-Za-z_][A-Za-z0-9_]*$` 且（sink 属 R2，或 context 显示进入 ID/payload/查表）→ **剔除**；`^[A-Z0-9_]{4,}$` 尤其可疑。
`##`/`###` 前缀的隐藏标签另定：`###` 后面原文是 ImGui ID，翻译补丁必须保留 `###原文` 后缀（现有补丁机制已知会加），但**标识符形态的窗口/控件名**如果本来就是 ID（如 `MissFisher_MiniWindow`），应进灰名单。

已验证例子：见 #10–#17；另 `[68] BossModReborn ] G{q.Unknown11} ` 含反编译插值残留 **[抽]**。

### R7 已经是中文/韩文/日文的条目——79 条（中文 42）

判定规则：文本含 CJK/谚文/西里尔 → 不得作为“英译中”候选。
已验证例子：**ArmoireButlerPlugin** 竟然有 39 条被收进 UI 候选，`ArmoireButlerPlugin.decompiled.cs:10089` 显示它们是四语词典 `LocalizedText("角色：{0} @ {1}", "Player: {0} @ {1}", "キャラクター：{0} @ {1}", "캐릭터: {0} @ {1}")` 里的韩文槽位——抽取器把字典值当成了待翻文案（样本 #40 『캐릭터: {0} @ {1}』） **[验]**。
同类：MareSynchronos『W/Q/P/D代表什么？』、Brio 中文过滤器标签、Yourcraft 7 条 **[抽]**。

### R8 CommandInfo.HelpMessage——23 条（混合类）

- `Strings.get_Xxx` 形态（VanillaPlus / EngageTimer / DailyDuty / Wholist / PlayerTags）：**资源 key → 剔除**（#7 已验证）。
- 原始帮助文本含命令语法（如 Malmstone `/pmalm <rank> <all/cc/fl/rw> -- Displays PVP games left…`）：命令 token 不能翻，整条翻译需专门处理 → **灰名单/默认剔除** **[抽]**。
- 纯中文帮助（CrescentAuto）→ R7。

### R9 Notification.Content——19 条

全部是 ChatTwo 的 `Language.get_Xxx` 资源 key（`ChatTwo.decompiled.cs` 同 #2 形态）→ **剔除** **[验/抽]**。

### R10 窗口标题——12 条

`→ 窗口标题`。标识符形态（`MissFisher_MiniWindow`、`Tracking_TabBar`、`PresetBrowser_Content` 等）实际是窗口/控件 ID，**灰名单**；只有自然语言标题（`AnoMech Settings` 这类）才可翻并保留 `###原文` **[抽]**。

---

## 2. “其实可翻、只是长得像” 的模式

### P1 带占位符的自然语言句（可翻，必须保占位符）

| 例子 | 插件 | 判断 |
|---|---|---|
| `View on {0}` | ItemSearchPlugin（`→ ImGui.Button`，`DataSiteActionButton.GetButtonText`） | 按钮标签，`{0}` 是站点名；**可翻**，保 `{0}` **[抽]** |
| `Bite time: {0}` | DistantSeas（SetTooltip） | 可翻 **[抽]** |
| `Seen by {0}` / `{0} day` / `{0} days` | Aetherphone（CalcTextSize） | 可翻；中文无复数，两条可译成同一句 **[抽]** |
| `Queue the {0} quest(s)…` / `Duplicate completion flags: {0}` | Questionable（`_LF` 兜底文本） | 可翻/应走插件本地化系统（见 G5） **[验]**：`Questionable.decompiled.cs:17462` `LocalizeShortcut._LF("Duplicate completion flags: {0}", …)` |
| `Use symbols {0} {1} {2} {3} {4} …` | WhereAmIAgain | 长帮助文本，可翻，占位符顺序敏感 **[抽]** |
| `Groove: {0} -> {1}` | WorkshopOptimizerPlugin | 可翻 **[抽]** |

> 判定口诀：**有自然语言单词的 `{n}` 句子 = 可翻**；只有格式记号/标点的 `{n}` 模板 = 剔除（R4）。

### P2 纯散文（最该保留的 30 条）

样本内已抽到的：Accountant 63 分钟农作物说明（`→ TextUnformatted`，`CropCache.DrawTooltip`）、FrenRider 预设说明、HDM 时间轴说明、PyonPix URI 说明、Wordsmith 反馈说明、AetherDraw `e.g., a direct link to a .png or .jpeg from Imgur.`。这些没有任何查表/路径/ID 语义，**可直接翻** **[抽]**（未逐条 IL 验证；形态无风险）。

### P3 显示用提示串（可翻但建议保留技术形态）

- VFXEditor `vfx/action/some_texture.atex`：`VFXEditor.decompiled.cs:39390` `InputTextWithHint("##Add", "vfx/action/some_texture.atex", ref AddPath, …)`——是输入框灰字示例。翻了不会坏功能，但示例路径最好保持 ASCII 原样 **[验]**。
- AetherDraw `e.g., a direct link to a .png or .jpeg from Imgur.`：纯提示文案，可翻 **[抽]**。

### P4 以“标识符”形态显示的插件数据（可翻但有风险）

- VFXEditor `SE_UI`：`VFXEditor.decompiled.cs:33362` `new CommonRow(num++, "sound/system/SE_UI.scd", "SE_UI", 0)`——`Name` 字段会显示在列表里，`Path` 是另一字段；列表支持按 Name 过滤。翻 Name 不影响 `Path`，但 Name 是用户识别游戏音效的稳定标签，建议保持 **[验]**。
- 结论：这一小类**不建议进 UI 翻译**，可进“保留原文”名单，不属于必须剔除但收益极低。

---

## 3. 灰色名单：既在 UI、又在别处被功能使用（应进灰名单而非 UI）

| # | 插件 | 条目 | UI 用法 | 功能用法（证据） |
|---|---|---|---|---|
| G1 | Altoholic | ` yyyy/MM/dd HH:mm` 等 9 条 | `ConfigWindow.DrawConfig` 用 `Selectable` 画成可选格式项 | `Altoholic.decompiled.cs:13116-13131` 同一串在 `FormatDate` switch 里当 `DateTime` 格式符；`:17974` `AppendFormatted<DateTime>(now, " yyyy/MM/dd HH:ss")` **[验]** |
| G2 | LightlessSync | `Mcd_ColumnContents` 等 | `TableSetupColumn` 表头文字 | 同时是表列 ID（表格设置持久化按名字算 ID）+ 资源 key **[验]**（#8） |
| G3 | Dresser | `MiragePrismBox_SetStorable`（`→ ImGui.TextWrapped`） | `Dresser.decompiled.cs:11804` `ImGui.TextWrapped(item.Handle)`（调试/列表窗口） | 同一串是 `UldBundle` 的 `Handle` 字段（`:12051` `new UldBundle(…, "MiragePrismBox_SetStorable")`），由反射枚举（`:11715-11730`）并写日志（`:11672`）。翻了会改动这个“数据名” **[验]** |
| G4 | SonarPlugin | `.json` | 打开/保存对话框过滤器 `".json{.json}"` | `const` 常量（`:8667`）* 正则（`:1336`）— const 内联导致补丁只改部分出现点 **[验]** |
| G5 | SonarPlugin / Questionable | `{0} fate(s) selected to sound` / `Duplicate completion flags: {0}` | 弹出的提示文字 | 是 `Loc.Localize(key, fallback)` 的 **fallback 参数**（`SonarPlugin.decompiled.cs:6509`）或 `_L/_LF` 的文本参数（`Questionable.decompiled.cs:2234`）；真正文案由插件本地化表提供 → 补丁应打到插件语言文件，打 `ldstr` 只会改兜底 **[验]** |
| G6 | EngageTimer | `Settings_FWTab_TextAlign_Right` | Combo 显示项 | 同项还拼了 `###Right\0` 作为 ImGui ID（`EngageTimer.decompiled.cs:2502`），且是 resx key（`:3665`） **[验]** |
| G7 | UntarnishedHeart / AetherDraw / Glamaholic / AutoHook | `STEP_REORDER_`、`AETHERDRAW_PAGE_DRAG`、`TREE_NODE`、`PRESET_ORDER` | 拖拽时随鼠标画的提示是另外的 `ImGui.Text("Moving …")` | 本串只用于 payload 匹配 **[验/抽]**（#10/#11/#16/#17） |
| G8 | BossModReborn | `] G{q.Unknown11} ` | Sink=GetID（调试窗口） | 插值 ID，非文案 **[抽]** |
| G9 | EurekaTrackerAutoPopper / ChilledLeves / AcquisitionDate | `https://ffxiv-eureka.com/`、仓库 URL、`/lodestone/character/` | 按钮/剪贴板 | 同一串是打开/复制/拼接的目标 **[抽]** |

灰色名单的落地建议：**默认不进 UI 翻译**；若确要翻，走“按 key 定点翻译 + 保留 ###ID/占位符”的专门通道，并且只在有插件作者确认时启用。

---

## 4. 建议落地规则（可直接写成过滤器）

```
DROP if (context =~ /\.get_(\w+)$/ && text == $1)                          # R1 强类型资源 key
DROP if (callee in {ResourceManager.GetString, R, RF, Tr, TrId, Localize, Translate, GetLocalized}
         && text is arg0 of that call && arg0 looks like key)              # R1 查表 key
DROP if (sink in NON_DRAW)                                                 # R2 110 条
DROP if (text =~ /\{\.\w+/ || text =~ /^\.\w+$/ || sink =~ /FileDialog/)   # R3
DROP if (text =~ /^[\s yMdHhmsfF:.,\\\/-]+$/ && text =~ /[yMdHhms]{2,}/)    # R4 日期时间格式
DROP if (text =~ /^[\s{}0-9FfDdXx:().,+-]+$/ && text =~ /\{\d/)            # R4 纯格式模板（无字母单词）
DROP if (text =~ /^(https?:\/\/|\/|\\\\|[A-Za-z]:\\)/ || text =~ /\.(png|tex|uld|json|scd|atex|dds|…)$/)  # R5
DROP if (text =~ /^[A-Za-z_][A-Za-z0-9_]*$/ && sink in {ID/payload/ID-lookup})  # R6
DROP if (text has CJK/谚文/西里尔)                                          # R7
GREY if (text is fallback/display arg of a localization API)               # G5
GREY if (text is arg0 label of ID-bearing call AND prose)                  # G2/G6
GREY if (text used as format spec AND drawn as option)                     # G1
KEEP if (sink in DRAWN && text has natural-language words && placeholders preserved)
```

配套校验：翻译产物必须过 `###`/`{n}`/`%x` 占位符一致性检查；ID 类调用补 `###原文`；`const` 常量与 `Dictionary` key 永不翻译。

---

## 5. 数字明细（对 2931 行的全量正则普查，类间重叠）

```
总行数 2931 / 119 插件
get_X 资源 key（text==getter 名）                1455  (16 plugins: TidyChat 502, ChatTwo 422,
                                                     WrathCombo 119, AutoHook 69, HoardFarm 62, Wholist 54 …)
LightlessSync 标识符形态                          796  (该插件共 801 行；验证为 R/RF key)
非绘制 sink                                    110
   TableSetupColumn 23 / BeginChild 14 / SetDragDropPayload 13 / PushID 12 / PushId 11 /
   SetClipboardText 9 / BeginTable 7 / TryGetTextureWrap 6 / GetProcAddress 4 /
   AcceptDragDropPayload 3 / GetID 3 / OpenPopup 3 / BeginTabBar 1 / GetModuleHandle 1
FileDialog 家族                                 51
CommandInfo.HelpMessage                         23
Notification.Content                            19  (全为 ChatTwo key)
日期时间格式串                                   37  (25 插件)
含 {n} 占位符                                   193
URL / ://                                       97
路径 64 / 资产扩展名 18
全大写常量                                       19
窗口标题                                         12
非英文（中文 42 等）                             79
散文型（很可能真该翻）                           30
```

---

## 6. 验证状态与残余不确定性

**已验证（反编译/IL，可复核）**：#1–#8（资源 key）、#10–#13（DnD/ID/贴图）、#18–#20（过滤器/const）、#23（日期格式）、#26（资产）、#40（韩文词典）、G1/G3/G5/G6。
抽样入口中 26 个插件完成反编译，前 44 条样本 40 条命中使用点。

**推断级（抽取条目形态 + 规则，无 IL）**：#9、#14–#17、#21–#22、#24–#25、#28–#31、P2/P3 的散文条目。
这些形态与已验证同类完全一致，风险低；若要 100% 确认需对相应插件再跑一次反编译。

**残余风险 / 局限**：
1. 抽取器按**文本去重**，同一字面量的多处不同用法被合并成一条；本报告的“UI + 功能双用”一栏因此主要靠样本反编译发现，可能仍有个别双用条目未被采样到（尤其只出现在单个插件里的罕见串）。
2. 样本 78/2931，大簇（LightlessSync/TidyChat/ChatTwo）抽了 3–7 条验证形态，但个别插件可能有自己的 key 系统（如 Sonar 的 `lang.json`、Questionable 的 `_L` 表），规则需要按插件白名单微调。
3. `const string` 的“内联不一致”风险只在 SonarPlugin 验证到；其他插件的常量形态未普查（建议对 `public const string` 做一次专项扫描）。
4. 未验证的散文条目（P2/P3）**看起来**安全，但仍应在正式翻译前过一遍占位符/`###` 检查。

**给流水线的直接建议**：把 R1–R7 作为“从 UI 候选剔除”的硬规则，G1–G9 输出到灰名单供人工定夺；预计可从 2931 条候选中去掉 **约 2600+ 条**（R1 1455 + LS 796 + R2 110 + R3 51 + R4 37 + R5 82 + R7 79 + R8 部分，扣除重叠），真正值得翻的是散文/带占位语句子为主的**几十到一两百条**量级。
