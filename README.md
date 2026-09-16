# FireGaze

> 卫月（Dalamud）插件库工具箱 —— 插件简介汉化 · 第三方仓库体检 · 插件列表自动刷新拦截

Author: **fire**

---

## 这是什么

FireGaze 把三个常用的小工具合成一个插件：

### ① 插件简介汉化

内置 1500+ 条第三方插件的简体中文词表，安装器里的三个字段都可以**各自独立**选择展现形式：

| 字段 | 原版 | 中文 | 双语（默认组合见括号） |
| --- | --- | --- | --- |
| 插件名 | English（默认） | 译名（无名可译时回退原文） | `中文 (English)` |
| 一行简介 | English | 纯中文（默认） | `中文` + 空行 + 原文 |
| 插件详情 | English | 纯中文 | `中文` + 空行 + 原文（默认） |

- 名字不动、简介中文、详情中英对照 —— 就是默认形态；想要别的组合，在「简介汉化」页用单选按钮随时切换。
- 词表可以「从 GitHub 更新」，也可以离线使用（插件包里自带一份）。
- 只在原文能对上号时才替换，不会覆盖 FastDalamudCN 等其他插件的产物。

### ② 第三方仓库体检

一个按钮扫描你添加的**全部**第三方仓库（包含已停用的）：

- 链接是否还能访问（404 / 410 = 死链）；
- 内容是不是合法仓库文件（用卫月自己的类型 + Newtonsoft 校验，判定与游戏完全一致）；
- 网络是否可达（超时 / 证书错误等另列为「连接失败」，不会被误当成死链）。

结果显示成一张表，列出状态、地址、**首次被本插件记录到的时间**（卫月本身不保存仓库添加时间，安装 FireGaze 之前就存在的库只能显示「—」）。可以：

- **一键停用**所选（链接保留，只是不加载）；
- **一键删除**所选（会弹窗确认；删除前自动备份完整仓库列表 + `dalamudConfig.json`，删除后可随时撤回）；
- **撤回**按钮始终可见，支持跨越重启恢复上一次「停用 / 删除」。

### ③ 插件列表自动刷新拦截

部分插件会在后台用反射调用卫月的「重载全部仓库」接口：实测来源包括 **每日随记（Daily Routines）** 核心管理器的 10 分钟定时器、**XSZToolbox** 的自更新模块，以及一些公共库（如 **OmenTools**）在插件安装依赖时的转发调用。它们会让插件安装器里的列表自己重刷、滚动位置被顶回顶部。

FireGaze 给这两个入口挂钩子，按**调用来源**拦截：插件发起的后台刷新一律跳过；手动刷新、打开安装器、卫月设置里改仓库、FireGaze 自己的改动都照常生效。三种模式：全部拦截（默认）/ 只在安装器打开时拦截 / 关闭。

---

## 安装

在卫月「设置 → 插件 → 第三方插件仓库」里添加下面任一条，然后到插件安装器里搜 `FireGaze`：

| 线路 | 地址 |
| --- | --- |
| GitHub 直连 | `https://raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/pluginmaster.json` |
| 国内镜像（推荐） | `https://gh.atmoomen.top/raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/pluginmaster.json` |

> 装好之后在游戏里输入 `/fg` 打开主窗口。

## 使用

| 命令 | 作用 |
| --- | --- |
| `/fg` | 打开 / 关闭主窗口 |
| `/fg on` / `/fg off` / `/fg open` | 拦截模式：全部 / 关闭 / 只在安装器打开时 |
| `/fg log` | 在聊天框里显示最近被拦截的来源 |
| `/fg zh` | 立刻重新应用一遍简介汉化 |
| `/fg update` | 从 GitHub 更新词表 |

## 从源码构建

```bash
# 国际服：默认路径 %APPDATA%\XIVLauncher\addon\Hooks\dev
dotnet build src/FireGaze/FireGaze.csproj -c Release

# 国服（XIVLauncherCN）
build.cmd
```

产物：`src/FireGaze/bin/Release/FireGaze/latest.zip`（DLL + 清单 + 词表 + libs/0Harmony.dll）。
把 `latest.zip` 解出来的文件放进卫月插件目录，或把 DLL 路径加进「开发插件位置」即可。

## 词表维护

```bash
python scripts/translate_descriptions.py --sample 10   # 简介 / 详情（DeepSeek）
python scripts/translate_names.py --sample 20          # 插件名（DeepSeek，保守：品牌名保留）
```

两份脚本都支持断点续传，产出根目录的 `translations.json`。更新后重新构建插件即可。

## 说明与限制

- 下载次数由 GitHub Release 的下载量汇总而来（`.github/workflows/download-count.yml` 每 6 小时刷新一次）；走镜像下载的数量不计入。
- 词表按 `InternalName` 匹配；上游改了简介或卫月改了字段名时，对应条目会自动跳过（不会显示过期译文）。
- 仓库删除只影响你的第三方仓库列表，不会删掉任何已安装的插件本体。
- Harmony 钩子只挂在「重载全部仓库」两个入口上，不改游戏本体。

## License

MIT © 2026 fire
