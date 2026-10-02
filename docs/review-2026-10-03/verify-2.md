## Review — FireGaze 问题清单交叉验证（A 抽取器 6 条 + B 补丁管线 13 条）

验证方式：只读通读当前工作区文件（`C:/everyone/FireGaze-Refactor`），逐条对 `file:line` 与调用方上下文核对。**注意**：本 cwd 无可用 HEAD diff 基线（`watchdog_diff` 不可用），所有结论只对当前整文件状态有效；未运行 dotnet build / fgtest / 任何脚本。**C 组（投稿与上传链路）问题清单为空（"/"）**，无可验证项；C1 属 A 组清单，已按其文件位置验证。

### Correct（核实无误的机制）
- `UITextQuality.Check`（`UITextQuality.cs:39-75`）确实只做空/超长/控制字符/违规词/占位符，**无容器与 key 合法性**；`inbox_uit.py:40` 的 `CONTAINER` 正则不含 `:` 与 `/`。
- `UiStringExtractor.cs:197` 的 `RoleRank` 合并、`UITextPatchManager.cs:432-444` 逐程序集共用同一 pack、`UITextPatcher.cs:110` map 只按 Original 建键 —— 与 A3 描述一致。
- `DictionaryKey` 唯一赋值点 `UiStringExtractor.cs:1453`（IsCollectionMutator 分支）；查表路径经 `:1663 IsDangerousCall` → `:1899` 只置 `HardKey`；`ClassifyFlow :2494` 只搬 `HardKey` —— 与 A1 描述一致。
- `IsUICall :288-299`、`NonTextCalls :410-421`、`UsesStringAsID :454-456` —— 与 A2 描述一致（`NonTextCalls` 无 `BeginTable`、也没有 ImRaii 的小写 `PushId`）。
- `UITextPatcher` 的 `:106-112` 全局去重、`:311/318` 的 suffix/exact map、`:659` 未 Trim 写回、`:474` Trim 比较、`:845-863` PDB 静默降级 —— 行号与逻辑均对得上。
- `UITextFlow.cs:158` 清账、`:355` 资源循环无 `HasTranslation` 守卫、`UITextTab.cs:338-340/1717-1719/2758`、`UITextPatchManager.cs:315/642/1528/1539/1588/1631` —— 行号与逻辑均对得上。

### Finding（需修正的引用/口径问题）
- P3 `scripts/inbox_uit.py`：C1 引用的 `:130` 实为 `:131`，`:183` 实为 `:165`；`ContributeSender.cs:44` 实为 `:43`。
- P3 `UI/UITextTab.cs`：C1 的 `865/877` 恰好正确（865 = 取 `resources`，877 = `FilterHealthy(resources,…)`），但真正入 payload 的是 `:892`。
- P3 `UITextPatchManager.cs`：B-02 说“同函数内 1546-1550 的 MigrateLegacyBackups 反而是 Task.Run”，实际该 `Task.Run` 在 `:1528`。
- P2 `UiCallSemantics.cs:607`（A4）：`Contains("Log", StringComparison.Ordinal)` 大小写敏感，`Dialog`/`Catalog` 里的 `log` 是小写**不命中**，只有 `Login`/`Logger`/`LogWindow` 这类才命中 —— 例子举错，机制成立。
- P2 `UiStringExtractor.cs:2675`（A1）：`短键 → Excluded（注释里写明应进灰名单）` 误读注释 —— `:2641-2643` 说的是“两边都沾才进灰名单”，纯查表短键本来就该 Excluded。
- P3 `UITextPatcher/UITextText`（B-10）：漏掉占位符通常不会让 `string.Format` 抛异常（多出/写坏 `{}` 才会），但“库合并/手工编辑不过 `CheckPlaceholders`”这一缺口成立。

### Merge verdict: OK with notes（19 条全部 exists=true，无凭空捏造；severity 有 3 处需下调、若干行号需修正）

