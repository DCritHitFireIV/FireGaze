# 任务 C：本地化资源（.resx / satellite resources / 自定义本地化表）里的界面文本可恢复性

- 材料：`top100/analysis/{summary,fn-suspects,fp-suspects}.tsv`、`top100/extract/*.json`（260 插件抽取结果）、
  `C:/Users/Fire/top100.json`、本机 `installedPlugins/`（191 个）、`top100/dll/`（69 个未安装的 top100 样本，仅主 DLL）。
- 只读手段：`ilspycmd --list-resources / --resource / --dump-table`、`UITextProbe.exe --json/--types`、PowerShell
  `System.Resources.ResourceReader`、dnlib（本地 NuGet 缓存 `dnlib 4.5.0`）+ .NET 10 file-based app 的**临时 PoC**
  （全部在 `%TEMP%/fgloc-analysis`，未改动任何仓库源码、插件 DLL；PoC 只读写 DLL 副本，已清理）。
- 术语：下文「键」= `.resources` 容器的 entry 名；「UI 键」= 当前抽取器把该键的 ldstr 归类为 `Role=UI`（会被当作
  可翻译文本）；「RM 键」= 归类为 `Excluded / 去向不明：ResourceManager.GetString`。

---

## 0. 结论速览

1. **本机语料里 27 个插件把界面文本放在主 DLL 的 `.resources` 里**（另有 3 个插件是空容器、1 个只有图片、
   1 个只放 JSON 数据；见 §1.4），合计 **14,479 个键**；其中 21 个在 top100（4,170 键）。
2. **10 个已装插件带 satellite 资源程序集**（`<culture>/<插件>.resources.dll`），**8 个自带 zh**；ProxyPlugin 在主
   DLL 里内嵌了第二份 `StringsZh`。也就是说一半以上资源型插件的官方中文已存在，只是抽取器看不到值（见 §1.3）。
3. **抽取器现在对 key 的处理是分裂的**：6,041 个键被正确排除（`去向不明：ResourceManager.GetString`），但
   **5,461 个键（37.7%）被标成 `Role=UI`**，会进翻译包并被 `UITextPatcher` 当成 ldstr 替换 —— 一旦有译文，
   `ResourceManager.GetString` 会查不到而返回 null（PoC 实证），**界面变空**。任务 A（`sub-fp-audit.md`）靠
   `fp-suspects.tsv` 数出的 2,255 条（R1 1,455 + LightlessSync 796 + …）**全部是含下划线**的形态，另有 3,212 条
   PascalCase key（`AboutTab`、`BestEXPCalculationCalculate`…）根本没被它的启发式捞出来。
4. **「把 .resources 里的值也改掉」技术可行，PoC 已跑通**：dnlib 能枚举/替换嵌入式资源，`ResourceReader/Writer`
   能无损重写容器；改完的 DLL 运行时 `ResourceManager.GetString` 能读出译文，satellite 同样能改（见 §3）。
   主要成本不在技术，而在「键身份/语言路由/更新回滚」的工程化（§4、§5）。
5. 建议：**先立刻止损**（把查表 key 全部剔出可翻译集 + UI 标「资源型本地化，暂不支持」），再按插件价值决定是否
   投入资源级补丁通道；已有 zh 资源的 9 个插件不该做资源补丁（§5）。

---

## 1. 盘点：谁把界面文本放在本地化资源里

### 1.1 口径

对 260 个插件（top100 ∪ 已装）的主 DLL 逐个 `--list-resources`，取容器名以 `<程序集名>.**.resources` 形式出现、
且不是第三方库（FxResources/System.*/Humanizer/Autofac/…）的**嵌入式 `.resources` 容器**；对已装插件额外扫了
`<版本目录>/<culture>/*.resources.dll` 卫星。容器条目用 `ilspycmd --resource` 抽出，再用
`System.Resources.ResourceReader` 逐条统计类型（共 92 个容器、另 1 份重复抽取，15,785 条，含 3 个空容器与 1 个图片容器）。
`NoSillyReborn.SourceGenerators.dll`（构建期工具）与 `FSharp.Core` 不算。

