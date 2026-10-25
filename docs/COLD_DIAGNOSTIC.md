新版默认使用不受六小时期限限制的下一次启动许可，见 [下一次启动修复](NEXT_BOOT_REPAIR.md)。本文以下限时参数描述保留用于旧 schema 2；需显式指定 `-Lifetime Timed`。

# 单次增强冷启动诊断候选

这条路径用于捕获未验收环境的原始增强启动过程。它不签发兼容记录，不修复引擎同步调用可能发生的挂起，也不代表 Windows 26H2 或 Explorer 交互已经验收。实现没有改变原引擎、注入范围、模块组合和启动时序策略。

## 许可与启动顺序

独立脚本 `scripts/Prepare-ColdDiagnostic.ps1` 默认 `Inspect`，只读检查指定候选宿主摘要、受保护安装和当前指纹。只有显式 `Arm` 才调用宿主 `--arm-cold-diagnostic <fingerprint> <minutes>`。该命令必须在同一个受保护安装下找到 LocalSystem、独立进程、路径完全匹配且启动类型为 Disabled 的服务。Stopped / PID 0 可准备；Running 则还必须核对同一路径、PID/创建时间、存活句柄，以及受保护状态中的 session-only / running-current-session-unverified / EngineStarted=1。前后身份变化或 delayed-auto 均拒绝。准备不会停掉当前增强会话。

许可固定为 184 字节二进制，保存在安装根目录 `cold-diagnostic.permit.bin`，使用 CREATE_NEW，不能覆盖已有许可。它包含 schema=2、随机试验 ID、准备时 64 位开机运行毫秒数、零值保留字段、创建及到期 UTC、ShellFingerprint，以及根路径 + 安装记录 + ShellFingerprint 的第二个绑定摘要。ShellFingerprint 覆盖当前 OS build / UBR、外壳文件、宿主、固定组件清单和所有生效引擎/模块 INI；原有 Verify 仍逐个检查实际组件摘要和 ACL。安装记录包含已应用方案/来源。任何更改均使许可失效。

许可落盘并 Flush 成功后，才把服务设成 Automatic；命令不启动或停止服务、不请求重启、不写 EnhancedShellAccepted，也不覆盖 ServiceMain 所有的 service-state.ini。许可、SCM 设置和返回值表示准备结果；当前会话的 PID/StartMode 保持由服务写入。已有消费记录不会被覆盖；上次资料必须先由操作者审阅归档，再准备另一次明确授权的试验。准备失败可能留下不完整许可，但服务不会因此获准加载。

下次无参数 SCM 启动遇到许可时：

1. 先把服务设置为 Disabled，再校验固定资产。无法禁用则拒绝加载。
2. 以独占 DELETE 文件句柄和不替换目标的 FileRenameInfo 把许可改名为 `cold-diagnostic.consumed.bin`，Flush 成功后才能继续。并发请求只有一个能取得同一源文件的独占句柄；旧消费记录阻止覆盖和重放。坏许可也被消费。
3. 验证 schema、两组摘要、安装记录、时间、运行时长重新计数和外壳尚未出现。准备时必须已开机至少 601 秒；启动时 GetTickCount64 必须严格小于准备时记录，并且不超过 600 秒；有效期默认 60 分钟，最多 360 分钟；当前开机已超过 10 分钟则拒绝。同一次开机手动启动会消费并拒绝，不能用它测试热加载。
4. 进入已有冲突检查、错误订阅和引擎路径；加载前及 Start 前再检查是否已有 Explorer / StartMenuExperienceHost。正常运行仍标记 `running-cold-diagnostic-unverified`，兼容阶段记录 Skipped，绝不记录兼容通过。

任何失败都不回落到正常兼容接受入口。`service-state.ini` 的 StartMode 为 `cold-diagnostic`；DiagnosticState / DiagnosticError 保留诊断决策，通用引擎失败另由 State / Error 和有界 startup-trace.log 表示。消费文件保存绑定信息，即使引擎调用不返回，下次自动重试已停用。普通 session-only 和已接受兼容路径保持独立。

