# 审查：玩家译文投稿链路（FireGaze，2026-09-22）

## 0. 审查快照（先读这一段）

- 我只读文件，**没有改任何文件，也没有跑任何命令**；所有结论来自逐行阅读 + 现有产物对账。
- **审查期间工作区在被编辑**：我第一次读 `scripts/import_contributions.py` 时还没有 `--maintainer` / 占位闸门 / `by` 字段；再读时已经有了（`--maintainer`、`_PLACEHOLDER`、`write_public_log` 分两节、`by`/`submitted` 字段、`maintainer-` 前缀），`scripts/README.md` 也同步写了这些参数，`scripts/translation-history.jsonl` 与 `docs/contributions.md` 已经是新格式，存档 `docs/contributions/2026-09-22T175541.json` 已改名成 `maintainer-2026-09-22T175541.json`。
- 所以：**下面 P1-1、P1-2、P1-3 的一部分在快照时点已经修了，我标成"已修 / 仍缺"**；其余条目是我在**当前工作区快照**上仍然成立的。行号以当前工作区为准（未提交状态）。
- 我核对过的最新产物：`docs/contributions.md`（玩家 2 条 + 维护者修正 2 条）与 `scripts/translation-history.jsonl`（4 行，`by`/`submitted` 齐全）**互相对得上**，时间列显示的是投稿时间（17:52 / 18:05）而不是收录时间（17:54 / 17:55）。这部分是对的。

---

## 1. Correct（已经好的，别推翻）

- **玩家投稿 / 维护者修正已经分开了**：`import_contributions.py:209-210` 按 `by` 分两组，`append_section("玩家投稿"/"维护者修正")`（`:250-275`）分两节输出；`:358` 写 `by`；`:180` 存档名前缀 `maintainer-`；README 也写清楚了。这次事故的**机制性**原因（同一个脚本、同一种产物）已经被处理。
- **占位闸门是"默认阻断、`--force` 放行"**（`import_contributions.py:295-305`），并且 `--dry-run` 不会被它卡死（`:301`），符合"先看一眼再收"的顺序。
- **`write_table` 的中文原文清理对 `Source=user` 是豁免的**（`update_translations.py:551-552` 的 `is_user is False`）——玩家译文不会被"上游原文已是中文就删掉 Translated"的清理逻辑误伤。
- **同一份文件重复 import 在"词表没被别的东西改过"时是幂等的**（`:340` 命中则 `applied==0`，`:379` 之后整块跳过，不会重复落存档/重复公示）。
- **`--recheck` 会跳过玩家译**（`update_translations.py:506-507` 明确排掉 `Source == user`，且要求上游给的是外文原文）。
- **`.gitignore` 的方向是对的**：`scripts/translation-history.jsonl` 明确不忽略（`.gitignore` 末段有注释说明），`docs/contributions/` 也没被忽略；`scripts/.cache/` 被忽略。这符合"留档要长期在库"的意图。
- **玩家的推送请求失败时不会丢数据**：`ContributeWindow.FinishSubmitIfNeeded()`（`:2631-2655`）只在 `ok==true` 时归档清空，失败会 `SaveExportFile()` 并把内容留在待提交，文案也不谎报（`:2641-2648`）。
- **`archive_payload` 存的 payload 里没有玩家可识别信息**：文档化路径的 JSON 只有 `InternalName/Field/Original/Translated`（`ContributionsStore.BuildJson`），投放进公开仓库不涉及隐私。

---

## 2. 问题清单（按严重度）

### P0-1　`Source=user` 的保护在"上游原文变了"这条路径上整体失效，而且覆盖动作在仓库里不留痕

**现象**：README 与脚本都承诺"写回的条目 `Source = user`，**机器翻译永不覆盖**"（`scripts/README.md:82`、`import_contributions.py:20-23` 规则①）。但只要上游原文变了（上游作者改简介、仓库换成 CN 分支、`--repos` 拉到新文本、中英混排被判定变化），周更就会**直接用机器译覆盖玩家译，并把 `Source` 改成 `ai`**。

