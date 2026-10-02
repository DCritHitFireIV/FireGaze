# Subagent run b45049dd-57c7-44a4-989b-a867dda3ff45

- **Mode:** single
- **CWD:** C:\Users\Fire
- **Started:** 2026-10-02T22:20:20.591Z
- **Ended:** 2026-10-02T22:25:57.997Z
- **Duration:** 5m37s
- **Session:** C:\Users\Fire\.pi\agent\sessions\--C--Users-Fire--\2026-10-02T21-51-26-998Z_01a0fe99-cb16-765b-838b-cda66a888216\a7bf7655-c9f3-402d-851b-6eabd21298ad\run-0\session.jsonl
- **Artifacts:** C:\Users\Fire\.pi\agent\sessions\--C--Users-Fire--\subagent-artifacts

## Steps
| Step | Agent | Status | Duration |
| --- | --- | --- | --- |
| 1 | reviewer | complete | 5m37s |

## Summary
reviewer:
## 复审报告：d840e40「修复批次」（父 00623b2）

复审范围：只审本批次改动（材料 `C:/Users/Fire/tmp-pdbscan/fix-diff.diff` 1461 行已通读，并逐一打开 `C:/everyone/FireGaze-Refactor` 里对应文件的当前内容核对 `+` 侧）。**未修改任何文件、未运行构建/脚本**（只读复审）。

---

## 一、确认修好（结论 + 证据）

### 1. 线程与锁 —— 没有死锁，没有「后台线程碰 UI」的新路径，异常不会永久卡住

**无死锁**：
- `applyGate` 是单锁，`Apply`（`UITextPatchManager.cs:322-346`）与 `Restore`（`:1369-1377`）之间**没有嵌套调用**：`ApplyCore` 的自愈重试调的是 `ApplyCore`（`:512`），不是 `Apply`；`ReloadAsync` 里的自动还原（`:1552`）发生在 `RestoreAndReloadAsync` 的 `Task.Run(() => this.Restore(...))` **之后**（`:1473`），锁已释放。即便嵌套，C# `lock` 可重入。
- 与其它锁没有反向获取环：`indexGate`（`EnsureIndex :1788-1800`）内部只调 `InstalledPluginsIndex.Build()`（纯反射读，不回调补丁管理器）；`store.gate` / `loadCache` / `hashCache` / `adoptGate` 都只在极短临界区内取一次，`CachedHash` 在算哈希前已释放 `hashCache` 锁（`:1815-1829`）。`Save` 的 `loadCache` 更新在 `gate` 之外（`UITextPatchStore.cs:283-296`）。→ 无「持 A 等 B」的环。
- 渲染线程**从不**取 `applyGate`：所有 UI 侧 `Apply/Restore` 都经 `Task.Run`（`UITextTab.cs:1964`、`2765`；`UITextEditorWindow.cs:567/583`）。唯一可能在渲染线程等 `indexGate` 的是启动期的 `VerifyOnStartup`（`Plugin.cs:239`），且旧代码本来就在渲染线程跑 `Build()`，不构成新卡顿来源。

**后台线程没有碰只能主线程碰的东西**：`CheckPending`/`RepatchUpdated`（现跑在 `Task.Run`，`:1582-1597`）只做 `store.ListAll/Save`、`File.*`、dnlib、`Plugin.Log`、`entry.IsLoaded` 读、以及 `InstalledPluginsIndex.Build()`（`RepoAudit/InstalledPluginsIndex.cs:133+`，反射**只读** Dalamud 的 `InstalledPlugins`/`LocalPlugin` 属性，不写任何 Dalamud 状态）。最坏情形是枚举期间框架线程改动该集合 → `InvalidOperationException` → `BuildCore` 自身 try/catch 返回 `Unavailable` → 本轮巡检安全跳过（`EnsureIndex` 返回 null，`CheckPending :1730-1734`）。`indexGate` 保证同一时刻只有一份 `Build()`。

**异常路径不会卡死**：`tickWorkActive` 在 `finally` 里复位（`:1594-1597`），`Task.Run` 只可能调度失败（进程退出期）才会不复位；委托体内部异常被 catch（`:1590-1593`）。