### 1.2 主表：27 个用 `.resources` 存界面文本的插件

| 插件 | top100 排名 | 来源 | 容器 | 键 | 现状 UI（会翻 key） | 现状排除（RM） | zh 资源 |
|---|---:|---|---:|---:|---:|---:|---|
| LightlessSync | — | 已装 | 41 | 4,978 | **2,325** | 67 | zh 卫星 5,027 条 |
| WrathCombo | — | 已装 | 21 | 4,572 | 303 | **4,098** | zh-Hans 卫星 4,205（另有 zh-Hant） |
| TidyChat | 78 | top100 | 2 | 623 | **508** | 58 | 样本无卫星 |
| VanillaPlus | 17 | top100+已装 | 1 | 572 | 9 | 561 | zh 卫星 572（全覆盖） |
| AutoHook | — | 已装 | 1 | 547 | 224 | 227 | zh 卫星 478 |
| SubmarineTracker | 30 | top100+已装 | 1 | 475 | **414** | 48 | zh 卫星 475（全覆盖） |
| ChatTwo | 24 | top100 | 1 | 453 | **421** | 32 | 样本无卫星 |
| PlayerTrack | 65 | top100+已装 | 1 | 302 | 207 | 81 | zh 卫星 299 |
| PlayerTags | 97 | top100 | 2 | 235 | 23 | 212 | 样本无卫星 |
| EurekaTrackerAutoPopper | 11 | top100 | 1 | 234 | **193** | 41 | 样本无卫星 |
| DailyDuty | 10 | top100 | 1 | 215 | 1 | 214 | 样本无卫星 |
| EngageTimer | 26 | top100 | 1 | 165 | 95 | 65 | 样本无卫星 |
| WaymarkPresetPlugin | 37 | top100 | 1 | 163 | 112 | 51 | 样本无卫星 |
| ChatTranslated | 84 | top100 | 1 | 121 | 73 | 39 | 样本无卫星 |
| PriceCheck | 58 | top100 | 1 | 116 | 106 | 5 | 样本无卫星 |
| Wholist | 96 | top100 | 1 | 102 | 54 | 48 | 样本无卫星 |
| GoodFriend | 81 | top100+已装 | 1 | 98 | 5 | 93 | zh 卫星仅 17 |
| NoSoliciting | 56 | top100 | 1 | 88 | 82 | 5 | 样本无卫星 |
| HoardFarm | — | 已装 | 1 | 80 | 62 | 18 | zh 卫星 80（全覆盖） |
| CharacterPanelRefined | 20 | top100+已装 | 1 | 70 | 14 | 44 | 卫星 de/fr/ja，无 zh |
| ProxyPlugin | — | 已装 | 2 | 69 | 64 | 0 | **内嵌 StringsZh 69**（不走 RM，走 `Loc.T`） |
| ContactsTracker | — | 已装 | 1 | 63 | 60 | 2 | 无 |
| PeepingTom | 16 | top100 | 1 | 48 | 44 | 4 | 样本无卫星 |
| ReadyCheckHelper | 80 | top100+已装 | 1 | 34 | 24 | 10 | 卫星 de/es/fr/ja，无 zh |
| BetterPlaytime | 98 | top100 | 1 | 24 | 18 | 6 | 样本无卫星 |
| Hunty | 42 | top100 | 1 | 21 | 20 | 1 | 样本无卫星 |
| Visibility | 41 | top100 | 1 | 11 | 0 | 11 | 样本无卫星 |

> 合计：**27 插件 / 15,222 条容器条目 / 14,479 键**（键=插件内去重；全局去重 14,449，跨插件重复仅 30）。
> top100 子集：**21 插件 / 4,170 键 / 其中 2,423 键当前是 UI**。纯已装（不在 top100）：6 插件 / 10,309 键 / 3,038 UI 键。
> 「现状 UI/排除」按 `top100/extract/<插件>.json` 的 Role/Reason 统计（口径与 §2 一致），每键取最强角色（UI > Ambiguous > Excluded）。
> top100 未安装样本只下载了主 DLL（`ls top100/dll/Accountant` 只有 `Accountant.dll`），**卫星存在性无法核验**，表内写「样本无卫星」。