**依据（`scripts/update_translations.py`）**：
- 保护只写在 `old_translated and not source_changed` 的提前返回里：`:609-617`（Name）、`:657-663`（Punchline/Description）。
- `source_changed` 一为真就走到覆盖分支：`:638-644`（Name）、`:684-690`（desc）→ `new_pair = {... "Source": "ai"}`，只有"机器译恰好等于旧玩家译"这一种巧合才保留 `user`（`:642-644`、`:688-689`）。
- 最狠的一条：新原文被判为"已是中文"时，`:680-682` 直接 `entry[field] = {"Original": original}` —— **连 `Translated` 和 `Source` 一起删掉**，玩家译文静默消失（该分支在 `source_changed=True` 或 `copy_of_source=True` 时才可达，即"上游把英文简介换成了中文简介"这类真实变化）。
- `copy_of_source`（`:441-450`）把"旧译文 == 外文原文"当成"没译"，**不看 `Source`**，因此 `:657` 的条件对玩家译同样放行覆盖（玩家把日文原文原样保留的情况会被重翻顶掉）。
- 唯一记录是 `needs_review` → `scripts/translation-review.md`（`:771-783`）。这个文件**不在仓库里**（`find scripts/*.md` 只有 README），而周更只提交一个文件：`.github/workflows/translate.yml:62-70`（`git diff --quiet -- translations.json` / `git add translations.json`）。也就是说：**覆盖发生了，报告生成在 runner 上、随后被丢弃**（Actions 日志 90 天后也没了）。

**后果**：公开词表可能被无声改回机器译；"永不覆盖"的承诺是错的（正确表述只能是"原文一字未变时成立"）；一旦发生，仓库里没有任何痕迹（既没有 history 追加——见 P1-3，也没有被提交的复核报告），玩家和插件 UI 反而会一直显示"你译"。

**最小修法（不重构）**：
1. `is_user and (source_changed or copy_of_source(...))` → **保留玩家译**，只更新 `Original`，把旧原文存到 `previousOriginal`，并打 `Review: "上游原文已变，译文待复核"`；
2. 报告路径从 `scripts/translation-review.md` 换到 `docs/translation-review.md`（或 `docs/` 下任意路径），并把 `translate.yml:68` 改成 `git add translations.json docs/`；
3. 万一确认要覆盖，**同时把这次覆盖追加进 `scripts/translation-history.jsonl`**（`by: "machine"`），否则"可回滚"在数据上是假的。

**复核命令（需要你跑）**：
```bash
# 1) 确认复核报告从未进库
git log --oneline -- scripts/translation-review.md docs/translation-review.md
# 2) 造一个最小场景验证覆盖（把某条的 Original 手动改成新文本后跑周更的 --stats-only 看不到，需要真跑一次）
python scripts/update_translations.py --limit 1        # 在有网/有 key 的环境
```

---

### P0-2　收稿端对内容零校验，而且推送凭据是**分发到每个玩家手里的客户端常量**

这两条要合起来看：一个"没有校验的收稿口" + 一个"人人可用的发稿通道"。

**现象 A（零校验）**：`import_contributions.py` 全流程只做了三件事：字段名在不在 `FIELDS`（`:328-330`）、原文对不对得上（`:333-339`）、内部名非空（`:328`）。**长度、URL、HTML、违规词、空译文一律不管**。而"同一套口径"的规则脚本**已经存在并且现在没有任何调用方**：
- 规则原文在 `scripts/validate_contribution.py:34-96`（`Name≤40 / Punchline≤99 / Description≤999`、控制字符、`URLISH`、`HTMLLISH`、`DENY_WORDS`、空译文判不合格）；
- 插件侧只是**客户端礼貌性**预检（`src/FireGaze/Translate/ContributionRules.cs:31-61`），文件头自己写着"与 scripts/validate_contribution.py 同一套口径"（`ContributionRules.cs:6`）；
- 仓库里已经没有 `.github/workflows/validate-contribution.yml`（`.github/workflows/` 只有 `download-count.yml / release.yml / translate.yml`），全仓 grep `validate_contribution` 只剩注释和 `docs/hci/` 旧评审 → **这个脚本是死代码**。
- 顺带两个不一致：`validate_contribution.py:79` 把空译文判成"不收"，而插件把空译文当合法语义（`ContributionsStore.cs` 的 `ContributionRecord.Translated` 注释"空字符串 = 清除译文"、`TranslationTable.cs` 的 `MarkUserTranslation` 参数文档同）。改之前必须先定：空译文是"撤销"还是"非法"。

**现象 B（凭据公开）**：`src/FireGaze/Translate/PushNotifier.cs:17` 的 `DefaultUrl` 里就带着 `uid + sendkey`（`https://27428.push.ft07.com/send/sctp…send`），并且是**默认值**：`Configuration.cs:97` `PushUrl { get; set; } = PushNotifier.DefaultUrl`。插件是分发给玩家的，DLL 可反编译；仓库本身也要公开（玩家用 `/firegaze update` 直接拉 `translations.json`，`TranslationTable.cs:49-50` 写死了 raw 地址）。

