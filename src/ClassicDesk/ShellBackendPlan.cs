using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClassicDesk;

public sealed record BackendBinary(string Name, string Path, string? Version);
public sealed record WindhawkInstallation(string Root, string? Version, string Mode, string? EnginePath,
    string? CompilerPath, string? AppDataPath, bool EnginePresent, bool CompilerPresent, string Status);
public sealed record ShellBackendEnvironment(DateTime ObservedUtc, string Architecture, int? Build, int? Revision,
    string? DisplayVersion, IReadOnlyList<BackendBinary> SystemBinaries, IReadOnlyList<WindhawkInstallation> Windhawk,
    IReadOnlyList<string> WindhawkProcessPaths, IReadOnlyList<string> StartAllBackEvidence,
    string StartAllBackLoaded, IReadOnlyList<string> ReadErrors, int? NativeTaskbarAlignment = null);
public sealed record BackendDownload(string Name, long Bytes, string Url, string? Sha256, string Purpose);
public sealed record ShellModPlan(string Id, string Version, string License, bool Selected, string Action,
    IReadOnlyList<string> Targets, IReadOnlyDictionary<string, string> Settings, string SourceUrl, string Status,
    string PrecompiledUrl);
public sealed record ShellBackendReviewPlan(ShellBackendEnvironment Environment, string ProposedPortableRoot,
    IReadOnlyList<ShellModPlan> Modules, IReadOnlyList<BackendDownload> Downloads, IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Steps, IReadOnlyList<string> PlannedPaths, bool CanApply, bool RealSystemWrites,
    string ProtocolStatus);

/// <summary>
/// Read-only discovery and review planning. No download, process launch, injection,
/// registry write, file write or configuration application is implemented here.
/// Run Describe/Detect on a worker thread; process-module and file queries may take time.
/// </summary>
public static class ShellBackendPlanner
{
    public const string EngineVersion = "1.7.3";
    public const string EngineCommit = "c165997c54e71dad897460232d595db7cf2c06ab";
    public const string ModsCommit = "f3ec3675168600d1decfcda97d2a12e375903ae9";
    const string EngineSource = "https://github.com/ramensoftware/windhawk/blob/" + EngineCommit + "/";
    const string ModsSource = "https://github.com/ramensoftware/windhawk-mods/blob/" + ModsCommit + "/mods/";

    public static IReadOnlyList<BackendDownload> KnownDownloads { get; } = Array.AsReadOnly(new[]
    {
        new BackendDownload("windhawk_setup.exe", 11_570_480,
            "https://github.com/ramensoftware/windhawk/releases/download/v1.7.3/windhawk_setup.exe",
            "5116cc1703384aac88b8d7a3b57f172aea5da32b6df7355fb2f8247d23813c8f", "官方在线安装器；便携文件需提取，不是独立 portable ZIP"),
        new BackendDownload("windhawk-compiler-1.6.7z", 55_975_130,
            "https://github.com/ramensoftware/windhawk-dependencies/releases/download/v1.6/windhawk-compiler-1.6.7z",
            null, "修改源码或没有匹配预编译模块时的开发候选，超过 50 MiB；尚未下载，非终端运行必选"),
        new BackendDownload("winrt_winui2_2.8.7_winui3_1.7.250401001.7z", 4_400_591,
            "https://github.com/ramensoftware/windhawk-dependencies/releases/download/v1.6/winrt_winui2_2.8.7_winui3_1.7.250401001.7z",
            null, "官方 WinRT/WinUI 头文件候选；WH_WINRT_WINUI2 所需头文件必须核验"),
        new BackendDownload("windhawk_setup_offline.exe", 148_917_720,
            "https://github.com/ramensoftware/windhawk/releases/download/v1.7.3/windhawk_setup_offline.exe",
            "4d93016570f982326eebdfc9068e924ff21f6448534ea32672bb4c1d52a8193b", "完整离线安装器备选，超过 50 MiB；不是轻量终端包")
    });

    public static string Describe(ShellProfile profile) => Describe(CreatePlan(profile, Detect()));