### 2. 缓存正确性 —— 「谁会被改 / 谁让缓存失效」都核过了，修对了

- **共享对象没有被调用方改动**：`UITextStore.LoadCached` 只有 3 个调用点 —— `UITextPatchStore.LoadCached`（经 `LoadStateOrAdopt :85`）、`UITextTab.BuildUpload`（`:921`）、`RefreshRows`（`:2827`）。三处都只读：LINQ 取 `Entries/Resources/Attributes`、`TranslatedCount`、`IsSkipped/IsResourceSkipped/IsAttributeSkipped`（`UITextPack.cs:354/409/532`，都是 `List.Contains`）、以及 `JsonSerializer.Serialize(healthyEntries)`。**都不会触发 `UITextPack` 的懒索引**（`EnsureIndex/RewriteResourceIndex/RewriteAttributeIndex` 只在 `Find*`/`GetOrAdd*`/`Remove*`/`MergeLibrary` 里被调），所以后台 `BuildUpload` 与渲染线程 `RefreshRows` 同时读同一实例**没有字典竞争**。`UITextPatchState` 全用 `with` 派生新对象（`ReloadAsync :1503`、`MarkVerified :1745`、`MarkAllVerified :1610`），没有原地改缓存实例。
- **失效面**：`packCache` 靠 mtime+length 自校验，写方全是 `UITextStore.Save`（`File.Move` 换文件→mtime 变）与 `Delete`（文件没了）；`loadCache` 由 `Save` 显式刷新（`UITextPatchStore.cs:283-296`）、`Delete` 删除（`:326-329`）、`TryAdopt → Save`（`:245`）覆盖；`hashCache` 在 `Apply`/`Restore` 末尾 `ClearHashCache`（`:344`、`:1433`）+ (mtime,length) 双保险；`TryRebuildPatchRecord` 只写状态/持久备份、**不动盘上 DLL**（`UITextPatchManager.cs:1120-1215`），不需要清哈希。

### 3. 上传链路 —— 后台化与状态复位正确

- `StartUpload`（`:866-914`）：`uploadBuilds.TryAdd` 防重入 → `Task.Run` 里只调 `BuildUpload`（纯读包 + 体检 + 序列化，不碰 UI），异常被 catch 成一条行内说明，**所有界面状态写入都在 `RunOnFrameworkThread` 回调里**（`notes`、`pendingUpload`、`uploadModalNeedsOpen`，`:899-911`），且 `TryRemove` 在弹窗赋值之前。渲染线程零重活 ✓。payload 去掉了缩进（`WriteIndented = false`，`:953`）✓。
- 后台 `BuildUpload` 也不在渲染线程做 `store.Load` —— 用的是 `LoadCached` ✓；`ActivityLog.Warning` 只在有问题时打（`:962`）。
- `ContributeSender.SubmitAsync` 的回退逻辑自洽：`relayNote` 的两条来源互斥且完备（`body.Length <= MaxBody` 才会试中继，成功即 return；失败必有 `relayError`；超限则 `relayError` 为空 → “正文很大…超过了中继上限”，`:37-51`）；URL 长度按 `BuildIssueURL` 的**转义后**长度判（`:54-55`，`ContributeRelay.cs:72-77` 用 `Uri.EscapeDataString`），20000 与简介通道同值；回退 2 的 `shortBody` ≤ 几百字符，必定进得了 URL（`:63-73`）；`OpenIssue` 现在返回结果、失败时追加“手动去 GitHub 新建 issue”的指引，不再谎报（`:95-109`），调用方 `UITextEditorWindow.cs:1496`/`UITextTab.cs:1034-1047` 都消费该结果。
- `feedbackSending/feedbackStatus/feedbackSentURL/feedbackText` 的写回全部搬进 `RunOnFrameworkThread`（`UITextTab.cs:2154-2176`，异常分支也有独立 try/catch 兜底）—— C-11 修对。

### 4. 服务端脚本 —— Source 分级写入与容器校验基本修对