**后果**：任何拿到插件的人（或读到仓库的人）都能往你手机推伪造的「FireGaze 翻译贡献」。收稿端又恰好"无校验、信任手机上的内容"，于是这条链能把任意内容写进公开词表 + 公开留档；同时它也是一条垃圾/钓鱼推送通道（你现在的判断依据就是"手机上这一条"）。这不需要复杂攻击：`title` 里那行"翻译贡献 N 条"是客户端自己写的，收稿时没有任何东西能证明它来自插件。

**最小修法**：
1. **客户端不发密钥**：`PushUrl` 默认值改空字符串（新装玩家走"导出文件 / 复制 issue 正文"），你自己的机器在设置里填；旧的 sendkey 轮换一次。
2. **收稿当作不可信输入**：`import` 里复用 `validate_contribution.py`（见 P1-2 的分级方案），并把 `extract_json`（`:47-60`）也一起复用——它顺带解决"把整条推送正文（含 ```json``` 围栏 / markdown 表格）存成文件时 `load_json` 直接抛 JSONDecodeError"的问题，也让 `contributions` 是**列表**的载荷不再 `AttributeError`。
3. 推送正文里带一个可核对的批次标识（如 `batchId`+条数+时间戳），收稿时打印出来让你对手机那一条——把"手机显示的内容"和"将要写库的内容"绑定起来。

---

### P1-1　留档的"谁改的"仍然是**靠人记得加参数**，这次的坑会原样重演

**现象**：分类完全依赖 `--maintainer`（`:358`：`"by": "maintainer" if args.maintainer else "player"`），**默认是 player**。今天这场事故的成因就是"维护者用同一个脚本改了自己的值、没有任何标记"——新代码没有改变这个默认，也没有任何自动识别。

**依据**：
- `:358` 默认 `player`；
- payload 里其实**有**可用信号却被完全忽略：维护者那份存档的原件里写着 `"note": "维护者修正：把测试期间写下的测试值恢复成正式译文。"`（`docs/contributions/maintainer-2026-09-22T175541.json`），脚本只读 `payload["contributions"]`（`:288`），顶层其它键一律不读；
- 另一类信号也没用上：文档化的推送路径产出的 JSON **不含 `Id` / `TimeLocal`**（`ContributionsStore.BuildJson` 只输出 4 个字段），而插件本地导出/records 里是有的。缺这两个键 = "不是插件导出的" = 大概率是维护者手写/手工整理的。

**后果**：一次忘记 `--maintainer`，公开留档里就又多一条"玩家把 X 改成了 Y"的假归因。因为投稿是匿名的，**`by` + `Id` + 时间就是这条链路唯一的可追溯性**，而 `by` 靠人的记忆力。

**最小修法（都很便宜）**：
1. 自动预判 + 明确提示：`Id`/`TimeLocal` 缺失、或 `note` 里含"维护者/修正/撤回/测试"→ 归 `maintainer`；否则归 `player`；
2. 拿不准时**不要猜**：改动比 `--dry-run` 更早一步——如果这份 JSON 看着不是插件导出的，就先停一次，要求显式 `--player` 或 `--maintainer`；
3. `--dry-run` 输出里加一行结论：`这批会被记为「玩家投稿」N 条 / 「维护者修正」M 条`（这一行就能让今天的自己当场发现不对）。

---

### P1-2　"像测试数据"的闸门该分层，现在这一版会误伤合法短译文，而且覆盖不到真正会污染词表的东西（已修一半）

**现象**：闸门只查 `Translated` 的"占位样"，且是**整批阻断**（`:301-303`），包含一条明显过宽的规则：`field in ("Punchline","Description") and len(text) <= 2` → "译文只有一两个字"（`:156-157`）。

**依据**：`looks_like_placeholder`（`:148-159`）三条启发；`_PLACEHOLDER`（`:145`）；阻断逻辑 `:301-303`；README 也把它写成了"停下来"（`scripts/README.md:70-71`）。

**后果**：`Punchline/Description` 两个字的合法译文（"截屏"、"副本"、"换装"这类）会让**整批**停摆，逼你每次都用 `--force`——而 `--force` 是一次性关掉**所有**启发（包括真正的 `测试1`），于是闸门很快变成"肌肉记忆加 --force"，等于没有。反过来，它挡不住真正会污染公开词表的东西：5000 字的 Description、`discord.gg`、"<b>"、违规词、空译文（这些都在 P0-2 里）。

**最小修法（分"警示/阻断"两层，成本≈10 行）**：
- **阻断**（默认停，必须 `--force` 或改数据）：复用 `validate_contribution.check_one` 的硬规则——译文为空（先定义好语义，见 P0-2）、超长（40/99/999）、控制字符、内部名非法（空/含空格/>200）。
- **警示**（打印 + 结尾汇总，不阻断）：URL / HTML / 违规词；占位模式；`译文长度≤2`；"改后 == 改前"；"改后 == 改前的旧值"（**这条正是这次事故的形状：把值改回原样**——它作为警示出现，你一眼就会看到）。
- 不管收不收，**都落存档**（见 P1-4），并把判断结果写进存档侧记。

