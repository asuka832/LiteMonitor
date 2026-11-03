# LiteMonitor · Game Ready

**游戏开始前，一次整理后台；游戏结束后，重新打开常用应用。**

基于 [Diorser/LiteMonitor](https://github.com/Diorser/LiteMonitor) 的个人二开版，为内存紧张、希望减少游戏后台占用的 Windows 电脑加入 **Game Ready**。保留原有硬件监控，在同一个软件里完成游戏发现、应用保留、后台退出和应用恢复。

二开维护：[asuka832](https://github.com/asuka832) · [详细使用说明](./docs/game-ready.md) · [上游英文介绍](./README.en.md)

> 二开代码目前位于 **`codex/game-ready` 分支**。上游 Releases 中的原版不包含 Game Ready，请使用此分支构建。

## 新增了什么

| 功能 | 使用体验 |
| --- | --- |
| 自动发现游戏 | 打开 Game Ready 时读取 Steam / Epic 游戏库，自动填入游戏名称、启动入口和主程序路径；支持多个 Steam 库。 |
| 拖入图标与路径 | 将游戏 EXE、快捷方式或 Steam 图标拖到游戏列表，自动补全本地路径；路径框支持拖入 EXE，手动指定路径会在重新扫描时保留。 |
| 保留名单 | 提前选择游戏期间仍需运行的应用，其余普通用户应用进入关闭候选。 |
| 更容易认出的应用列表 | 显示图标、友好名称、EXE、窗口标题和进程数量；窗口应用优先，托盘匹配其次。支持搜索，同一路径的多个进程合并为一项。 |
| 一次清理并启动 | 先请求应用正常退出，超时后强制结束仍在运行的候选，再启动选定游戏。只清理一次。 |
| 退出范围预览 | 执行前查看哪些进程将关闭、哪些受到保护及其原因。 |
| 应用恢复 | 记录本次关闭的应用，游戏结束后手动一键重新打开；已在运行的应用跳过，失败项可重试。 |
| 按游戏设置 Steam 策略 | 默认保留客户端后台；可为经实测兼容的游戏开启启动后尝试退出 Steam。 |
| 与原设置页一致的界面 | 侧栏导航、白色设置分组和蓝色主按钮；程序路径、高级配置和日志按需展开。 |

原有的 CPU / GPU / 内存 / 磁盘 / 网络等监控、桌面和任务栏显示、主题及插件功能继续沿用 LiteMonitor。原项目的 JSON 插件指南见 [插件开发指南](./resources/plugins/PLUGIN_DEV_GUIDE.md)。**Game Ready 是主程序扩展，不是 JSON 插件。**

## 快速开始

### 运行环境

- Windows 10 / 11，x64。
- 运行本文发布命令生成的便携版，需要 **.NET 8 Windows Desktop Runtime**。
- 程序沿用上游的管理员运行清单及硬件监控初始化流程。

将构建产物放到有写入权限的目录，运行 `LiteMonitor.exe`。保留完整目录中的 `resources` 和辅助库，不要只复制主 EXE。

### 第一次使用

1. 在主界面或托盘右键菜单选择 **Game Ready · 游戏准备**，也可以从设置页进入。
2. 等待自动扫描，选择想玩的游戏。新安装游戏后可点击 **重新扫描**；未识别时可把桌面游戏图标、快捷方式或 EXE 拖到左侧列表，也可通过 **手动添加** 补充。路径框支持直接拖入 EXE，修改后点击 **保存配置**。
3. 打开 **保留名单**，点击 **从运行中应用添加…**，勾选需要保留的浏览器、聊天软件、语音工具等。普通后台进程默认隐藏，可搜索或开启 **显示其他后台进程**。
4. 点击 **预览关闭范围**，确认需要的应用受到保护。在游戏准备页确认退出策略，保存配置。
5. 点击 **清理并启动游戏**。清理完成后，新启动或自行重启的应用不会被继续关闭。
6. 游戏结束后点击 **恢复应用**，或从右键菜单选择 **Game Ready · 恢复应用**。

日常流程：**选游戏 → 预览范围 → 清理并启动 → 结束后恢复应用**。

## 清理和恢复如何工作

### 保留名单是“留下谁”

名单内的应用保留；名单外的普通用户应用，包括已打开的窗口，可能被关闭。名单按 EXE 完整路径匹配，不区分大小写；能够确认关系的现有子进程也会受到保护。没有父子关系的独立辅助程序需要单独加入。

Windows 关键组件、系统及驱动服务、桌面 Shell、其他用户或登录会话、LiteMonitor 自身和所选游戏等由保护规则排除。身份或权限无法确认的进程会被跳过，原因可在预览中查看。

### 正常退出优先，但仍有强制阶段

默认请求正常退出后等待 **10 秒**，超时的候选再尝试强制结束。没有正常退出接口的后台进程也会经过等待阶段。

**保存对话框不会自动获得豁免，强制结束仍可能丢失未保存内容。** 请先保存工作，并将游戏期间需要的程序加入保留名单。取消操作只停止后续步骤，已经退出的应用需要通过恢复功能重新打开。

### 恢复是重新启动应用

恢复不承诺还原文档、标签页、原窗口或登录状态，这些取决于应用自身。有窗口的普通主程序会自动记录恢复入口；无窗口应用可以在 **后台应用恢复** 中补充可直接启动的主程序路径。

辅助进程或无法可靠识别启动入口的应用会标记为需手动打开。不要将 renderer、更新器或需要特定命令行参数的宿主填为恢复入口。

恢复记录先保存再执行退出。取消、游戏启动失败或程序重启后仍可读取；存在待恢复项时，不会静默覆盖旧会话。可在 **清理与恢复** 中选择 **放弃本次恢复**，旧记录会保留为备份。

## Steam、截图和游戏兼容性

| 项目 | 当前行为 |
| --- | --- |
| Steam 默认策略 | 清理期间保留；游戏启动后尝试关闭主窗口，保留客户端后台。 |
| 游戏启动后退出 Steam | 按游戏单独开启，默认延迟 30 秒再请求正常退出；客户端拒绝退出时保留，不升级为强杀。Steam 组件被显式加入保留名单时，保留名单优先。 |
| 退出 Steam 的兼容性 | 游戏进程出现不代表它已经不依赖 Steam，需要逐款实测；客户端功能和云存档同步也需自行确认。 |
| NVIDIA Alt+F1 | 默认启用保留 NVIDIA 覆盖层的预设；实际截图是否正常，仍需在自己的 NVIDIA App 版本上验证。 |
| 《影之刃零》 | 已在开发环境中通过只读扫描识别安装目录及主程序；退出 Steam 后的运行兼容性尚未验证，默认不开启退出选项。 |

## 小巧与低占用

Game Ready 使用项目已有的 .NET / Windows Forms 和 Windows 原生接口，**没有新增 NuGet 包依赖，也没有常驻清理或整盘扫描任务**。

- 游戏库在打开功能窗口或手动刷新时扫描，范围限定在平台记录的安装目录。
- 游戏启动等待有超时，按候选 EXE 查询；运行后通过进程退出事件发送通知，不轮询整个游戏过程。
- 应用图标在打开选择窗口时异步读取，关闭后释放。
- 清理只针对本次快照，不持续拦截新进程，不默认执行全局工作集修剪。

目标是腾出不需要的后台应用所占内存。实际收益取决于当时运行的程序，**不保证消除游戏内存不足或带来固定的帧率提升**。

## 从源码构建

需要 Git 和 **.NET 8 SDK**。以下命令在 Windows PowerShell 中执行：

```powershell
git clone --branch codex/game-ready https://github.com/asuka832/LiteMonitor.git
cd LiteMonitor
dotnet restore LiteMonitor.csproj --source https://api.nuget.org/v3/index.json
dotnet build LiteMonitor.csproj -c Release --no-restore
```

普通构建入口：`bin/Release/net8.0-windows/LiteMonitor.exe`。

生成便携版：

```powershell
dotnet publish LiteMonitor.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o artifacts/GameReady-win-x64
```

产物位于 `artifacts/GameReady-win-x64/`。此方式不捆绑 .NET 运行时；分发时请保留整个目录。

### 验证

```powershell
# 核心回归：使用模拟平台，不批量关闭用户应用
dotnet run --project test/GameReady/GameReady.Tests.csproj -c Release

# 原生检查：只读枚举，并仅结束测试自己创建的专用子进程
dotnet run --project test/GameReady/GameReady.Tests.csproj -c Release -- --native

# 配置窗口渲染检查
dotnet run --project test/GameReady.UI/GameReady.UI.csproj -c Release

# 应用选择窗口：图标、分组、搜索和勾选状态检查
dotnet run --project test/GameReady.UI/GameReady.UI.csproj -c Release -- artifacts/game-ready-picker-preview --picker
```

UI 检查导出的是测试程序的控件渲染图，不是用户桌面的直接截图。游戏实机兼容性、真实应用的保存及恢复行为需要另行验证。

## 配置文件

文件保存在运行程序的目录中，迁移时可一并备份。

| 文件 | 内容 |
| --- | --- |
| `settings.json` | LiteMonitor 原有设置。 |
| `game-ready.json` | 游戏、保留名单、退出策略和后台恢复入口。 |
| `game-ready-session.json` | 当前会话的退出结果与恢复记录。 |
| `game-ready-session.json.discarded-*` | 放弃恢复时保留的旧会话备份。 |

构建目录和个人运行配置已加入 Git 忽略规则。更多说明见 [完整使用文档](./docs/game-ready.md)。

## 当前范围

- 自动扫描支持 Steam / Epic 的已安装 Windows 游戏；暂未覆盖 Xbox、WeGame 或没有平台记录的独立游戏。
- 主程序识别依据安装信息和文件特征，特殊目录结构可能需要在高级配置中修正。
- 托盘分类是系统可读记录或图标名称的匹配结果，不等同于实时托盘进程清单。
- 新增 Game Ready 界面主要使用中文；原有监控界面的多语言功能沿用上游。
- 软件内更新检测仍指向上游原版。更新二开版请使用此分支的构建，直接安装上游更新可能覆盖 Game Ready 功能。

## 上游与致谢

感谢 **Diorser** 开发 [LiteMonitor](https://github.com/Diorser/LiteMonitor)，提供硬件监控、主题、插件和界面基础。本仓库是个人二开版本，Game Ready 的新增功能与问题请和上游原版区分。

- [上游项目](https://github.com/Diorser/LiteMonitor)
- [上游英文介绍](./README.en.md)
- [Game Ready 使用说明](./docs/game-ready.md)
- [首版验证记录](./docs/game-ready-validation.md)
