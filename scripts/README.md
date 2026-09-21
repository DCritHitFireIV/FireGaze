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

## 玩家贡献（「参与翻译」页）

插件里第四个页签「参与翻译」让玩家直接改译文，存在插件配置目录的 `contributions.json`，
攒够了一条提交到 GitHub issue。收到后：

```bash
python scripts/import_contributions.py <issue 里的 json 或玩家导出的文件> [--dry-run]
```

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