---

### P1-3　四份材料会互相对不上：history 不被周更追加、公示不被周更重生成、存档只写一半

**现象**：这条链上的"四份材料"的一致性其实**没有被任何机制保证**，只有一个"重新生成"关系：`jsonl → contributions.md`（`import_contributions.py:189`）。两边都不受周更影响。

**依据**：
- 周更**从不**追加 `scripts/translation-history.jsonl`（`update_translations.py` 全文没有对它写入；`.github/workflows/translate.yml` 只提交 `translations.json`）→ 一旦周更改动了某个 `Source=user` 的字段（P0-1），`translations.json` 里是新机器译，而 `translation-history.jsonl` 最后一条还写着"→ 玩家译文"，公示也照旧写着"玩家投稿 … → 玩家译文"。**"可回滚"（`:21`、`README:82`）在这类场景下回滚不到正确状态**。
- `archive_payload` 只在 `if applied:` 里面（`:379-395`），且**原样整份保存**——包括被 `:333-339` 跳过的记录。当这批里既有收录又有跳过时：**存档 ⊃ 公示**（存档里有条目、公示里没有），存档里也没有任何字段标明哪条被收、哪条没被收。
- 反向情况更糟：当**全部**记录都被跳过时 `applied == 0` → 整块跳过 → **不落存档、不更新公示、退出码仍是 0**（只剩一句"没有需要写回的条目。"）。README 说的"仓库里这两份才是长期凭证"（`scripts/README.md:78`）在最需要凭证的场景（"收到了但没并入"）恰好不成立，而推送消息会过期。

**后果**：拿留档做统计/审计的人会得到互相矛盾的结论；"投稿被收到过"这件事可能完全无据；跳过原因只存在于当时那次终端的 stdout 里。

**最小修法**：
1. 把 `archive_payload` 移出 `if applied`，**无条件落盘**；
2. 在存档里加一段侧记（不改动原始 records）：`"import": {"at":…, "by":…, "applied":N, "skipped":[{"plugin":…, "field":…, "reason":…}], "force":false}`——存档本来就是"你存下来的那份拷贝"，加侧记不影响它作为证据；
3. 公示里加一节 **「已收到 · 未并入（待复核）」**（只要 `skipped` 非空就出现）；这同时是 Q5 的答案（玩家能看见"收到了但没通过"）。

---

### P1-4　`--push` 的 `git commit` 失败时会报"已推送到 GitHub ✓"

**现象**：`push_to_github` 只在 `git add` 失败时提前返回（`:104-111`），`git commit` 失败**只打印一行就继续**（`:113-118`），随后无论有没有新提交，只要 `git push` 返回 0 就打印 `已推送到 GitHub ✓` 和留档 URL（`:127-132`），并 `return True`。

**依据**：`:113-118` 无 return；`:127-132` 成功分支。

**后果**：最容易踩的触发条件恰好是常见条件——本机 `user.name`/`user.email` 未配置（脚本从来没设置过，`github_token()` 只解决凭据），或 hook 失败。此时文件已经写好在工作区、`git push` 会成功（远端已是最新），于是你看到"已推送 ✓ + 留档链接"，**以为留档已公开，其实什么都没提交**。rebase 失败的分支也一样：`:120-125` abort 后继续 push，而手动提示只给了 `git push origin main`（此时必然还需要先 fetch/rebase）。

**最小修法**：`commit.returncode != 0` → 立即打印"没提交成功，已停在这里"并 `return False`（不要继续 push）；同时把手动提示改成 `git fetch origin main && git rebase origin/main && git push origin main`。

---

### P1-5　玩家"自己补"的字段（上游没提供的）**永远不会在游戏里生效**，但会被公示、被 UI 承诺

**现象**：`ContributeWindow` 明确鼓励"上游没提供、你也可以自己补一份"（`:1540-1545` 的提示、`:466-478` 的筛选说明），README 也写"玩家可以补，补的也算 user"（`scripts/README.md:86-87`），`TranslationTable.MarkUserTranslationCore` 也专门支持"原文置空、译文照写"。但真正把文本写进游戏清单的 `ManifestPatcher` 第一件事就是拒绝：`ManifestPatcher.cs:126-131` —— `if (string.IsNullOrWhiteSpace(original)) return false;`。

