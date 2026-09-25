# ClassicDesk · Windows 桌面布局与外观工具

**0.11.16-preview** 改进设置界面，新增全靠左布局、独立原生自动隐藏，以及 Windows 任务栏合并/文字标签设置入口。详见 [功能说明](docs/LOCAL_TASKBAR_FEATURES.md) 与 [验证记录](VALIDATION.md)。

[下载 Windows x64 预览版](https://github.com/NOXEVYR/classicdesk/releases/tag/v0.11.16-preview) · [历史版本](https://github.com/NOXEVYR/classicdesk/releases) · [功能对照](docs/STARTALLBACK_PARITY.md)

## 本版新增

- **界面与编辑体验**：统一控件、焦点及禁用样式，改善小窗口布局、预览对比和草稿修改摘要，修复重置与相关选项同步。
- **三种任务栏布局**：开始靠左/应用居中、全部居中、全部靠左。全靠左停用开始定位模块，尺寸、背景和托盘仍按各自选项启用。
- **原生自动隐藏**：独立检查、应用与恢复，使用 Windows API，不依赖增强引擎；先保存操作意图再写入，回读确认，保留原状态及冲突保护。
- **系统设置入口**：点击后打开 Windows 任务栏设置，合并和文字标签由 Windows 管理。
- **保留现有能力**：四类布局预设、六款皮肤、本地方案库、导入导出、快捷键、按页重置及开机方案管理。

**这是预览版，尚不能完整替代 StartAllBack。** 公开包不含第三方增强运行组件，任务栏增强、资源管理器和右键菜单的实际启用仍需匹配已审查的独立组件包。原生自动隐藏为独立功能，但 StartAllBack/StartIsBack 正在加载或宿主检查失败时会拒绝写入。

保存草稿不等于系统已应用。自动隐藏只有显式点击应用/恢复才修改状态；不安装服务、不自动停用其他软件、不重启 Explorer。Explorer 会话变化、未知结果或外部修改会阻止恢复。真实触边、多屏、DPI、睡眠唤醒和长期稳定性仍待验收。

历史版本的实机记录保存在验证文档中，不代表本版在每台电脑已验收。Windows x64 前端需要 .NET 8 Desktop Runtime x64。
## 界面预览

以下为应用离屏界面，展示方案编辑和原创参数图示，未应用到 Windows。

![任务栏界面预览](docs/screenshots/taskbar.png)

![风格预设画廊](docs/screenshots/presets.png)

![独立皮肤资料库](docs/screenshots/skins.png)

![星空二次元方案](docs/screenshots/starlight.png)

插画素材及生成提示词见 [星空素材说明](docs/ARTWORK.md) 与 [新增皮肤素材说明](docs/SKIN-ARTWORK.md)。

<details>
<summary>查看资源管理器与右键菜单页面</summary>

![资源管理器界面预览](docs/screenshots/explorer.png)

![右键菜单界面预览](docs/screenshots/context-menu.png)

</details>

## 下载

[Windows x64 · 0.11.16-preview ZIP](https://github.com/NOXEVYR/classicdesk/releases/download/v0.11.16-preview/ClassicDesk-0.11.16-preview-windows-x64.zip) · [SHA-256 校验](https://github.com/NOXEVYR/classicdesk/releases/download/v0.11.16-preview/SHA256SUMS.txt)

## 运行

1. 使用 Windows 11 x64。
2. 安装 [Microsoft .NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)。发布包复用系统运行时，不包含 SDK 或 .NET 运行时。
3. 解压下载的前端 ZIP，运行 `app/ClassicDesk.exe`。保留整个 app 目录，包括 DLL、JSON、开机管理脚本和恢复入口。

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

`scripts/Test.ps1` 运行 Core、Host、Frontend、ReleaseBoundary、ServiceUI、AutoHide 和 ColdUpgrade 七组检查。另有 `tests/ShellService` 离线服务包测试，需要本机已核对的预备包和全新测试输出目录。测试不显示应用窗口、不调用真实注册表写入、不启动增强引擎。测试生成的本地报告与夹具不应提交到仓库；它们可能含运行机器路径。

## 源码结构

- `src/ClassicDesk/`：默认前端、独立主题、原创图标与宿主适配。
- `tests/`：可移植隔离测试；不依赖开发者原本的组件目录。
- `scripts/Test.ps1`：一键运行基础检查；传入 `-ServiceBundle` 可追加离线服务包和模拟安装检查。
- [THIRD_PARTY.md](THIRD_PARTY.md)：外部增强组件与公开分发边界。
- [V2_ROADMAP.md](V2_ROADMAP.md)：下一版的小步目标与验收条件。

此版本是供审查、反馈与继续开发的初版。它没有把原 StartAllBack 设置界面嵌入应用，也没有包含其程序或授权数据。

## 权利说明

前端及 C# 宿主采用版权保留方式公开源码，见 [LICENSE](LICENSE)；[原生模块源码](native/README.md) 按其 GPL-3.0-or-later 声明提供。第三方权利归原作者；公开前端不改变上游许可。预览程序尚未提供代码签名，可按发布材料的 SHA-256 核对文件。
