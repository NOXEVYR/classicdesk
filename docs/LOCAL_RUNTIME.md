# 本地增强试用准备

本地 0.11.1-dev 已支持[按需选择四类功能](FEATURE_SELECTION.md)。首次检查仍只选择任务栏；资源管理器与右键菜单可单独勾选，应用范围不会改写用户保存的完整方案。当前尚未完成真实桌面效果与性能验收。

## 固定组件与离线准备

[资产清单](../runtime/windows-x64-assets.json)记录官方 Windhawk 1.7.3 安装文件、四个固定版本模块的来源、字节数、SHA-256，以及提取后的 17 个运行文件。运行文件合计 11,059,674 字节，不包含完整编辑器与编译器。体积不代表常驻内存。

该清单的 SHA-256 为 `87EBC2871F686E9E26DFF7CCDCC65CD181AC98DB80EAEC442B8D82720C3E139E`，同时固定在前端代码中。二进制来自官方预编译下载；记录源码修订不等于已完成独立重编译一致性验证。第三方二进制不提交到源码仓库，也不加入当前公开前端包。

下载清单中的官方安装器和模块，校验其摘要。使用已安装的 7-Zip 解压安装器，**不执行安装器**。保留解压后的 `Engine/$R1` 目录结构；不要用 PowerShell 双引号展开 `$R1`。将四个模块按清单中的 `sourcePath` 命名后存入同一目录。准备脚本不会下载文件、安装服务或启动引擎。

```powershell
dotnet publish ./src/ClassicDesk/ClassicDesk.csproj -c Release -o ./artifacts/local-test/app -p:Version=0.11.0-dev
./scripts/Prepare-Runtime.ps1 `
  -ApplicationDll ./artifacts/local-test/app/ClassicDesk.dll `
  -Installer ./artifacts/downloads/windhawk_setup.exe `
  -ExtractedInstaller ./artifacts/downloads/extracted `
  -ModuleDirectory ./artifacts/downloads/modules `
  -Destination "$PWD/artifacts/local-test/原生组件-未启用"
```

父目录必须已存在，目标目录必须不存在。脚本逐项验证后生成停用配置，将所需运行库从 `.dll` 映射为模块实际导入的 `.whl` 文件名，最后通过产品内置摘要检查。失败时保留新建的局部目录，不覆盖重试，也不清理已有资料。生成的所有权标记、配置和以后产生的恢复记录仅留本机。

## 检查与试用

只读命令通过 `dotnet` 调用，便于接收 JSON 输出；不会打开设置窗口、创建恢复记录或启用增强：

```powershell
dotnet ./artifacts/local-test/app/ClassicDesk.dll --check-runtime "$PWD/artifacts/local-test/原生组件-未启用"
dotnet ./artifacts/local-test/app/ClassicDesk.dll --inspect-system
```

准备后四个模块均为 `Disabled=1`，主程序和引擎均为 `SafeMode=1`；更新检查、托盘和工具窗口保持停用。要实际试用，打开此构建的设置窗口，进入“系统应用检查”，选择功能并重新检查，核对提示后点击“试用所选功能”。没有无人值守启用命令，也不会添加开机启动。

若检测到 StartAllBack 已加载或其他 Windhawk 实例，启用被阻止。应先保存工作，再通过现有工具停用并确认其卸载出当前会话；必要的注销或 Explorer 重启属于单独的实机切换步骤，准备脚本不执行这些动作。恢复使用原运行目录和本机事务记录，不能删除目录后指望恢复仍可执行。

现场观察中，StartAllBack 3.9.25 在 Windows 11 25H2 上勾选“为当前用户禁用该程序”后，单独重启 Explorer 仍有完整组件加载；之后用户重启电脑，再次检查仍检测到加载和经典任务栏。停用勾选或重启不能当作组件已退出的证据。遇到这种情况继续保持冲突阻断，不自动注销、反复要求重启或仅凭注册表停用值忽略已加载模块。

首轮需核对开始靠左、应用居中、尺寸变化、开始与搜索窗口、多显示器/DPI、恢复，以及[整体资源对照](PERFORMANCE.md)。配置和进程回读成功不等于视觉效果验证通过。上游便携引擎会驻留并定时检查新进程；关闭前端后不能声称整个方案零轮询或零占用。