**依据**：`ManifestPatcher.cs:126-131`；`TranslationTable.cs:262-275`（"上游本来就空着、玩家自己补的：把原文置空，译文照写"）；`ContributeWindow.cs:1697`（提交时 `original` 就是空串）；`ManifestPatcher` 是唯一的改写入口（`Plugin.cs:94/590/601`）。

**后果**：这类投稿会被写进词表、被 `docs/contributions.md` 公示为"（空）→ 你的译文"、插件界面也会显示"你译/完成度提升"，但游戏里永远看不到——**留档公示了"已收录"，玩家验收不了**。

**最小修法（二选一，都不要重构）**：
- 诚实标注（最便宜）：UI 与公示里把这类条目标成"待上游提供原文后生效"；或干脆在 `ContributeWindow.DrawField` 对 `!upstream` 的字段加一行说明；
- 真让它生效：`ManifestPatcher.ApplyField` 里允许"`Original` 为空 && 当前文本为空"时写入 `translated`（`ManifestPatcher.cs:128/149-152` 两处），并在 `restore` 时写回空串。

---

### P1-6　这次事故的产物**现在仍然公示在公开留档里**，而且没有任何"测试"标记

**现象**：`docs/contributions.md` 当前内容仍然是：

| 时间 | 插件 | 字段 | 改前 | 改后 |
|---|---|---|---|---|
| 17:52 | AQuestReborn | 插件名 | 重生任务 | **测试1** |
| 17:52 | AQuestReborn | 插件详情 | … | **测试1** |

并且文件顶部写着"玩家投稿累计 2 条"——**这个计数里 2 条全是测试值**。

**依据**：`docs/contributions.md`（玩家投稿小节）；`scripts/translation-history.jsonl` 前两行。

**后果**：这是长期公开凭证。将来（包括你自己）回看会读出"有个玩家把 AQuestReborn 的插件名提交为『测试1』且被收录"；做统计时这 2 条会一直算数。

**最小修法**：保留证据但**改语义**——在玩家投稿小节里加一条说明（或把这两行挪进「测试数据（已回滚）」小节），并在文件顶部把计数口径写死（例如"真实玩家投稿 0 条 · 测试数据 2 条（已回滚）· 维护者修正 2 条"）。存档 `docs/contributions/2026-09-22T175441.json` 建议保留，但可以加个说明文件或把它也标为测试来源。

---

### P2-1　"时间"这一列将来会变成两种含义；`Id` 在文档化路径上根本传不到

**现象**：公示的"时间"取值是 `row.get("submitted") or row.get("time")`（`:238`、`:248`）——`submitted` 来自记录里的 `TimeLocal`（`:357`），缺就 fallback 成**入库时间**。而文档化的流程是"把推送正文里的 ```json``` 存成文件"（`scripts/README.md:59`），推送正文里的 JSON 来自 `ContributionsStore.BuildJson`，**不含 `TimeLocal`，也不含 `Id`**。

**后果**：以后从推送正文收的批次，公示里"时间"= 入库时间（与今天这 4 行的语义不同，同一列两种含义，无从分辨）；`translation-history.jsonl` 的 `source` 会是空串（`:359` 的 `record.get("Id")` 为空）→ 无法与玩家本机 `contributions-history.json` 的记录对上，也无法与存档互链。
**最小修法**：`BuildJson` 里补 `Id` / `TimeLocal`（一行）；公示拆成"投稿时间 / 收录时间"两列，缺失显示 `—`。

### P2-2　重复 import 不是无条件幂等；存档文件名会被同秒覆盖

**现象**：`:340` 的跳过条件是"词表值已经等于译文 **且** `Source=user`"。如果这份投稿此前已收，之后该值又被别的东西改过（周更覆盖、你手改、别的玩家），**再 import 同一份文件会再写一条 history、再落一份存档**（`:349-372`、`:394`），公示里同一条投稿出现两次、计数翻倍；`source`(Id)+field 没有去重。存档文件名是秒级 `%Y-%m-%dT%H%M%S`（`:179-181`），同一秒内两次 import 会静默覆盖前一个文件。
**最小修法**：import 前扫一遍 history，若已存在相同 `source`(Id)+`plugin`+`field` 的行，打印"这条已经收过（time=…），跳过"并计入 `skipped`；文件名加 `-2` 序号或内容 hash 后缀。

### P2-3　留档里的"值"与词表里的 `Source` 是两套语义，`_meta` 被重写