### 1.3 satellite 程序集（官方本地化）

| 插件 | culture 目录 | zh? | zh 条目 |
|---|---|:--:|---:|
| AutoHook | de/es/fr/it/ja/ko/ru/zh | ✔ | 478 |
| WrathCombo | de/fr/ja/ko/zh-Hans/zh-Hant | ✔ | 4,205（+zh-Hant） |
| LightlessSync | de/fr/zh | ✔ | 5,027 |
| PlayerTrack | de/es/fr/it/ja/nb/pt/ru/zh | ✔ | 299 |
| SubmarineTracker | de/fr/ja/zh | ✔ | 475 |
| VanillaPlus | de/fr/ja/zh | ✔ | 572 |
| HoardFarm | de/fr/ja/ko/zh | ✔ | 80 |
| GoodFriend | de/es/fr/ja/zh | ✔（仅 17/98） | 17 |
| CharacterPanelRefined | de/fr/ja | ✘ | — |
| ReadyCheckHelper | de/es/fr/ja | ✘ | — |

证据（条目原文）：
- `zh/AutoHook.resources.dll` → `AutoHook.Resources.Localization.UIStrings.zh.resources` 478 条；
  `--dump-table Assembly` 显示 culture=zh、PublicKey=nil。
- `zh-Hans/WrathCombo.resources.dll` → ...`CustomComboPresets.zh-Hans.resources` 3,600 条等，共 4,205 条。
- ProxyPlugin（内嵌双容器）反编译：`Loc.ZhResources = CreateManager("StringsZh")`，`Apply()` 里
  `Resolved == ChineseSimplified ? ZhResources : EnResources`，`T(key) => _active.GetString(key) ?? EnResources.GetString(key) ?? key`。

### 1.4 看起来像资源、其实不是界面文本的容器

| 插件 | 容器 | 实际内容 | 证据 |
|---|---|---|---|
| Wordsmith | `Wordsmith.Properties.Resources.resources` | **空容器 0 条** | Cor20 资源区仅 184B；`ResourceReader` 读出 entries=0 |
| MiniMappingway | `MiniMappingway.Properties.Resources.resources` | 空容器 0 条 | 同上（184B） |
| KodakkuAssist | `KodakkuAssist.Properties.Resources.resources` | 空容器 0 条 | 该 DLL 资源区 14.7MB（ttf/avfx），但 `.resources` 只有 180B、0 条 |
| NyaDraw | `NyaDraw.Properties.Resources.resources` | 14 条 `System.Byte[]`（图片），0 字符串 | PowerShell 类型统计 |
| AutoDuty | `AutoDuty.Properties.Resources.resources` | 2 条字符串，值是 BossMod 预设 **JSON** | `keys.tsv` 原文以 `{ "Name": ...` 开头 |

### 1.5 另一类：自定义本地化表（不在 .resx，风险相同）

- **HaselTweaks**：主 DLL 内嵌 `HaselTweaks.Translations.json`（`--list-resources` 第 2 行），结构
  `"key": {"en":..., "de":..., "zh":..., "ja":...}`，例：`"AnimationTimestamp" → "zh": "动画时间戳"`（DLL 内 UTF-8 命中）。
  `TextService.Translate(key)` 查这张表 —— 抽取器照样只能看到 key。
- **ProxyPlugin**：见 §1.3，双容器 + 自定义选择。
- 这两类与 `.resources` 的 key 问题同源，后文的「剔除 key」方案同样适用；它们的**值**也可恢复（读内嵌 JSON / 另一容器），
  但本文的补丁可行性验证只覆盖 `.resources`。

---

## 2. 抽取器现状：key 的两种命运

### 2.1 机制（代码定位）