- 分级写入三处一致：新建走 `filled`（`user` 才标 user，否则 `library`），已存在条目 `user` 才覆盖（`overwritten`），机器译只补空槽（`kept` 不写），`accepted == 0` 时按 `kept` 给专门文案（`inbox_uit.py:163-186 / 196-219 / 238-261 / 275-277`）。docstring 已同步（`:15-27`），并且删掉了与新行为矛盾的「容器名必须在库包或索引里能找到对应插件」那句 ✓。payload 内重复 `Original` 不会写坏包（第 2 条命中 `entries_by_original` 走“已存在”分支），只是计数会重复（见 P3-b）。
- 缺包新建：**不写空包**（`save_json` 在 `accepted == 0` 判断之后，`:283`），失败也留档（`:267-273`）✓；`refresh_index`（`:325-341`）与 `uit_library_build.write_index`（`uit_library_build.py:581-594`，注释即“含以前构建过、这次没处理的”）都按目录枚举 → 「新包随下次库更新进索引、可被下载」这条 docstring 成立 ✓。
- 路径遍历在 `valid_container` 这一侧**挡住了**：`file:` 分支拒 `..`、`\`、前导 `/`、超长与非白名单字符（`:50-58`）；客户端确实只产出正斜杠相对路径（`UITextLocalizationFiles.cs:340` 显式 `Replace('\\','/')`），`json:` 只在资源名以 `.json` 结尾时产生（`UiStringExtractor.cs:2806`）。

### 5. 其余各条（B/C 清单）

| 项 | 结论 | 证据 |
|---|---|---|
| B-01/B-03 状态与哈希缓存 | 修好 | `CachedHash :1804-1832`、`loadCache :231-266`，编辑器每帧/列表 5 秒不再全量 SHA-256 与反复解析 JSON |
| B-02 Tick 后台化 | 修好 | `:1565-1598`，`checkRepatch` 提前算好传给后台任务，同一时刻只跑一轮 |
| B-04 机器译顶掉人工译 | 修好 | `UITextFlow.cs:375-380`（资源）、`:418-423`（属性）新增 `entry.HasTranslation → unchanged` |
| B-06 状态没落盘却删备份 | 修好 | `Save` 返回 bool（`UITextPatchStore.cs:274-306`），`Apply :336-343` 仅在 `!ok || statePersisted` 时清理，否则只记警告；重试分支把 `retryPersisted` 传出（`:512-513`） |
| B-07 Apply/Restore 互斥 | 修好 | `:322`、`:1371` |
| B-09 原文自带 `###` 还原不回来 | 修好（附 P3-d 反例） | `UITextPatcher.cs:322` 补登 `BuildPatched(original, translated, false) → original`，与 `Patch` 实际写出的 `译文+IDPart(原文)`（`UITextText.cs:39-49`）逐字对得上 |
| B-11 PDB 降级 | 部分修好 | `WriteModule` 记警告并置 `PdbDropped`（`:858-881`），但无人读（见 P3-c） |
| B-12 资源写回 Trim | 修好 | 写 `:677` 用 `entry.Translated.Trim()`，反向还原比较也是 Trim（`:474-477`）→ 口径真的一致；`json:` 那对在 `UITextJSONResources.cs:156/189` 内部统一 Trim；`file:` 路线不做值比较（整文件备份还原，`UITextLocalizationFiles.cs:520-521`），不受影响 |
| C-01 分级写入 | 修好（受客户端自报 Source 限制，见残余） | 见上 §4 |
| C-02 容器名放宽 | 修好 | `valid_container :46-62` + 三处调用点 |
| C-03 附件死路 | 修好 | `ContributeSender.cs:63-73` 改成“导出文件 → 把内容整段粘进正文”的可完成流程 |
| C-04 缺包假成功 | 修好 | `inbox_uit.py:125-134`（副作用见新发现 1） |
| C-05 不翻名单 | 修好（3 处） | `UITextTab.cs:922-925`、`UITextEditorWindow.cs:1454/1458/1462` |
| C-07 URL 长度判定 | 修好 | `ContributeSender.cs:27/54-55` |
| C-08 点击路径后台化 | 修好 | 见 §3 |
| C-09 删 Info 死分支 | 修好 | 分支已删（副作用见 P3-a） |

