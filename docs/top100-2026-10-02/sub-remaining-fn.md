# 任务 B：top100 + 本机插件「剩余假阴性」的可复用抽取器缺口

- 材料：`top100/analysis/fn-suspects.tsv`（211 个插件、57,756 条有效行）、`fn-groups.tsv`、`summary.tsv`、
  `top100/extract/*.json`（260 插件抽取结果）。
- 只读校验手段：`UITextProbe.exe --trace/--json`（对 FrenRider.dll 实跑）、`UiStringExtractor.cs` / `UiCallSemantics.cs` 源码对照。
- 结论口径：只收「显示给玩家看的字面量」；日志/错误文本、IPC 名、throttle key、本地化 key、CLI 参数、文件路径、
  十六进制/纯符号，一律不算可恢复量（见文末「确认排除」）。
- 下文的「预计可恢复量」是按 reason × context 分桶估算的量级（低/中置信已标注），不是实测值；验证方法见最后一节。

## 0. 数据速览（fn-suspects.tsv 的前几大原因）

| reason | 条数 | 其中大部分实际是什么 |
|---|---:|---|
| 没有流向 UI 调用 | 9,518 | 静态表/数组元素（ctor/cctor 2,961）、Draw 内自定义 helper（1,188）、getter 返回值（975）、MoveNext（331） |
| 去向不明：ResourceManager.GetString | 5,472 | resx 本地化 key（**排除**） |
| 功能语境：StringBuilder.Append(-Line) | 5,155 | 92.3% 是 record 生成的 `ToString`/`PrintMembers` 调试文本（**排除**） |
| 功能语境：IPluginLog.* | ~6,000 | 真日志（**排除**） |
| 功能语境：String>.Add / Boolean>>.Add | 1,082 / 687 | List/Dictionary 元素、BossMod AddHints 提示（**可恢复池**） |
| 功能语境：返回值 | 666 | getter/工厂返回值（**可恢复池**，需分拣） |
| 去向不明：AddOption/9（801）、R/1（858）、.ctor/N（~1,200）、SeStringBuilder.AddText（166） | ~3,000 | 未解析到方法体的调用目标（**可恢复池**） |
| 去向不明：EzThrottler.Throttle（545）、GetIpc*（743）、TaskManager.Enqueue（314） | ~1,600 | throttle key / IPC 名 / 任务调试标签（**排除**） |

## 1. 总排名（按 预计可恢复显示文本量 × 通用性）

| 排名 | 缺口 | 预计可恢复显示文本 | 通用性 | 证据强度 | 修复成本 |
|---:|---|---|---|---|---|
| 1 | **容器元素流**：`List/HashSet/Dictionary` 装字符串 → 枚举/索引后画进 UI | 800–2,000（中） | 极高，跨 50+ 插件 | 强（同方法 UI/排除分裂） | 中 |
| 2 | **多元素字符串数组在数组初始化处丢失**（栈模拟与声明的 stack effect 不一致） | 300–1,500（低-中） | 极高，所有 Combo/ListBox/菜单 | 很强（IL+trace 实证） | **低（一处修正+回归测试）** |
| 3 | **调用目标解析缺口**：本地 helper / 编译器生成方法 / 已知外部框架（AddOption、R/1、SeStringBuilder、DtrBarEntry.Text） | 300–800（中） | 高 | 强（reason 聚类+样本） | 中 |
| 4 | **属性 getter / 返回值 → 泛型 UI/外部契约消费**（get_Name、GetCategoryName、GetFormattedName、get_ModuleInfo） | 200–600（中） | 高，FP 风险中 | 中-强（同方法分支分裂） | 中-高 |
| 5 | **文本构建器（StringBuilder/StringWriter）作为生产者 + record ToString 降噪** | 100–300（低） | 高 | 中（多为剪贴板/调试，非 UI） | 低-中 |

---

## 2. 缺口 1：容器元素流（List/Dictionary/HashSet → 枚举后进 UI）

**模式**：`list.Add("Label")` / `dict["k"] = "Label"` / `hints.AddHint("...")` 把显示文本存进集合，
之后 `foreach (var s in list) ImGui.Text(s)` 或表头/列/选项通过枚举集合画出来。当前数据流在 `.Add`
处就把它判成「功能语境」（`DangerousMethodNames` 含 `Add`，`UiCallSemantics.cs:127`；`IsDangerousCall`
对 `System.*` 的 `Add` 一律危险），集合本身没有“元素值”跟踪（`V` 只有 IDs/Params/CallKeys/FieldKey，
`UiStringExtractor.cs:55-71`；`IsArray` 只覆盖数组，不覆盖泛型集合），枚举时 `GetEnumerator/Current`
是外部调用，值链断开 → 条目永远到不了 UI。

