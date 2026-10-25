using ClassicDesk;
using System.Text.Json;
using System.Text.Json.Serialization;

var checks = new List<object>();
var failures = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
void Test(string name, Action action)
{
    try { action(); checks.Add(new { name, passed = true }); }
    catch (Exception e) { failures.Add(name + ": " + e.Message); checks.Add(new { name, passed = false, error = e.Message }); }
}

Test("successful current-session taskbar includes real child subtree", () =>
{
    var report = Analyze(GoodTaskbar());
    Check(report.Status == TaskbarCheckStatus.Healthy, "expected healthy structure and response");
    var taskbar = report.Taskbars.Single();
    Check(taskbar.ChildCount == 3 && taskbar.HasXamlBridge && taskbar.HasTrayNotifyWindow && taskbar.HasTaskList,
        "expected the XAML, tray-notify, and task-list descendants");
    Check(taskbar.Children.Any(c => c.Hwnd == "0x22" && c.ParentHwnd == "0x11"), "expected immediate parent identity");
});

Test("true zero-child observation is not confused with failed child enumeration", () =>
{
    var assessment = ShellTaskbarDiagnostics.AssessChildEnumeration(true, 100, 200, true, 100, 200, false);
    Check(assessment.Available && !assessment.Truncated,
        "stable parent identity must establish a completed enumeration even when the Win32 return value is unused");
    var report = Analyze(GoodTaskbar(children: []));
    var taskbar = report.Taskbars.Single();
    Check(taskbar.ChildEnumerationAvailable && taskbar.ChildCount == 0, "zero children must be a successful empty enumeration");
    Check(taskbar.Status == TaskbarCheckStatus.Degraded && taskbar.Findings.Contains("no-child-windows-observed"),
        "an empty taskbar subtree must be reported as structurally degraded");
});

Test("taskbar disappearing or changing identity during child enumeration stays unknown", () =>
{
    var vanished = ShellTaskbarDiagnostics.AssessChildEnumeration(true, 100, 200, false, 0, 0, false);
    var replaced = ShellTaskbarDiagnostics.AssessChildEnumeration(true, 100, 200, true, 101, 201, false);
    Check(!vanished.Available && vanished.Error == "taskbar-window-disappeared-during-child-enumeration",
        "a destroyed taskbar window must invalidate the child tree");
    Check(!replaced.Available && replaced.Error == "taskbar-window-identity-changed-during-child-enumeration",
        "a reused or replaced HWND must invalidate the child tree");
});

Test("child safety cap remains explicitly truncated", () =>
{
    var assessment = ShellTaskbarDiagnostics.AssessChildEnumeration(true, 100, 200, true, 100, 200, true);
    Check(assessment.Available && assessment.Truncated, "hitting the safety cap is a partial, not empty, observation");
});

Test("failed child enumeration is unknown instead of reporting zero children", () =>
{
    var sample = GoodTaskbar() with { ChildEnumerationAvailable = false, ChildEnumerationError = "synthetic error" };
    var taskbar = Analyze(sample).Taskbars.Single();
    Check(taskbar.Status == TaskbarCheckStatus.Unknown && taskbar.Findings.Contains("child-window-enumeration-unknown"),
        "failed enumeration must remain unknown");
    Check(taskbar.ChildCount == 3, "a failed enumeration must not erase supplied observations");
});

Test("missing Windows 11 XAML bridge is degraded", () =>
{
    var children = StandardChildren().Where(c => !c.ClassName.Contains("DesktopWindowContentBridge")).ToArray();
    var taskbar = Analyze(GoodTaskbar(children: children)).Taskbars.Single();
    Check(taskbar.Status == TaskbarCheckStatus.Degraded && taskbar.Findings.Contains("xaml-composition-bridge-not-found"),
        "missing XAML bridge must be explicit on Windows 11 builds");
});

Test("XAML bridge is not required on Windows 10 builds", () =>
{
    var report = ShellTaskbarDiagnostics.Analyze(Snapshot([GoodTaskbar(children: StandardChildren().Where(c =>
        !c.ClassName.Contains("DesktopWindowContentBridge")).ToArray())], build: 19045));
    Check(report.Status == TaskbarCheckStatus.Healthy, "Windows 10 taskbar layout should not require the Windows 11 bridge");
});