- 强类型资源类由 `StronglyTypedResourceBuilder` 生成：
  `AutoHook.Resources.Localization.UIStrings` →
  `internal static string AboutTab => ResourceManager.GetString("AboutTab", resourceCulture);`
  （`ilspycmd -t AutoHook.Resources.Localization.UIStrings AutoHook.dll`）。
- ldstr `"AboutTab"` 是**外部调用 `ResourceManager.GetString` 的参数** → `UiStringExtractor.cs:1295-1297` 记
  `UnknownTarget=ResourceManager.GetString`（最终 reason `去向不明：...`，`UiStringExtractor.cs:2056`）。
- 同一字面量若所在属性的**返回值**被跟踪到 UI 调用，就会命中 `literal.UIViaReturn`（`UiStringExtractor.cs:1965-1972`），
  于是 `hasUI=true`（`:2011`）、走 `Role=UI`（`:2057`）。`HardKey` 兜底只覆盖「字典键名 / 方法名硬编码的查表
  （Translate/Localize…，`UiCallSemantics.cs:339-348`）」，**不覆盖 `ResourceManager.GetString` 的 key**。
- 结果：同一插件里一部分 key 被排除、另一部分 key 被当成 UI 文案。

### 2.2 实测分布（`top100/extract/*.json`）

| 插件 | 容器的键 | UI 键（危险） | Ambiguous 键 | RM 排除键 | 抽取里完全没出现的键 |
|---|---:|---:|---:|---:|---:|
| LightlessSync | 4,978 | 2,325 | 1,293 | 67 | 1,293 |
| WrathCombo | 4,572 | 303 | 171 | 4,098 | 0 |
| TidyChat | 623 | 508 | 57 | 58 | 0 |
| AutoHook | 547 | 224 | 96 | 227 | 0 |
| ChatTwo | 453 | 421 | 0 | 32 | 0 |
| PlayerTrack | 302 | 207 | 14 | 81 | 0 |
| …（其余 21 插件见表 1.2） | | | | | |
| **合计** | **14,479** | **5,461** | **1,686** | **6,041** | **1,293** |

条目级：`去向不明：ResourceManager.GetString` 的 entry 共 **6,042 条 / 26 插件**；`fn-suspects.tsv` 的
原始字节命中 6,020 处（该 TSV 存在未转义换行导致的解析噪声——脚本里能看到 internal 列混入 `CharacterId BIGINT ...`
之类的 SQL 片段，逐插件核对以 `extract/*.json` 为准，两边逐插件一致）。

实例（原文即键）：
- `AutoHook / AboutTab / UIStrings.get_AboutTab / Role=UI / Reason="→ ImGui.Selectable"`
- `SubmarineTracker / BestEXPCalculationCalculate / Language.get_... / Role=UI / "→ ImGui.Button"`
- `AutoHook / AddCurrentBaitMooch / UIStrings.get_AddCurrentBaitMooch / Excluded / "去向不明：ResourceManager.GetString"`

### 2.3 与任务 A（`sub-fp-audit.md`）的关系：少算了 3,212 条

`fp-suspects.tsv` 里「是资源键」的行共 2,255 条，**全部含下划线**（`Add_new_bait`、`Admin_AddNewBan`…）；
而抽取里 UI 角色的资源键条目共 5,469 条，其中只有 2,257 条（41.3%）含下划线。即
**PascalCase 形态的 key（`AboutTab`、`BestEXPCalculationCalculate`、`AbbreviateCharaNames`…）整类没被任务 A 的
启发式捞出来**：SubmarineTracker 的 414 个 UI 键在 fp-suspects 里 0 条（该插件 fp 清单总共只有 2 条）、
PlayerTrack 207 个里只有 1 条、ProxyPlugin 64 个 0 条。
任务 A 的 R1 规则（「get_X 且 text==X」等）方向正确，但落地清单以 fp-suspects 为准会漏掉 58.7%。

### 2.4 危害实证

