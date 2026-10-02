# 190 插件适配任务 —— 交接文档（2026-10-02 夜，用户休息前委派）

## 目标

对用户安装的**全部插件（190 个有主 DLL）**做「FireGaze 汉化适配」评估与修复：

- 逐个检查：能否抽取、候选是否合理、有无特殊形态（本地化文件 / 资源 / 多语言词典 / 数据文件）、是否加壳；
- 修复抽取器 / 适配问题（构建 + fgtest + 本地提交）；
- 产出每个插件的适配卡片与汇总报告。

## 已完成（阶段 0–1）

- **盘点**：`C:\Users\Fire\fg-adaptation\inventory.json`（190 行）
  - 31 个有译文包、26 个已打补丁、**159 个从未处理**；
  - 有本地化目录：**Aetherphone、AutoDuty、BOCCHI、Henchman、PetRenamer**（BOCCHI 在不汉化名单）。
- 云上 `uit-packs` 已清空（用户要求）；AutoDuty 本地汉化已删、DLL 已还原（哈希对得上）。
- P0–P4 已完成（重复译文保 ID / 字典值候选 / 术语表重译入口 / P4 评估结论），见
  `docs/known-issues-2026-10-02.md`。版本 1.3.1。

## 进行中（阶段 2：批量分析）

- **分组子代理并行分析**（每组约 24 个插件）→ 报告 `C:\Users\Fire\fg-adaptation\reports\group-<N>.md`；
- 每组报告要求：**顶部「异常清单」**（候选=0 / 加壳 / 特殊形态），其后每插件 3–6 行
  （候选数：UI/Ambiguous/Excluded、资源/属性段、目录特殊文件、异常）；**不 dump 大 JSON**。

## 待办（阶段 3–4）

- 汇总 8 份报告 → 修复清单 → **逐类修复**（每类：Rebuild 0 警告 + fgtest 全绿 + 本地提交 + 版本递增）；
- 汇总报告 `C:\Users\Fire\fg-adaptation\SUMMARY.md` + **用户操作建议**（哪些插件推荐一键汉化 / 勾灰名单）。

## 关键约束

- **不改用户的插件 DLL**（补丁由用户在游戏内「一键汉化」触发）；**不替用户跑翻译**（要花用户 token，且翻译规则在云端）。
- 修复只动 FireGaze 源码；**每次提交前跑 `dotnet build -t:Rebuild`（0 警告）+ fgtest**。
- 探针：`C:\everyone\FireGaze-Refactor\tools\UITextProbe\bin\Release\net10.0\UITextProbe.exe <dll> --all --json`
- **上下文策略**：父会话保持轻量、不读大 JSON；**接近 80% 时以本文件为续接点**（新会话先读本文件 + `reports/`）。

## 执行记录（2026-10-02 夜，完成后更新）

### 子代理基础设施问题（重要，不要再踩）

- **workflow fan-out 不可用**：报错 `Background children require the host npm package ... does not provide @earendil-works/pi-agent-core/node`。`pi-agent-core@1.0.0`（npm 公开版）的 exports 只有 `.` / `./package.json`，**没有 `/node`**；workflow 内的子代理（前台/后台都试过）统一走 async 启动 → 全部失败。
- **已做的尝试**：① 前台/后台 workflow —— 均失败；② 在 `pi-coding-agent` 包目录 `npm install`（重装 102 个依赖；嵌套 `node_modules/@earendil-works/` 里原本已有 pi-agent-core，但版本不含 `/node`）——仍失败。**修改过全局 npm 依赖树**（added 102 / removed 25）——若 pi 重启后行为异常，回滚点 = 重装 pi（`npm i -g @earendil-works/pi-coding-agent`）。
- **可用的替代**：**单个直调 subagent（非 workflow）可用**（前台 `scout` 实测成功）。批量任务改用**脚本批处理**（见下）——反而更省父会话上下文。

### 改用脚本批处理（已完成）

- `C:\Users\Fire\fg-adaptation\probe_all.py`：对 inventory 全部 190 个 DLL 跑探针（4 并发、180s 超时）+ 统计（Role 分布/中文数）+ 目录特殊形态 + 异常规则；
- 产物：`probe-stats.json`（每插件一条）、`reports/scan.md`（异常清单 + 全表）、`SUMMARY.md`（分类结论与操作建议）、`recommended.json`（116 个推荐汉化清单）。

### 盘点结论（摘要）

推荐汉化 116 · 自带中文 34 · 不汉化名单 12 · 加壳/无字面量 10；UI 候选合计 14366。9 个可疑项全部复检有结论（FootIk 的配置内部名保持不翻；GoodFriend 有 zh 卫星；其余为游戏数据 key / 内部文本 / 自带中文，均无需修改）。

### 下一步（若继续）

1. FootIk 类「自定义 UI 框架」专项（低优先）；
2. **本轮盘点未发现需要修 FireGaze 的新盲区**（前几轮已把 AcquisitionDate 词典值、AutoHook 重复译文等修掉）。