```text
===VERDICTS-JSON===
[
  {"issue_id":"C1","exists":true,"severity":"P2","reasoning":"机制属实：UITextQuality.Check(39-75) 只做 check_text 四项+占位符，无容器/key 校验；UITextTab.cs:865 取 pack.Resources、:877 交体检、:892 进 payload，含 file:/json: 容器；inbox_uit.py:40 正则不含 ':' '/', :131 拒绝、:165 accepted==0 才 fail；ContributeSender.cs:43 中继成功即回『已上传 N 条…感谢！』。但『整条投稿被丢』仅在 accepted==0（本条投稿只有 file:/json: 资源、无任何行文本条目被收）时成立，不是必然；不损坏数据，故 P1→P2。引用 130/183/44 应为 131/165/43。"},
  {"issue_id":"A1","exists":true,"severity":"P3","reasoning":"机制属实：DictionaryKey 唯一赋值在 :1453（IsCollectionMutator 写路径），查表路径 get_Item/TryGetValue/ContainsKey 经 :1663 IsDangerousCall→:1899 只置 HardKey，ClassifyFlow :2494 也只搬 HardKey。但影响有限：这类字面量落到 :2644 分支后是 Ambiguous/Excluded，灰名单默认不翻（UITextPack.IsPatchable:79 需 includeAmbiguous），只有用户主动开全局灰名单开关才会被翻；同一 DLL 内既有 Add 又有查表时 DictionaryKey 已置位，主场景正常；且『短键应进灰名单』误读了 :2641-2643 注释。故 P2→P3。"},
  {"issue_id":"A2","exists":true,"severity":"P2","reasoning":"mechanism 属实：IsUICall 只有 ImGui 分支查 NonTextCalls(:288-290)，包装前缀 :293-299 无条件 return true；UsesStringAsID :454-456 对类型名不含 ImGui 者返回 false→不补 ###；NonTextCalls(:410-421) 只收 ImGui 原生 PushID，无 BeginTable、也无 ImRaii 的 PushId；docs/top100-2026-10-02/sub-fp-audit.md R2 确把 PushId(11)/BeginTable(7) 列为必须剔除。实际损害取决于模型是否真去译 ID 串（纯 ID 常量常原样返回→equals 原文→落不进包），维持 P2 而非 P1。"},
  {"issue_id":"A3","exists":true,"severity":"P2","reasoning":"属实：ExtractMany :197 keep=RoleRank 更强者，丢弃『在哪份程序集是功能串』；UITextPatchManager.cs:432-444 对每个程序集调同一 pack，UITextPatcher.cs:110 map 仅按 Original 建键，故 B.dll 同文本的功能串也被替换。触发需跨程序集同文本异角色，真实但少见 → P2。"},
  {"issue_id":"A4","exists":true,"severity":"P3","reasoning":"日志分支位置属实：UiStringExtractor.cs:1656-1660 在 ④:1662 与 ⑤:1688 之前 return，只 Leave 不登记 passRefs；IsLogCall(:599-607) 的方法名兜底（Debug/Error/Warning/Print/Verbose/Fatal/WriteLine）确无类型约束。但 Contains('Log') 为 Ordinal 大小写敏感，Dialog/Catalog 不命中（例子错），且后果方向安全（漏翻，不会翻坏）→ P2→P3。"},
  {"issue_id":"A5","exists":true,"severity":"P3","reasoning":"属实：Scanner 两遍扫描（:492 for pass 0..1）都过全部方法体，累加写在 HandleCall(:1616-1619)，故约为真实值两倍；全仓 grep resourceKeyCount/ResourceKeyCount 只有定义(:446)、赋值(:256/:2760/:1618)与 UiTextModels.cs:131，无任何读取方/消费点。P3 恰当（死字段+口径误导）。"},
  {"issue_id":"B-01","exists":true,"severity":"P1","reasoning":"逐条核对属实：DrawCore 每帧 DrawHeader；:839 StatusOf→UITextPatchManager.cs:146 HashOf→UiTextModels.cs:159-160 SHA256.HashData(File.OpenRead(path))；StatusOf 对 Applied 状态遍历 EffectiveFiles 全量哈希（只对 NotPatched/PendingVerify 提前返回）；store.Load(:73) 无缓存，DrawHeader+DrawToolbar 一帧约 3 次读/解析状态 JSON。P1 恰当（硬约束『渲染线程零重活』违规+大 DLL 每帧哈希）。"},
  {"issue_id":"B-02","exists":true,"severity":"P1","reasoning":"属实：Plugin.cs:147 UiBuilder.Draw += TickInstallerListScroll、:582 uiTextPatchManager.Tick()（渲染线程）；RepatchCheckSeconds=30(:43)，Ticket :1539 RepatchUpdated→:1631 this.Apply(entry) 同步 dnlib 读改写+备份+File.Move 写盘，EnsureIndex/InstalledPluginsIndex.Build 反射；同函数 :1528 的 MigrateLegacyBackups 反而 Task.Run。仅行号小误（Task.Run 在 1528，非 1546-1550）。P1 恰当。"},
  {"issue_id":"B-03","exists":true,"severity":"P2","reasoning":"属实：UITextTab.cs:338-340 每 5 秒或 dirty 时 RefreshRows；:2741 store.Load（每次 File.ReadAllText+Deserialize，无缓存；仅对存在的包读盘）、:2758 StatusOf（对已补丁 DLL 逐文件 SHA-256）、PluginUiBridge.Probe 反射。P2 恰当。"},
  {"issue_id":"B-04","exists":true,"severity":"P2","reasoning":"属实：UITextFlow.cs:355 起的资源循环只查 values.TryGetValue 与 pack.IsResourceSkipped，无 HasTranslation/IsUserSource，直接 entry.Translated=clean、Source='ai:'+channel。TranslationTargets(:164-180) 只把未译条目放进 targets，但资源身份是『容器+key』、回写键是原文，同值另一个 key 上已有的人工/库译文会被覆盖，违反 docs/uitrans.md:70 口径。P2 恰当。"},
  {"issue_id":"B-05","exists":true,"severity":"P2","reasoning":"属实：UITextFlow.cs:158 `result.Prune = pack.PruneAgainstExtraction(extraction)` 后函数结束，ReapplyNeeded 仅在 :79/95/101/122/134/153 置位（新增/角色变化/PreserveID 变化），清账不置真；UITextTab.cs:1717-1719 的 packTouchedSincePatch 用保存前的包 mtime 计算（store.Save 在 :1735 之后），故 :1792-1800 的『已经是最新』短路可达，盘上旧译文撤不掉。P2 恰当。"},
  {"issue_id":"B-06","exists":true,"severity":"P2","reasoning":"属实：Apply(:308-316) 不论成败都 DeleteUnreferencedBackups/DeleteUnreferencedOriginals；UITextPatchStore.Save(:227-243) 返回 void、catch 只 Log.Warning；ApplyCore 成功路径 :641 store.Save(newState)，写失败时本轮新建备份无人引用→被删，而 PublishManifest 可能已写出指向它的清单→TryAdopt 因 File.Exists(backup) 失败拒认。P2 恰当（触发需状态写盘失败）。"},
  {"issue_id":"B-07","exists":true,"severity":"P2","reasoning":"属实：RepatchIfNeeded(:1588) 无锁，内部 :1631 this.Apply(entry)；UITextRunLock 只在 UITextTab.cs:1610/1694/2665 与编辑器按钮 TryEnter 处使用，Tick 路径从不调用；ApplyCore 内只有 adoptGate 与 store 的 gate（仅护 Save），无按插件互斥。与 UI 发起的 Apply 可并发。P2 恰当。"},
  {"issue_id":"B-08","exists":true,"severity":"P2","reasoning":"属实：UITextPatcher.cs:99-112 的 usedTranslation/forcePreserve 覆盖面是整个包（跨 DLL、跨窗口），命中重复译文即 forcePreserve.Add(Original)；:214 replaced=BuildPatched(literal, translated, entry.PreserveID||forcePreserve.Contains(...))；UITextText.cs:39-49 在 preserveID 且原文无 ### 时输出 display+'###'+original。对抽取判为非标签的字面量会原样漏出后缀，与 docs/uitrans.md『只有标签才当 ID』血教训冲突。P2 恰当。"},
  {"issue_id":"B-09","exists":true,"severity":"P2","reasoning":"属实：:311 suffixMap.TryAdd('###'+original, original) 键为完整原文，:318 exactMap[translated]=original 键为译文；RecoverLiteral(:396-418) 只用 literal 每个 ### 之后的整段比 suffixMap、或整串比 exactMap。原文 'A###B' 经 BuildPatched 产出 '译文###B' 时两条都不中（suffixMap 有 '###A###B'、exactMap 键是 '译文'）→ 永不还原。P2 恰当。"},
  {"issue_id":"B-10","exists":true,"severity":"P2","reasoning":"主体属实：CheckPlaceholders 仅 UITextFlow.cs:339/375/410 与 UITextQuality.cs:74 调用；UITextPack.MergeLibrary(:538 起) 直接 existing.Translated=incoming.Translated 无校验；编辑器手工编辑 SetTranslationValue(:1033-1053) 与导入(:1358/1379/1399) 也直接赋值；UITextPatcher.Patch 收候选不复核；uit_library_build.py 只在提示词里要求『占位符原样保留』。唯一不精确：漏占位符通常不抛 FormatException（多出/写坏 {} 才抛），但校验缺口本身成立。P2 恰当。"},
  {"issue_id":"B-11","exists":true,"severity":"P3","reasoning":"属实：WriteModule(:845-863) 的 catch when(options.WritePdb) 先 TryDelete(tempPath) 再 module.Write(tempPath, new ModuleWriterOptions(module))，无日志、UITextPatchOutcome 无 PdbDropped 标记，用户/日志都看不出降级。P3 恰当。"},
  {"issue_id":"B-12","exists":true,"severity":"P3","reasoning":"属实：写回路径 :659 items.Add((resourceKey, entry.Translated)) 未 Trim；反向路径 :474 string.Equals(value, entry.Translated.Trim(), Ordinal) 已 Trim。UITextText.CleanTranslated(:297) 会 Trim，故机器翻译/AcceptTranslations 路径不触发；只有手工编辑/导入直接赋原值时才触发。P3 恰当。"},
  {"issue_id":"B-13","exists":true,"severity":"P3","reasoning":"属实：UITextPack.cs:821/828 让 file:/json: 容器跳过自动清账；UITextLocalizationFiles.TryWrite(:160) 准入只用 CanWrite，CanWrite(:426-436) 在 Resolve 返回 null 时返回 true；SetString(:457-486) 会逐级新建 JsonObject 再写入。写路径只校验中文侧 baseline、不与英文侧比对，故陈旧指针会往 zh-CN 文件塞孤立键（json: 侧因只写已存在根 key 无此问题）。P3 恰当。"}
]
===END-VERDICTS===
```

残余风险 / 需监督者执行的命令（本次未运行）：
- `dotnet build` / `fgtest`：本次为只读评审，未编译，无法确认上述路径在真实构建下无回归。
- 若要量化 B-01/B-02/ B-03 的实机卡顿（59MB 级插件、编辑器打开时的帧时），需实机跑；B-10 需跑 `scripts/audit_packs.py` 看 `uit-packs/*.json` 里是否已存在占位符缺失的库译文。
- 本 cwd 无 HEAD diff 基线，若需要「变更集级」审查，请提供 commit range 或 diff 工件。