- **PoC（运行时）**：`ResourceManager.GetString("关于###AboutTab", Invariant)` 与任意不存在的 key → 返回
  **null**（打印为空串）。当前 `UITextPatcher.cs:102` 会把 ldstr 直接换成译文（控件 ID 场景换成
  `译文###原文`，仍是新 key），所以被翻译的 key 必然查不到；`ImGui.Text(...)` 类调用拿到 null 轻则空文本/ID 撞车，
  重则在某些封装里抛异常。
- **旁证（本机真实配置）**：`XIVLauncherCN/pluginConfigs/FireGaze/uitrans/` 现有 9 个包里 2,075 条 AI 译文，其中
  **428 条是标识符形态原文**；HaselTweaks 一个包就有 112 条，例：`ShowCompletionStatus → "显示 CompletionStatus"`、
  `AnimationTimestamp → "动画时间戳"`（这些正是它的查表 key，随补丁写进 DLL 后查找就断了；`UITextPack.PruneAgainstExtraction`
  的注释也记录了同类历史事故）。本机 12 个现存包里**没有** `.resources` 型插件（AutoHook/TidyChat 等都没包），
  所以暂时没有线上 `.resources` 键翻车实例——但这只是因为还没抽到它们。

---

## 3. 「打补丁时改 .resources 值」的可行性（已 PoC 验证）

### 3.1 dnlib 能力事实

- `ModuleDef.Resources` 是 `ResourceCollection`，能枚举；嵌入式资源是 `EmbeddedResourceMD`，暴露
  `Name / Length / Attributes / CreateReader()`（`ilspycmd -t dnlib.DotNet.EmbeddedResource` 反编译，见附 B）。
- **不能原地改数据**：`EmbeddedResource` 没有 `Data` setter（只有 `DataReaderFactory+offset+length` 只读字段）。
  正确姿势：`mod.Resources.Remove(old); mod.Resources.Add(new EmbeddedResource(name, newBytes, old.Attributes));`
  然后 `mod.Write(path)`。
- `.resources` 是微软自描述格式（magic `0xBEEFCACE`），用 `System.Resources.ResourceReader` 读成
  `Dictionary<string,object>`、`ResourceWriter` 重写即可；dnlib 只负责字节替换。

### 3.2 主程序集 PoC（AutoHook 6.0.2.4，只动副本）

```
read  : 容器 47,446B / 547 条；AboutTab="About"
patch : remove+add(ResourceWriter 重写后 47,456B)，dnlib 写出 AutoHook.patched.dll
verify: ResourceReader 547 条，diff 原文/补丁仅 AboutTab 一行不同
runtime: Assembly.LoadFrom(patched) → new ResourceManager("...UIStrings", asm)
         .GetString("AboutTab", Invariant) = "关于（PoC）"     ← 原版返回 "About"
元数据 : ilspycmd --list-resources 仍列出 2 张 png + 该容器；-t UIStrings 仍反编译出 547 个 GetString
```

### 3.3 satellite PoC + 语言路由

把 `AutoHook.dll` 与真 `zh/AutoHook.resources.dll` 复制到临时目录：

| 操作 | `GetString("AboutTab", 该 culture)` |
|---|---|
| 原版 + culture=zh / zh-CN / zh-Hans / zh-Hant | `关于`（satellite 命中，父文化回退） |
| 原版 + culture=en-US / Invariant | `About`（中性资源） |
| **只改中性**（patched 主 DLL）+ zh | **仍是 `关于`** ← 中性补丁对已选中文的用户无效 |
| 只改中性 + Invariant | `关于（PoC）` |
| **改 zh 卫星**（`...UIStrings.zh.resources`，478 条保持）+ zh | `关于（卫星PoC）` |

### 3.4 风险清单（逐项核对本机语料）

