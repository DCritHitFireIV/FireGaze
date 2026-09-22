# 词表维护

`translations.json` 是插件内置/可分发的简介词表（按 `InternalName` 索引）：

```json
{
  "PalacePal": {
    "Name":       { "Original": "Palace Pal",      "Translated": "宫殿寻宝" },
    "Punchline":  { "Original": "...",             "Translated": "..." },
    "Description":{ "Original": "...",             "Translated": "..." }
  }
}
```

## 自动化（推荐）

`.github/workflows/translate.yml` 每周一自动跑 `scripts/update_translations.py`：

- 数据源 = DalamudRepoBrowser 使用的 [Aetherfeed 仓库清单](https://raw.githubusercontent.com/Aetherfeed/aetherfeed.github.io/refs/heads/main/public/data/plugins.json)（约 1300 个仓库 / 1600+ 插件）；
- **只翻译新增或原文有变化的条目**（原文没变就保留旧译文，一行简介也不会被顺带重翻）；
- Aetherfeed 只有 Name/Description，缺 Punchline 的条目会回抓源仓库补齐（带镜像兜底）；
- 同一个插件有国际版 + 国服汉化分支时，**优先保留英文原文**（中文原文会让游戏里的英文匹配不上）；
- **官方术语表**（`ffxiv_glossary.py`）：运行时从两个公开 datamining 仓库按行 key 拼出「英文 → 国服官方中文」对照（地名/副本/职业/技能/状态等 3.5 万条），把命中的译名随批次喂给模型——例如 `Palace of the Dead → 死者宫殿`、`Eureka Orthos → 正统优雷卡`；游戏文本不入库，只在运行时拉取并缓存到 `scripts/.cache/`（已 gitignore）；
- 完成后自动提交 `translations.json`；用户侧可用 `/firegaze update` 或等插件每两周一次自动更新拿到（词表本身每周一更新）。

数据源：
- 英文：[`xivapi/ffxiv-datamining`](https://github.com/xivapi/ffxiv-datamining)（`csv/en/<Sheet>.csv`）
- 中文：[`thewakingsands/ffxiv-datamining-cn`](https://github.com/thewakingsands/ffxiv-datamining-cn)（国服客户端文本）

需要仓库 secret `DEEPSEEK_API_KEY`。

### 补全「一行简介」与 Aetherfeed 没收录的仓库（本地偶尔跑一次）

Aetherfeed 的语料**完全不含 Punchline**（一行简介），而且它是公开仓库索引，会漏掉国服/小众库。
所以偶尔用本机的 Dalamud 配置跑一遍，把你这 1000+ 个仓库文件当语料：

```bash
python scripts/update_translations.py --repos "%APPDATA%\XIVLauncherCN\dalamudConfig.json"
```

- `--repos` 也接受「一行一个仓库地址」的纯文本文件；
- 它会抓每个仓库的 pluginmaster.json，把 Name/Punchline/Description 一次取全（比 Aetherfeed 新，也会修正原文已变的条目）；
- 加 `--stats-only` 可先看差异不写文件（2026-09-21 实测：Aetherfeed 1669 → 合并后 1724 个插件，
  补出 101 条一行简介、把 384 处缺口翻完，之后 `--stats-only` 报 0 待翻译）；
- 每周的 GitHub 工作流**不带** `--repos`（跑在云端、拿不到你的配置，也用不着每周围着 1178 个仓库转）。


## 玩家贡献（「参与翻译」页）

插件里「参与翻译」窗口让玩家直接改译文，存在插件配置目录的 `contributions.json`；
点「一键提交」会**打开填好的 GitHub 新建 issue 页**（玩家在网页按 Submit）。
**插件里不含任何推送密钥** —— 密钥只在仓库 secret `SERVER3_PUSH_URL` 里，由
`.github/workflows/inbox.yml` 在收到 issue 后通知维护者（独立审查 P0-2：

为什么不让插件直接推手机：插件是公开分发的，内嵌密钥等于把收件地址发给所有人）。

维护者的流程是：

1. 手机收到通知（或直接看 issue）：`docs/contributions/inbox/<issue>-<时间>.json` 里就是原始投稿 + 体检结论
2. 按需看一眼，然后跑：

```bash
python scripts/import_contributions.py docs/contributions/inbox/<存档>.json --dry-run
python scripts/import_contributions.py docs/contributions/inbox/<存档>.json --push
```

（从 issue 收件箱里的文件收录时，`import` 侧记会直接补写到那份文件里，不会另存一份。）

几个参数：

- `--maintainer` / `--player`：这批算谁的。**不写就按内容猜**（缺 `Id`/`TimeLocal`、
  或 `note` 里写了维护者动作 → 猜维护者修正），猜不出来会直接停下要你明确指定 ——
  免得哪天又把自己的修正记成玩家投稿。
- `--force`：明文确认要收「硬问题」条目（测试值 / 超长 / 控制字符 / 空操作）。
- `--dry-run` 不写任何文件，并会打印「这批会被记成玩家投稿还是维护者修正」；
  `--push` 走完收录后自动 `git add/commit/fetch+rebase/push`（提交失败会停住，不会假报成功）。

留档分三节：**玩家投稿 / 维护者修正 / 测试数据（已回滚）**，另加一节
**「已收到 · 未并入（待复核）」**（原文对不上、校验不过的那些也算有据可查，
以前它们只存在于终端输出里）。`docs/contributions/<时间戳>.json` 无条件落盘，
里面带一段 `import` 侧记（收录时间 / 是谁 / 应用几条 / 跳过几条与原因）。

**两层体检**（与 `validate_contribution.py` 同一套口径）：

- **硬问题 → 停下要 `--force`**：插件内部名不合法、字段名不认识、译文超长（40/99/999）、
  控制字符、占位值（`测试/test/todo/xxx/123…`）、**改后与改前一样（空操作）**；
- **提醒 → 只打印**：译文为空（等于清除这条）、含网址、含 HTML、含违规词、只有一两个字。

2026-09-22 事故就是这么把「测试1」写进公开词表的，现在它会被硬规则当场拦下。

`--push` 会依次做完：写词表 → 落 `docs/contributions/<时间戳>.json`（原始投稿）
→ 重写 `docs/contributions.md`（人可读汇总）→ 追加 `scripts/translation-history.jsonl`
→ `git add / commit / fetch+rebase / push origin main`；
GitHub 直连不通时自动走本机代理，失败只会把要手敲的命令打出来，不会丢已写好的文件。

为什么要把投稿存进仓库：推送服务里的消息会过期，仓库里这两份才是长期凭证。

规则（与插件、`update_translations.py` 三边一致）：

- 写回的条目 `Source` = `user`，**机器翻译永不覆盖**；旧值追加进 `translation-history.jsonl`（可回滚）；
- 上游原文变了（贡献里的 Original 与词表对不上）→ **跳过**并列出来，由人决定；
- 包含官方主库（Dip17）的插件：Aetherfeed 语料不含官方库，那部分译文只能靠玩家/人维护；
- 上游本来就空着的字段（没写一行简介 / 详情）**不算缺译**，但玩家可以补，补的也算 `user`。


## 手动维护

```bash
python scripts/update_translations.py --stats-only   # 只报告差异
python scripts/update_translations.py                # 增量更新

python scripts/translate_descriptions.py --sample 10   # 简介 / 详情，先看质量
python scripts/translate_names.py --sample 20          # 插件名（保守：品牌名保留原文）
```

- `translate_*` 两个脚本从 `%TEMP%/repo_index.tsv`（本地全量扫描抓下来的仓库文件索引）抽取语料，适合离线批量重翻。
- 缓存文件（`*_cache.jsonl`）可安全删除，删掉即重翻。
- 改完词表重新构建插件（`dotnet build src/FireGaze/FireGaze.csproj -c Release`）即可随包发布；
  用户侧也可以在游戏里 `/firegaze update` 直接从本仓库拉最新词表，无需等发版。
