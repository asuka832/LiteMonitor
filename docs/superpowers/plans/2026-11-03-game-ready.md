# Game Ready 实施计划

目标：实现已批准的 Game Ready 设计，新增零运行时依赖；仅按需扫描和运行。
执行方式：当前会话逐项实施，使用 executing-plans 和 test-driven-development 的验证流程。

## 任务与文件

- [x] `src/GameReady/GameReadyModels.cs`：配置、进程身份、会话与结果数据；测试项目 `test/GameReady` 使用无依赖控制台断言。
- [x] `src/GameReady/GameReadyPolicy.cs`：路径精确匹配、系统保护与候选集合。测试白名单、游戏、服务、PID 复用、跨会话保护。
- [x] `src/GameReady/GameReadyStore.cs`：原子 JSON 存储、崩溃后恢复、禁止覆盖未恢复会话；损坏记录报错而非重置。
- [x] `src/GameReady/GameReadyService.cs`：单次快照、正常退出与强制阶段、游戏启动、Steam 例外、取消与恢复。测试用可控平台代替真实退出操作。
- [x] `src/GameReady/WindowsGameReadyPlatform.cs`：Windows 原生进程与服务枚举、当前用户身份与关键进程验证、单进程退出、受限启动。无 WMI、无新增 NuGet 包。
- [x] `src/UI/GameReadyForm.cs`：按需创建的独立功能窗口，包含游戏配置、保留名单、退出预览、执行、结果与恢复。复用主题颜色。
- [x] `src/UI/MenuManager.cs`、`src/UI/SettingsForm.cs`：菜单入口及设置入口，避免在主刷新循环增加工作。
- [x] `docs/game-ready.md`：使用说明、恢复边界、Steam 兼容性和实机验收步骤。

## 验证命令

使用 .NET 8 SDK，以下命令中的 dotnet 可替换为独立 SDK 的可执行文件路径。

1. `dotnet build LiteMonitor.csproj -c Release`：记录上游构建基线。
2. `dotnet run --project test/GameReady/GameReady.Tests.csproj -c Release`：先观察新增测试失败，再实现；测试不得关闭实际用户应用。
3. `dotnet build LiteMonitor.csproj -c Release`：集成后通过编译。
4. 使用测试平台验证清理、身份变化、取消、启动失败、Steam 拒绝退出、恢复去重、记录损坏；只对专用可丢弃的辅助进程做真实终止测试。
5. 通过只读预览验证 Windows 枚举不会把系统和当前会话外的进程列入候选。
6. 对比构建产物大小及依赖变化；空闲时不创建定时器，游戏退出检测只在本次游戏运行时存在。

## 约束

保留用户指定的只清理一次、白名单之外关闭、正常退出后超时强制、一键启动游戏和恢复应用。Steam 超时不强杀。未实测的游戏不能标记兼容。不得为验收批量结束用户正在使用的应用。