| 风险 | 本机实测 | 结论 |
|---|---|---|
| 强命名 / 签名失效 | 27 个主 DLL + 10 个 satellite 的 Assembly 表 `PublicKey=nil`、Flags=0 | 本机无此问题；若将来签名，dnlib 写出会丢签名，需要重签或明确降级 |
| 资源格式 / hash 布局 | ResourceReader 读出全部容器 0 报错；资源键 14,427/14,449 是 ASCII 标识符，只有 8 个键含空格 | 只改 value 不动 key，格式兼容 |
| 非字符串资源 | 27 个文本容器 **100% 字符串**；另有 NyaDraw 的 14 个 `byte[]`、AutoDuty 2 条 JSON 字符串 | 现有容器可安全 dict 往返；若遇 Bitmap/自定义类型需 `System.Resources.Extensions` 或保留原序列化 |
| satellite 覆盖 | 只改中性不影响 zh（3.3 实证）；10 个已装插件 8 个带 zh | 需要「主 DLL + 各 culture 卫星」成套处理，且按插件语言路由（**AutoHook 自选** `UIStrings.Culture=new CultureInfo(CurrentLanguage)`，默认 `"en"`；**ProxyPlugin** 看 Dalamud UI 语言前缀 `"zh"`） |
| 运行时读取方式 | 27 个插件都是 `ResourceManager.GetString(key[,culture])`（ProxyPlugin 走 `Loc.T→_active.GetString`）；HaselTweaks 走自定义 JSON | RM 路径已验证；自定义表要单独实现（读 JSON 就行，但补丁格式不同） |
| 插件更新覆盖 | 更新会替换主 DLL + 卫星 | 现行 `UITextPatchManager.cs:214/220` 的 backup/restore 只盯 `entry.DLLPath`，必须把卫星文件纳入同一状态机 |
| 插件自校验 | 语料里未发现对自身 resources 的 hash/长度校验；资源读取都经 RM | 低风险，但建议补丁后断言 |

**判定：技术上完全可行、不依赖 hook；成本主要在工程侧（键身份、语言路由、回滚）。**

---

## 4. 成本 / 收益

| 方案 | 改动点 | 预估成本 | 风险 |
|---|---|---|---|
| A. 把查表 key 剔出可翻译集 | `UiStringExtractor` 的 `HardKey`（`:2015`）补规则：`ResourceManager.GetString` 的 arg0、`get_X && text==X`、`R/RF/Loc.T/Translate(key)` 的 key 参数 | 低（1 处抽取器改动 + 回归） | 极低；只在「key 已被翻进包」时需要清包 |
| B. 资源级补丁通道 | 抽取期 dnlib 读容器产出 `key→原文`；包格式按 `(container,key)` 存；补丁期 Remove+Add + 卫星发现 + 更新/回滚 | 中（新 pass + 包格式 + 管理器改动） | 中（satellite/语言路由/非文本容器，但已被 PoC 拆掉技术风险） |
| C. 只标记不支持 | UI 徽标 + 排除说明 | 低 | 无（但等于放弃 27 个插件的大头文本） |
| D. 运行时 Hook `ResourceManager.GetString` | 违反项目「不用 Harmony」约定 | — | 不可用 |

注：`.resources` 文本量（14,479 键）与 ldstr 可翻量同数量级，且很多插件（TidyChat/ChatTwo/SubmarineTracker…）**主要界面文本都在资源里**；
不处理 B 的话，这些插件的翻译覆盖率天花板很低（例：AutoHook 626 条 UI 候选里只有 402 条**不是**资源键）。

---

## 5. 建议（按执行顺序）

1. **立刻止损（必做，低成本）**
   - 抽取器把「查表 key」一律 `HardKey → Excluded`：
     - `ResourceManager.GetString/GetObject/GetResourceSet` 的**第 0 个字符串参数**；
     - 强类型资源属性：`context` 匹配 `\.get_\w+$` 且 `text == 属性后缀`（覆盖 PascalCase，补上任务 A 漏的 58.7%）；
     - 已知本地查表：`Translate/Localize/R/RF/Tr/TrId/Loc.T` 的第 0 参（沿用 `UiCallSemantics.cs:339-348` 的名字表）。
   - 抽包时对存量包做一次性清理：读主 DLL 容器键集合，把 `Original ∈ 键集合` 的条目标「不翻」并从补丁候选剔除
     （`PruneAgainstExtraction` 现在不会清它们，因为它们的 Role 仍是 UI）。
   - UI 给这 27 个插件打徽标：**「资源型本地化（暂不支持）」**，并显示「本工具只翻该插件的其它字面量」。