- 维护者的修正也会以 `Source="user"` 写进词表（`:349` 无条件），于是**词表层面把"维护者改的"和"玩家译的"混为一谈**：插件 UI 会一直显示"你译"，`--recheck` 会永远跳过它（`update_translations.py:506-507`），你以后想用机器重译它就得多做一步。建议加一个**不破坏现有解析**的旁键（`TransEntry`/`TransPair` 用的是宽松反序列化，未知键会被忽略：`TranslationTable.cs:408-437`），例如 `"By": "maintainer"`，只用于报表与跳过判断，不动 `Source`。
- `import_contributions.py:381` 把 `_meta` 重写成**只有** `updatedAt`。今天 `_meta` 只有一个键所以无痛，但下次加键就会被这个脚本静默清掉（`update_translations.py:530-537` 同样）。

### P2-4　写入不是原子的，而且没有"补一次"的入口

`write_table`（`:388`）→ history 追加（`:391`）→ 存档（`:394`）→ 公示（`:395`）四步无事务、无临时文件+rename。中途崩/断电会留下"词表已变、公示/存档没跟上"的状态；此时**重跑同一份文件是 no-op**（`applied==0`，P2-2），所以没有自助修复路径。建议：先写临时文件再 `os.replace`；再加一个 `--rebuild-log`（只从 history 重新生成公示+补齐缺失存档，不动词表）。

### P2-5　推送成功但窗口被关掉时不会归档 → 同一批可能被推两次

`FinishSubmitIfNeeded()` 只在绘制入口被调用（`ContributeWindow.cs:215`），而 `sendResults` 的收尾（归档/清空/状态行）都在它里面（`:2631-2655`）。玩家点完"一键提交"立刻关掉窗口 → 成功后不归档，待提交仍在，再点一次就重复推送同一批（与 P2-2 的重复收录叠加）。最小修法：把收尾挂到不依赖绘制的时机（例如推送完成后投递到 Dalamud 的框架线程执行队列），或在窗口关闭时补跑一次收尾。

---

## 3. 逐条回答你问的 6 个问题

**Q1 玩家投稿 vs 维护者修正是同一份留档会怎样？留档语义应当怎么定？**
具体后果有三层（不止"看着乱"）：
1. **假归因**：匿名投稿没有作者可追溯性，`by` 是唯一归因字段；维护者的撤回被写成玩家投稿 = 把维护者造成的变化记到玩家头上（这次 4 条里 2 条就是）。今天的文件里"玩家投稿累计 2 条"包含两条测试值，计数本身已经是错的。
2. **追溯断裂**：撤回/修正的**原因**（"撤回测试值"）只存在于你手写 JSON 的那个 `note` 里，而收稿脚本不读它（`:288`），公示/存档/jsonl 都不保存原因 → 三个月后没人知道为什么改回去。`Id` 现在也不是每条都有（P2-1）。
3. **统计被污染**：将来做"投稿数/活跃度/某插件被改过几次"必然被误导——同一次测试贡献被当成 2 条玩家投稿 + 2 条玩家投稿（因为撤回也被算进去），churn 直接翻倍。
**建议语义**：`scripts/translation-history.jsonl` 定位为 **"词表变更的 append-only 审计日志"**，每条必须带 `by`（player / maintainer / machine）+ `source`(Id) + `submitted` + 原因（可选 `reason`）；`docs/contributions.md` **只是它的一个视图**，且"玩家投稿"必须是过滤后的视图，标题/计数要写成"玩家投稿 N 条 · 维护者修正 M 条 · 测试/作废 K 条"。这条语义现在已经实现了一半（分节 + `by`），还缺：`by` 的默认值与自动判定（P1-1）、原因字段、"作废/测试"这一档（P1-6）、以及"machine 也要进日志"（P0-1/P1-3）。

**Q2 测试数据误入公开词表还有哪些缝？最低成本闸门怎么加？**
缝有五处（按链路顺序）：① **推送正文 → 存文件**（人工复制，可能只存一半/带 markdown 围栏，`load_json` 直接抛异常）；② **文件 → import**（无 schema 校验、`contributions` 是列表就崩、不读 `note`、不认"这不是插件导出的"）；③ **import → 词表**（内部名是否真实存在不检查 → 拼错/伪造的 `InternalName` 会凭空造条目，`:332` `table.setdefault`；长度/URL/HTML/违规词/空译文不检查）；④ **词表 → 公开产物**（测试值无标记，P1-6）；⑤ **词表 → 周更**（`Source=user` 在原文变化时失效，P0-1）。
**最低成本闸门**（维护者手动流程，不能搞重）：一条 import 里分三层就够了——
- 复用已经写好的 `scripts/validate_contribution.py`（**今天它是死代码**）：硬规则（空译文语义先定好 / 40·99·999 长度 / 控制字符 / 内部名非法）→ **阻断**；URL / HTML / 违规词 → **警示**；顺手复用它的 `extract_json` 解决"存了整条正文"的情形；
- 占位启发降级：`^测试\d*$`/`test`/`xxx`/`123…` 与"改后 == 改前的旧值"→ **阻断**；"Punchline/Description ≤2 字"→ **警示**（现在这条会把合法的两字简介变成整批停摆，是这一版闸门最需要改的地方）；
- **兜底留痕**：无论收不收都落存档 + 侧记 `applied/skipped/force`（P1-3）。这三层加起来不到 30 行，而且不需要新规则文件。