**跨插件例子**（原文 | reason | context | fn-suspects.tsv 行）：

| 插件 | 原文 | reason | context | 行 |
|---|---|---|---|---|
| AutoHook | `Spearfishing preset rework` 等 199 条 | 功能语境：String>.Add | PluginChangelog..cctor | 8996 |
| BossModReborn | `Go to neurolink!` / `GTFO from neurolink!` | 功能语境：Boolean>>.Add | Hatch.AddHints | 12676 |
| InventoryTools | `Points` / `Completed` / `Title` | String>.Add | AchievementCompendiumType.BuildColumns | — |
| InventoryTools | `Rank Req.` / `Ceruleum Req.` | String>.Add | AirshipRoutesCompendiumType.BuildViewFields | — |
| ReMakePlacePlugin | `Interior` / `Exterior` / `Unused` | Boolean>>.Add | ConfigurationWindow.DrawItemListRegion | 47287 |
| CurrencySpender | `Currencies` / `Societies` | Boolean>>.Add | ConfigWindow.Draw | 17255 |
| Messenger | `Not Loaded` | Boolean>>.Add | TabHistory.DisplayChats | — |
| GatherBuddy | `Average` / `Large` / `Double Hook Yield: ` | String>.Add | Interface.GenerateSpotReport | — |
| Yourcraft | `NPC: `（同方法 `Actor: ` 已是 Ambiguous，证明链路只是部分断） | String>.Add | MainWindow.FindMapPresetConflicts | — |

**为什么现有数据流漏掉（假设，已核对 IL）**：`List<T>.Add(T)` 的返回值是 void、接收者是外部
`System.Collections.Generic.List`1`；`HandleCall` ④ 分支只把参数字面量标危险，不会把值“写进”接收者；
`List<T>::GetEnumerator/MoveNext/Current`、`List<T>::get_Item` 也都是外部调用 → ⑤⑥ 分支只产生
UnknownTarget/空结果。因此“存进集合 → 以后枚举出来”这条最常见的插件写法整段丢失。

**规则提案（通用）**：
1. 给 `V` 增加容器键（与字段伪方法同一套机制即可，`GetFieldScan/RecordFieldStore`，
   `UiStringExtractor.cs:2196-2242`）：`ICollection<T>.Add/Insert/Enqueue/Push/AddRange` 视为“写入接收者”，
   `GetEnumerator/MoveNext/Current/get_Item/ToArray/ForEach` 视为“读出接收者”，跨方法经字段伪方法合流。
2. 分类延迟到最终消费者：只有集合被当键查找（`ContainsKey/TryGetValue/get_Item` 且键位）才 `HardKey`；
   仅被枚举画出来则按 UI 处理。这与 `ImU8String`/插值处理器的既有思路一致（`UiStringExtractor.cs:1170-1178`）。
3. 对常见第三方 API 补语义：`AIHints.AddHint/AddOption`（BossMod，见缺口 3 的例子）、
   `HashSet<string>` 的 `Add` 等。

**回归样例**：`var l = new List<string>{"A","B"}; foreach(var s in l) ImGui.Text(s);` 以及
`hints.AddHint("X"); hints.AddOption("Y", ...);` → 期望 A/B/X/Y 全部 UI（无 `###`）。

---

## 3. 缺口 2：多元素字符串数组在数组初始化处丢失（实测：只有第 1 个元素被标 UI）

**模式**：`new[] { "A", "B", "C" }` / `new string[] {...}` 直接传给 `ImGui.Combo(..., ReadOnlySpan<string>)`、
`ListBox`、菜单项等。数组里第 2..n 个元素**在抽取阶段就消失**：既不进 UI、也不进 UnknownTarget。

**实证（FrenRider 1.4.0.0 `ConfigWindow.DrawUiSettingsSection`，`UITextProbe --trace` 输出）**：

