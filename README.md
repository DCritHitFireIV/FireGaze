<div align="center">

  <img src="Resources/icon.png?v=2" alt="FireGaze" width="160" />

  <h1>FireGaze</h1>
  <h5>卫月 (Dalamud) 插件库工具箱</h5>

<p>
  <a href="https://github.com/DCritHitFireIV/FireGaze/releases"><img alt="最新版本" src="https://img.shields.io/github/v/release/DCritHitFireIV/FireGaze?display_name=release&label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC&style=for-the-badge" /></a>
  <a href="https://github.com/DCritHitFireIV/FireGaze/releases"><img alt="下载" src="https://img.shields.io/github/downloads/DCritHitFireIV/FireGaze/total?label=%E4%B8%8B%E8%BD%BD&style=for-the-badge" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/DCritHitFireIV/FireGaze?label=License&style=for-the-badge" /></a>
</p>

</div>

基于 **卫月 (Dalamud)** 平台的 **最终幻想 14 (FF14)** 游戏插件
Final Fantasy XIV Game Plugin Based On Dalamud.

提供以下功能：第三方插件简介汉化，第三方插件仓库坏链检测，拦住插件安装器的自动刷新（并记住列表浏览位置）

Provides: localization of third-party plugin descriptions; scanning of third-party plugin repositories; blocking automatic reloads while browsing plugin repositories.

### 仓库 / Repo

```
https://raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/pluginmaster.json
```

国内镜像：`https://gh.atmoomen.top/raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/pluginmaster.json`

## 下载使用

1. 打开 **Dalamud 设置 - 插件 - 第三方插件**，填入上面的仓库链接并保存。
2. 打开 **插件安装器**，搜索并安装 `FireGaze`。
3. 进游戏后输入 `/firegaze` 打开窗口。

## 从源码构建

```bash
dotnet build src/FireGaze/FireGaze.csproj -c Release   # 国际服
build.cmd                                              # 国服（XIVLauncherCN）
```

## 免责声明

- 本项目为第三方工具，与 `SQUARE ENIX`、`盛趣游戏` 均无附属关系。
- 仓库「删除」只影响你的第三方仓库列表，不会删除任何已安装的插件。
- 使用任何第三方工具都可能伴随潜在风险，请自行评估并承担后果。

---

<div align="center">
  <sub>Final Fantasy XIV © SQUARE ENIX CO., LTD. / 盛趣游戏。<br />本项目仅为社区维护的第三方工具</sub>
</div>
