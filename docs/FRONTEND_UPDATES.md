# 前端软件更新（0.11.20-preview）

0.11.20-preview 提供前端更新流程及独立Release清单。旧公开0.11.16-preview没有本更新器，需要手动下载新版才有软件内更新入口。检查、暂存和安装是不同阶段；真实在线升级及WPF重启尚未实机验收，隔离进程交接不等于用户安装已升级。

## 使用流程

标题栏“软件更新”保留手动检查、下载和“安装并重新打开”。主窗口就绪 30 秒后开始后台检查；检查周期为 24 小时，失败按 1–24 小时退避，状态跨重启保留，手动操作有 1 分钟短时节流。可关闭自动检查，不安装额外常驻服务。关窗取消未完成网络操作。

只读取官方 NOXEVYR/classicdesk GitHub Releases，验证 ClassicDesk、Windows x64、preview/stable 通道、严格递增版本、40 位构建身份及清单绑定。缺少新格式清单时提示无法自动更新，不能声称已更新。相同版本换包和降级不受支持，程序修补必须递增版本。

暂存按四个文件的哈希判断差异，复用未变文件；每个变化文件对应独立 Release 资产。差异不超过 50 MiB 可以后台暂存，超过需明确确认。此版本不实现 ZIP Range 提取或断点续传，不会在 Range 不可用时偷偷退回完整安装包。请求有大小上限、超时、取消和受限 HTTPS 重定向；四文件和原始清单均校验后才成为可安装状态。部分下载不可安装，也不会覆盖原文件。

安装始终由用户选择。未保存草稿和正在进行的外观操作阻止退出；请先保存或撤销草稿、结束对应面板。检查、下载、已暂存、等待退出、安装及重启确认是不同阶段。只有新前端加载主窗口、核对身份与文件并完成握手后，安装事务才记录 Completed。

## 安装范围与恢复

只替换登记过的 `ClassicDesk.exe`、`ClassicDesk.dll`、`ClassicDesk.deps.json`、`ClassicDesk.runtimeconfig.json` 和登记清单自身。外部 Windhawk 配置、增强引擎、开机服务、任务栏自动隐藏记录、方案库、凭据、运行库和未登记文件不在更新范围内。前端关闭不等于增强引擎退出，更新器不会启停增强。

安装器复制当前前端到独立 helper 目录运行；只等待当前前端的 PID、创建时间和可执行路径所确定的实例退出，超时拒绝安装，不按进程名强杀。每个程序文件先备份，再持久记录写入意图，写后回读。失败按文件摘要归属回退；外部变更、不确定的进程启动或损坏记录保留备份，阻止新事务。协作锁与逐次校验不是操作系统原子 CAS。

事务在 `%LOCALAPPDATA%/ClassicDesk/FrontendUpdates/<安装路径标识>/transactions/<事务标识>/`。保留 `request.json`、`journal.json`、原始备份、helper 和启动握手。`Prepared`/`Applying`/`AwaitingAcknowledgement` 不等于完成；`RecoveryRequired`/`Unknown` 需要核对。不要通过删除日志强行解除保护。

需要人工恢复时，先正常退出该前端（保留草稿），再用该事务原有 helper 执行：

```powershell
dotnet "<事务目录>/helper/ClassicDesk.dll" --recover-frontend-update "<事务目录>/request.json"
```

恢复只处理本事务确认拥有的程序文件。无法确认新进程已退出、PID 被复用、备份变动或外部修改时，命令会拒绝覆盖，必须人工核对。不会回退用户数据。

## 发布契约

在全新 publish 输出上运行：

```powershell
dotnet publish src/ClassicDesk/ClassicDesk.csproj -c Release --no-self-contained -o artifacts/app
powershell -NoProfile -File scripts/New-FrontendUpdateManifest.ps1 -AppDirectory artifacts/app -ReleaseDirectory artifacts/update-assets -Version 0.11.20-preview -Build <对应源码提交的40位SHA> -Channel preview
```

脚本校验 DLL 版本、x64 apphost 和四个文件，生成 app 内的 `frontend-installation.json` 与相同字节的 `frontend-update.json`，以及 `frontend-ClassicDesk.*` 四个独立资产。不替换已有登记、不上传或安装。清单的 Build 必须与 Release 的锁定 `target_commitish` 相同，标签/版本/通道保持一致；每个 GitHub 资产必须有 SHA-256 digest。官方发布前还需确认标签确实指向该源码提交。

ZIP 保留完整 app 目录以及登记清单；Release 同时附加上述五个更新资产。不能只上传 ZIP，不能对已发行版本原地换包。开发中的本地包可用明确标记的源码树指纹作为隔离构建身份，这不是公开源码提交。

## 验证范围

`tests/UpdatesFeed` 验证固定来源、清单/版本/通道/构建/大小/摘要、受限重定向、差异下载、大文件门、取消与跨重启调度。

`tests/UpdatesInstall` 使用隔离文件和模拟宿主覆盖回退与未知结果；另有真实的合成父进程、私有 helper 和新进程交接，不显示正式窗口、不操作真实任务栏。`tests/UpdatesUI` 检查草稿保护、取消、安装选择、标题栏命中属性和离屏布局。

已有记录未完成正在使用的真实前端在线升级或WPF更新重启。公开Release元数据/附件核对、生产HTTP检查、模拟HTTP与隔离进程交接分别报告，不宣称本版已完成用户实机自动升级验收。相同版本不会自动替换，因此原本地0.11.20候选不会因同号公开版本被更新器换包。