**Q3 四份材料的一致性与 `--dry-run`/重复 import/被周更覆盖**
- `--dry-run`：真的不写任何文件（`:305-307` 在写盘前返回），这点没问题。唯一意见是输出里"⚠ 像测试数据"和"应用 N 条"挨在一起、重定向到日志时容易漏看 → 结尾加一行结论。
- 重复 import 同一份：**只在"词表值没被别的东西动过"时幂等**（`:340`）。值被改过（周更覆盖/手改）时再 import → history 多一条、存档多一份、公示里同一条投稿算两次（P2-2）。没有任何按 `Id` 去重的机制。
- 被周更覆盖：**这才是最大的不一致来源**。周更永远不改 `translation-history.jsonl`，也永远不重生成 `docs/contributions.md`，但**可以改 `translations.json` 里 `Source=user` 的值**（P0-1）→ 于是"词表 ≠ history ≠ 公示"。而反方向（该被覆盖的没被覆盖）也有：`write_table` 对 `Source=user` 一律豁免中文原文清理（`:551-552`），这是对的；但玩家补的空原文条目永远不会被周更补上 `Original`（周更只按语料写，语料多半也没这个字段），于是它一辈子留在"有译文、无原文"的状态（P1-5）。
- `--push`：写盘在 push 之前，push 失败不丢文件（`:104-118` 的注释与行为一致），但 **commit 失败会假报成功**（P1-4）。

**Q4 `Source=user` 的保护在所有路径都成立吗？**
不成立。逐条：
- **原文一字未变**：成立（`update_translations.py:609-617`、`:657-663`）；`--recheck` 也跳过（`:506-507`）。这是唯一真正成立的情形。
- **原文变了**：不成立——被机器译覆盖，`Source` 变 `ai`（`:638-644`、`:684-690`），只有复核报告（未被提交的 `scripts/translation-review.md`）。
- **原文变成中文（upstream_is_localized）**：对 `Punchline/Description` 最严重——**`Translated` 与 `Source` 一起被删**（`:680-682`）；对 `Name` 则把中文原文当译文写进去且 `Source=ai`（`:633-641`）。
- **旧译文 == 外文原文**：`copy_of_source`（`:441-450`）不看 `Source`，所以玩家译同样会被重翻覆盖（`:657`）。
- **`write_table` 清理**：对 `Source=user` 正确豁免（`:551-552`）；但注意 `is_user is False` 对"没有 `Source` 键的老条目"也成立，中文原文的老条目译文会被删（这是原意）。
- **玩家译 vs 机器译冲突**：实现上只有"两者完全相同时"才站在玩家这边（`:642-644`、`:688-689`）——说明意图是"玩家优先"，但落地成了"巧合优先"。
- 另外一条保护是**意外成立**的：官方主库（Dip17）插件不在 Aetherfeed+`--repos` 语料里，所以永远不会进 `for key, entry in corpus.items()`（`:470`），玩家译天然安全（但也因此没有任何自动维护/复核）。

**Q5 玩家怎么知道自己的译文被收了？仓库侧还能做什么？**
现在什么都不做，也做不到：插件的"历史提交"只在本机（`ContributionsStore.HistoryFilePath`），推送消息会过期，公开留档里没有任何可核对的键，而**文档化路径还把 `Id`/`TimeLocal` 丢在客户端**（`BuildJson` 只输出 4 个字段）。在"不做插件侧状态列"的前提下，仓库侧能做的最小集：
1. **留住钥匙**：`BuildJson` 带上 `Id`（+`TimeLocal`），收稿时原样写进 jsonl 的 `source`（`:359` 已经预留了）——这样玩家拿自己 `contributions-history.json` 里的 `Id` 就能对上，是唯一零成本的"我那条在哪"。
2. **把 JSONL 当成公开查询接口来对待**：`scripts/translation-history.jsonl` 已经是机器可读索引（`source/plugin/field/from/to/by/submitted`）；在 `docs/contributions.md` 顶部写死"怎么查自己那条"（搜 `InternalName` + 你的译文，或看 jsonl），并把留档 URL 写进 `scripts/README.md`（现在 URL 只出现在脚本 `:129` 的一句 print 里，README 里没有）。
3. **把"已收到 · 未并入"也留档**（P1-3）：否则玩家永远分不清"没收到"和"收到了没通过"。
4. **写清"什么会被跳过"**（原文变了会被跳过，`:333-339`）：一段 README 说明 + 上面那条公示，玩家自己就能判断，零代码。

