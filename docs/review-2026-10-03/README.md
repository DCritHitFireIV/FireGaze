# 发布前独立评审（2026-10-03，对应 1.3.21）

评审对象：提交 `00623b2`（1.3.20）时的工作树。方法：TRAE code-review 方法学 × 3 路（A 抽取器 / B 补丁管线 / C 投稿与上传）
+ HCI 两路（Nielsen 启发式 / 认知摩擦检测）+ 交叉验证 × 2 路；另外：fixture 边界——C 组问题清单在传入验证器时
被脚本的标记提取吃掉了（报告正文里的 `===ISSUES-JSON===` 示例先命中），**C 的 11 条未经交叉验证**，处置时按需自行核对。

- `code-A-extractor.md` / `code-B-pipeline.md` / `code-C-upload.md`：三路代码核查全文（含 Mermaid 图与问题表）
- `verify-1.md` / `verify-2.md`：交叉验证员的逐条判定（含 A/B 组 19 条的 exists/severity 修正）
- `ui-heuristic.md` / `ui-friction.md`：插件汉化页两路 HCI 评审（v5 还原图 + 实现对照）

修复批次见提交 `d840e40`（1.3.21）与 `docs/uitrans.md` 的「发布前独立评审的修复」一节；未修项也在那一节列出。
