# 元数据维护边界

`Name`、`Punchline`、`Description` 是 FireGaze 维护者维护的插件仓库元数据，不接受普通玩家投稿。旧 Issue 原文、`contributions.json`、`contributions-history.json` 与历史记录继续保留用于审计；拒绝只发生在普通投稿的校验、收件和写回三层。

`type=uit-contribution` 的插件界面文本投稿继续走 `inbox_uit.py`，只处理 `entries`、`resources`、`attributes`，不触碰插件元数据。

维护者在本地使用 `import_contributions.py --maintainer` 可更新元数据；必须显式传入该开关。投稿正文的 `note` 即使含有“维护者”或 `maintainer`，也不能取得写回权限。`--player` 与 `--maintainer` 互斥，`--force` 不会绕过玩家元数据限制。策略模块缺失时导入器直接报错停止。

策略实现：`scripts/metadata_contribution_policy.py`；接线：`scripts/validate_contribution.py`、`scripts/inbox_contribution.py`、`scripts/import_contributions.py`。测试：`scripts/test_metadata_contribution_policy.py`。