2. **按需上资源级补丁通道（已验证可行）**
   - 只对「没有官方 zh、且键多/界面可见度高」的插件开启（本机优先：TidyChat 623、ChatTwo 453、EurekaTrackerAutoPopper 234、
     PlayerTags 235、DailyDuty 215、EngageTimer 165、WaymarkPresetPlugin 163、ChatTranslated 121、PriceCheck 116、Wholist 102、
     CharacterPanelRefined 70、ContactsTracker 63…）。
   - 已自带 zh 的 9 个（AutoHook/HoardFarm/LightlessSync/PlayerTrack/SubmarineTracker/VanillaPlus/WrathCombo/ProxyPlugin/GoodFriend†）
     **不建议**资源补丁：直接让插件用自己的 zh（†GoodFriend 只覆盖 17/98，可单独考虑补差）。
   - 补丁策略：有目标 culture 卫星 → 改卫星（用户已选中文时唯一生效路径）；否则改主 DLL 中性资源；同时把 satellite 文件
     纳入备份/恢复/哈希状态；补丁后逐 key 断言 `GetString(key, culture) == 译文`，失败即整体回滚。
3. **如果两者都不做**：至少保证「明确不支持」不是沉默的——现在这种「key 被当 UI」的中间态是最危险的（既没翻到真文本，
   又会把 key 翻坏）。

---

## 附录 A：证据文件 / 命令

- PoC 全程在 `%TEMP%/fgloc-analysis`（只操作 DLL 副本；未改动任何仓库/插件文件），harness 全文见附 B。
- 关键命令：
  - `ilspycmd --list-resources <dll>` / `--resource <container> <dll>` / `--dump-table Assembly <dll>`
  - `UITextProbe.exe AutoHook.dll --json`（复现 224/227 拆分）
  - `ilspycmd --dump-table ManifestResource <dll>`（Wordsmith/MiniMappingway 容器 offset=0 → Cor20 区仅 184B）
  - PowerShell：`New-Object System.Resources.ResourceReader <file>` 逐容器统计（结果：92 容器 +1 份重复、0 读取错误；仅 NyaDraw 14 个 byte[]）
  - 交叉表：`top100/extract/*.json` × `keys.tsv`（容器键集合）→ 14,479 键 / 5,461 UI 键 / 6,041 RM 键
- 关联报告：`sub-fp-audit.md`（任务 A，key 泄漏只覆盖含下划线子集）、`sub-remaining-fn.md`（任务 B）。

## 附录 B：PoC（.NET 10 file-based app，`dnlib` 本地包）

```csharp
#:package dnlib@4.5.0
using System.Collections; using System.Globalization; using System.Resources; using dnlib.DotNet;

// patch <srcDll> <dstDll> <container> <key> <newValue>
using var mod = ModuleDefMD.Load(args[1]);
var res = mod.Resources.OfType<EmbeddedResource>().First(r => r.Name.String == args[3]);
var bytes = res.CreateReader().ToArray();
var dict = new Dictionary<string, object>();
using (var rr = new ResourceReader(new MemoryStream(bytes)))
    foreach (DictionaryEntry e in rr) dict[(string)e.Key] = e.Value!;
dict[args[4]] = args[5];
var ms = new MemoryStream(); var w = new ResourceWriter(ms);
foreach (var kv in dict) w.AddResource(kv.Key, kv.Value);
w.Generate(); var newData = ms.ToArray();
mod.Resources.Remove(res);
mod.Resources.Add(new EmbeddedResource(args[3], newData, res.Attributes));
mod.Write(args[2]);

// get <dll> <baseName> <key> <culture>：Assembly.LoadFrom + new ResourceManager(base, asm).GetString(key, culture)
```

（实测记录见 §3.2/§3.3；`ResourceWriter` 生成时不要 `using` 包住 `MemoryStream`——它 Dispose 时会关流。）
