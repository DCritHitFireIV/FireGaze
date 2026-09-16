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
- **只翻译新增或原文有变化的条目**（原文没变就保留旧译文）；
- Aetherfeed 只有 Name/Description，缺 Punchline 的条目会回抓源仓库补齐（带镜像兜底）；
- 完成后自动提交 `translations.json`；用户侧可用 `/firegaze update` 或等插件每两周一次自动更新拿到。

需要仓库 secret `DEEPSEEK_API_KEY`。

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