**Q6 你没问但我认为危险的地方**
- `--push` 的假成功（P1-4）——最容易踩、后果最隐蔽。
- 空原文补译永不生效但被公示（P1-5）——"已收录"的公示与游戏内效果不一致。
- 凭据内嵌进玩家手上的客户端（P0-2 B）——把收稿口变成公开入口。
- 存档命名 = 收录时间、秒级、可被同秒覆盖（P2-2）；公示"时间"列语义会漂移（P2-1）。
- 写入非原子 + 无自助修复入口（P2-4）；重复 import 不是无条件幂等（P2-2）。
- `.gitignore`：`translation-history.jsonl` 与 `docs/contributions/` 都没被忽略（对）；但 `scripts/translation-review.md` 是"每次生成、永远不提交"的孤儿（P0-1），建议换到 `docs/` 并纳入工作流提交。
- 隐私：目前 payload 里没有玩家可识别信息（对）；提醒一句——将来若在 `note` 或导出里加"来自谁的机器/uid"，就会直接进公开仓库。
- 并发：`ContributionsStore.Save/SaveHistory` 是 `File.WriteAllText` 直写（无临时文件+rename），多开游戏/多实例会互相覆盖；`main` 分支上"周更 bot 提交"与"你本地 import push"是典型的非快进竞争，脚本对 rebase 失败只是 abort 后继续（P1-4）。

---

## 4. 总结

**能不能放心用**：现在的链路"日常能用，但不能放心依赖"——因为它对外承诺的三件事（机器译永不覆盖玩家译 / 公开留档是长期凭证 / `--push` 一条命令进仓库）在真实边界条件下都会破：原文一变 `Source=user` 就被顶掉且不留痕（P0-1），投稿被跳过时"凭证"和"计数"同时缺失或翻倍（P1-3/P1-1/P2-2），`--push` 还会在没提交的情况下报成功（P1-4）。

**必须先补的 4 条**（按顺序，都是小改动）：
1. **P0-1**：`update_translations.py` 里 `is_user` 遇 `source_changed` / `copy_of_source` / 中文原文时**一律保留玩家译并打 `Review`**，把复核报告写到 `docs/` 并让 `translate.yml` 一起提交（或至少覆盖时追加 history）；
2. **P0-2 / P1-2**：`import_contributions.py` 复用 `validate_contribution.py`（`check_one` + `extract_json`）做"硬规则阻断 + 软规则警示"，占位闸门把"≤2 字"降级为警示；`PushUrl` 默认值清空并轮换 sendkey；
3. **P1-3 / P1-4**：存档无条件落盘 + 侧记 `applied/skipped`，公示加"已收到 · 未并入"一节；`git commit` 失败必须中止而不是报"已推送 ✓"；
4. **P1-1 / P1-6**：`by` 改成可自动判定（并让 `--dry-run` 打印这批会记成"玩家投稿"还是"维护者修正"）；把 `docs/contributions.md` 里这两条测试值标成测试数据（并修正计数口径）。

做完这 4 条，这条链路才谈得上"可信的公开留档"；其余 P2 可以按周更节奏慢慢补。

---

## 5. 需要你执行的复核命令（我没有跑任何命令）

```bash
# A. 证明规则脚本现在是死代码（P0-2）
grep -rn "validate_contribution" --include="*.yml" --include="*.py" . ; ls .github/workflows/

# B. 证明 source=user 的字段存在（用于 P0-1 的针对性检查）
python - <<'PY'
import json;d=json.load(open("translations.json",encoding="utf-8"))
print(sum(1 for k,v in d.items() if isinstance(v,dict) for f,p in v.items() if str(p.get("Source","")).lower()=="user"))
PY

# C. 证明复核报告从未进库（P0-1）
git log --oneline -- scripts/translation-review.md docs/translation-review.md ; git status --porcelain

# D. 复现"重复 import 会双记"（P2-2）：先 dry-run，再改一次词表值，再 import 同一份文件
python scripts/import_contributions.py <某个已收过的.json> --dry-run

# E. 复现"--push 假成功"（P1-4）：在临时克隆里清掉 git 身份后跑带 --push 的收录
git -C <tmp-clone> config --unset user.email   # 然后观察是否仍打印"已推送到 GitHub ✓"
```