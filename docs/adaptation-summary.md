# 190 插件适配盘点报告（2026-10-02 夜）

## 方法

全量探针扫描（190 个插件主 DLL，4 并发，**0 失败**）+ 规则分类 + 对 9 个可疑项人工复检。
原始数据：`probe-stats.json`（每插件统计）、`reports/scan.md`（异常清单 + 全表）。

## 分类结论

| 类别 | 数量 | 处置 |
|---|---|---|
| **推荐汉化**（UI 候选 ≥ 5） | **116** | 可「一键汉化」；完整清单 `recommended.json` |
| 自带中文（含汉化分支） | 34 | 已中文，跳过 |
| 不汉化名单（作者中文维护等） | 12 | 跳过 |
| 加壳 / 无字面量 | 10 | 不可抽（本来就无法汉化） |
| 其余（候选少、个别说明） | ~18 | 见 scan.md 异常清单 |

总计 UI 候选 **14366** 条（平均 75/插件）——适配面比预想健康。

**加壳/无字面量 10 个**：AbilityAnts、AlmondHousing、BigPlayerDebuffs、BlindBoxPlugin、
FuckAnimationLock、PFClassifier、PFRadar、PvPSelector、SpeedRadiusPlugin、UnloadErrorFuckOff
（其中多数为中文壳插件，本就不该处理）。

## 9 个可疑项复检（全部有结论）

| 插件 | 结论 |
|---|---|
| CharacterPanelRefined | key 指向**游戏数据**（Buff 名等）——显示走游戏数据，**保持不翻正确** |
| GoodFriend | 译文在 **zh 卫星资源程序集**（自带中文）——**保持不翻正确** |
| AetherBlackbox / AetherDraw | 大量「无 UI」文本是**日志/模板/内部名**——正常，UI 候选本就不多 |
| AutoHunt / DCTravelerX / CustomizePlus | **自带中文**为主（cjk 过半）——跳过 |
| AlmondHousing | 0 字面量（无 UI 文本）——跳过 |
| FootIk | `Feet / Where / OpenWorld / Duties…` 是**配置内部名**（自定义 UI 框架，探针判「去向不明」）——**翻了可能破坏配置读写，保持不翻**；列为低优先研究项 |

## 用户操作建议

1. **推荐清单 116 个**（`recommended.json`，按 UI 候选数排序，前几名：WrathCombo 1068、
   Yourcraft 888、PvpStats 611、Dresser 587、PyonPix 567…）→ 逐个「一键汉化」即可。
2. **灰名单（Ambiguous）多的插件**：见 `ambiguous.md`（2026-10-03，52 个分档详表 + 逐插件建议）。
   速览：**建议勾 5 个**（Aetherphone、Questionable、XivMediaPlayer、AcquisitionDate、Autofate）、
   **可勾·注意 9 个**（InventoryTools、Brio、WrathCombo、CurrencySpender、Collections、ChilledLeves、
   TouristMod、Glamaholic、AllaganItemSearch）；**52 个里 20 个安装版自带中文**，
   灰名单多是其本地化 key——别整包勾，只在「某处确实还显示英文」时用编辑器单条补。
3. 34 个自带中文 + 12 个名单内 + 10 个加壳 → **跳过**。

## 待办（记录）

- FootIk 类「自定义 UI 框架」（去向不明 284 条）——低优先、风险高；
- 「新建 zh 语言目录」能力（当前本地化文件通道只补「中文侧已存在」的文件；AutoDuty 属此类已覆盖）；
- 云端公共译文库已按用户要求清空——本轮起汉化走「本机一键汉化」，不经云端；
- **汉化分支内部名与不汉化名单的对齐**（2026-10-03 发现）：ICE、BossModReborn、Artisan、InventoryTools、
  Lifestream、Phantom、ai02、Beastmaster、DalamudACT、lccTools、Pawprint、Yourcraft 装的都是汉化分支/
  中文版（DLL 内置大量简体中文），但内部名不在名单里（名单用的是 ICECN、BossModRebornCN、
  InventoryToolsCN 这类带 CN 的名字），运行时按内部名匹配不到——要不要补名单或改成按仓库/元数据判定，等拍板。

## 附：灰名单（Ambiguous）插件注解（2026-10-03）

详见 `ambiguous.md`（52 个插件：分档详表 / 主要原因构成 / 内置中文条数 / 逐插件建议 / 操作入口）。
