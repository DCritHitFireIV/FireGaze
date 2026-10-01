# 公共译文包（uit-packs）

这个目录是 FireGaze「插件汉化」用的**纯数据译文包**：把第三方插件界面上的英文翻译成简体中文，
由玩家端按需下载、在本地打补丁（FireGaze 不改任何在线内容，也不分发插件 DLL）。

## 文件

- `index.json`：索引——哪些插件有包、条数、维护日期；
- `<内部名>.json`：一个插件一个包，格式见 FireGaze 仓库 `docs/uitrans.md` 的「包格式」：
  - `entries`：`ldstr` 字面量（`Original` → `Translated`，`PreserveID` 标记控件 ID 场景）；
  - `resources`：内嵌 `.resources` 容器的文本（身份 = `Container` + `Key`）。

## 生成

由 GitHub Actions 工作流 `uit-packs.yml` 每周一在云端生成并提交（`scripts/uit_library_build.py`）：
下载插件 → 静态抽取界面文本 → DeepSeek 增量翻译（带 FF14 官方译名术语表）→ 写包。
**只翻“会画到界面上”的字符串**；命令名、日志、配置键不翻；官方已有的中文（`zh` 卫星资源）不被覆盖。

## 声明

- 这里的译文是**机器 / 社区翻译**，不代表插件原作者；插件名与界面原文的版权归原作者所有。
- 用不用完全自愿：客户端只在玩家点「一键汉化」时按需拉取对应插件的包，玩家自己改过的译文永不被覆盖。
- **作者要求移除**：在 [Issues](https://github.com/DCritHitFireIV/FireGaze/issues) 提一条（写明插件内部名即可），
  会把对应包从本目录移除并在索引里下架。
