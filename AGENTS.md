# 本地 ClassicDesk UI 优化

## 2026-09-24 后续功能批次

- 用户要求继续补功能：本批实现全靠左布局与独立原生自动隐藏，合并/文字标签提供固定的 Windows 任务栏设置入口，不宣称代管。
- `LeftAlignedApps=false` 缺省序列化必须省略，保留历史方案和恢复摘要；开启时对齐事务明确为0，停用开始定位模块，但不关闭尺寸、背景或托盘模块。
- 自动隐藏通过当前交互用户的 SHAppBarMessage API，独立日志先写 intent 再提交，回读确认；SETSTATE 返回值不等于成功。禁止使用 session 0 开机服务修改此用户设置，禁止真实宿主测试。
- 原生自动隐藏面板进入后只读检查，点击应用/恢复才写；StartAllBack仍加载、读取失败、会话变化、日志损坏或结果不确定时拒绝写入。
- 新检查：`dotnet run --project tests/AutoHide/AutoHide.csproj -c Release -- <隔离目录>`；`scripts/Test.ps1` 包含此检查和 ColdUpgrade。

- 本批源码基线：官方 main 提交 3c7a359e7a014e969bf3bd365ca6c42ac2340c5f。提交、发布及安装状态须按实际回读结果报告。
- 中文交付；用户授权本轮多代理 UI 与交互优化。各代理按文件分工，集成人统一构建验证。
- 后台开发与离屏测试；不要自动显示窗口、启用引擎、写真实系统设置、安装服务、重启 Explorer 或替换用户桌面入口。
- 保留 C# WPF/.NET 8、现有六款皮肤、方案文件兼容、恢复与冲突保护；不引入第三方依赖。
- 构建：`dotnet build src/ClassicDesk/ClassicDesk.csproj -c Release`。
- 隔离检查：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test.ps1 -OutputDirectory <绝对报告目录>`。未提供 ServiceBundle 时不会运行需要真实预备包的服务包/安装测试。
- 前端测试：`dotnet run --project tests/Frontend/ShellFrontendChecks.csproj -c Release -- <隔离夹具目录> <报告目录>`；只做离屏 Measure/Arrange/Render，不得改成 Show/EnsureHandle。
- 用户已于 2026-09-26 授权测试通过后同步源码并发布预览版。保留历史包和用户文件；后续提交/发布依当次用户授权执行。
- 草稿保存、开机方案提交、组件加载与桌面实效分开表述。测试通过不能替代实机验收。
