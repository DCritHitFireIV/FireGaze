# 独立代码评审：抽取器修复 + FF14 术语表（提交 4025e9c 及后续未提交的深度修复）

- 日期：2026-10-01
- 评审范围：`4025e9c`（抽取器修栈漂移/字段流、FFXIVGlossary、LLM 通道术语表）+ 未提交的 `ComputeDepths` 深度哨兵修复
- 材料：`uit-review.diff` 与相关源文件全文；评审员只读、不参与编写
- 信度说明：评审员当时只有 read 工具，无法自行编译；结论未含编译验证（提交后由 fgtest 29 组断言补齐）
- 处置：P1-1 / P1-2 已修并随 1.2.0.51 提交；P1-3 按评审建议留待后续版本

---

# FireGaze 1.2.0.50（抽取器修复 + FF14 术语表）独立评审报告

评审对象：commit `4025e9c`（`uit-review.diff`），改动文件逐个通读：`UIStringExtractor.cs` / `UiCallSemantics.cs` / `FFXIVGlossary.cs` / `TranslationChannels.cs`（LLMTranslationChannel）/ `UITextSettingsWindow.cs`，以及接线改动 `Configuration.cs` / `Plugin.cs` / `UITextChannelFactory.cs` / `UITextTab.cs`。未覆盖：fgtest 断言本身（未提供）。

---

## 一、P0 / P1 列表

### P0

**无。**

四条硬约束逐条核对结论：
- ① **无死循环 / 无内存爆炸**：`ComputeDepths` 是按 CFG 的 BFS，每个指令索引只在 `depths[...] < 0` 时入队一次（不可能重复入队）；`SolveFlow` 有 `rounds < 12` 上限；`CollectInterfaces` 是迭代 + `seen` 去重；`V.Merge` / `AppendCapped` / `MakeResult` 都走 96 封顶；字段值只增不删且引用在 `Extract` 返回后随 Scanner 一起释放。
- ② 新增路径没有把功能串当界面文本的**已知确定**案例（潜在回归见 P1-1/P1-3）。
- ③ 无 Harmony / MonoMod / detour：改动没有碰钩子，dnlib 是纯静态读 IL。
- ④ 渲染线程零重活：术语表构建在 `Task.Run`（设置窗）或翻译线程（`UITextTab.RunPipelineAsync` 本身就在 `Task.Run` 里）执行；新增的 ImGui 调用只有 `Checkbox` / `SetTooltip` / `SameLine` / `TextDisabled`，没有任何 Push/Pop。

---

### P1-1（会翻坏第三方插件行为）`Equals` / `GetHashCode` 被移出危险名单，「被比较的字符串」失去灰名单保护，会被默认翻译

**证据**
- `src/FireGaze/UIText/UiCallSemantics.cs`：`DangerousMethodNames` 删掉 `"Equals"`（diff hunk `@@ -114`）与 `"GetHashCode"`（`@@ -141`），注释里写明是为了救 record 文本。
- 分类口径（`UIStringExtractor.BuildResult`）：只进 UI = 候选；UI + 危险 = 灰名单。灰名单**默认不翻**——`Configuration.UITextTranslateGreyList` 默认 `false`（`Configuration.cs`，注释也写着「默认不翻」），`UITextFlow.TranslationTargets` 里 `Ambiguous` 只有开关打开才进目标列表。
- 关键放大因素 1：`UIStringExtractor.Intern` 按**文本**去重（`literalIDs`），所以「同一个字符串既被画、又被比较」是**同一个 Literal**，一次翻译同时改掉两处，灰名单是唯一隔离层。
- 关键放大因素 2：本次改动新增了大量 UI 出口（`ldelem` 带整个数组值、`stfld/ldsfld`↔`ldfld/ldsfld` 字段流、`newobj` 登记实参、record 字段），同类字符串正被从「灰名单」推向「纯 UI 候选」。

**触发条件**：`if (mode.Equals("Auto"))` 且 `"Auto"` 同时出现在 `ImGui.Combo(..., Modes, ...)` 的 items 里；`if (string.Equals(name, "Yes"))` 而 `"Yes"` 同时也是按钮标签。翻译后比较恒不相等 → 插件的设置项/分支**静默失效**（不崩，但功能坏）。

**建议修法**（既保住 record 修复又不放弃保护）：
1. 把 `"Equals"` / `"GetHashCode"` 加回 `DangerousMethodNames`；
2. 在 `IsDangerousCall` 里（`IsStringProducer` 判断之后、最后两个 `DangerousMethodNames.Contains` 之前）加一条**窄豁免**：
   `if (typeFullName.Contains("EqualityComparer", StringComparison.Ordinal)) return false;`
   依据：编译器给 `record` 生成的 `Equals` / `GetHashCode` 对每个字段调用的是 `System.Collections.Generic.EqualityComparer<T>.Default.Equals/GetHashCode`（也就是把 ARSR 教程文本打成灰名单的那条路径）；而 record 自己的 `op_Equality` / `Equals` 声明类型不是 `System.*`，本来就不会命中 `DangerousMethodNames && StartsWith("System.")` 那支，所以豁免 `EqualityComparer` 就够；
3. 补两条 fgtest 断言钉方向：`String.Equals(...)` 算危险、`EqualityComparer<T>.Default.Equals(...)` 不算。

---

### P1-2 术语表构建失败/半截会被当成「已就绪」永久缓存 —— 功能静默失效 + 界面谎报

**证据**：`FFXIVGlossary.EnsureBuilt()` 是 `terms ??= Build()`；`Build()` 把异常吞进 `catch` 后**返回部分甚至空字典**，`AddSheet` 对读不到的表（`english is null || chinese is null`）也只 return。于是 `Ready == true` 而 `Count == 0`（或明显偏小），本进程内**永不重建**（静态字段，只有重载插件才复位）。

