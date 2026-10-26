# 2026-10-26 全量翻译审查

本文件记录本次全量 `Punchline`/`Description` 重译后的收尾审查。完整过程和工作流见
[`translation-full-retranslation-2026-10-26.md`](translation-full-retranslation-2026-10-26.md)。

本次覆盖 4,658 个字段、4,422 个唯一原文，结果完整率为 100%。逐字段检查确认：

- 原文字段没有被翻译结果改写；
- `Source=user` 字段没有被覆盖；
- URL、所检查的关键数值、占位符和真正的聊天命令均保留；
- 正文没有日文假名或乱码残留。

## 有意保留的非中文文本

| 条目 | 保留内容 | 原因 |
|---|---|---|
| Beastmaster | `/魔獣使い 出力` | 真实游戏命令，不能翻译命令本身 |
| MacroSlot | `<マクロ番号>` 等 | 命令占位符，必须原样传给插件 |
| 多个短条目 | `/huntkit`、`XTeleportOnline`、`Skibidi。` 等 | 命令、品牌名或短名称，没有可翻译正文 |

`/HQ` 和 `/AoE` 只出现在 `item/HQ`、`tower/AoE` 的缩写写法里，不是漏掉了命令。

## 已处理的术语问题

`Dawntrail`→“金曦之遗辉”、`青磷水桶`→“青磷水罐”、`yalm`→“星码”、
`right face buttons`→“右侧正面按键”、`Auroral Mirage`→“蜃景”，以及
`Dancing Mad (Ultimate)`→“妖星乱舞绝境战”已统一。Dawntrail Ready 保留插件名，转换文件名保留
`(Dawntrail).pmp`；南/北征篇统一为“新月岛南部/新月岛北部”，Hunting Log 使用“讨伐笔记”，
Logos Manipulator 使用“文理融合器”，并保留 `Aurora Mirage` 英文名作核对标记。
Glamour Dresser、Glamour Plate、Knowledge Crystal、Shared FATE 窗口分别使用
“投影台”“投影模板”“知见水晶”“危命任务完成度”。TreasureHelper 和 SGTProductionBox 保留干净中文原文及同文译文。

名称字段的 `YokaiWatchTracker.Name` 仍保留日文品牌名称，不属于本轮简介/详情范围。
两处 `Source=user` 字段（详情和名称）均与旧表完全一致。术语来源与歧义处理详见完整报告。

本轮自动检查与分批语义复核中发现的问题已修正，未发现剩余阻塞项；这不表示机器审查能证明绝无语义错误。
后续每周增量运行仍应复用同一术语表并继续运行
命令/占位符/数字审计。