    public static string Describe(ShellBackendReviewPlan plan)
    {
        var env = plan.Environment;
        var text = new StringBuilder();
        text.AppendLine("只读方案检查 · 尚未安装或应用");
        text.AppendLine($"Windows {env.DisplayVersion ?? "版本未知"} · 构建 {env.Build?.ToString() ?? "未知"}.{env.Revision?.ToString() ?? "未知"} · {env.Architecture}");
        foreach (var binary in env.SystemBinaries) text.AppendLine($"{binary.Name}：{binary.Version ?? "未能读取版本"}");
        text.AppendLine($"原生任务栏 TaskbarAl：{env.NativeTaskbarAlignment?.ToString() ?? "未设置或未知"}（0 靠左、1 居中；不推断第三方任务栏的实际效果）");
        text.AppendLine(env.Windhawk.Count == 0
            ? "Windhawk：常用安装位置、卸载项及运行进程中未检测到；未遍历所有磁盘。"
            : "Windhawk：" + string.Join("；", env.Windhawk.Select(i => $"{i.Version ?? "版本未知"} / {i.Mode} / {i.Status}")));
        text.AppendLine($"StartAllBack 在 Explorer 中加载：{env.StartAllBackLoaded}");
        foreach (var item in env.StartAllBackEvidence) text.AppendLine("  " + item);
        text.AppendLine(); text.AppendLine("选中模块与参数：");
        foreach (var mod in plan.Modules)
        {
            text.AppendLine($"• {mod.Id} {mod.Version}：{mod.Action}");
            if (mod.Selected) text.AppendLine("  " + string.Join("；", mod.Settings.Select(s => s.Key + "=" + s.Value)));
        }
        text.AppendLine(); text.AppendLine("当前不能应用的原因：");
        foreach (var blocker in plan.Blockers) text.AppendLine("• " + blocker);
        text.AppendLine(); text.AppendLine("下一步：");
        foreach (var step in plan.Steps) text.AppendLine("• " + step);
        text.AppendLine();
        text.AppendLine("前端可关闭；实际效果需要 Windhawk 引擎/便携守护进程继续运行。关闭引擎会结束挂钩会话，不能承诺所有后台组件都退出后效果仍永久保留。");
        text.AppendLine("优先核对官方预编译模块，不要求用户安装编译器。自适应背景使用 ClassicDesk 本地构建组件，编译器仅用于开发，不随应用分发。所有终端组件仍需固定 hash、检查运行库与实际兼容性。");
        text.AppendLine("经典 Ribbon 模式不带标签页；关闭方案选项只表示不启用对应模块，不会擅自关闭其他工具的设置。");
        return text.ToString();
    }