**症状**：设置页「已就绪 0 条」（或偏小的数），翻译照跑但完全不带术语表；日志只有一行 Warning。国际服客户端（无 `_chs` 数据）会稳定命中；国服任一张表读失败就是半截表且用户无从判断。

**建议**：`Build()` 用返回 `null`（或 `(dict, ok)`）表示失败/空表，失败时不写 `terms`，UI 显示「构建失败 · 重试」；`Count == 0` 显示成「未就绪」。另外让 `AddSheet` 回报每张表条数，日志一行列出，方便定位是哪张表挂了。

（不拖慢翻译：`FindTerms` 对空表提前 return。）

---

### P1-3（低概率）`UIHelperMethodNames` 只认方法名、不认声明类型，可能把功能串判成界面文字

**证据**：`UiCallSemantics.cs` 的 `IsUICall` 末尾 `if (UIHelperMethodNames.Contains(methodName)) return true;`，名单 `SelectableCombo / SelectableButton / DrawSectionTitle / DrawHeader / DrawLabel / SectionTitle / DrawTooltip` **完全不看 `typeFullName`**；且 `IsUICall` 命中后 `HandleCall` 直接 `MarkUI + Leave + return`，**不会登记 `passRefs` / `paramFlowRefs`**（那是第 ⑤ 步「本程序集内方法」才做的），所以被调方法内部把该字符串写文件/当键名的危险语境**传不回来**，少了一层兜底。

**触发条件**：插件里任何叫这些名字但不是画 UI 的方法（写报表/CSV 表头的 `DrawHeader(string)`、生成日志分节的 `SectionTitle(string)`），其字符串参数会被判候选；若那串同时是键名/命令名就会翻坏。

**建议**：加类型侧条件（声明类型名含 `ImGui` / `UI` / `Ui` / `Window` / `Tab`），或把命中名单从「直接当 UI 出口」改成「仍需继续走内部分析」。

---

## 二、三个最需要实机确认的点

1. **国服客户端上术语表到底建成几条 + 后台线程读 Lumina 是否安全。** 开游戏 → 翻译设置勾术语表 → 看日志 `[内部文本] 术语表就绪：N 条`（应为数万条；若是 0 或明显偏小 = P1-2 命中，同时定位是哪张表）；同时观察一两分钟：读 10 张表 × 2 语言，Lumina 会把表常驻缓存（内存与耗时都要量），确认没有崩溃/卡顿。如果出现异常，改用 `Framework.RunOnTick` 在框架线程构建。顺带（我无法在此编译验证）：`row.Name.ExtractText()` 若来自 `Lumina.Text` 的扩展方法而本文件只 `using Lumina.Excel.Sheets;`，会是编译错——跑一次 build/fgtest 就能排除。
2. **抽取回归的「误翻」方向**：拿改前/改后对同一批已汉化插件（ARSR / AnoMech / SimpleTweaks / 有 `Equals` 比较的）跑「只抽取」，把这次**新增的 UI 候选**逐条过一遍，找不像界面文字的先筛出来（命令名 / IPC 名 / 字典键 / 正则 / 路径）；SimpleTweaks 的 `LocString`/`Localize` key 必须仍然不进候选（上次的血教训）。重点盯 P1-1 的形态：同一条字面量既在 items 数组/字段里被画、又被 `Equals` 比较。
3. **抽取耗时与内存（第二遍 + 字段合并之后）**：挑最大的插件跑一次真实抽取，看墙钟与峰值内存、以及游戏是否有可感卡顿（抽取在 `Task.Run` 里，但进度条阶段「取消」不可用，跑久了用户只能等）；确认没有触到 96 封顶外的膨胀（`Stelem_Ref` 的 `array.IDs.AddRange` 那一处没封顶，虽然上界是模块字面量数，量一下更踏实）。同一轮里顺便确认三处「漏翻修复」真的生效：AnoMech 的窗口标题、ARSR 的 `SelectableCombo` 标签与教程 record 文本、某个字符串数组的 Combo 条目。

---

## 三、结论（一句话）

**不会崩、不会卡死、不碰钩子，功能可用——但建议先修 P1-1 再给玩家：它是这次改动里唯一「有明确机制、可被第三方插件真实触发」的回归（从「少翻几条」升级为「某些插件的设置/分支静默失效」），修法就是加回两个方法名 + 一行 `EqualityComparer` 豁免；P1-2 建议同版本一起收（几分钟的事），P1-3 可随后续版本处理。**

---

## 附：已核对、未列为问题的几处（供你判断我漏没漏）

- `ComputeDepths` 的深度夹紧用 `Insert(0, ...)` 补底部占位，保住「栈顶对齐」，这个思路是对的；`Stelem` 在 `Execute` 里是「弹 3 压 1」而 `StackEffect` 是「弹 3 压 0」，两者不对称——但因为 C# 语句边界基本都会清空求值栈，值错位不会跨语句累积，且夹紧恰好把多出来的那份数组值清掉，暂不构成 P1。
- 术语表构建有 `Gate` 锁、`terms` 只在 `Build()` 完整返回后才赋值（读侧看到的一定是完整表）；设置窗与翻译通道两条调用都走 `EnsureBuilt`，不会并发构建两次。
- `MarkUI`/`MarkDangerous`/`GetFieldScan` 只在扫描期改 `methodByKey`，`SolveFlow` 之后只读，没有「遍历中改集合」。
- 新增的 `IsDtrCall` 有类型后缀条件（`IDtrBar`），且 DTR 标题本身是给玩家看的，翻译合理。
- `UITextTab` 里新增的那两行 `ImGui.TextDisabled` 都在既有的 `Indent/Unindent` 对里，没有多余缩进残留。
