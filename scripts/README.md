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

更新流程（需要 `DEEPSEEK_API_KEY` 环境变量）：

```bash
python scripts/translate_descriptions.py --sample 10   # 简介 / 详情，先看质量
python scripts/translate_descriptions.py               # 全量（断点续传）
python scripts/translate_names.py --sample 20          # 插件名（保守：品牌名保留原文）
python scripts/translate_names.py
```

- 两个脚本都从 `%TEMP%/repo_index.tsv`（上次全量扫描抓下来的仓库文件索引）抽取语料；没有就现场抓取。
- 缓存文件（`*_cache.jsonl`）可安全删除，删掉即重翻。
- 改完词表重新构建插件（`dotnet build src/FireGaze/FireGaze.csproj -c Release`）即可随包发布；
  用户侧也可以在游戏里 `/fg update` 直接从本仓库拉最新词表，无需等发版。
