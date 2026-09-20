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

仅操作主屏任务栏。前台窗口最大化或可见边框覆盖工作区时读取屏幕底部属于该窗口的九个像素，按亮度中位数选择浅色或深色；离开最大化窗口后背景透明。样本不足时尝试窗口声明的深色属性，均不可用时保持已知状态。桌面图标与应用位图不重绘，任务栏 XAML 主题用于文字对比度。

窗口事件触发 180 ms 单次延迟，并在 750 ms 做一次绘制完成复核；空闲不循环采样、不连续截屏。像素、窗口标题和页面内容不保存、不记录日志。保留一个等待消息的工作线程；这仍有少量内存和事件处理开销，不能称为零占用。没有窗口事件的页面动画不会持续跟踪；未承诺多显示器、所有 UWP 或游戏全屏支持。

退出时移除事件钩子、等待已投递的主题操作完成、恢复本模块仍持有的主题，删除诊断属性。原静态透明组件留作回退，二者不会同时启用。
