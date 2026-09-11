# ClassicDesk 0.6.0 公开预览版

[← 返回 portfolio](https://github.com/turnsolesama/portfolio) · [Releases · v0.6.0-preview](https://github.com/turnsolesama/classicdesk/releases/tag/v0.6.0-preview)

ClassicDesk 定位为 Windows 桌面修改工具，面向任务栏、资源管理器与右键菜单的外观和布局调整。当前公开预览版提供三页修改方案编辑与原创参数图示，采用 C# / WPF 实现。

**这个下载包可用于预览和保存方案，没有捆绑 Windhawk 等第三方增强运行组件，因此当前公开包不能直接启用真实桌面改造。** 原生宿主与恢复代码已纳入源码，但尚未完成 Windows 实机效果验收。图片是示意图，不是系统效果截图。

## 界面预览

以下为应用离屏界面，展示方案编辑和原创参数图示，未应用到 Windows。

![任务栏界面预览](docs/screenshots/taskbar.png)

<details>
<summary>查看资源管理器与右键菜单页面</summary>

![资源管理器界面预览](docs/screenshots/explorer.png)

![右键菜单界面预览](docs/screenshots/context-menu.png)

</details>

## 运行

1. 使用 Windows 11 x64。
2. 安装 [Microsoft .NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)。发布包复用系统运行时，不包含 SDK 或 .NET 运行时。
3. 解压下载的前端 ZIP，运行 `app/ClassicDesk.exe`。保留旁边的 DLL、deps.json 和 runtimeconfig.json。

设置窗口关闭后前端退出，没有自有 Dock、托盘驻留或自动启用任务。未来真正启用增强时，独立增强引擎仍需运行；前端退出不等于增强引擎退出。

## 当前功能

| 页面 | 已实现的方案参数 |
| --- | --- |
| 任务栏 | 开始按钮布局、图标大小、任务栏高度、普通/小图标按钮宽度、小图标尺寸、其他系统按钮位置、开始与搜索菜单位置 |
| 资源管理器 | Windows 11 原样、经典 Ribbon、早期 Windows 11 导航栏三种互斥方案 |
| 右键菜单 | 经典菜单方案与 Ctrl 切换策略 |

- 草稿跨页面保留；保存、撤销、待保存状态及失败反馈可用。
- 方案默认保存在 `%LOCALAPPDATA%/ClassicDesk/ShellProfile.json`。保存前核对文件修订，外部修改会拒绝覆盖；前一份方案保存在 `.previous`。损坏配置保留原文件。
- 参数与图示联动；提供只读环境/方案检查。缺少组件时明确提示，禁止假报“已应用”。
- 可在新目录生成完整停用配置：主/引擎 SafeMode=1，四个模块 Disabled=1。这不会下载、安装或启动引擎。

```powershell
& .\app\ClassicDesk.exe --prepare-disabled-config C:\Temp\ClassicDesk-disabled
# 可在末尾附加一个已存在的方案 JSON 文件路径。
```

目标目录必须全新且不存在。没有无交互启用参数。

## 真实启用与恢复的边界

源码包含固定 Windhawk 1.7.3 协议的包校验、七目标事务、原始字节备份、写入归属、进程身份检查与恢复实现。真正启用需要匹配已审查清单的独立增强包，并由用户明确点击。此公开版本暂不提供该增强包，不能使用任意 Windhawk 安装目录替代。

已有 StartAllBack 正在加载、其他增强实例、读取不完整、包/配置修订变化或未完成记录都会阻止冲突操作。不会替用户停用 StartAllBack、绕过激活、重启 Explorer、强停其他进程或安装自启动。安装存在本身不代表冲突，完整检查明确未加载时可以保留原安装。

恢复仅处理本工具确认拥有且没有被外部修改的状态。未知写入/启动/退出结果进入待人工检查；文件锁和修订检查不是操作系统原子 CAS。实际 Ribbon、任务栏与菜单效果、重启后的行为、多显示器和不同 Windows 构建仍待验证。

## 构建与测试

构建需要 Windows 和 [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)。没有第三方 NuGet 依赖。

```powershell
dotnet build src/ClassicDesk/ClassicDesk.csproj -c Release
dotnet publish src/ClassicDesk/ClassicDesk.csproj -c Release --no-self-contained -o artifacts/app
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test.ps1
```

测试由四个独立项目组成：事务核心、真实文件加 fake 系统接口、WPF 离屏交互、缺组件发布边界。测试不显示应用窗口、不调用真实注册表写入、不启动增强引擎。测试生成的本地报告与夹具不应提交到仓库；它们可能含运行机器路径。

## 源码结构

- `src/ClassicDesk/`：默认前端、独立主题、原创图标与宿主适配。
- `tests/`：可移植隔离测试；不依赖开发者原本的组件目录。
- `scripts/Test.ps1`：一键运行四组检查。
- [THIRD_PARTY.md](THIRD_PARTY.md)：外部增强组件与公开分发边界。
- [V2_ROADMAP.md](V2_ROADMAP.md)：下一版的小步目标与验收条件。

此版本是供审查、反馈与继续开发的初版。它没有把原 StartAllBack 设置界面嵌入应用，也没有包含其程序或授权数据。

## 权利说明

初版采用版权保留方式公开源码，尚未选择开源许可证，见 [LICENSE](LICENSE)。第三方权利归原作者；公开前端不改变上游许可。预览程序尚未提供代码签名，可按发布材料的 SHA-256 核对文件。
