# 冷试验验收后恢复自动启动

`scripts/Complete-ColdDiagnostic.ps1` 是独立入口，不改既有安装器或宿主。默认 `Inspect` 只读；仅显式 `Complete` 能写 `shell-compatibility.ini` 并设置 Automatic。它从不停止、启动服务或重启系统。

2026-10-16 增加 `Explorer-StartupPrerequisite.ps1` 只读核对。Win10 方案在系统提供原生窗口预加载选项时，安装记录 OwnerSid 对应的已加载用户 hive 必须已关闭预加载。准备由该用户的 ClassicDesk 显式入口保存原值后完成；提权脚本不改管理员 HKCU，也不从 LocalSystem 写用户设置。准备脚本 Arm 和正式安装器 EnableNextBoot 使用同一检查。缺失前置条件会在修改自动启动前拒绝，并指出准备命令；验收通过后仍由下述完成事务转为 Automatic，Disabled 不是最终交付状态。

适用状态是本次冷试验已完成、当前宿主仍 Running、启动类型 Disabled。必须是受保护安装、相同 LocalSystem 独立进程、无活动许可、有效消费许可、schema=2 运行时长重置证据成立。消费许可必须匹配当前 ShellFingerprint 及安装记录绑定摘要；服务 PID、创建 FILETIME 和 `running-cold-diagnostic-unverified` 状态必须对应。普通运行和 session-only 均拒绝。实际固定资产由宿主 `--inspect-protected` 校验，当前全部有效 INI 由 ShellFingerprint 绑定；不以缓存写入后变化的旧安装文件全表作为固定资产校验。

先以管理员运行只读检查，提供已审阅候选的宿主 SHA-256：

```powershell
./scripts/Complete-ColdDiagnostic.ps1 -Installation '<protected-installation>' -ExpectedHostSha256 '<candidate-sha256>'
```

返回的 `Identity` 是报告绑定字段，不是通过结果。保存同次开机的启动日志、任务栏、完整 Win10 文件窗口、窗口拖动及顶部点击的实际证据。操作者自行制作 UTF-8 JSON 报告，字段如下；以下故意全部 pending，不能直接完成验收：

```json
{
  "Schema": 1,
  "Decision": "pending",
  "Operator": "",
  "ObservedUtc": "",
  "Installation": "",
  "HostSha256": "",
  "Fingerprint": "",
  "BootEvidence": "monotonic-uptime-reset-v2",
  "PreparedUptimeMs": 0,
  "TrialId": "",
  "ConsumedSha256": "",
  "InstallRecordSha256": "",
  "ServiceProcessId": 0,
  "ServiceCreationFileTimeUtc": "",
  "Checks": {
    "ColdBoot": {"Result":"pending", "Observation":"", "Evidence":[]},
    "Taskbar": {"Result":"pending", "Observation":"", "Evidence":[]},
    "Win10Explorer": {"Result":"pending", "Observation":"", "Evidence":[]},
    "Drag": {"Result":"pending", "Observation":"", "Evidence":[]},
    "TopClick": {"Result":"pending", "Observation":"", "Evidence":[]}
  },
  "Evidence": [{"Path":"relative-local-evidence-file", "Sha256":"lowercase-sha256"}]
}
```

`BootEvidence`、`PreparedUptimeMs`、`TrialId` 使用 Inspect 返回值；TrialId 是 16 字节随机标识的 32 位小写十六进制串。验收绑定服务启动时的受保护运行时长记录，不能用准备阶段的进程或旧开机日志代替。`ObservedUtc` 使用带 `Z` 或 `+00:00` 的 UTC ISO 时间；必须处于当前服务实例存活期间。每项观察需写出实际操作及结果，并引用 Evidence 中的文件。全部实际通过后，操作者才将五个 Result 改为 `pass`，Decision 改为 `accepted`。本脚本核对显式声明及证据身份，不会从画面或日志自动证明操作成功；操作者真实性仍是信任边界。

独立核对 JSON 内容及 SHA-256 后：

```powershell
./scripts/Complete-ColdDiagnostic.ps1 -Action Complete -Installation '<protected-installation>' -ExpectedHostSha256 '<candidate-sha256>' -EvidenceReport '<reviewed-report.json>' -ExpectedReportSha256 '<reviewed-report-sha256>'
```

报告及全部证据在完成期间保持只读锁；记录包含报告 SHA-256、指纹、trial、运行时长证据、PID 和创建时间。临时文件在安装目录中写入并 Flush，保护 ACL 后以不覆盖的同目录原子 rename 发布。原生 `--inspect-compatibility` 必须返回 compatible，之后才改 Automatic 并复查同一实例；当前桌面继续运行。

已有记录只允许与本次预期字节完全相同的重试，其他记录（包括其他有效验收记录）均保留且拒绝覆盖。部分失败保留原记录及临时资料；Automatic 修改失败或后续校验失败时尝试恢复 Disabled。若服务注册或实例已改变、禁用失败，则报“rollback could not be verified”，此时必须先核对 SCM，不能按成功处理或盲目重试。没有 Stop/Start 兜底。写记录后、改 Auto 前中断通常留下 Disabled，可在原实例仍运行且报告身份完全相同时重试；改 Auto 后进程被终止的结果需先 Inspect 服务/记录，不能宣称已回滚。

安装目录必须位于本机 ProgramW6432 的 ClassicDesk/ShellService 下，路径拼写须与消费许可的安装根完全一致。脚本拒绝重解析路径，要求 ClassicDesk 起到记录文件的 ACL 仅管理员/LocalSystem 可写。它不防御同时有管理员权限的其他程序修改，也不承诺任意断电的跨文件/SCM 事务原子性。新协议不再调用内部 boot GUID API；保留受保护许可和当前活着的服务实例绑定。验收花费较长时间不会因当前运行时长超过准备值而误拒绝，判断使用启动时记录。

隔离验证：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Complete-ColdDiagnostic.ps1 -OutputDirectory '<new-isolated-directory>'
```

测试用明确的合成报告运行纯策略、隔离目录中的原子发布及模拟 SCM 事务失败；不运行原生宿主、不改 SCM/注册表、不生成真实安装的接受记录。这不能代替冷启动、拖动和顶部点击实机验收；公开发布仍须等待实际验收结果。