---

## 二、新发现问题

### 1. P2 —— `inbox_uit.py` 的 `plugin` 未做字符集/路径校验，C-04 让「用投稿字段拼路径并写盘」变成可达

位置：`scripts/inbox_uit.py:118`（唯一校验：只有空白与长度）、`:122`（`pack_path = os.path.join(args.packs_dir, plugin + ".json")`）、`:127`（新建空包）、`:283`（`save_json(pack_path, pack)`）。

证据链：
- payload 来自**任意公开 issue 正文**的 ```json 块（`validate_contribution.py:47-59`，`extract_json` 取最后一个 fenced block；`inbox.yml:14-16` 是 `issues: [opened, edited]` 无标签门控，`:55-70` 步骤带 `contents: write`）。
- 旧代码在 `pack is None` 时 `return fail(...)`，**不碰文件系统**；新代码 `:127-134` 先建空包，随后 `:283` 直接写这个由未校验 `plugin` 拼出来的路径。`plugin = "../docs/x"` → 写 `docs/x.json`；`plugin = "a/b"` → 父目录不存在 → `save_json` 抛 `FileNotFoundError`，`main()` 无 try/catch → 该 issue 的工作流整条失败（后续「提交存档 / 回话 / 通知」全被跳过，投稿与回话一起丢）。
- 影响有上界：工作流的 `git add` 只覆盖 `docs/contributions/inbox` 与 `uit-packs`（`inbox.yml:72`），所以大多数落点不会被提交；`uit-packs/` 内的落点等价于正常投稿路径。但这仍是「未校验输入直接决定写盘路径」+ 一条未捕获异常 DoS 路径（正常客户端不会造出带 `/` 的内部名，构造 issue 才能触发）。
- 最小修法（一行）：在 `:118` 后加 `if not re.fullmatch(r"[A-Za-z0-9_.\-]{1,120}", plugin): return fail(args, "插件内部名不合法。")`，与 `valid_container` 同一套纪律（旁证：兄弟脚本 `inbox_contribution.py:136` 的输出路径不含任何未校验字段）。

### 2. P2 —— B-05 的修法把「已经是最新」短路**粘死**了（自动清账过的包每次点主按钮都会全量重打 + 重载）

位置：`src/FireGaze/UIText/UITextFlow.cs:162-165`（`if (result.Prune.Any) result.ReapplyNeeded = true;`）配 `UITextPack.cs:809-811`、`UITextTab.cs:1860` / `1978-1982`。

证据：`PruneAgainstExtraction` 对「自动排除且仍有译文」的条目**每轮都会再记一次 `pruned++`**（`UITextPack.cs:797-811`：`IsSkipped && !IsAutoSkipped` 的 continue 不成立 → 不是纯空壳 → 再 `MarkSkipped` + `pruned++`），所以这类包的 `Prune.Any` **恒为真** → `ReapplyNeeded` 恒为真 → `UITextTab.cs:1860` 的「已经是最新」条件永不成立，用户每次点主按钮都会走完整的 `ApplyAndReloadAsync`（dnlib 全量读改写 + 写盘 + 重载插件），并弹出 `:1980` 的“抽取结果变了（有新增文本或控件 ID 标记被修正），已按新的写法重打。”——而实际什么都没变。触发面不罕见：本仓注释自己点名过 Allagan Tools 509 条「有译文却没进候选」的形态（`UITextPack.cs:490-500`），正是这个粘滞形态。
最小修法：只用**真正动了包**的数量判定，例如 `if (result.Prune.Pruned > 0 || result.Prune.Removed > 0 || result.Prune.Restored > 0)` 改成「本轮相对上一轮状态有变化」的判定，或在 `PruneAgainstExtraction` 里跳过「已经是自动排除」的条目再计数。

### 3. P3-a —— 降级成功后一律标红（`OpenIssue` 恒返回 `Bad`）

位置：`src/FireGaze/Translate/ContributeSender.cs:100`。回退 1 的两种情况（中继不可用 / 正文超中继上限但 URL 放得下）现在都返回 `ContributeSendSeverity.Bad` → `UITextTab.cs:1037` 映射成红色 `NoteKind.Bad` 且 `:1042` 记 `ActivityLog.Error`；`UITextEditorWindow.cs:1496` 同样按错误上色。旧的 `Info`（“译文较多，已替你打开提交页”）路径改回灰/绿更贴合事实。注：这是 C-09「删掉不可达的 Info 分支」的直接后果，可能是作者有意统一，故只记 P3。最小修法：`OpenIssue(url, message, severity)` 多带一个参数，`relayError` 为空（即只是正文超限）时用 `Info`。

### 4. P3-b —— `PdbDropped` 是死标记，B-11 想要的「消息里能说明」没落地

位置：`src/FireGaze/UIText/UITextPatcher.cs:61`（定义）、`:879`（赋值）。全仓 grep 只有这两处，`ApplyCore` 拼 message 时（`UITextPatchManager.cs:1180-1210`）完全没有引用 → 用户只能从日志知道本次补丁丢了 PDB。B-11 原文要求“让消息里能说明”。

### 5. P3-c —— `RecoverLiteral` 精确映射优先于后缀：有反例（窄但真实）

位置：`src/FireGaze/UIText/UITextPatcher.cs:415-419`（精确已提到后缀之前）。

反例：包里有两条 → P：`Original="Audio", Translated="音频", PreserveID=true`（补丁产物 `音频###Audio`，`suffixMap["###Audio"]="Audio"`）；Q：`Original="Sound###Audio", Translated="音频"`（新补登的 `exactMap["音频###Audio"]="Sound###Audio"`）。盘上字面量 `音频###Audio` 若来自 P，旧代码还原成 `Audio`（对），新代码命中 exactMap 还原成 `Sound###Audio`（错，会把错串写进“恢复出来的原文”基线）。另外 `exactMap` 现在混装两类键（纯译文 / 补丁产物），两者的碰撞只由 `TryAdd`（`:322`）的**插入顺序**决定，`:324-330` 的 `ambiguous` 守卫覆盖不到「先插入 BuildPatched 键、后插入纯译文键」的顺序。建议：`BuildPatched` 键单独放一个字典、在 suffix 之后、纯译文 exact 之前尝试；或至少在这两类键冲突时放弃还原（返回 null，宁可让用户重装）。

