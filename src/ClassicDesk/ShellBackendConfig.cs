using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassicDesk;

public static class ShellTaskbarStyle
{
    public static Dictionary<string, string> BackdropSettings() => new()
    {
        ["backgroundStyle"] = "blur", ["color.red"] = "0", ["color.green"] = "0", ["color.blue"] = "0",
        ["color.accentColor"] = "0", ["color.transparency"] = "24", ["onlyWhenMaximized"] = "0", ["styleForDarkMode.use"] = "0"
    };
    public static Dictionary<string, string> Settings(bool compact, bool translucent)
    {
        var values = new Dictionary<string, string> { ["theme"] = "", ["clickThroughTaskbar"] = "0", ["xamlDiagnosticsHandling"] = "allow" };
        int index = 0;
        void Style(string target, params string[] styles)
        {
            values[$"controlStyles[{index}].target"] = target;
            for (int i = 0; i < styles.Length; i++) values[$"controlStyles[{index}].styles[{i}]"] = styles[i];
            index++;
        }
        if (translucent)
        {
            Style("Taskbar.TaskbarBackground#BackgroundControl", "Opacity=0");
            Style("Rectangle#BackgroundFill", "Fill=Transparent", "Opacity=0");
            Style("Rectangle#BackgroundStroke", "Fill=Transparent");
            Style("Grid#IconPanel@RunningIndicatorStates > Rectangle#RunningIndicator, Taskbar.TaskListLabeledButtonPanel@RunningIndicatorStates > Rectangle#RunningIndicator", "Width=28", "Height=2", "RadiusX=0", "RadiusY=0", "Fill=#00A8E8");
        }
        if (compact)
        {
            Style("SystemTray.ChevronIconView", "Padding=0", "MinWidth=20");
            // Let the content measure itself: a fixed outer width clips the
            // image's own padded grid, and can truncate language indicators.
            Style("SystemTray.NotifyIconView#NotifyItemIcon", "Padding=0", "MinWidth=20", "Width=Auto");
            Style("SystemTray.NotifyIconView#NotifyItemIcon > Grid#ContainerGrid > ContentPresenter#ContentPresenter > Grid#ContentGrid > SystemTray.ImageIconContent > Grid#ContainerGrid", "Padding=0");
            Style("SystemTray.NotifyIconView#NotifyItemIcon > Grid#ContainerGrid > ContentPresenter#ContentPresenter > Grid#ContentGrid > SystemTray.TextIconContent > Grid#ContainerGrid", "Padding=0");
            Style("SystemTray.LanguageTextIconContent", "Width=Auto", "MinWidth=20");
            Style("SystemTray.IconView#SystemTrayIcon", "Padding=0", "MinWidth=20");
            Style("SystemTray.TextIconContent > Grid#ContainerGrid", "Padding=2,0,2,0");
            Style("SystemTray.OmniButton", "Padding=0");
            Style("SystemTray.OmniButton#ControlCenterButton > Grid > ContentPresenter > ItemsPresenter > StackPanel > ContentPresenter > SystemTray.IconView#SystemTrayIcon > Grid#ContainerGrid > Grid#ContentGrid > SystemTray.TextIconContent > Grid#ContainerGrid", "Padding=2,0,2,0");
            Style("SystemTray.OmniButton#NotificationCenterButton > Grid > ContentPresenter > ItemsPresenter > StackPanel > ContentPresenter > SystemTray.IconView#SystemTrayIcon > Grid", "Padding=4,0,4,0");
            Style("SystemTray.IconView#SystemTrayIcon > Grid#ContainerGrid > ContentPresenter#ContentPresenter > Grid#ContentGrid > SystemTray.TextIconContent > Grid#ContainerGrid", "Padding=0");
            Style("SystemTray.StackListView#IconStack > ItemsPresenter > StackPanel > ContentPresenter > SystemTray.IconView#SystemTrayIcon", "Padding=0");
        }
        return values;
    }
}

