# 0.11.16-preview 任务栏功能增量

此预览版承接上一轮 UI 优化。代码实现和隔离检查不等于本机真实效果验收。

## 三种布局

1. 开始靠左、应用居中：原有行为。
2. 开始与应用居中：原有行为。
3. 开始与应用全靠左：新增，`LeftAlignedApps=true`。

新字段默认 false 并省略序列化，未选择此功能的旧方案和事务摘要保持兼容。新字段开启的方案应使用本版读取，旧版不支持此新能力。

全靠左使用现有有归属的对齐事务写入 TaskbarAl=0，而不是把“关闭布局增强”当成全靠左。开始定位模块关闭，尺寸、背景、托盘模块仍按各自选择启用；与开始定位模块相关的系统按钮/开始菜单/搜索定位选项禁用但保留草稿值。回退恢复原0/1/未设置状态，遇到外部改变不覆盖。原生对比示意不受草稿污染。

通过增强应用入口使用时仍需要已核对的运行组件和无冲突外壳。仅选择原生对齐且没有任何增强模块时沿用现有边界，不启动空引擎，应通过 Windows 设置调整。开机方案更新只修改组件规则，要求系统当前对齐已匹配目标；它不会偷偷改写系统对齐。只读检查或停用现有服务不会被不匹配的新草稿对齐阻止。

## 原生自动隐藏

入口：任务栏 → Windows 任务栏行为 → 自动隐藏任务栏 → 检查与调整。

独立于方案 JSON 和增强引擎。面板构造不检查系统；显示后只读检查，勾选仅修改本次计划，点击应用/恢复才写。使用官方 SHAppBarMessage：GETSTATE 的0是有效值，SETSTATE 返回成功不能证明完成；修改只涉及自动隐藏位，保留其他标志，提交后回读。

日志位于 LocalAppData/ClassicDesk/TaskbarAutoHide。每次应用和恢复均先落盘intent，确认后再进入已确认状态；不确定结果、损坏/缺失记录、会话或外部状态变化均阻止覆盖和自动重试。日志保留原状态、当前用户与Explorer会话身份、修订摘要；不是操作系统原子CAS，也不能检测设置被他人改走又改回的全过程。

当前桌面中检测到 StartAllBack/StartIsBack 仍加载或无法完整读取宿主时拒绝操作。不会停用第三方工具、重启Explorer或修改注册表。该功能没有后台监控、不管理逐屏独立自动隐藏；Explorer重建后不沿用旧恢复确认，保留记录待核对。

官方依据：[GETSTATE](https://learn.microsoft.com/en-us/windows/win32/shell/abm-getstate)、[SETSTATE](https://learn.microsoft.com/en-us/windows/win32/shell/abm-setstate)。真实鼠标触边唤出、全屏和睡眠唤醒尚待验收。

## 合并与文字标签

“打开 Windows 设置”使用固定的 `ms-settings:taskbar` URI，仅由点击触发。这是系统设置入口，不是 ClassicDesk 已接管合并策略；不新增未验证的注册表配置项，也不把组织策略接口用于个人偏好。

官方依据：[启动 Windows 设置](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app)。

## 验证

现有 Core / Host / Frontend / ReleaseBoundary / ServiceUI 回归，加独立 AutoHide 和 ColdUpgrade 检查。所有宿主写入均为fake；方案往返和日志使用隔离临时文件。`scripts/Test.ps1` 已纳入新检查。

本次发布没有更换本机桌面入口、安装服务、启用增强或修改真实系统设置。详细通过数见 VALIDATION.md；构建与包摘要随 Release 提供。