### 6. P3-d —— B-10 只堵了库合并一条路；编辑器手工改/导入仍能把丢占位符的译文写进 DLL

位置：`UITextPack.cs:544-549 / 613-618 / 681-686`（闸门齐全）vs `UITextPatcher.Patch` 收候选处（`:99-145`）**没有**占位符闸门。B-10 原报告点名的是“库合并**与**手工编辑/导入”两条路（`docs/review-2026-10-03/code-B-pipeline.md:105`）。本批次 docstring 只声称修了库译文那一条，口径诚实，但手工改坏的 `{0}` 仍会在插件运行时抛 `FormatException`。

### 7. P3-e —— 服务端 `json:` 白名单与客户端口径有两处不严格对齐

位置：`scripts/inbox_uit.py:59-61`。`json:` 要求 `name.endswith(".json")`（**大小写敏感**），而客户端只在 `EndsWith(".json", OrdinalIgnoreCase)` 时才产出该容器（`UiStringExtractor.cs:2806`）→ 形如 `X.Translations.JSON` 的合法投稿会被逐条拒收并在回话里点名。`file:` 白名单字符集不含 `(`/`'`/中文标点（`:57`），比客户端“任意相对路径”更窄。两处都只是“少收”，但 `valid_container` 的注释自称“与插件端三类容器对齐”，口径不严格成立。

### 8. P3-f —— 批次文档的「已知未修」清单漏了 C-06

`docs/uitrans.md:813-824` 的“已知未修”列了 B-08 / B-13 / A1 / A3 / A4 / A5 / C-10，但 C-06（本地把「与原文相同，等于没翻」和“空译文/超长/控制字符”并列成 problems，要求用户去修一个无解的条目）既未修（`UITextQuality.cs:69-72` 仍在，`UITextTab.cs:940-947` 仍进 problems）也未列入清单。不影响运行，只影响后续改动的口径。