Test("WM_NULL timeout is explicitly unresponsive", () =>
{
    var taskbar = Analyze(GoodTaskbar() with { Responsive = false, ResponseElapsedMilliseconds = 1000, ResponseWin32Error = 1460 }).Taskbars.Single();
    Check(taskbar.Status == TaskbarCheckStatus.Degraded && taskbar.Findings.Contains("wm-null-did-not-respond-within-timeout"),
        "a bounded WM_NULL timeout must fail the response check");
});

Test("hidden auto-hide taskbar is accepted", () =>
{
    var taskbar = Analyze(GoodTaskbar() with { Visible = false, AutoHide = true }).Taskbars.Single();
    Check(taskbar.Status == TaskbarCheckStatus.Healthy && taskbar.Findings.Contains("hidden-while-auto-hide-enabled"),
        "auto-hide may legitimately hide a responsive taskbar");
});

Test("hidden taskbar with auto-hide disabled is degraded", () =>
{
    var taskbar = Analyze(GoodTaskbar() with { Visible = false, AutoHide = false }).Taskbars.Single();
    Check(taskbar.Status == TaskbarCheckStatus.Degraded && taskbar.Findings.Contains("hidden-while-auto-hide-disabled"),
        "hidden visibility must be assessed against auto-hide state");
});

Test("window PID and session errors remain unknown", () =>
{
    var noPid = Analyze(GoodTaskbar() with { ProcessId = 0, IdentityError = "synthetic PID error" }).Taskbars.Single();
    var noSession = Analyze(GoodTaskbar() with { ProcessSessionId = null, IdentityError = "synthetic session error" }).Taskbars.Single();
    Check(noPid.Status == TaskbarCheckStatus.Unknown && noPid.SessionRelation == TaskbarSessionRelation.Unknown,
        "PID failure must not be classified as a healthy current-session taskbar");
    Check(noSession.Status == TaskbarCheckStatus.Unknown && noSession.SessionRelation == TaskbarSessionRelation.Unknown,
        "session lookup failure must remain unknown");
});

Test("failed current-session lookup cannot report a healthy taskbar", () =>
{
    var snapshot = Snapshot([GoodTaskbar()]) with { CurrentSessionId = null, CurrentSessionError = "Win32 error 5" };
    var report = ShellTaskbarDiagnostics.Analyze(snapshot);
    Check(report.Status == TaskbarCheckStatus.Unknown && report.CurrentSessionError == "Win32 error 5",
        "current-session query failure must retain its error and unknown health");
    Check(report.Taskbars.Single().SessionRelation == TaskbarSessionRelation.Unknown,
        "known taskbar session cannot be matched to an unknown current session");
});

Test("other-session window is excluded from current-session health", () =>
{
    var report = Analyze(GoodTaskbar(), GoodTaskbar(hwnd: "0x99", session: 4));
    Check(report.Status == TaskbarCheckStatus.Healthy, "an unrelated session must not taint this session's taskbar result");
    Check(report.Taskbars.Single(t => t.Hwnd == "0x99").Status == TaskbarCheckStatus.OutOfSession,
        "foreign-session taskbar must be identified and excluded");
});

Test("module access denial is unknown and never reported as no matching module", () =>
{
    var unknown = new ProcessModuleObservation(ProcessModuleReadStatus.Unknown, [], [], "UnauthorizedAccessException");
    var taskbar = Analyze(GoodTaskbar() with { Modules = unknown }).Taskbars.Single();
    Check(taskbar.Modules.Status == ProcessModuleReadStatus.Unknown && taskbar.Modules.Error is not null,
        "module access denial must retain unknown status and error");
    Check(taskbar.Modules.ClassicDeskModules.Count == 0 && taskbar.Modules.WindhawkModules.Count == 0,
        "unknown module access must not synthesize a module match");
    Check(taskbar.Status == TaskbarCheckStatus.Healthy, "module attribution is reported separately from structure/response health");
});

