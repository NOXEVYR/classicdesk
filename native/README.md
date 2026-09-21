# ClassicDesk 自适应任务栏背景

本地 GPL-3.0-or-later 模块，在现有背景模块位置运行；并非上游发布的预编译版本。

- 背景实现参考 m417z 的 Taskbar Background Helper 1.2。
- XAML 根节点发现参考 r1file 的 Dynamic Taskbar Transparency 0.3.9。
- 上游固定提交：[f3ec3675](https://github.com/ramensoftware/windhawk-mods/tree/f3ec3675168600d1decfcda97d2a12e375903ae9/mods)。许可证见 [COPYING](COPYING)。
- 源码、策略头和产物哈希记录于 [运行时清单](../runtime/windows-x64-assets.json)。不使用上游 DLL 地址冒充本地产物。

## 构建

使用 Windhawk 1.7.3 安装包中的 Compiler SDK、`Engine/$R1/64/windhawk.lib`，以及官方依赖：

| 依赖 | SHA-256 |
| --- | --- |
| [编译器 v1.6](https://github.com/ramensoftware/windhawk-dependencies/releases/download/v1.6/windhawk-compiler-1.6.7z) | `16352e7dfa65d092af7b7113ddf6cae695e3678da500e611828c1bf2a7d9a2d0` |
| [WinRT / WinUI 头文件](https://github.com/ramensoftware/windhawk-dependencies/releases/download/v1.6/winrt_winui2_2.8.7_winui3_1.7.250401001.7z) | `43870ab78640c7c62d1825a3d39532f3bf78a4fe3895173a1615f521c82f55b3` |

将 WinRT 头文件放入编译器 `include/winrt`。运行 `Build.ps1 -Toolchain <编译器目录> -Sdk <Compiler目录> -EngineLibrary <windhawk.lib> -Output <DLL路径>`。编译器和 SDK 不随应用分发。策略测试：使用同一 clang++ 编译并执行 `appearance-policy-tests.cpp`。

## 行为和边界

仅操作主屏任务栏。0.11.11 起只有本屏最大化或全屏铺满的应用使任务栏不透明，普通窗口和桌面保持透明。需要不透明时，从窗口自身可见区域底部读取属于该窗口的九个像素，按亮度中位数选择浅色或深色。样本不足时尝试窗口声明的深色属性，均不可用时保留上次明暗色。焦点落在任务栏或系统浮层时检查本屏最上层可见应用，无法判断则保留状态。桌面图标与应用位图不重绘，任务栏 XAML 主题用于文字对比度；全屏应用隐藏任务栏时不强制显示。

配置沿用 `FollowMaximizedTheme` / `followMaximizedWindow` 字段，以保持旧方案和恢复记录兼容；0.11.9 曾采用“所有应用不透明”，0.11.11 已按用户澄清改为“仅最大化或全屏时不透明”。旧包仍按其原实现执行，修改源代码不会自动改变正在运行的包。

0.11.10 补充最小化/显示桌面的过渡：接收已失去焦点的窗口最小化与顶层显示/隐藏事件。焦点暂留系统浮层或隐藏窗口时，只在工作区可见应用枚举明确为空的情况下变透明；过滤工具浮窗、最小化、隐藏、cloaked 窗口，枚举异常时保留现状。这是事件后的短暂查询，没有新增空闲扫描。

同版支持任务栏创建前的模块初始化：挂接窗口创建、合成背景和 DLL 加载路径后等待对象出现，不因 Shell_TrayWnd / Taskbar.View 尚未创建而退出；初始化后复查加载竞态。模块自身能提前初始化不代表引擎已经在登录前加载，首屏效果必须另行实测，详见[启动架构](../docs/SHELL_STARTUP.md)。

窗口事件触发 180 ms 单次延迟，并在 750 ms 做一次绘制完成复核；空闲不循环采样、不连续截屏。像素、窗口标题和页面内容不保存、不记录日志。保留一个等待消息的工作线程；这仍有少量内存和事件处理开销，不能称为零占用。没有窗口事件的页面动画不会持续跟踪；未承诺多显示器、所有 UWP 或游戏全屏支持。

退出时移除事件钩子、等待已投递的主题操作完成、恢复本模块仍持有的主题，删除诊断属性。原静态透明组件留作回退，二者不会同时启用。

## 原生开机服务宿主

`shell-service.cpp` 为固定 Windhawk 1.7.3 引擎 API 的 SCM 宿主，使用同一 v1.6 工具链构建为 i686 程序；该版本全局引擎启动接口只在 32 位实现。`Build-Service.ps1 -Toolchain <编译器目录> -OutputDirectory <输出目录>` 读取已固定的运行资产清单并生成内嵌摘要表，禁用 PE 时间戳以支持可重复构建。来源和产物摘要见 [service-host-build.json](service-host-build.json)。

离线生命周期/崩溃阈值断言在 `service-session-tests.cpp`，不加载引擎。`--inspect` 校验固定资产和启动策略，`--inspect-protected` 额外核对安装目录及权限；二者不注册服务或注入进程。安装、冲突保护、恢复与未完成的开机验收见 [SHELL_STARTUP.md](../docs/SHELL_STARTUP.md)。
