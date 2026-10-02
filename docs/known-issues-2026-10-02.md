# 未翻译 / 汉化问题分析与计划（2026-10-02 晚，用户批量反馈）

用户报了 6 个插件的一批问题。本文是**逐条定位的结果 + 修复计划**（本轮只分析，不改代码）。

## 一、逐条定位

| # | 问题 | 根因（实测定位） | 修复路径 |
|---|---|---|---|
| 1 | **AutoDuty** 一堆英文（`Board Menus` / `Rest at camps` / `Heal items` / `Prevent items with detrimental effect…` / `Waits on the specified plugins…` 等） | AutoDuty 的界面文本在 **`Localization/en-US|zh-CN/*.json`**（插件自带语言文件）；它的 zh-CN **缺 172 条**（递归统计，含嵌套段）。**而用户的 AutoDuty 是 17:37 汉化的——本地化文件支持 18:26 才上线（1.2.0.84），整整早 49 分钟**，所以一条都没抽到（包 resources 段 = 0） | **用户重跑**：对 AutoDuty 点「一键汉化」即可（会出 172 条候选 → 翻译 → 写入 zh-CN）。1.2.0.84 的实现提交里本来就验证过「AutoDuty 171 条」 |
| 2 | **InventoryTools**：`Dungeon Chest` / `Dungeon Boss Chest` / `These combine…` 仍是英文 | 这些已判为**灰名单**（可翻但默认不翻）；用户 23:04 重跑过一次但**没勾「批量翻译时连灰名单一起翻」**（补丁数只 +1） | 勾开关 → 重跑（详见操作清单） |
| 3 | **InventoryTools**：`部队理符交付`（应为「筹备稀有品」）、`精炼`（应为「分解」） | **机器翻译永不覆盖已有译文**——旧 AI 译文还在包里/补丁里（术语表 1.2.0.95 已补，但没触发重翻） | 编辑器里清除这两条译文后重翻；或做「按术语表强制重译」功能（列入待办） |
| 4 | **AcquisitionDate** 12 条 `Draw Dates for/on …` 未翻 | 文本**在 DLL 里**（UTF-16 命中）但抽取器判 **Excluded /「没有流向 UI 调用」**。探针 Context = **`Translator..cctor`**——它们在一个**静态构造器里建立的字符串表**里，消费点跨方法（从静态字段读），**数据流分析断链** | **抽取器盲区（通用）**：支持「静态构造器字符串表 / 跨方法字段流」→ 下轮专项 |
| 5 | **AutoHook**：汉化后**刺鱼设置点不了、左边几个框点不了**（P0，功能被破坏） | 高度疑似 **ImGui ID 冲突**：包里有 **18 组「多原文→同一译文」**（`Swimbait/Swimbaits→泳饵`、`Status/Statuses→状态`、`Mission/mission→任务`、`Gig/gig###gig→鱼叉`、`Territory/zone→区域`…）。ImGui 控件 ID 由 label 生成——**同一窗口里两个 label 翻成同一文本 → 第二个控件点不了** | **下轮修**（通用）：打补丁时检测「同一文件内译文重复」，自动给重复项补 `###原文`（ID 保持原文）。临时缓解：AutoHook 先「还原原文」 |
| 6 | **BlueMageHelper**：`(2nd Boss) Cuca Fera` | 文本在 **`spells.json`**（插件外部数据文件，非 DLL、非语言文件）——所有文件搜索只在它命中 | **评估结论：不做**。理由：① 数据文件不算「界面文本」，翻它超出本插件边界；② 会被插件更新覆盖，要另做一套「数据文件补丁 + 重应用」；③ boss 名应走官方译名（上游应读游戏数据而不是硬编码英文）——属上游问题 |
| 7 | **BossModReborn** 6 条（`Try to avoid traps` / `Cairn of Passage` / `Automatic mob targeting` / `non-boss floors` / `mobs to pull before pausing` 等） | 文本在 **59 MB 主 DLL 的「序列化数据块」里**（UTF-8、非 ldstr、非 .NET 资源——`--resources` 为空）。它是 BMR 自己的配置描述符数据（Crucible / DeepDungeon 等模块） | **评估结论：不做**。理由：① 数据是**带长度前缀/结构的自定义序列化**，中文（3 字节/字）与英文（1 字节）字节数不同，只换文本会**破坏二进制结构**；② 上游（BossmodRebornCN 中文分支）可以直接改自己的数据——属上游。**其余 5403 条正常** |
| 8 | （未点名但同因）AetherBlackbox / AetherDraw / AkuTrack / AllaganItemSearch / AnoMech / ArmoireButler / Artisan 等 **17:32–17:37 汉化的插件** | 它们的汉化早于 1.2.0.80+ 的多项抽取规则改进（继承链 getter、委托工厂、集合召回、本地化文件…） | **建议重跑**（会多抽到当时漏的文本） |

## 二、修复计划（按优先级）

- **P0 · AutoHook 破坏**（功能问题，最优先）：
  1. 读 AutoHook（6.0.2.4）源码确认「刺鱼/Gig」面板的实际控件与冲突对；
  2. 实现**通用修复**：打补丁时按文件统计「译文重复」→ 重复项自动 `###原文`（保 ID）；探针侧可选：把「疑似控件标签」的 PreserveID 判定放宽；
  3. fgtest 加断言（重叠译文 → 产出 `###原文` 形式）；
  4. 用户重打 AutoHook 验证。
- **P1 · 用户重跑清单**（不需要改代码，用户操作，见下）。
- **P2 · 抽取器盲区（AcquisitionDate 类）**：支持「静态构造器里的字符串表」+ 跨方法字段流；先读 AcquisitionDate 源码确认 Translator 的消费方式（决定翻了安不安全、要不要走灰名单）。
- **P3 · 旧译文更新**：给「按术语表强制重译」加一个入口（只重翻「术语表命中且旧译文是机器译」的条目），解决「部队理符交付/精炼」这类历史译文。
- **P4 · BMR 数据块 / BlueMageHelper 数据文件**：各自专项评估（成本高、收益中，先记录）。

## 三、用户操作清单（立刻可做）

1. **AutoHook**：先「还原原文」（恢复功能），等 P0 修复后再汉化；
2. **AutoDuty**：点「一键汉化」（会出 172 条）；
3. **InventoryTools**：翻译设置里勾「批量翻译时连灰名单一起翻」→ 点「一键汉化」；
4. **17:32–17:37 那批插件**（AetherBlackbox / AetherDraw / AkuTrack / AllaganItemSearch / AnoMech / ArmoireButler / Artisan / AutoHunt / BDTHPlugin）：**逐个重跑**（一键汉化）；
5. InventoryTools 的两条旧译（部队理符交付、精炼）：在「编辑校对」里清除译文后重翻（或等 P3 功能）。