Test("module attribution uses known ClassicDesk folders and returns full source paths", () =>
{
    var modules = ShellTaskbarDiagnostics.ClassifyProcessModules([
        new("styler.dll", @"C:\Program Files\ClassicDesk\Runtime\styler.dll"),
        new("icons.dll", @"C:\Program Files\ClassicDesk\ShellService\Runtime\icons.dll"),
        new("custom-mod.dll", @"C:\Users\TestUser\AppData\Local\Windhawk\Engine\ModsWritable\custom\custom-mod.dll"),
        new("shell32.dll", @"C:\Windows\System32\shell32.dll")
    ]);
    Check(modules.Status == ProcessModuleReadStatus.Available, "readable module paths should be classified");
    Check(modules.ClassicDeskModules.Any(m => m.ModuleName == "styler.dll" && m.FullPath.EndsWith(@"ClassicDesk\Runtime\styler.dll") && m.Source == @"ClassicDesk\Runtime"),
        "runtime modules with ordinary filenames must be attributed by their full path");
    Check(modules.ClassicDeskModules.Any(m => m.FullPath.EndsWith(@"ClassicDesk\ShellService\Runtime\icons.dll")),
        "shell-service descendants must retain full paths");
    Check(modules.WindhawkModules.Any(m => m.FullPath.EndsWith(@"Windhawk\Engine\ModsWritable\custom\custom-mod.dll") && m.Source == "Windhawk"),
        "Windhawk modules must be attributed by module path even when the filename is generic");
    Check(modules.ClassicDeskModules.All(m => Path.IsPathFullyQualified(m.FullPath)) && modules.WindhawkModules.All(m => Path.IsPathFullyQualified(m.FullPath)),
        "all source attributions must include absolute paths");
});

Test("unreadable or non-full module paths preserve unknown status", () =>
{
    var modules = ShellTaskbarDiagnostics.ClassifyProcessModules([
        new("ordinary.dll", @"C:\Windows\System32\shell32.dll"),
        new("mystery.dll", "")
    ]);
    Check(modules.Status == ProcessModuleReadStatus.Unknown && modules.Error is not null,
        "a failed path read must not become an available empty module list");
});

Test("physical DPI bounds preserve negative and high-DPI pixel coordinates", () =>
{
    var bounds = new TaskbarBounds(-2560, 1344, 0, 1440, 144, "physical");
    var taskbar = Analyze(GoodTaskbar() with { Bounds = bounds }).Taskbars.Single();
    Check(taskbar.Bounds == bounds && taskbar.Status == TaskbarCheckStatus.Healthy,
        "physical pixel coordinates and 144 DPI must be preserved verbatim");
});

Test("logical or unspecified bounds cannot be called physically accurate", () =>
{
    var taskbar = Analyze(GoodTaskbar() with { Bounds = new(0, 896, 1707, 960, 144, "logical") }).Taskbars.Single();
    Check(taskbar.Status == TaskbarCheckStatus.Unknown && taskbar.Findings.Contains("physical-bounds-not-confirmed"),
        "virtualized coordinates must not be accepted as physical bounds");
});

Test("GetShellWindow identity and diagnostic limits are represented", () =>
{
    var shell = new ShellWindowObservation("0xAA", 100, 200, 1, null);
    var report = AnalyzeWithShell(GoodTaskbar(), shell);
    Check(report.ShellWindow == shell, "shell window HWND/PID/TID/session identity must be preserved");
    Check(report.ShellTaskbarIdentity == ShellTaskbarIdentityStatus.Matched,
        "the main taskbar must match GetShellWindow by PID and session");
    Check(report.ReadOnly && !report.WindowsStateChanged && !report.RenderedPixelsVerified &&
          !report.ClickOperationsVerified && !report.ServiceRunningUsedAsHealthEvidence,
        "report must describe its read-only scope and validation limits");
});

Test("GetShellWindow query failure is unknown, not confirmed by same class name", () =>
{
    var shell = new ShellWindowObservation(null, 0, 0, null, "synthetic query failure");
    var report = AnalyzeWithShell(GoodTaskbar(), shell);
    Check(report.ShellTaskbarIdentity == ShellTaskbarIdentityStatus.Unknown && report.Status == TaskbarCheckStatus.Unknown,
        "same Shell_TrayWnd class cannot substitute for a missing ShellWindow identity");
});

