# 2026-10-26 简介/详情全量重译

本次重译覆盖 `Punchline` 与 `Description` 字段。翻译输入使用仓库内的
`ffxiv-glossary.tsv`（输入快照 31,557 条国服译名映射），并按唯一原文去重后分成 46 个批次；
同一原文在多个插件中出现时共用同一译文。收尾按国服官网和同源游戏数据纠正歧义、补充系统名，
最终术语表为 31,566 条。不能脱离语境把同名技能或乐曲的译名套到资料片、绝境战上。

## 工作流调查

- 工作入口：`scripts/update_translations.py`。
- 数据源：Aetherfeed，外加 `scripts/repos.txt`、`community-repos.txt`、
  `curated-repos.txt` 中的仓库清单；仓库文件用于补齐一行简介。
- 调度：Cloudflare Worker 通过 GitHub `repository_dispatch: translate-now` 触发，
  GitHub Actions schedule 作为兜底；当前周计划为北京时间周一、周五 08:00。
- 增量规则：只处理新增或原文变化的字段；`Source=user` 永不被机器翻译覆盖；
  已经是简体中文的上游字段保持原文。
- 本次修复：`--recheck` 重新请求模型后，成功的新译文现在会写回未变化的机器译文；
  空结果、模型回显原文和用户译文仍受保护。
- 周更当前仍使用 `deepseek-flash`、关闭 thinking；本轮未改动模型配置。默认增量会保留原文未变的
  本次新译文；上游原文变化或手动开启 `recheck` 时仍可能由该模型重译。
- 客户端代码目前每 24 小时检查一次更新，失败 1 小时后重试；工作流注释中的“两周”不是当前实现。

## 本次结果

| 项目 | 数量 |
|---|---:|
| 唯一待翻译原文 | 4,422 |
| 实际字段引用 | 4,658 |
| 原生中文字段保留 | 384 |
| 简介/详情用户来源字段保护 | 1 |
| 全表用户来源字段保护（含名称） | 2 |
| 完成批次 | 46 |
| 缺失结果 | 0 |

对上游已损坏的四条记录，先恢复了干净原文再合并：MirageMirror、TreasureHelper、
SGTProductionBox、PlayerTagsPlus。TreasureHelper 与 SGTProductionBox 的干净中文原文
及同文译文保留；PlayerTagsPlus 的中英混排原文已翻译。上述四处原文恢复是有来源快照的明确例外，
其余原文不改动。

## 语义与术语复核

- `Dawntrail` 统一为“金曦之遗辉”。
- `青磷水桶` 统一为官方“青磷水罐”。
- `yalm` 统一为“星码”。
- `right face buttons` 统一为“右侧正面按键”。
- `Auroral Mirage(s)` / 插件中的 `Aurora Mirage` 统一为“蜃景”；说明保留英文名供交叉核对。
- `Dancing Mad (Ultimate)` 采用国服官网的“妖星乱舞绝境战”，不能误套裸 `dancing mad` 的同名技能译名“魔发狂舞”。
- `Dawntrail Ready` 保留插件名，转换文件名保留 `(Dawntrail).pmp`；南/北征篇统一为“新月岛南部/新月岛北部”。
- `Hunting Log` 使用“讨伐笔记”，`Logos Manipulator` 使用“文理融合器”，并保留 `Aurora Mirage` 英文名用于核对。
- `Glamour Dresser` 使用“投影台”，`Glamour Plate` 使用“投影模板”，`Knowledge Crystal` 使用“知见水晶”，
  `Shared FATE` 窗口使用“危命任务完成度”，不是“双色宝石交易”。
- 修复数字 `1.2k`、`15.5k`、命令 `/n`、`Party-/Alliance-Mitspieler` 等被模型改写的可验证令牌。

本轮核对来源：

- [国服 7.5 专题](https://actff1.web.sdo.com/project/20240927dawntrail/patch75/index.html)：妖星乱舞绝境战、新月岛北部相关内容。
- [国服 7.0 专题](https://actff1.web.sdo.com/project/20240927dawntrail/)：金曦之遗辉。
- [英文游戏数据](https://github.com/xivapi/ffxiv-datamining/tree/master/csv/en) 与
  [国服游戏数据镜像](https://github.com/thewakingsands/ffxiv-datamining-cn)：Weather row 192（蜃景），
  EObjName row 2009439/2013856（投影台/知见水晶），Addon row 3924（危命任务完成度）。

相应高频词补入 `scripts/ffxiv_glossary_extra.tsv`，供后续周更重建术语表时保留。
`Dancing Mad Ultimate` 的匹配键不带括号，并已验证能命中带括号的原文；裸技能名映射保持不变。

## QA 结论

最终表按字段检查了 JSON 结构、原文保真、用户译文保护、URL、命令、占位符、关键数值、
日文假名和乱码字符。4,658 个重译字段全部有结果且原文一致；没有发现缺失的 URL、
占位符或所检查的关键数值，也没有发现正文日文残留。去重原文的所有引用还检查了译文一致性。

以下假名是刻意保留、不是漏译：

- Beastmaster 的真实游戏命令 `/魔獣使い 出力`。
- MacroSlot 的 `<マクロ番号>`、`<ホットバー番号>`、`<スロット番号>` 命令占位符。

全表名称字段另有 `YokaiWatchTracker.Name` 的日文品牌名称，未改动；本轮全量范围是简介/详情，不是插件改名。

`/HQ`、`/AoE` 出现在 `item/HQ`、`tower/AoE` 这种英文缩写斜杠写法中，不是聊天命令。
纯命令、品牌名和无中文的短文本（例如 `/huntkit`、`XTeleportOnline`、`Skibidi。`）
保持原样是有意行为。

## 后续基准

本次词表和术语表随仓库提交；以后每周增量翻译以本次 `translations.json` 为基准。
提交前的运行词表另存为本地备份，未在仓库中复制用户机器路径或私密信息。
本轮没有修改或发布 DLL；文件与自动化检查不等同于游戏内界面验收。

## 离线回归

- `python -m unittest discover -s scripts -p 'test_*.py' -v`：20/20 通过。
- `node --test scripts/relay/worker.test.mjs`：13/13 通过。
- 相同重译回归测试在旧脚本上准确复现“不落盘”与文件句柄未关闭两处失败，在修正后通过。