public sealed record BackendConfigurationFile(string RelativePath, long Bytes, string Sha256);
public sealed record DisabledBackendConfiguration(string Directory, string ManifestPath, string PackageId,
    IReadOnlyList<BackendConfigurationFile> Files, bool EngineStarted = false, bool HostSettingsChanged = false);

/// <summary>
/// Creates a new, owned directory containing disabled configuration only. This
/// is not an activation transaction. No process, registry or existing config is changed.
/// </summary>
public static class ShellBackendConfig
{
    const string OwnerFile = "classicdesk-package-owner.json";
    const string ManifestFile = "configuration-manifest.json";
    static readonly Encoding IniEncoding = new UnicodeEncoding(false, true, true);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly IReadOnlyDictionary<string, (string Version, string[] Targets)> Supported =
        new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            ["taskbar-start-button-position"] = ("1.3.2", ["explorer.exe", "StartMenuExperienceHost.exe"]),
            ["taskbar-icon-size"] = ("1.3.10", ["explorer.exe"]),
            ["explorer-frame-classic"] = ("1.0.8", ["explorer.exe"]),
            ["explorer-context-menu-classic"] = ("1.0.2", ["explorer.exe"]),
            ["windows-11-taskbar-styler"] = ("1.9", ["explorer.exe"]),
            ["taskbar-background-helper"] = ("1.2", ["explorer.exe"])
        };

    public static string GetLibraryFileName(ShellModPlan plan)
    {
        ValidateIdentity(plan);
        return plan.Id + "_" + plan.Version + ".dll";
    }

    /// <summary>
    /// Produces text only. enabled=true does not write or activate anything;
    /// CreateDisabledConfiguration always uses false. Library paths are forbidden.
    /// </summary>
    public static string BuildModIni(ShellModPlan plan, bool enabled = false, DateTimeOffset? now = null,
        string? libraryFileName = null)
    {
        ValidateIdentity(plan);
        var library = libraryFileName ?? GetLibraryFileName(plan);
        if (string.IsNullOrEmpty(library) || library.Length > 180 ||
            !library.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            library is "." or ".." || library.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ||
            library.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("模块库必须是受限 DLL 文件名，不能包含路径、空白或控制字符。");
        var settings = plan.Settings.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        ValidateSettings(plan.Id, settings);
        var stamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() & 0x7fffffff;
        var text = new StringBuilder();
        void Line(string value) => text.Append(value).Append("\r\n");
        Line("[Mod]"); Line("LibraryFileName=" + library); Line("Disabled=" + (enabled ? "0" : "1"));
        Line("LoggingEnabled=0"); Line("DebugLoggingEnabled=0");
        Line("Include=" + string.Join('|', Supported[plan.Id].Targets));
        Line("Exclude="); Line("IncludeCustom="); Line("ExcludeCustom=");
        Line("IncludeExcludeCustomOnly=0"); Line("PatternsMatchCriticalSystemProcesses=0");
        Line("Architecture=x86-64"); Line("Version=" + plan.Version);
        Line("SettingsChangeTime=" + stamp.ToString(CultureInfo.InvariantCulture));
        Line(""); Line("[Settings]");
        foreach (var item in settings.OrderBy(p => p.Key, StringComparer.Ordinal)) Line(item.Key + "=" + item.Value);
        return text.ToString();
    }

    public static DisabledBackendConfiguration CreateDisabledConfiguration(string newDirectory, ShellProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile); profile.Validate();
        var root = ValidateNewDirectory(newDirectory);
        // Obtain module mapping without probing or changing the host environment.
        var unprobed = new ShellBackendEnvironment(DateTime.UtcNow, "unknown", null, null, null, [], [], [], [], "unknown", []);
        var plan = ShellBackendPlanner.CreatePlan(profile, unprobed, root);
        var styled = profile.CompactTray || profile.TranslucentTaskbar;
        var mods = plan.Modules.Where(m => Supported.ContainsKey(m.Id) && (styled || m.Id is not ("windows-11-taskbar-styler" or "taskbar-background-helper"))).ToArray();
        if (mods.Length != (styled ? 6 : 4)) throw new InvalidDataException("固定模块的计划不完整。");
        var created = DateTimeOffset.UtcNow;
        var texts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["windhawk.ini"] = "[Storage]\r\nPortable=1\r\nEnginePath=Engine\\1.7.3\r\nUIPath=UI\r\nCompilerPath=Compiler\r\nAppDataPath=AppData\r\n",
            ["Engine/1.7.3/engine.ini"] = "[Storage]\r\nPortable=1\r\nAppDataPath=..\\..\\AppData\\Engine\r\n",
            ["AppData/settings.ini"] = "[Settings]\r\nSafeMode=1\r\nHideTrayIcon=1\r\nDisableUpdateCheck=1\r\nDontAutoShowToolkit=1\r\nDisableToolkitHotkey=1\r\n",
            ["AppData/Engine/settings.ini"] = "[Settings]\r\nSafeMode=1\r\n"
        };
        foreach (var mod in mods) texts.Add("AppData/Engine/Mods/" + mod.Id + ".ini", BuildModIni(mod, false, created));
        var packageId = Guid.NewGuid().ToString("N");
        // CreateDirectoryW reports an already-existing target atomically. Do not
        // accept an existing empty directory or replace another creator's directory.
        if (!CreateDirectory(root, 0))
            throw new IOException("预备目录必须全新且不存在；创建失败，未覆盖任何目录。", new Win32Exception(Marshal.GetLastWin32Error()));
        var files = new List<BackendConfigurationFile>();
        try
        {
            RejectReparseChain(root);
            WriteNew(root, OwnerFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, owner = "ClassicDesk", packageId, directory = root,
                createdUtc = created, purpose = "disabled-offline-preparation", engineStarted = false
            }, Json)), files);
            foreach (var pair in texts)
            {
                var payload = IniEncoding.GetPreamble().Concat(IniEncoding.GetBytes(pair.Value)).ToArray();
                WriteNew(root, pair.Key, payload, files);
            }
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, owner = "ClassicDesk", packageId, createdUtc = created,
                state = "disabled-configuration-prepared", activationInThisOperation = false,
                engineStarted = false, hostSettingsChanged = false, allModulesDisabled = true,
                manifestScope = "configuration-only-at-creation", binariesIncludedAtConfigurationPreparation = false,
                configurationEncoding = "UTF-16LE with BOM",
                engineVersion = ShellBackendPlanner.EngineVersion, engineCommit = ShellBackendPlanner.EngineCommit,
                modsCommit = ShellBackendPlanner.ModsCommit,
                selectedMeans = "profile intent only; every written mod remains Disabled=1",
                modules = mods.Select(m => new { m.Id, m.Version, m.Selected, disabled = true,
                    libraryFileName = GetLibraryFileName(m), m.Settings, m.SourceUrl, m.PrecompiledUrl }),
                files = files.ToArray(),
                limits = new[] { "This operation only prepares disabled configuration; explicit frontend activation is separate and not live-validated.",
                    "Binaries, dependencies, source correspondence and licenses require separate verification.",
                    "Reparse and revision checks do not provide a transaction against a hostile concurrent filesystem writer." }
            }, Json);
            WriteNew(root, ManifestFile, Encoding.UTF8.GetBytes(manifest), files);
            return new(root, Path.Combine(root, ManifestFile), packageId, files.ToArray());
        }
        catch (Exception e)
        {
            // Preserve this newly-owned partial directory as evidence. No recursive
            // cleanup, retry-overwrite, rename or operation outside root is attempted.
            throw new IOException("离线配置未完成；已创建的自有目录保留供检查，不会覆盖重试或启动引擎：" + root, e);
        }
    }

    static void ValidateIdentity(ShellModPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Supported.TryGetValue(plan.Id, out var expected) || plan.Version != expected.Version)
            throw new InvalidDataException("只接受已核对的固定版本模块，不能生成任意模块配置。");
        if (plan.Targets.Count != expected.Targets.Length ||
            !plan.Targets.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Targets))
            throw new InvalidDataException("模块进程范围与固定源码不一致。");
    }

    static void ValidateSettings(string id, Dictionary<string, string> values)
    {
        if (id == "windows-11-taskbar-styler")
        {
            // Only the four product-owned combinations are accepted, never arbitrary XAML.
            if (!(new[] { false, true }).Any(compact => (new[] { false, true }).Any(translucent =>
                ShellTaskbarStyle.Settings(compact, translucent) is var expected && expected.Count == values.Count &&
                expected.All(p => values.TryGetValue(p.Key, out var value) && value == p.Value))))
                throw new InvalidDataException("任务栏样式必须是已核对的内置组合。");
            return;
        }
        if (id == "taskbar-background-helper")
        {
            var expected = ShellTaskbarStyle.BackdropSettings();
            if (values.Count != expected.Count || expected.Any(p => !values.TryGetValue(p.Key, out var value) || value != p.Value))
                throw new InvalidDataException("原生背景只接受已核对的静态半透明配置。");
            return;
        }
        string[] keys = id switch
        {
            "taskbar-start-button-position" => ["otherSystemButtonsOnTheLeft", "startMenuOnTheLeft", "searchMenuPositionInAllCases"],
            "taskbar-icon-size" => ["TaskbarHeight", "IconSize", "IconSizeSmall", "TaskbarButtonWidth", "TaskbarButtonWidthSmall"],
            "explorer-frame-classic" => ["explorerStyle"],
            "explorer-context-menu-classic" => ["overrideWithCtrl"],
            _ => throw new InvalidDataException("未知模块。")
        };
        if (values.Count != keys.Length || !values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(keys))
            throw new InvalidDataException("设置必须恰好包含固定白名单字段。");
        foreach (var item in values)
        {
            if (item.Value is null || item.Value.Any(char.IsControl)) throw new InvalidDataException("设置不能包含控制字符。");
            var valid = item.Key switch
            {
                "TaskbarHeight" => IsOneOf(item.Value, [40, 44, 48, 52, 56, 64]),
                "IconSize" or "IconSizeSmall" => IsOneOf(item.Value, [16, 20, 24, 28, 32]),
                "TaskbarButtonWidth" or "TaskbarButtonWidthSmall" => IsOneOf(item.Value, ShellProfile.ButtonWidths),
                "explorerStyle" => item.Value is "classicRibbonUI" or "classicNavigationBar",
                _ => item.Value is "0" or "1"
            };
            if (!valid) throw new InvalidDataException("设置值超出当前方案白名单：" + item.Key);
        }
    }

    static bool IsOneOf(string value, int[] allowed) => allowed.Any(n => value == n.ToString(CultureInfo.InvariantCulture));

    static string ValidateNewDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("必须指定本地绝对新目录；不接受相对路径、UNC 或设备路径。");
        var segments = directory.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Skip(1).Any(s => s is "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.EndsWith(' ') || s.EndsWith('.')))
            throw new InvalidDataException("目录路径包含不允许的段或路径跳转。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("目标已存在，拒绝覆盖或接管，包括现有空目录。");
        var parent = Path.GetDirectoryName(root);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException("父目录必须已存在。");
        RejectReparseChain(parent);
        return root;
    }

    static void RejectReparseChain(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("拒绝通过符号链接或重解析点写入预备包。");
    }

    static void WriteNew(string root, string relativePath, byte[] bytes, List<BackendConfigurationFile> files)
    {
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("生成路径越出新预备目录。");
        var parent = Path.GetDirectoryName(target)!;
        // Check the existing ancestry before mkdir, then all ancestry before opening.
        var existing = new DirectoryInfo(parent);
        while (!existing.Exists) existing = existing.Parent ?? throw new IOException("目录边界无效。");
        RejectReparseChain(existing.FullName); Directory.CreateDirectory(parent); RejectReparseChain(parent);
        using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        var readback = File.ReadAllBytes(target);
        if (!readback.AsSpan().SequenceEqual(bytes)) throw new IOException("配置文件回读不一致：" + relativePath);
        files.Add(new(relativePath, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateDirectory(string path, nint securityAttributes);
}