```
[il] 0145 Ldstr 3->4 仅文字###Text Only
[il] 014D Ldstr 3->4 图标+文字###Icon+Text
[il] 0155 Ldstr 3->4 仅图标###Icon Only
[il] 015B Stloc_S 1->0 V_4
[ui] ... DrawUiSettingsSection/0 -> ImGui.SetNextItemWidth args=[仅文字+图标+文字+仅图标]   ← 三个值泄漏到这个 float 参数
[il] 0175 Call System.ReadOnlySpan`1<System.String>::op_Implicit(System.String[])
[ui] ... DrawUiSettingsSection/0 -> ImGui.Combo args=[DTR 栏模式###DTR Bar Mode |  | 仅文字###Text Only | ]
```

`--json` 复核：`Text Only` = UI(→ ImGui.Combo)，`图标+文字###Icon+Text` / `仅图标###Icon Only` =
Excluded「没有流向 UI 调用」。

**跨插件例子**（同一方法/字段里，同族标签只有 1 个变成 UI，其余 noflow；fn-suspects.tsv）：

| 插件 | 存活/丢失 | context | 行 |
|---|---|---|---|
| FrenRider | UI=Text Only；丢=Icon+Text / Icon Only | ConfigWindow.DrawUiSettingsSection | 20406 |
| PvpStats / PvpStatsCN | UI=IS；丢=IS NOT | ConfigWindow..ctor | 44681 |
| MarketBoardPlugin | UI=All；丢=Weapons / Equipments / Others | MarketBoardWindow..ctor | 38113 |
| DelvUI | UI=Center；丢=Left / Right / Top | AnchorAttribute..ctor | — |
| DelvUI | UI=Lowest；丢=Low / Mid-Low / Mid | StrataLevelAttribute..ctor | — |
| ascended-ezwondroustails | UI=0 decimals；丢=1 decimal / 2 decimals | ConfigWindow..cctor | — |
| MapPartyAssist | UI=Current；丢=Past 24 hours / Past 7 days | TimeFilter..cctor | — |
| BossModReborn | UI=Very Low；丢=Low / Medium / High | UIStrategyValue..cctor | — |
| AetherBlackbox | UI=Capture Settings；丢=Do Nothing / Chat Message | RecapConfigWindow.Draw | 502 |
| AcquisitionDate | UI=Url: ；丢=Queue State: | AcquisitionDebugWindow.DrawNetworkingTab | 172 |

（自动扫描「同 plugin+context 同时存在 Combo/MenuItem/Selectable UI 条目和 noflow 兄弟条目」共有 70 个
context，横跨 AllaganMarket、Altoholic、BossModReborn、DelvUI、Dresser、FrenRider、MapPartyAssist、
MarketBoardPlugin、Malmstone、Lifestream、LightlessSync 等。）

**为什么漏（代码级假设，需要修复时确认）**：`UiStringExtractor.cs:557-559` 声明的 `Stelem` 栈效应是
**(pop 3, push 0)**，但解释器 `:876-893` 在 pop 3 之后又 `Push(stack, array)`（net −2 而不是 −3），
与 `Dup` 组合后数组初始化的栈深每元素漂移 +1；配合分支 clamp（`Push` 里的 carried 逻辑）后，
第 2..n 个元素的 `Ldstr` 没有并进本地数组值，而残余值漂到后面的调用参数上（trace 里
`SetNextItemWidth args=[…3 个值…]` 就是漂移的直接证据）。第 1 个元素因为第一次 `Stelem` 时栈还对齐，
所以只有它活下来。

**规则提案**：
1. 修 `Stelem_*`：按声明的 (3,0) 执行（保留 `array.IDs.AddRange(value.IDs)` 的在对象原地合并），
   去掉多余的 `Push(stack, array)`；`Newarr/Dup/Ldelem/Initobj` 一起做栈深对账。
2. 加回归测试：3 元素 `string[]` → `ImGui.Combo(label, ref i, span)`，断言 3 个元素全部 UI、且
   `SetNextItemWidth` 之类后续调用的参数不会被污染；再测静态 `string[]` 字段/`ReadOnlySpan` 隐式转换。
3. 顺手把 `ReadOnlySpan<string>`/`Span<string>` 的 `op_Implicit` 显式识别为容器垫片
   （`UiCallSemantics.cs:409` 的 `IsStringConversionShim` 目前只认 ImU8String/ImGui），保证跨过
   `op_Implicit(string[])` 后元素不丢。

---

## 4. 缺口 3：调用目标解析缺口（helper / 编译器生成方法 / 已知外部框架）

**模式**：字面量传给了本插件内的 helper，但 `methodByKey` 查不到调用点目标 → 走 ⑥「外部方法、语义不明」，
只记 `去向不明：<ShortKey>`（`UiStringExtractor.cs:1341-1358`；`ShortKey` 只留 `方法名/参数个数`，`:2260`）。
典型聚类：

| reason | 条数 | 插件数 | 代表例子 |
|---|---:|---:|---|
| 去向不明：AddOption/9 | 801 | 1（BossModReborn） | `'No automatic movement'` @ Ex3TitanAIRotation.Definition（行 12897）——AI 选项文本，会在 BossMod 界面里显示 |
| 去向不明：R/1 | 858 | 1（LightlessSync） | `'GetInfoFromCode'` / `'LoadMcdf'` @ CharaDataHubUi.DrawDataApplication（行 32588）——按钮标签 |
| 去向不明：SeStringBuilder.AddText | 166 | 24 | AllaganMarket `'You currently have '` @ DtrService.SetDtrBar；AnoMech `'[AnoMech] Starting: '` @ Game.RunScenarioInternal；ChatTwo 等 |
| 去向不明：.ctor/1..4 | ~1,200 | 20+ | 多为数据/key（Aetherphone `'casino.rules'`、Beastmaster `'high-clear'`），少量显示文本（Aetherphone `'Sadist'/'Romantic'` @ VelvetKinks..cctor） |
| 去向不明：Run/3 | 145 | 1（Aetherphone） | `'ads directory'` 等任务标签（**不可恢复，排除**） |

另外两个直接相关但当前被误分类的点：
- **DtrBarEntry.Text 写入**：`IsDtrCall`（`UiCallSemantics.cs:233`）只认 `IDtrBar.Get/TryGet`，不认
  `DtrBarEntry::set_Text`。FrenRider `UpdateDtrBar` 的 `'Fren Rider disabled - Click to toggle. Coppelia: '`
  落在「没有流向 UI 调用」；AllaganMarket 的 DTR 文本也走 `SeStringBuilder.AddText → set_Text`。
- **本地 helper 命名**：现有白名单只有 7 个方法名（`UiCallSemantics.cs:219`），而插件普遍自建
  `DrawLabel/AddOption/R/AddHint/SectionTitle` 等。

**为什么漏（假设）**：① `MethodKey = 类型全名::方法名/参数个数`（`:2257`），调用点若是 **MethodSpec**
（泛型实例化）、**编译器生成的局部函数/闭包**（`<...>g__AddOption|...`、`R/1`）或 **另一个程序集** 的
方法，键对不上且 `DeclaredInModule` 为 false；② 外部框架类型没有语义表，`AddOption/AddHint/SeStringBuilder/
DtrBarEntry` 全落进 unknown。

**规则提案（通用）**：
1. 建 `(方法名, 参数个数) → [候选 key]` 的**回退索引**：唯一命中即解析；多命中时按
   `MethodSpec` 的泛型定义归一化（`Describe` 已经取 `spec.Method`，但 key 仍可能不一致，`UiStringExtractor.cs:2171-2183`）。
2. 归一化编译器生成名：`<X>g__Y|...`、`<X>b__...` → 归属 `X`，把 `R/1`、`Run/3` 这类接回宿主方法。
3. **已知生态语义表**（数据驱动，放 `UiCallSemantics`）：BossMod `AIHints.AddOption/AddHint`（第 1 个字符串 =
   显示文本）、Dalamud `DtrBarEntry.Text`/`IDtrBarEntry.Text` setter = UI、`SeStringBuilder.AddText` 同
   ImU8String 生产者处理、`SeString.op_Implicit` 等。
4. **跨程序集扫描**（可选，成本高）：对 `DeclaredInModule == false` 但能在同目录引用程序集里找到定义的方法，
   也建扫描/不动点。

---

## 5. 缺口 4：属性 getter / 返回值 → 泛型 UI / 外部契约消费

**模式**：文本由 `get_Name`、`get_ModuleInfo`、`GetCategoryName()`、`GetFormattedName()` 等返回，
再被（a）外部框架（Dalamud 插件列表、通用配置渲染器）读取，（b）泛型表格/标签页枚举对象属性，
（c）`ToString()`/switch 表达式消费。当前 `ClassifyReturns`（`UiStringExtractor.cs:1946-1975`）只在
“调用点的返回值直接进 UI”时才标 `ReturnsToUI`；反射/泛型/外部契约都看不到。

**跨插件例子**：

| 插件 | 原文 | reason | context | 行 |
|---|---|---|---|---|
| PvpStats | `PvpStats` | 没有流向 UI 调用 | Plugin.get_Name（插件列表显示名） | 44674 |
| PvpStats | `CC Records` | 没有流向 UI 调用 | CrystallineConflictRecords.get_Name | — |
| PvpStatsCN | `FL Records` / `RW Records` | 没有流向 UI 调用 | FrontlineRecords.get_Name 等 | — |
| NoTankYou | `Tanks` / `Summoner` / `Spiritbond` | 功能语境：返回值 | Tanks.get_ModuleInfo | 40253 |
| InventoryTools | `Mining` / `Botany` / `Venture`（同方法 `Leves` 已是 UI→ImGui.MenuItem） | 没有流向 UI 调用 | ItemInfoRenderService.GetCategoryName | 28740 |
| AllaganItemSearch | `General` / `Features` | 没有流向 UI 调用 | SettingTypeConfiguration.GetFormattedName | 4843/4844 |
| BossModReborn | `Very Low` 等（见缺口 2） | — | UIStrategyValue..cctor | — |

**注意误伤**：同桶里还有大量**不是**显示文本的 getter，必须过滤：
`VanillaPlus.get_ImageName`（`'WondrousTailsProbabilities.png'`）、`Aetherphone.get_Id`（`'clock.faces'`）、
`CharacterPanelRefined.Localization.get_*`（`'Tooltips_Crit_Tooltip'`，本地化 key）、HuntHelper 的
`'./data/ARR-A.json'`（路径）。规则必须带“像显示文本”与“非路径/非 key/非 ID”的判据。

**规则提案**：
1. **契约白名单**：`IPlugin.Name`、`Dalamud.Interface.Windowing.Window` 子类的 `WindowName`/标题、
   `IDtrBar` 文本、已知模块基类的 `ModuleInfo`（先用最小集合，避免误翻）。
2. **返回值精度修复**：`GetCategoryName` 里 `Leves` 已进 UI 而 `Mining` 未进的“同方法分裂”，优先查
   switch/字典分派下的返回流（`Ldelem`/`Dictionary.get_Item`/`MethodSpec`），并用回归样例
   `switch(x){ A=>"Mining", B=>"Leves" }` → `ImGui.MenuItem(GetCategoryName(x))` 锁定。
3. **对象字段经集合枚举后被画**：与缺口 1 共用容器/字段跟踪；`obj.Prop` 在 UI 里读取时把字段值视为 UI 输入。
4. **`ToString()` 覆盖**：记录/枚举型显示名常经 `ToString()` 进 UI，保持 `CallKeys`/`ReturnsToUI` 链路，
   同时排除编译器生成的 record `PrintMembers`（见缺口 5）。

---

## 6. 缺口 5：文本构建器生产者化 + record ToString 降噪

**模式 A（可恢复小）**：`sb.Append("...")` / `TextWriter.WriteLine` / `SeStringBuilder.AddText` 把片段攒进构建器，
最后 `sb.ToString()` 交给 UI。当前 `System.Text.StringBuilder` 只在 `Format/Concat/Join/Intern/Copy` 时算生产者
（`UiCallSemantics.cs:195,373-407`），`Append/AppendLine` 反而在危险名单（`:127`），片段被整段判成功能语境。
例子：GatherBuddy `Interface.GenerateSpotReport`（`Location: `/`Mission: `/`Average`…）——但复核 extract 后确认
它是**剪贴板/导出报告**，同插件 UI 条目是 `Copy Time Stats when reporting.` 这类勾选项，故实际可恢复显示文本很少；
Penumbra/Glamourer `GatherSupportInformation` 同理是复制到剪贴板的 markdown。**建议只做生产者化（让最终消费者决定），
不要靠这个冲量。**

**模式 B（净收益是降噪，不是恢复）**：`功能语境：StringBuilder.Append` 5,155 条中 4,728 条是 record 自动生成的
`ToString`/`PrintMembers`（LightlessSync `'PluginHostDependencies'`/`'Uri = '`；Aetherphone、BossModReborn、
AutoHook、Beastmaster、BazookaLens、MissFisher 等）。这些永远不该翻。建议在抽取器里显式识别
record 合成方法（`ToString`/`PrintMembers` + 方法名前缀 `<`）→ 直接归「非 UI」，把 fn-suspects 噪音砍 ~8%。

**规则提案**：把 `StringBuilder/StringWriter/SeStringBuilder` 当容器接收者（同缺口 1 机制），
`ToString/ToStringAndClear` 为读出；最终消费者是 UI 就进 UI、是日志/键就危险。单测：
`var sb=new StringBuilder(); sb.Append("A"); ImGui.Text(sb.ToString());` → A 应为 UI。

---

## 7. 确认排除（不要在这些方向花时间）

| 类别 | 数量级 | 证据 |
|---|---:|---|
| `ResourceManager.GetString` 的 key | 5,472 | 全是形如 `ActionChanging_Name`、`ActionHighlight_Configuration`、`AddCurrentBaitMooch`、`BetterStrings_CommendationSingular` 的 resx key（WrathCombo/VanillaPlus/AutoHook/TidyChat/EngageTimer/GoodFriend/LightlessSync 等） |
| throttle key | 545 | `EzThrottler.Throttle` 参数全是 `'InteractWithBell'`/`'BossChecker'` 之类的节流名 |
| IPC 名 | 743 | `GetIpcSubscriber/GetIpcProvider`（479/264） |
| TaskManager 调试标签 | 314 | `'WaitForStateToUseAction'`、`'ListQS99WaitStart'` 等 |
| CLI 参数 | 数十 | `'--no-warnings'`/`'--force-ipv4'`（Aetherphone） |
| 文件路径 | 数百 | `'./data/ARR-A.json'`、`'PluginImages/toolbar/*.png'`、`'/animation/a0001/'` |
| 日志/错误/异常文本 | ~7,000+ | `IPluginLog.*`/`Logger.*`/`AepLog.*`/`LunaLogger.*`；`'Invalid Vector2 index!'` 等异常消息 |
| record 调试 ToString | 4,728 | 见缺口 5-B |

## 8. 边界政策项（不进前五，需要产品决策）

- **IChatGui.Print：401 条 / 52 个插件**（`功能语境：IChatGui.Print`），例如 ActionTimelineReborn
  `'[ActionTimelineReborn] All timeline data cleared.'`、Altoholic `' has been blacklisted.'`。
  这些是**玩家可见的聊天输出**，不是日志；当前仅因为 `IsLogCall` 命中方法名 `Print` 被判危险
  （`UiCallSemantics.cs:466-479`）。同时 `IChatGui.PrintError` 落到「去向不明」（179 条），规则不对称。
  若产品接受翻译聊天文本，这是一块跨插件很广的稳定增量；若不接受，建议也在规则里写明并统一两边的 reason。
- **输出型文本**（GatherBuddy Spot Report、Penumbra/Glamourer Support Information）是剪贴板/导出文本，不建议翻。

## 9. 怎么验证修复效果（建议给后续 worker）

1. 按缺口 2 修 `Stelem` 与 `op_Implicit` 垫片后，重跑 top100 + 本机插件抽取，做前后 diff：
   - 期望：`没有流向 UI 调用`（9,518）与 `功能语境：String>.Add`（1,082）明显下降；
   - 期望：`UI` 总数上升，且**不新增** `Ambiguous`/`HardKey`（防误翻）。
2. 对新进 UI 的条目按 context 抽样人工复核（重点 ctor/cctor、Draw、get_Name、AddHints）。
3. 固化回归清单：FrenRider 3 元素 Combo、PvpStats IS/IS NOT、AutoHook changelog、BossMod AddHints/AddOption、
   LightlessSync R/1 按钮、InventoryTools GetCategoryName、GatherBuddy GenerateSpotReport（应仍为排除）。

### 主要证据来源
- `fn-suspects.tsv`（行号已随例标注）、`fn-groups.tsv`、`top100/extract/<插件>.json` 的 Role/Reason/Context；
- `UiStringExtractor.cs:55-71, 557-559, 865-904, 1170-1178, 1341-1358, 1426-1450, 1946-1975, 2257-2264`；
- `UiCallSemantics.cs:127, 195, 219, 233, 239, 373, 409, 417, 466`；
- FrenRider 实跑探针输出（`--trace DrawUiSettingsSection` / `--json`），关键行已在缺口 2 内联引用。