---

## 三、回归风险（逐条结论）

1. **`UITextPack.MergeLibrary` 占位符闸门会不会误拦正常译文 → 不会。** `CheckPlaceholders` 在原文（取 `###` 之前的显示段）没有任何占位符时直接 `return null`（`UITextText.cs:75-79`），且只判“译文缺 token”，`have < need` 之外**不判多**（`:83-91`）→ 多写/换序/带额外占位符的译文都不会被拦。唯一可期的误伤是把 `%s` 等价改写成 `%1$s` 这类会被当作“丢占位符”而跳过合并（后果是这条库译文不进包，不写坏数据）→ 记为可接受。
2. **`RecoverLiteral` 次序 → 有反例但触发面很窄**（见 P3-c）：需要「同一条译文 + 同一个 `###` 后缀」同时存在于「自带 `###` 的原文」与「普通原文」两条上，且都打进了同一个 DLL。日常包基本不会同时满足。
3. **资源写回 `.Trim()` 与还原比较 → 现在真的一致**（见 §1 表 B-12）。`json:` 一对在 `UITextJSONResources` 内部统一 Trim；`file:` 一对不做值比较（整文件基线还原）。**无回归**。
4. **`NonTextCalls` 新增 `Child`/`BeginTable`/`PushId` → 未找到反例。** 该集合只有一个消费点（`UiCallSemantics.cs:290` 与 `:299`），影响面＝「类型名含 ImGui」或命中四个包装前缀（`:11-17`）的调用；`ImGui.BeginChild/BeginTable` 的首参本就是 ID 不绘制，`ImRaii.Child("ScrollingPlugins")`（本仓 `UI/InstallerListScroll.cs:40`）正是要拦的形态。注意点（非问题）：`Child` 会把这四个命名空间下**将来**出现的“带标题子窗口”语义一起排掉，若日后新增此类包装方法需重新评估；`TableSetupColumn`/`Popup` 之类仍未收（属 A2 的残余范围，不在本次改动内）。
5. **未审到但值得主控知道**：`LoadCached` 会在后台线程（`BuildUpload`）与渲染线程（`RefreshRows`）共享同一可变 `UITextPack` 实例 —— 当前两条路径都只读（已逐行确认），但这是一个靠约定维持的不变量，将来任何调用方原地改包就会串台；`(mtime,length)` 失效法识别不了「同长度且在 Windows 文件时间 ~15.6ms 粒度内重写」，以及 `packCache` 不随 `Save/Delete` 显式失效（只靠时间戳）。以上两条当前无害，列为残余风险。

---

## 四、判定

**Merge verdict: OK with notes** —— 未发现 P1 阻断项：本批次把 C-01…C-11、B-01…B-12 的问题按原评审建议修对了，线程/锁、缓存失效面、上传后台化、服务端分级写入这几条主线都经得起逐行核对。建议发布前收口 **2 条 P2**（服务端 `plugin` 路径校验、B-05 粘滞 `ReapplyNeeded`），其余 P3 可随下个周期处理。

需要主控执行的验证（本次只读，**未运行**）：
1. `dotnet build src/FireGaze/FireGaze.csproj -t:Rebuild -c Release`（仓库纪律：全量重建确认 0 警告）。
2. `python -m py_compile scripts/inbox_uit.py`，另可用一个 `"plugin": "../docs/x"` 的假正文跑一次 `inbox_uit.py --issue 0 --body …`（预期：当前会在 `uit-packs/../docs/x.json` 落盘）。
3. `python scripts/audit_packs.py`（查库包里是否已有缺占位符的译文）。
4. 维护者自带的 `fgtest`（本仓无测试工程；建议补 `UITextPatcher.RecoverLiteral`、`UITextFlow.MergeExtraction` 的 `Prune.Any → ReapplyNeeded`、`UTextPack.MergeLibrary` 占位符闸门三条离线断言，正好钉住本批次改动）。