Test("main taskbar with matching class but different shell PID or session is degraded", () =>
{
    var pidMismatch = AnalyzeWithShell(GoodTaskbar(), new("0xAA", 999, 200, 1, null));
    var sessionMismatch = AnalyzeWithShell(GoodTaskbar(), new("0xAA", 100, 200, 2, null));
    Check(pidMismatch.ShellTaskbarIdentity == ShellTaskbarIdentityStatus.Mismatch &&
          pidMismatch.Findings.Contains("main-taskbar-does-not-match-shell-window-pid-and-session"),
        "matching window class is insufficient when process identity differs");
    Check(sessionMismatch.ShellTaskbarIdentity == ShellTaskbarIdentityStatus.Mismatch,
        "matching PID in a different session is not the current shell identity");
});

Test("missing main taskbar is reported when enumeration completed", () =>
{
    var report = Analyze();
    Check(report.Status == TaskbarCheckStatus.Degraded && report.Findings.Contains("main-taskbar-not-found"),
        "successful enumeration without the current-session main taskbar is degraded");
});

Test("failed top-level enumeration is unknown", () =>
{
    var report = ShellTaskbarDiagnostics.Analyze(new(1, 26100, false, "synthetic enum error",
        new("0xAA", 100, 200, 1, null), [GoodTaskbar()]));
    Check(report.Status == TaskbarCheckStatus.Unknown && report.Findings.Contains("taskbar-enumeration-incomplete"),
        "an incomplete top-level enumeration cannot prove absence or health");
});

Test("cloaked taskbar without auto-hide is degraded", () =>
{
    var taskbar = Analyze(GoodTaskbar() with { Cloaked = true, AutoHide = false }).Taskbars.Single();
    Check(taskbar.Status == TaskbarCheckStatus.Degraded && taskbar.Findings.Contains("taskbar-window-is-cloaked"),
        "cloaking is reported independently from visibility");
});

var result = new { total = checks.Count, passed = checks.Count - failures.Count, failed = failures.Count, checks, failures };
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
jsonOptions.Converters.Add(new JsonStringEnumConverter());
string json = JsonSerializer.Serialize(result, jsonOptions);
if (args.Length > 0) File.WriteAllText(Path.GetFullPath(args[0]), json);
Console.WriteLine(json);
if (failures.Count > 0) Environment.ExitCode = 1;

TaskbarDiagnosticsReport Analyze(params TaskbarWindowObservation[] taskbars) =>
    ShellTaskbarDiagnostics.Analyze(Snapshot(taskbars));
TaskbarDiagnosticsReport AnalyzeWithShell(TaskbarWindowObservation taskbar, ShellWindowObservation shell) =>
    ShellTaskbarDiagnostics.Analyze(Snapshot([taskbar], shell: shell));
TaskbarDiagnosticsSnapshot Snapshot(TaskbarWindowObservation[]? taskbars = null, int build = 26100,
    ShellWindowObservation? shell = null) =>
    new(1, build, true, null, shell ?? new("0xAA", 100, 200, 1, null), taskbars ?? []);

TaskbarWindowObservation GoodTaskbar(string hwnd = "0x11", int? session = 1,
    IReadOnlyList<TaskbarChildObservation>? children = null) => new(
    hwnd, "Shell_TrayWnd", 100, 200, session, null,
    true, null, false, null, false, null, true, 3, null,
    new(0, 1344, 2560, 1440, 144, "physical"), null,
    true, null, false, children ?? StandardChildren(),
    new(ProcessModuleReadStatus.Available, [], [], null));

TaskbarChildObservation[] StandardChildren() =>
[
    new("0x22", "0x11", "Windows.UI.Composition.DesktopWindowContentBridge", 100, 201),
    new("0x23", "0x11", "TrayNotifyWnd", 100, 202),
    new("0x24", "0x11", "MSTaskListWClass", 100, 203)
];
