# 2026-10-07 批次：页签重排 / 图标管线 / 兼容性检测 / 插件汉化对齐（v1.3.0.48）

用户一次提出的八件事，全部落地；末尾要求「这两部分都过全部 skill」（=「插件汉化」的风格对齐 与「插件发现」本轮改动），
技能复查结论见 §4。

## 1. 逐项

| # | 需求 | 实现 |
|---|---|---|
| 1 | 详情·库链里的「加入自己的库」去掉 | 详情只留「复制」（未加库的加库动作只在行尾）；已在库显灰字「已在库」，已加库但停用给「启用」 |
| 2 | 插件汉化 UI 风格与插件发现对齐 | ① 字母头像抽成 `InsStyle.DrawLetterAvatar`（正圆），两页签共用；② 插件汉化列表套 `InsStyle.PushRounded()`（按钮/滚动条圆角一致）。按钮颜色语义**保持不变**（主操作=蓝/启用=绿/破坏=红，2026-10-03 跨页规则），未改成发现页的粉色 CTA |
| 3 | 体检页图标「下载过、重启又要下」 | 真因：`RunIconCheck` 先用 `TryGetHandle`（**异步**建纹理，首帧必返回 false）计数 → 盘上已缓存的也报「缺图标」。改为补一步 `Has()`（盘上有就算有），不再假缺失；实际也不会重下 |
| 4 | 发现页图标：按视口加载 + 预载 + 可扛全量并行 | 新增 `DiscoveryTab.Icons.cs`：可见行 ±3/+8 行进队列（去重）→ 每 100ms 发动一个、在飞 ≤6 → 完成回调入队、纹理在 UI 线程建；落盘复用 `IconStore`/`IconCache`（重开不重下、也注入卫月缓存）；失败 2 分钟后可视再试一次；刷新云库时整队重置 |
| 5 | 去掉「连着已停用的库」 | 复选框删除；已停用库不再过滤，行尾「启用」一键回库 |
| 6 | 学习安装器的合格检测器，显示前先筛 | 复刻卫月 `PluginManager.IsManifestEligible` 的 API 等级口径（当前或当前−1 才合格）。反编译核实：`num < DalamudApiLevel - 1` 即不加载。新增 `PluginCompatibility`（未知等级不拦）；词表新增 `ApiLevel`（本轮重生成 2229/2306 条带上），本机清单侧从 `DalamudApiLevel` 反射读取；列表预筛 + 计数 tooltip 说明隐藏了多少 |
| 7 | 发现页加刷新按钮 | 工具栏「刷新」：`UpdateTranslationTableAsync`（防倒退）→ 全量重建索引 → 核对「我投稿的库链上云了吗」并报结果；带「刷新中…」禁用态 |
| 8 | 页签顺序 + 更新公告 | 顺序改为 插件汉化 / 插件发现 / 仓库体检 / 简介汉化 / 插件安装器（默认仍是插件汉化）；公告改为「上线了插件汉化和插件发现的功能，现在可以汉化插件界面、浏览云端插件库了」，新增 `DiscoveryFeatureAnnounced` 让老玩家也收到 |

## 2. 兼容性检测器（第 6 项的取证）

卫月 `PluginManager.IsManifestEligible`（反编译）：

```csharp
if (manifest.ApplicableVersion < dalamud.StartInfo.GameVersion) return false;
int num = (可用测试分支 && TestingDalamudApiLevel > DalamudApiLevel)
    ? TestingDalamudApiLevel.Value : manifest.DalamudApiLevel;
if (num < DalamudApiLevel - 1) return false;   // 当前或当前−1 合格
```

我们做等级这半段（`ApplicableVersion` 需要游戏版本对象，暂不做；等级未知时不拦）。
本轮词表重生成后等级分布：15 → 1902、14 → 110、12 → 67、13 → 65、11 → 38、≤10 → 47 条；
当前 API 15 时，等级 < 14 的约 230 条会被预筛掉（即用户实机看到的「api 不合适」那批）。
测试专供（`IsTestingExclusive`）条目早前已不进列表，不重复处理。

## 3. 图标管线（第 4 项的边界）

- 队列只进「看过的」：滚到哪排队到哪，滚到底也只是一次性地把看过的补齐；发动速率恒定（100ms/个）。
- 绝不在绘制里批量开下载/纹理（2026-09-18 卡死教训）；纹理跨帧持有 wrap（2026-09-18 渲染器崩溃教训）。
- 失败不谎报：占位字母块保留、Debug 留痕、2 分钟后重试；重开游戏图标在盘上，直接出图。

## 4. 技能复查（两部分）

| Skill | 结论 / 落地 |
|---|---|
| design-sense | 两页签头像/圆角/行节奏统一；刷新按钮进第一行工具栏，不新增层级 |
| heuristic-eval | H1 状态可见：刷新「刷新中…」+ 状态行、图标「加载中，还剩 N」；H9 帮助：按钮与计数均有 tooltip；H4 一致性：按钮语义不跨页乱改 |
| cognitive-friction | 兼容性静默筛选是唯一「看不见」的动作 → 计数 tooltip 说明隐藏数量与原因 |
| ux-writing（文本审查） | 状态行去括号（「等云端收录并翻译后才会出现」）；公告按用户给定内容润色；刷新/图标的 tooltip 给「点它会怎样/为什么」 |
| consistency-audit | 页签顺序改动同步到启动日志文案；「参与翻译」旧称仅留在内部注释 |
| accessibility-precheck | 刷新禁用态可悬停看 tooltip；图标状态不靠颜色单独表达（字母块/图片） |
| failure-path-audit | 刷新失败 → 状态行红字 + Warning；图标失败 → 退避重试；投稿核对失败 → 只报剩余数量不谎报收录 |
| state-model | 刷新 = 索引+图标队列全重置；兼容性筛选计入列表计数口径 |
| logging-conventions | 刷新成功 Info / 失败 Warning；图标下载失败 Debug（不刷屏，自动重试） |

## 5. 验证与遗留

- Release 构建 **0 警告 0 错误**；fgtest **56/56**（新增兼容性边界断言 5 条）。
- 词表语义 diff：仅 2229 条新增 `ApiLevel` + 3 条 `Updated`，无原文/译文改动；三份副本同步。
- 实机截图：页签顺序 / 刷新按钮 / 图标逐步点亮 待窗口重开后复核。
- 已知保留：`ApplicableVersion` 不做；`ContributeShowDisabled` 配置字段保留但界面不再使用。
