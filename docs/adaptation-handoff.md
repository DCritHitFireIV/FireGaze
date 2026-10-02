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
