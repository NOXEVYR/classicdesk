# 发布前置检查（只读）

`scripts/Test-ReleaseReadiness.ps1` 检查声明的发布版本、干净源码提交、当前运行资产清单中的本地原生源码/构建入口，以及由 `scripts/New-FrontendUpdateManifest.ps1` 生成的四个前端文件和 `frontend-update.json`。检查器不构建、不下载、不解压、不签名、不发布、不安装，也不修改系统或产品设置。

先在已提交的候选源码上生成前端发布目录，再运行：

```powershell
pwsh -NoProfile -File ./scripts/Test-ReleaseReadiness.ps1 `
  -SourceRoot "$PWD" `
  -ReleaseVersion 0.11.21-preview `
  -SourceCommit (git rev-parse HEAD) `
  -PackageDirectory ./artifacts/release/frontend `
  -Profile frontend-only
```

`PackageDirectory` 必须是清洁的新目录，且只包含生成器输出的 `frontend-update.json` 与四个 `frontend-ClassicDesk.*` 文件。检查器逐项核对 manifest 的版本、通道、平台、架构、Build 提交、四个文件名、字节数和 SHA-256。版本必须符合现有生成器格式，且不能已占用 `releases/<版本>` 目录。源码必须在仓库顶层、HEAD 必须等于 `SourceCommit`，工作树不能有已修改或未跟踪的文件；检查器还会只读检查 Git 索引，拒绝 `assume-unchanged` 与 `skip-worktree` 标记，且不会自行清除这些标记。清单声明的本地原生模块源码和构建脚本必须存在、被 Git 跟踪且不匹配忽略规则；源码摘要必须等于清单中的 `sourceSha256`。若模块额外声明 readiness/policy 源码摘要，摘要必须能唯一匹配一个已跟踪的原生源码文件。构建脚本摘要会随提交固定并在报告中显示；运行清单没有独立的构建脚本摘要字段，因此检查器不会虚构一个摘要约束。

`frontend-only` 明确表示只核验这四个前端资产，不表示增强运行时已包含。若候选包声称包含完整的固定运行时资产，使用 `-Profile full-runtime -RuntimeBundleDirectory <目录>`；profile 的比较不区分大小写，所以 `FULL-RUNTIME` 等合法写法也会触发同一运行时证据要求。该目录根部需有与 `runtime/windows-x64-assets.json` 字节完全一致的 `runtime-assets-manifest.json`，且须包含清单中的全部资产并匹配其大小和摘要。该目录只允许清单文件与这些运行资产，不得夹带本机设置或其他未清单文件。缺少目录、任何资产或出现额外文件时检查失败。此项只证明运行时文件与固定清单相符，不证明原生宿主构建、许可/再分发授权、签名、安装、真实 Windows 行为或开机验收。

脚本会在标准输出写 JSON 报告。所有检查通过时退出码为 `0`；任何失败时仍输出报告并以非零状态结束。可用以下隔离测试验证成功和失败路径：

```powershell
pwsh -NoProfile -File ./tests/ReleaseReadiness/Test-ReleaseReadiness.ps1
```

测试只在系统临时目录创建合成 Git 仓库和非可执行占位文件，并在结束时移除其自行创建的临时目录。它覆盖有效前端候选、不同大小写的 `full-runtime` 有效/无证据写法、有效运行资产包、源码工作树变脏、Git 隐藏的 assume-unchanged/skip-worktree 修改、缺少或匹配忽略规则的本地构建输入、文件长度不变但摘要过期、未清单运行文件，以及声明提交与 HEAD 不符。