    public static ShellBackendReviewPlan CreatePlan(ShellProfile profile, ShellBackendEnvironment environment,
        string? proposedPortableRoot = null)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(environment); profile.Validate();
        var root = Path.GetFullPath(proposedPortableRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "Backends", "Windhawk-" + EngineVersion));
        static Dictionary<string, string> Settings(params (string Key, string Value)[] values) => values.ToDictionary(x => x.Key, x => x.Value);
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        ShellModPlan Mod(string id, string version, string license, bool selected, string[] targets,
            Dictionary<string, string> settings) => new(id, version, license, selected,
                selected ? "拟启用，尚未执行" : "本方案不启用；若此前由本工具拥有，需另作归属恢复计划",
                targets, settings, version.Contains("classicdesk") ? "native/taskbar-background-helper.wh.cpp（ClassicDesk 本地源码）" : ModsSource + id + ".wh.cpp",
                version.Contains("classicdesk") ? "local-source-build; requires-reviewed-runtime" : "planned; precompiled-and-live-compatibility-unknown",
                version.Contains("classicdesk") ? "" : $"https://mods.windhawk.net/mods/{id}/{version}_64.dll");
        var modules = new[]
        {
            Mod("taskbar-start-button-position", "1.3.2", "GPL-3.0", !profile.SkipTaskbarLayout && profile.StartOnLeft && !profile.LeftAlignedApps,
                ["explorer.exe", "StartMenuExperienceHost.exe"], Settings(("otherSystemButtonsOnTheLeft", profile.OtherSystemButtonsOnLeft ? "1" : "0"),
                    ("startMenuOnTheLeft", profile.StartMenuOnLeft ? "1" : "0"), ("searchMenuPositionInAllCases", profile.SearchMenuOnLeft ? "1" : "0"))),
            Mod("taskbar-icon-size", "1.3.10", "GPL-3.0", !profile.SkipTaskbarSizing, ["explorer.exe"],
                Settings(("TaskbarHeight", Number(profile.TaskbarHeight)), ("IconSize", Number(profile.IconSize)),
                    ("IconSizeSmall", Number(profile.SmallIconSize)), ("TaskbarButtonWidth", Number(profile.TaskbarButtonWidth)), ("TaskbarButtonWidthSmall", Number(profile.SmallTaskbarButtonWidth)))),
            Mod("explorer-frame-classic", "1.0.8", "GPL-3.0", profile.ClassicRibbon || profile.UseClassicNavigationBar, ["explorer.exe"],
                Settings(("explorerStyle", profile.UseClassicNavigationBar ? "classicNavigationBar" : "classicRibbonUI"))),
            Mod("explorer-context-menu-classic", "1.0.2", "MIT", profile.ClassicContextMenu, ["explorer.exe"],
                Settings(("overrideWithCtrl", profile.ClassicMenuWithCtrl ? "1" : "0"))),
            Mod("windows-11-taskbar-styler", "1.9", "GPL-3.0", profile.CompactTray || profile.TranslucentTaskbar, ["explorer.exe"],
                ShellTaskbarStyle.Settings(profile.CompactTray, profile.TranslucentTaskbar)),
            Mod("taskbar-background-helper", profile.FollowMaximizedTheme ? "1.2-classicdesk.1" : "1.2", "GPL-3.0", profile.TranslucentTaskbar, ["explorer.exe"], ShellTaskbarStyle.BackdropSettings(profile.FollowMaximizedTheme))
        };
        var blockers = new List<string>();
        if (environment.Build is null) blockers.Add("Windows 构建号未能读取，不能推断兼容性。");
        else if (environment.Build < 22000) blockers.Add("所选模块面向 Windows 11，当前 Windows 构建不满足前提。");
        if (environment.Architecture != "X64") blockers.Add("本方案只核对了 x86-64 源码，当前架构尚未支持。");
        int requiredAlignment = profile.LeftAlignedApps ? 0 : 1;
        if (!profile.SkipTaskbarLayout && environment.NativeTaskbarAlignment != requiredAlignment)
            blockers.Add($"本方案要求原生 TaskbarAl={requiredAlignment}（{(requiredAlignment == 0 ? "全靠左" : "应用居中")}）；当前未确认满足，需要独立、有归属且可恢复的 Windows 对齐事务，此只读检查不执行。");
        if (environment.StartAllBackEvidence.Count > 0 || environment.StartAllBackLoaded == "detected")
            blockers.Add("检测到 StartAllBack：必须先审查退出/停用及恢复方案，再经明确同意切换；可能需要注销或重启 Explorer，不在此检查中执行。");
        if (environment.StartAllBackLoaded == "unknown") blockers.Add("未能完整读取 Explorer 模块，StartAllBack 是否在运行未知。");
        if (environment.ReadErrors.Count > 0) blockers.Add("部分检测未完成：" + string.Join("；", environment.ReadErrors));
        if (environment.Windhawk.Count == 0) blockers.Add("未检测到已就绪的 Windhawk；便携部署尚未执行。");
        else
        {
            if (!environment.Windhawk.Any(i => i.EnginePresent && i.Mode == "portable" && VersionMatches(i.Version)))
                blockers.Add("没有检测到与固定 v1.7.3 协议匹配且文件完整的便携引擎。");
            if (environment.WindhawkProcessPaths.Count > 0)
                blockers.Add("已有 Windhawk 进程：必须核对现有实例及数据归属，不能并行启动另一个全局守护进程或覆盖其配置。");
        }
        blockers.Add("启用前必须从所选运行包重新校验固定 hash 和依赖；下载地址可达或其他目录的静态报告不能代替该包的当前校验。");
        blockers.Add("尚未在当前 Explorer / ExplorerFrame 二进制上验证符号、加载、效果和恢复；Windows 构建号不能代替这些证据。");
        blockers.Add("真实启用/恢复适配已接入检查应用面板，但尚未实机验收。本只读计划不执行切换；隔离测试不代表当前桌面效果通过。");
        var steps = new[]
        {
            "审查目标与冲突：保留 StartAllBack 原配置；明确首次切换是否允许注销/重启 Explorer，并准备返回原状的入口。",
            "优先审查官方预编译 DLL 与 versions.json，固定所选版本、文件 hash 和 PE 依赖；只有修改源码或预编译不匹配时才另行考虑开发编译器。",
            "核对可携带最小运行文件、GPL/MIT 与第三方许可；预编译 DLL 必须与目标架构、Windhawk API 和运行库匹配。",
            "仅在审查通过后设计备份事务：保存原文件字节/修订，写入独立便携目录中的 Disabled=1 配置，再逐模块加载并回读。",
            "先验证开始按钮位置、图标尺寸和经典 Ribbon，再单独验证右键菜单；Taskbar Styler 仅在勾选半透明或紧凑托盘时启用内置样式。",
            "全靠左布局使用 TaskbarAl=0 并停用开始定位模块；两种应用居中布局使用 TaskbarAl=1。对齐事务单独备份并记录归属；禁用模块不等于恢复原对齐。",
            "通过后由本 WPF 前端控制配置；使用 -tray-only 运行引擎而不打开 Windhawk 设置 UI，关闭 WPF 后验收效果与资源占用。"
        };
        var paths = new List<string> { Path.Combine(root, "windhawk.exe"), Path.Combine(root, "windhawk.ini"),
            "[Storage] EnginePath / CompilerPath / AppDataPath 必须读取实际 windhawk.ini，不能猜测或覆盖现有值。",
            "<EnginePath>/engine.ini：[Storage]，引擎 AppDataPath 指向 <主 AppDataPath>/Engine（独立于主程序配置）",
            "<AppDataPath>/settings.ini：[Settings]（便携模式）",
            "<AppDataPath>/Engine/settings.ini：[Settings]（引擎便携模式）；暂存包保持 SafeMode=1",
            "<AppDataPath>/Engine/Mods/<mod-id>.ini：[Mod]、[Settings]（便携模式）",
            "<AppDataPath>/Engine/Mods/64/<LibraryFileName>：已编译模块与对应运行库",
            "非便携模式为 <RegistryKey>/Engine/Mods/<mod-id> 及 Settings 子键；本计划不写这些注册表路径。" };
        return new(environment, root, modules, KnownDownloads, blockers, steps, paths, false, false,
            "review-only; v1.7.3 storage protocol derived from pinned official source; not a public install-mod CLI");
    }

    public static ShellBackendEnvironment Detect(IEnumerable<string>? additionalWindhawkRoots = null)
    {
        var errors = new List<string>(); var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sab = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var running = new List<string>();
        int? build = null, revision = null, alignment = null; string? displayVersion = null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var version = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
            if (int.TryParse(version?.GetValue("CurrentBuild")?.ToString(), out var number)) build = number;
            if (int.TryParse(version?.GetValue("UBR")?.ToString(), out number)) revision = number;
            displayVersion = version?.GetValue("DisplayVersion")?.ToString();
        }
        catch (Exception e) { errors.Add("系统版本读取：" + e.GetType().Name); }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false);
            if (int.TryParse(key?.GetValue("TaskbarAl")?.ToString(), out var number)) alignment = number;
        }
        catch (Exception e) { errors.Add("原生任务栏对齐读取：" + e.GetType().Name); }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", false);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var item = uninstall.OpenSubKey(name, false);
                    var title = item?.GetValue("DisplayName")?.ToString() ?? "";
                    if (title.Contains("Windhawk", StringComparison.OrdinalIgnoreCase))
                    { if (item?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location)) roots.Add(location); }
                    if (title.Contains("StartAllBack", StringComparison.OrdinalIgnoreCase) || title.Contains("StartIsBack", StringComparison.OrdinalIgnoreCase))
                        sab.Add(title + " " + item?.GetValue("DisplayVersion") + "（卸载项已检测）");
                }
            }
            catch (Exception e) { errors.Add($"{hive}/{view} 安装项读取：{e.GetType().Name}"); }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            roots.Add(Path.Combine(root, "Windhawk"));
            if (File.Exists(Path.Combine(root, "StartAllBack", "StartAllBackCfg.exe"))) sab.Add("StartAllBack 程序目录已检测");
        }
        if (additionalWindhawkRoots is not null) foreach (var root in additionalWindhawkRoots) if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
        try
        {
            foreach (var process in Process.GetProcessesByName("windhawk"))
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path)) { running.Add(path); roots.Add(Path.GetDirectoryName(path)!); }
                    else errors.Add("Windhawk 运行进程路径未知。");
                }
                catch (Exception e) { errors.Add("Windhawk 进程读取：" + e.GetType().Name); }
            }
        }
        catch (Exception e) { errors.Add("Windhawk 进程枚举：" + e.GetType().Name); }
        var loaded = "not-detected"; var sawExplorer = false; var complete = true;
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName("explorer"))
            using (process)
            {
                try
                {
                    if (process.SessionId != current.SessionId) continue;
                    sawExplorer = true;
                    foreach (ProcessModule module in process.Modules)
                        if (module.ModuleName.Contains("StartAllBack", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Contains("StartIsBack", StringComparison.OrdinalIgnoreCase))
                        { loaded = "detected"; sab.Add(module.ModuleName + "（当前会话 Explorer 已加载）"); }
                }
                catch { complete = false; }
            }
        }
        catch { complete = false; }
        if (loaded != "detected" && (!sawExplorer || !complete)) loaded = "unknown";
        var installations = new List<WindhawkInstallation>();
        foreach (var root in roots)
        {
            try
            {
                var fullRoot = Path.GetFullPath(root);
                if (!File.Exists(Path.Combine(fullRoot, "windhawk.exe"))) continue;
                installations.Add(InspectWindhawk(fullRoot));
            }
            catch (Exception e) { errors.Add("Windhawk 路径检测：" + e.GetType().Name); }
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var binaries = new[] { ReadBinary(Path.Combine(windows, "explorer.exe")),
            ReadBinary(Path.Combine(windows, "System32", "ExplorerFrame.dll")) };
        return new(DateTime.UtcNow, RuntimeInformation.OSArchitecture.ToString(), build, revision, displayVersion,
            binaries, installations.GroupBy(i => i.Root, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray(),
            running, sab.ToArray(), loaded, errors.Distinct().ToArray(), alignment);
    }

    static BackendBinary ReadBinary(string path)
    {
        try { return new(Path.GetFileName(path), path, File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null); }
        catch { return new(Path.GetFileName(path), path, null); }
    }

    static bool VersionMatches(string? version) => version is not null &&
        (version == EngineVersion || version.StartsWith(EngineVersion + ".", StringComparison.Ordinal));

    static WindhawkInstallation InspectWindhawk(string root)
    {
        var version = ReadBinary(Path.Combine(root, "windhawk.exe")).Version;
        var ini = Path.Combine(root, "windhawk.ini");
        if (!File.Exists(ini)) return new(root, version, "unknown", null, null, null, false, false, "缺少 windhawk.ini，存储协议未知");
        if (new FileInfo(ini).Length > 1024 * 1024) return new(root, version, "unknown", null, null, null, false, false, "windhawk.ini 超出只读解析上限");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var storage = false;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.Length == 0) continue;
            if (line.StartsWith('[')) { storage = line.Equals("[Storage]", StringComparison.OrdinalIgnoreCase); continue; }
            var split = line.IndexOf('=');
            if (storage && split > 0) values[line[..split].Trim()] = line[(split + 1)..].Trim().Trim('"');
        }
        string? Resolve(string name) => !values.TryGetValue(name, out var path) || string.IsNullOrWhiteSpace(path) ? null :
            Path.GetFullPath(Path.Combine(root, Environment.ExpandEnvironmentVariables(path)));
        var engine = Resolve("EnginePath"); var compiler = Resolve("CompilerPath"); var data = Resolve("AppDataPath");
        var mode = values.GetValueOrDefault("Portable") == "1" ? "portable" : values.ContainsKey("RegistryKey") ? "installed-registry" : "unknown";
        var architecture = RuntimeInformation.OSArchitecture switch { Architecture.X64 => "64", Architecture.Arm64 => "arm64", _ => "32" };
        var enginePresent = engine is not null && File.Exists(Path.Combine(engine, architecture, "windhawk.dll"))
            && File.Exists(Path.Combine(engine, "engine.ini"));
        var compilerPresent = compiler is not null && File.Exists(Path.Combine(compiler, "bin", "clang++.exe"));
        return new(root, version, mode, engine, compiler, data, enginePresent, compilerPresent,
            !VersionMatches(version) ? "已检测，版本协议未核对" : enginePresent && data is not null ? "已检测文件，加载/兼容未验证" : "配置或引擎文件不完整");
    }

    public static IReadOnlyList<string> ProtocolSources { get; } = Array.AsReadOnly(new[]
    {
        EngineSource + "src/windhawk/app/app.cpp#L267", // -tray-only; no install-mod command in this release.
        EngineSource + "src/windhawk/app/storage_manager.cpp#L137", // Read [Storage] paths from executable-adjacent INI.
        EngineSource + "src/vscode-windhawk/src/utils/modConfigUtils.ts#L190", // Portable Mod/Settings and SettingsChangeTime.
        EngineSource + "src/vscode-windhawk/src/utils/compilerUtils.ts#L242", // clang++ arguments and runtime copies.
        EngineSource + "src/windhawk/engine/storage_manager.cpp#L432", // File/registry change monitoring.
        EngineSource + "src/windhawk/app/engine_control.cpp#L43", // Session ends when engine owner exits.
        "https://github.com/ramensoftware/windhawk-mods/blob/" + ModsCommit + "/README.md"
    });
}