## 隔离验证

使用新输出目录运行：

```powershell
./scripts/Test-ColdDiagnostic.ps1 -Toolchain <reviewed-toolchain> -OutputDirectory <new-report-directory>
```

它构建真实宿主及纯策略、原有 trace / arguments / compatibility / session 测试；IO 测试编译实际宿主适配器，验证两个并发消费者、目标已存在时不覆盖、非受保护目录拒绝，并只读核对 GetTickCount64；策略测试包括真实故障对应的 3 小时到 17 秒计数重置、同次启动、睡眠后继续计数、64 位大计数、过期和旧 schema 拒绝。它不调用 SCM 启动或安装、不改真实设置、不启引擎。测试中的现有外壳检测不等于真实启动中无竞态；Explorer 可在最后一次检查之后开始，是否在正确创建阶段注入仍需模块日志和实机证据。

## 后续部署和实际试验

候选必须先经独立审查，再按现有安装约束更新候选包的宿主摘要/组件清单、安装记录和部署 pin；本改动没有改 Install-ShellService.ps1 的既有固定摘要，也没有绕过其限制。不能把新 EXE 覆盖到正在运行的服务，更不能沿用旧安装记录宣称已完成交付。

在经审阅的新候选已经安装且服务 Disabled（Stopped，或已验证的 Running / session-only）后，使用独立脚本 Inspect 获得并复核当前指纹。给 Arm 提供该安装的真实候选宿主 SHA-256、同一指纹及短有效期；随后核对服务保持原来的 Stopped 或同一 Running 身份、启动类型 Auto、许可存在、没有兼容接受记录被写入。这个阶段才安排用户明确同意的重启时间。失去返回值时先检查原操作及许可，不盲目重试。

若在重启前取消，以管理员执行 `sc.exe config ClassicDeskShell start= disabled` 并确认服务启动类型 Disabled、运行状态和身份未意外变化；保留并归档许可、安装记录和状态资料后再试验。不要用 Start-Service 来“试一下”冷启动许可，它会在当前 boot 消费并拒绝。试验后保存消费许可、状态、startup-trace.log / previous、模块阶段日志和同次开机人工结果，检查任务栏、完整 Win10 文件窗口、拖动与顶部点击，再做明确验收。这里没有自动授予接受、自动重启或自动反复尝试的入口。

2026-10-15 实机发现：SystemBootEnvironmentInformation 返回的 GUID 在一次真实重启前后未变，旧 schema=1 因此错误拒绝加载。该 GUID 已退出授权和验收协议；旧许可不能升级复用。新协议使用受保护许可中的 GetTickCount64 准备值与启动值证明计数重置，不依赖墙上时钟来判断重启，也不把 64 位值截断成 32 位。601 秒准备下限比整个 600 秒启动窗口更长，避免同一开机中的启动请求被接受。UTC 仍独立用于许可到期，时钟异常可保守拒绝而不会创造重启证据。睡眠/休眠恢复若未重置内核计数，按同一次内核会话拒绝，应使用实际“重启”验证。

参见 [微软 GetTickCount64 文档](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-gettickcount64)。计数重新开始只允许进行一次诊断，并不自动签发兼容接受；最终必须绑定当前活着的服务 PID、创建时间、消费许可和实机交互证据。SCM 禁用和文件 Flush 降低重复启动风险，但不声明任意断电、文件系统损坏或管理员修改下的事务保证。
2026-10-17 源码增加 runningSessionArmSupported 能力：新的宿主可在同一已核对的热测试会话运行时准备下一次冷启动，避免为了先停服务额外重启一次。旧宿主不支持时脚本明确拒绝，先安排候选安装；不能靠重启来绕过版本检查。这项新能力已作隔离检查，真实 Running/SCM 与后续冷启动仍需验证。
