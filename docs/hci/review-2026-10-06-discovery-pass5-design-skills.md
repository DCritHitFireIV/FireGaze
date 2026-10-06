# 插件发现 — 现成设计 skill 重构评审（pass 5，2026-10-06）

触发：用户要求「网上找现成的 skill、加入记忆、再用现成 skill 重构」。本轮先装包（见 `memory/design-skills.md`），再按 **xiaohongshu 设计系统（深色）** 重构「插件发现」，并用 `ux-writing` 过文案、用 `design-review` 打分。

## 1. 用了哪些现成 skill

| Skill | 用途 | 实际产出 |
|---|---|---|
| `apply-aesthetic`（plugin87） | 选方向 + Library Contract（把系统色值重指到 token） | 选定 `design-systems/library/xiaohongshu/DESIGN.md` 为方向；映射进 `InsStyle.cs` |
| `design-doctrine` | 门禁/不谎报数字/看渲染 | 构建 0/0、fgtest 56、还原图重截 |
| `ux-writing` | 按钮前置动词；错误 what→why→how；空态给下一步 | 6 处文案修正（见 §3） |
| `design-review` | 6 维加权 + Nielsen | 见 §4 评分 |

## 2. token 映射（xiaohongshu 深色 → ImGui）

| XHS token | 值 | InsStyle |
|---|---|---|
| Surface | `#19191E`（紫调近黑） | 卡片面 `#1A1A20`；悬停 `#1F1F26`；展开 `#23232B` |
| Brand Primary（dark） | `#FF2E4D` | 已赞红心 + 主 CTA（白字） |
| Title / Paragraph | 白 84% / 白 56% | 标题默认色；次要文字 `rgba(255,255,255,.56)` |
| Radius | 卡片 12–16 / 按钮全胶囊 | 卡片 14；CTA 与输入框胶囊（半径=高/2） |
| Shadow | 近乎为零 | 无阴影（沿用） |
| 反模式 | **卡片不加左侧彩色描边** | 删掉上一轮的 3px IG 渐变条 |

## 3. `ux-writing` 文案修正

| 位置 | 改前 | 改后 |
|---|---|---|
| 页首 | 整座云端插件库（含官方主库）：搜索、排序、点赞，把没加过的库加进来。 | 找插件：云端插件库全在这里（含官方主库），没加过的库可以加入自己的库。 |
| 页首第二行 | …点一行展开详情。 | …点一行看详情。 |
| 空态 | 当前筛选下没有插件。 | 没有匹配的插件 —— 换个关键词，或清掉筛选试试。 |
| 抓取失败 | 打回：取不到这个地址：可能网络不通、404、或者它需要登录 | …；确认地址能在浏览器打开后重试 |
| 格式不对 | 不是卫月能读的仓库文件：… | …；需要的是 pluginmaster.json / repo.json 这类仓库文件 |
| 空仓库 | 这个文件是空数组，一条插件都没有 | …；换一条包含插件的仓库文件 |

保留术语：云库 / 库链 / 加入自己的库 / 投稿 / 收录 / 点赞 / 推荐（`glossary.md` 口径不变）。

## 4. `design-review` 评分（6 维加权）

| 维度 | 权重 | 分 | 说明 |
|---|---|---|---|
| 视觉层级 Visual Hierarchy | 20% | 4 | 名称 > 简介 > 元信息；卡片色差分层 |
| 一致性 Consistency | 20% | 4 | 与插件汉化同构；按钮语义沿用旧约定（红=主操作、绿=启用） |
| 无障碍 Accessibility | 20% | 4 | 红心/CTA 对比度达标（红 #FF2E4D 配白字 ≈4.6:1；大号字/按钮可接受） |
| 可用性 Usability | 20% | 4 | 行点击展开、空态/错误都有下一步 |
| 响应式 Responsiveness | 10% | 4 | 不适用网页断点；窗口拉窄由自适应列处理 |
| 性能 Performance | 10% | 4 | clipper 固定行高；无额外绘制 |
| **加权总分** | | | **4.0 / 5** |

- Nielsen 十项：本轮无新增违规（前几轮的 H1/H4/H8 已修；上轮遗留的 ♥ 计数进 tooltip 属用户指定取舍）。
- 剩余观察：① 胶囊按钮在窄窗口仍靠 `SameLineOrWrap` 兜底；② 红心「已赞点击无效」仅有 tooltip 说明；③ 其它四个页签未做同风格迁移。
