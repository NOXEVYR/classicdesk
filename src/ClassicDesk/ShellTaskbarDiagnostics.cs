using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassicDesk;

public enum TaskbarCheckStatus { Healthy, Degraded, Unknown, OutOfSession }
public enum TaskbarSessionRelation { Current, Other, Unknown }
public enum ProcessModuleReadStatus { Available, Unknown, Error }
public enum ShellTaskbarIdentityStatus { Matched, Mismatch, Unknown }

public sealed record TaskbarBounds(int Left, int Top, int Right, int Bottom, uint Dpi, string CoordinateSpace);
public sealed record TaskbarChildObservation(string Hwnd, string ParentHwnd, string ClassName, uint ProcessId, uint ThreadId);
public sealed record ProcessModuleCandidate(string ModuleName, string FullPath);
public sealed record ProcessModuleMatch(string ModuleName, string FullPath, string Source);
public sealed record ProcessModuleObservation(ProcessModuleReadStatus Status, IReadOnlyList<ProcessModuleMatch> ClassicDeskModules,
    IReadOnlyList<ProcessModuleMatch> WindhawkModules, string? Error);
public sealed record ChildEnumerationAssessment(bool Available, bool Truncated, string? Error);

/// <summary>Raw observations are kept separate from the rules that interpret them.</summary>
public sealed record TaskbarWindowObservation(
    string Hwnd, string ClassName, uint ProcessId, uint ThreadId, int? ProcessSessionId, string? IdentityError,
    bool? Visible, string? VisibilityError, bool? Cloaked, string? CloakError, bool? AutoHide, string? AutoHideError,
    bool? Responsive, int? ResponseElapsedMilliseconds, int? ResponseWin32Error,
    TaskbarBounds? Bounds, string? BoundsError,
    bool ChildEnumerationAvailable, string? ChildEnumerationError, bool ChildrenTruncated,
    IReadOnlyList<TaskbarChildObservation> Children, ProcessModuleObservation Modules);

public sealed record ShellWindowObservation(string? Hwnd, uint ProcessId, uint ThreadId, int? ProcessSessionId,
    string? Error);

public sealed record TaskbarDiagnosticsSnapshot(int? CurrentSessionId, int OsBuild, bool EnumerationAvailable,
    string? EnumerationError, ShellWindowObservation ShellWindow, IReadOnlyList<TaskbarWindowObservation> Taskbars,
    string? CurrentSessionError = null);

public sealed record TaskbarWindowReport(
    string Hwnd, string ClassName, uint ProcessId, uint ThreadId, int? ProcessSessionId,
    TaskbarSessionRelation SessionRelation, string? IdentityError,
    bool? Visible, string? VisibilityError, bool? Cloaked, string? CloakError, bool? AutoHide, string? AutoHideError,
    bool? Responsive, int? ResponseElapsedMilliseconds, int? ResponseWin32Error,
    TaskbarBounds? Bounds, string? BoundsError,
    bool ChildEnumerationAvailable, string? ChildEnumerationError, bool ChildrenTruncated,
    int ChildCount, IReadOnlyList<TaskbarChildObservation> Children,
    bool HasXamlBridge, bool HasTrayNotifyWindow, bool HasTaskList,
    ProcessModuleObservation Modules, TaskbarCheckStatus Status, IReadOnlyList<string> Findings);

public sealed record TaskbarDiagnosticsReport(
    string Scope, bool ReadOnly, bool WindowsStateChanged, bool RenderedPixelsVerified, bool ClickOperationsVerified,
    bool ServiceRunningUsedAsHealthEvidence, int? CurrentSessionId, int OsBuild, bool EnumerationAvailable,
    string? EnumerationError, ShellWindowObservation ShellWindow, ShellTaskbarIdentityStatus ShellTaskbarIdentity,
    TaskbarCheckStatus Status,
    IReadOnlyList<string> Findings, IReadOnlyList<TaskbarWindowReport> Taskbars, string? CurrentSessionError = null)
{
    public string Summary => Status switch
    {
        TaskbarCheckStatus.Healthy => "当前会话的任务栏结构、WM_NULL 响应和 ShellWindow PID/会话身份检查通过；未验证像素渲染或点击行为。",
        TaskbarCheckStatus.Degraded => "发现任务栏结构或响应异常；该检查未验证像素渲染或点击行为。",
        _ => "任务栏诊断信息不完整，无法确认结构与响应状态。"
    };
}

/// <summary>
/// Read-only taskbar structure and bounded WM_NULL responsiveness inspection. This does not inspect rendered
/// pixels or simulate clicks, and does not use service state as health evidence.
/// </summary>
public static class ShellTaskbarDiagnostics
{
    public const int SendMessageTimeoutMilliseconds = 1000;
    private const int Windows11Build = 22000;

    public static TaskbarDiagnosticsReport InspectCurrentSession() => Analyze(WindowsTaskbarObservationSource.Collect());
    public static TaskbarDiagnosticsReport Capture() => InspectCurrentSession();

    /// <summary>Checks a stable parent-window identity around child enumeration; EnumChildWindows' return is ignored by design.</summary>
    public static ChildEnumerationAssessment AssessChildEnumeration(bool rootWasWindow, uint processIdBefore, uint threadIdBefore,
        bool rootIsWindowAfter, uint processIdAfter, uint threadIdAfter, bool truncated, bool childIdentityFailed = false)
    {
        if (!rootWasWindow || processIdBefore == 0 || threadIdBefore == 0)
            return new(false, truncated, "taskbar-window-identity-unavailable-before-child-enumeration");
        if (!rootIsWindowAfter || processIdAfter == 0 || threadIdAfter == 0)
            return new(false, truncated, "taskbar-window-disappeared-during-child-enumeration");
        if (processIdBefore != processIdAfter || threadIdBefore != threadIdAfter)
            return new(false, truncated, "taskbar-window-identity-changed-during-child-enumeration");
        if (childIdentityFailed)
            return new(false, truncated, "one-or-more-child-window-identities-could-not-be-read");
        return new(true, truncated, truncated ? "child-window-enumeration-truncated-at-safety-limit" : null);
    }

    /// <summary>Classifies already-read module names and paths without touching a process or the filesystem.</summary>
    public static ProcessModuleObservation ClassifyProcessModules(IEnumerable<ProcessModuleCandidate> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var classicDeskModules = new List<ProcessModuleMatch>();
        var windhawkModules = new List<ProcessModuleMatch>();
        foreach (var module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.ModuleName) || string.IsNullOrWhiteSpace(module.FullPath) ||
                !Path.IsPathFullyQualified(module.FullPath))
                return new(ProcessModuleReadStatus.Unknown, classicDeskModules, windhawkModules,
                    "A loaded module name or full file path could not be read.");
            string fullPath = Path.GetFullPath(module.FullPath);
            if (TryGetClassicDeskSource(fullPath, out string source))
                classicDeskModules.Add(new(module.ModuleName, fullPath, source));
            if (module.ModuleName.Contains("Windhawk", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains("\\Windhawk\\", StringComparison.OrdinalIgnoreCase))
                windhawkModules.Add(new(module.ModuleName, fullPath, "Windhawk"));
        }
        return new(ProcessModuleReadStatus.Available, classicDeskModules, windhawkModules, null);
    }

    /// <summary>Pure classification function. Tests pass synthetic observations and never call Win32.</summary>
    public static TaskbarDiagnosticsReport Analyze(TaskbarDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reports = snapshot.Taskbars.Select(t => AnalyzeTaskbar(t, snapshot.CurrentSessionId, snapshot.OsBuild)).ToArray();
        var findings = new List<string>();

        var current = reports.Where(t => t.SessionRelation == TaskbarSessionRelation.Current).ToArray();
        var unknownSession = reports.Any(t => t.SessionRelation == TaskbarSessionRelation.Unknown);
        var mainCount = current.Count(t => string.Equals(t.ClassName, "Shell_TrayWnd", StringComparison.Ordinal));
        var mainTaskbars = current.Where(t => string.Equals(t.ClassName, "Shell_TrayWnd", StringComparison.Ordinal)).ToArray();
        if (!snapshot.EnumerationAvailable)
            findings.Add("taskbar-enumeration-incomplete");
        if (!snapshot.CurrentSessionId.HasValue)
            findings.Add(snapshot.CurrentSessionError is null ? "current-session-unknown" : "current-session-query-error");
        if (mainCount == 0)
            findings.Add(snapshot.EnumerationAvailable && !unknownSession ? "main-taskbar-not-found" : "main-taskbar-presence-unknown");
        else if (mainCount > 1)
            findings.Add("multiple-main-taskbars-in-current-session");

        var status = TaskbarCheckStatus.Healthy;
        if (!snapshot.EnumerationAvailable || (mainCount == 0 && unknownSession)) status = TaskbarCheckStatus.Unknown;
        else if (mainCount == 0 || mainCount > 1) status = TaskbarCheckStatus.Degraded;

        foreach (var taskbar in current)
            status = Combine(status, taskbar.Status);

        var shellIdentity = AssessShellTaskbarIdentity(snapshot.ShellWindow, mainTaskbars);
        if (shellIdentity == ShellTaskbarIdentityStatus.Unknown)
        {
            findings.Add("shell-window-identity-unknown");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }
        else if (shellIdentity == ShellTaskbarIdentityStatus.Mismatch)
        {
            findings.Add("main-taskbar-does-not-match-shell-window-pid-and-session");
            status = Combine(status, TaskbarCheckStatus.Degraded);
        }

        if (current.Length == 0 && unknownSession && status != TaskbarCheckStatus.Degraded)
            status = TaskbarCheckStatus.Unknown;

        return new TaskbarDiagnosticsReport(
            "当前登录会话的任务栏窗口结构、WM_NULL 响应及主任务栏与 GetShellWindow 的 PID/会话身份；不验证渲染像素或点击行为。",
            ReadOnly: true, WindowsStateChanged: false, RenderedPixelsVerified: false, ClickOperationsVerified: false,
            ServiceRunningUsedAsHealthEvidence: false, snapshot.CurrentSessionId, snapshot.OsBuild,
            snapshot.EnumerationAvailable, snapshot.EnumerationError, snapshot.ShellWindow, shellIdentity, status,
            findings, reports, snapshot.CurrentSessionError);
    }

    private static ShellTaskbarIdentityStatus AssessShellTaskbarIdentity(ShellWindowObservation shell,
        IReadOnlyList<TaskbarWindowReport> mainTaskbars)
    {
        if (shell.Hwnd is null || shell.ProcessId == 0 || shell.ThreadId == 0 ||
            !shell.ProcessSessionId.HasValue || shell.Error is not null || mainTaskbars.Count != 1)
            return ShellTaskbarIdentityStatus.Unknown;
        var main = mainTaskbars[0];
        if (main.ProcessId == 0 || main.ThreadId == 0 || !main.ProcessSessionId.HasValue || main.IdentityError is not null)
            return ShellTaskbarIdentityStatus.Unknown;
        return main.ProcessId == shell.ProcessId && main.ProcessSessionId == shell.ProcessSessionId
            ? ShellTaskbarIdentityStatus.Matched
            : ShellTaskbarIdentityStatus.Mismatch;
    }

    private static bool TryGetClassicDeskSource(string fullPath, out string source)
    {
        string normalized = fullPath.Replace('/', '\\');
        if (normalized.Contains("\\ClassicDesk\\ShellService\\", StringComparison.OrdinalIgnoreCase))
        {
            source = "ClassicDesk\\ShellService";
            return true;
        }
        if (normalized.Contains("\\ClassicDesk\\Runtime\\", StringComparison.OrdinalIgnoreCase))
        {
            source = "ClassicDesk\\Runtime";
            return true;
        }
        source = "";
        return false;
    }

    private static TaskbarWindowReport AnalyzeTaskbar(TaskbarWindowObservation item, int? currentSession, int osBuild)
    {
        var findings = new List<string>();
        var relation = item.ProcessId == 0 || !item.ProcessSessionId.HasValue || !currentSession.HasValue
            ? TaskbarSessionRelation.Unknown
            : item.ProcessSessionId.Value == currentSession ? TaskbarSessionRelation.Current : TaskbarSessionRelation.Other;
        var status = TaskbarCheckStatus.Healthy;

        if (relation == TaskbarSessionRelation.Other)
        {
            return MakeReport(item, relation, TaskbarCheckStatus.OutOfSession, ["taskbar-belongs-to-another-session"]);
        }
        if (relation == TaskbarSessionRelation.Unknown)
        {
            findings.Add(item.ProcessId == 0 || item.ThreadId == 0 ? "taskbar-window-identity-unavailable" : "taskbar-session-unknown");
            if (item.IdentityError is not null) findings.Add("taskbar-identity-query-error");
            status = TaskbarCheckStatus.Unknown;
        }
        if (item.ProcessId == 0 || item.ThreadId == 0)
        {
            findings.Add("taskbar-window-identity-invalid");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }

        if (!item.ChildEnumerationAvailable)
        {
            findings.Add("child-window-enumeration-unknown");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }
        else if (item.ChildrenTruncated)
        {
            findings.Add("child-window-enumeration-truncated");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }
        else
        {
            if (item.Children.Count == 0)
            {
                findings.Add("no-child-windows-observed");
                status = Combine(status, TaskbarCheckStatus.Degraded);
            }
            bool hasTaskList = item.Children.Any(c => IsTaskList(c.ClassName));
            bool hasXaml = item.Children.Any(c => IsXamlBridge(c.ClassName));
            if (!hasTaskList)
            {
                findings.Add("task-list-window-not-found");
                status = Combine(status, TaskbarCheckStatus.Degraded);
            }
            if (osBuild >= Windows11Build && !hasXaml)
            {
                findings.Add("xaml-composition-bridge-not-found");
                status = Combine(status, TaskbarCheckStatus.Degraded);
            }
        }

        if (item.Responsive == false)
        {
            findings.Add("wm-null-did-not-respond-within-timeout");
            status = Combine(status, TaskbarCheckStatus.Degraded);
        }
        else if (item.Responsive is null)
        {
            findings.Add("wm-null-response-unknown");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }

        if (item.Visible is null)
        {
            findings.Add("visibility-unknown");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }
        else if (!item.Visible.Value)
        {
            if (item.AutoHide == true)
                findings.Add("hidden-while-auto-hide-enabled");
            else if (item.AutoHide == false)
            {
                findings.Add("hidden-while-auto-hide-disabled");
                status = Combine(status, TaskbarCheckStatus.Degraded);
            }
            else
            {
                findings.Add("hidden-with-auto-hide-state-unknown");
                status = Combine(status, TaskbarCheckStatus.Unknown);
            }
        }

        if (item.Cloaked == true && item.AutoHide != true)
        {
            findings.Add("taskbar-window-is-cloaked");
            status = Combine(status, TaskbarCheckStatus.Degraded);
        }
        else if (item.Cloaked is null)
        {
            findings.Add("cloaking-state-unknown");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }

        if (item.AutoHide is null)
        {
            findings.Add("auto-hide-state-unknown");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }

        if (item.Bounds is null)
        {
            findings.Add("physical-bounds-unknown");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }
        else if (!string.Equals(item.Bounds.CoordinateSpace, "physical", StringComparison.OrdinalIgnoreCase) || item.Bounds.Dpi == 0)
        {
            findings.Add("physical-bounds-not-confirmed");
            status = Combine(status, TaskbarCheckStatus.Unknown);
        }

        return MakeReport(item, relation, status, findings);
    }

    private static TaskbarWindowReport MakeReport(TaskbarWindowObservation item, TaskbarSessionRelation relation,
        TaskbarCheckStatus status, IReadOnlyList<string> findings)
    {
        bool hasXaml = item.Children.Any(c => IsXamlBridge(c.ClassName));
        bool hasTray = item.Children.Any(c => c.ClassName.Contains("TrayNotifyWnd", StringComparison.OrdinalIgnoreCase));
        bool hasTaskList = item.Children.Any(c => IsTaskList(c.ClassName));
        return new(item.Hwnd, item.ClassName, item.ProcessId, item.ThreadId, item.ProcessSessionId, relation,
            item.IdentityError, item.Visible, item.VisibilityError, item.Cloaked, item.CloakError,
            item.AutoHide, item.AutoHideError, item.Responsive, item.ResponseElapsedMilliseconds,
            item.ResponseWin32Error, item.Bounds, item.BoundsError, item.ChildEnumerationAvailable,
            item.ChildEnumerationError, item.ChildrenTruncated, item.Children.Count, item.Children, hasXaml, hasTray,
            hasTaskList, item.Modules, status, findings);
    }

    private static bool IsTaskList(string className) =>
        className.Contains("TaskList", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("MSTaskListWClass", StringComparison.OrdinalIgnoreCase);

    private static bool IsXamlBridge(string className) =>
        className.Contains("DesktopWindowContentBridge", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Xaml", StringComparison.OrdinalIgnoreCase);

    private static TaskbarCheckStatus Combine(TaskbarCheckStatus left, TaskbarCheckStatus right)
    {
        if (left == TaskbarCheckStatus.Degraded || right == TaskbarCheckStatus.Degraded) return TaskbarCheckStatus.Degraded;
        if (left == TaskbarCheckStatus.Unknown || right == TaskbarCheckStatus.Unknown) return TaskbarCheckStatus.Unknown;
        return TaskbarCheckStatus.Healthy;
    }
}

internal static class WindowsTaskbarObservationSource
{
    private const int MaximumChildren = 4096;
    private const uint WmNull = 0x0000;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoErrorOnExit = 0x0020;
    private const uint DwmwaCloaked = 14;
    private const uint AbmGetState = 0x00000004;
    private const uint AbsAutoHide = 0x00000001;
    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    internal static TaskbarDiagnosticsSnapshot Collect()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("任务栏诊断仅支持 Windows。");
        int? session = ReadProcessSessionId(unchecked((uint)Environment.ProcessId), out string? sessionError);
        int build = Environment.OSVersion.Version.Build;
        var handles = new List<(nint Handle, string ClassName)>();
        bool enumOk = true;
        string? enumError = null;
        bool classReadFailed = false;

        Marshal.SetLastPInvokeError(0);
        bool topResult = EnumWindows((window, _) =>
        {
            string? className = ReadClassName(window);
            if (className is null) { classReadFailed = true; return true; }
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") handles.Add((window, className));
            return true;
        }, 0);
        int topError = Marshal.GetLastPInvokeError();
        if (!topResult || classReadFailed)
        {
            enumOk = false;
            enumError = !topResult ? FormatWin32Error(topError) : "一个或多个顶层窗口类名读取失败。";
        }

        bool? autoHide = ReadAutoHide(out string? autoHideError);
        var taskbars = handles.Select(h => CollectTaskbar(h.Handle, h.ClassName, autoHide, autoHideError)).ToArray();
        var shell = CollectShellWindow();
        return new(session, build, enumOk, enumError, shell, taskbars, sessionError);
    }

    private static TaskbarWindowObservation CollectTaskbar(nint handle, string className, bool? autoHide, string? autoHideError)
    {
        bool rootWasWindow = handle != 0 && IsWindow(handle);
        uint threadId = GetWindowThreadProcessId(handle, out uint pid);
        int? processSession = null;
        string? identityError = null;
        processSession = ReadProcessSessionId(pid, out identityError);

        var children = new List<TaskbarChildObservation>();
        bool truncated = false;
        bool childIdentityFailed = false;
        if (rootWasWindow && pid != 0 && threadId != 0)
        {
            // EnumChildWindows' BOOL return is documented as not used. Stability checks below distinguish
            // a valid empty subtree from a taskbar window that vanished while it was being inspected.
            _ = EnumChildWindows(handle, (child, _) =>
            {
                if (children.Count >= MaximumChildren) { truncated = true; return false; }
                bool childWasWindow = IsWindow(child);
                string? observedClass = ReadClassName(child);
                string childClass = observedClass ?? "<class-unavailable>";
                uint childThread = GetWindowThreadProcessId(child, out uint childPid);
                nint parent = GetParent(child);
                if (!childWasWindow || observedClass is null || childThread == 0 || childPid == 0 || parent == 0 || !IsWindow(child))
                    childIdentityFailed = true;
                children.Add(new(FormatHandle(child), FormatHandle(parent), childClass, childPid, childThread));
                return true;
            }, 0);
        }
        bool rootIsWindowAfter = handle != 0 && IsWindow(handle);
        uint threadIdAfter = GetWindowThreadProcessId(handle, out uint pidAfter);
        var childAssessment = ShellTaskbarDiagnostics.AssessChildEnumeration(rootWasWindow, pid, threadId,
            rootIsWindowAfter, pidAfter, threadIdAfter, truncated, childIdentityFailed);
        truncated = childAssessment.Truncated;

        bool? visible = null;
        string? visibilityError = null;
        try { visible = IsWindowVisible(handle); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { visibilityError = SafeError(e); }

        bool? cloaked = null;
        string? cloakError = null;
        try
        {
            int value = 0;
            int hr = DwmGetWindowAttribute(handle, DwmwaCloaked, out value, Marshal.SizeOf<int>());
            if (hr >= 0) cloaked = value != 0;
            else cloakError = $"HRESULT 0x{hr:X8}";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { cloakError = SafeError(e); }

        bool? responsive = null;
        int? responseError = null;
        int? elapsed = null;
        try
        {
            var timer = Stopwatch.StartNew();
            nint result = SendMessageTimeout(handle, WmNull, 0, 0,
                SmtoAbortIfHung | SmtoBlock | SmtoErrorOnExit,
                ShellTaskbarDiagnostics.SendMessageTimeoutMilliseconds, out _);
            timer.Stop(); elapsed = (int)Math.Min(timer.ElapsedMilliseconds, int.MaxValue);
            responsive = result != 0;
            if (!responsive.Value) responseError = Marshal.GetLastPInvokeError();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        { responseError = e.HResult; }

        TaskbarBounds? bounds = ReadPhysicalBounds(handle, out string? boundsError);
        var modules = ReadRelevantModules(pid);
        return new(FormatHandle(handle), className, pid, threadId, processSession, identityError,
            visible, visibilityError, cloaked, cloakError, autoHide, autoHideError,
            responsive, elapsed, responseError, bounds, boundsError, childAssessment.Available, childAssessment.Error,
            truncated, children, modules);
    }

    private static ShellWindowObservation CollectShellWindow()
    {
        try
        {
            nint handle = GetShellWindow();
            if (handle == 0) return new(null, 0, 0, null, "GetShellWindow returned no shell window.");
            uint thread = GetWindowThreadProcessId(handle, out uint pid);
            int? session = ReadProcessSessionId(pid, out string? error);
            return new(FormatHandle(handle), pid, thread, session, error);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        { return new(null, 0, 0, null, SafeError(e)); }
    }

    private static bool? ReadAutoHide(out string? error)
    {
        try
        {
            var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
            uint state = SHAppBarMessage(AbmGetState, ref data);
            error = null;
            return (state & AbsAutoHide) != 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        { error = SafeError(e); return null; }
    }

    private static TaskbarBounds? ReadPhysicalBounds(nint handle, out string? error)
    {
        nint previousContext;
        Marshal.SetLastPInvokeError(0);
        try { previousContext = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        { error = SafeError(e); return null; }
        if (previousContext == 0)
        {
            error = "无法临时切换到 Per-Monitor-V2 DPI 上下文，未声称坐标为物理像素。 " + FormatWin32Error(Marshal.GetLastPInvokeError());
            return null;
        }

        TaskbarBounds? bounds = null;
        string? boundsError = null;
        try
        {
            Marshal.SetLastPInvokeError(0);
            if (!GetWindowRect(handle, out var rect))
            {
                boundsError = FormatWin32Error(Marshal.GetLastPInvokeError());
            }
            else
            {
                Marshal.SetLastPInvokeError(0);
                uint dpi = GetDpiForWindow(handle);
                if (dpi == 0) boundsError = FormatWin32Error(Marshal.GetLastPInvokeError());
                else bounds = new(rect.Left, rect.Top, rect.Right, rect.Bottom, dpi, "physical");
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        { boundsError = SafeError(e); }
        finally
        {
            try
            {
                if (SetThreadDpiAwarenessContext(previousContext) == 0)
                {
                    bounds = null;
                    boundsError = "无法恢复原线程 DPI 上下文；物理边界不予报告。";
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                bounds = null;
                boundsError = SafeError(e);
            }
        }
        error = boundsError;
        return bounds;
    }

    private static ProcessModuleObservation ReadRelevantModules(uint pid)
    {
        if (pid == 0)
            return new(ProcessModuleReadStatus.Unknown, [], [], "HWND 所属 PID 不可用。");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var candidates = new List<ProcessModuleCandidate>();
            foreach (ProcessModule module in process.Modules)
            {
                string moduleName = module.ModuleName;
                string path = module.FileName;
                candidates.Add(new(moduleName, path));
            }
            return ShellTaskbarDiagnostics.ClassifyProcessModules(candidates);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or Win32Exception or InvalidOperationException or
                                  ArgumentException or NotSupportedException or IOException)
        {
            return new(ProcessModuleReadStatus.Unknown, [], [], SafeError(e));
        }
    }

    private static int? ReadProcessSessionId(uint processId, out string? error)
    {
        if (processId == 0)
        {
            error = "GetWindowThreadProcessId returned PID 0.";
            return null;
        }
        try
        {
            Marshal.SetLastPInvokeError(0);
            if (!ProcessIdToSessionId(processId, out uint sessionId))
            {
                error = FormatWin32Error(Marshal.GetLastPInvokeError());
                return null;
            }
            if (sessionId > int.MaxValue)
            {
                error = "ProcessIdToSessionId returned an unsupported session identifier.";
                return null;
            }
            error = null;
            return (int)sessionId;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            error = SafeError(e);
            return null;
        }
    }

    private static string? ReadClassName(nint handle)
    {
        var buffer = new StringBuilder(256);
        int count = GetClassName(handle, buffer, buffer.Capacity);
        return count == 0 ? null : buffer.ToString();
    }

    private static string FormatHandle(nint handle) => $"0x{unchecked((ulong)handle.ToInt64()):X}";
    private static string FormatWin32Error(int code) => $"Win32 error {code} (0x{code:X8})";
    private static string SafeError(Exception e) => e is Win32Exception w ? FormatWin32Error(w.NativeErrorCode) : e.GetType().Name;

    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rect;
        public nint Parameter;
    }
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    private struct NativeWindowRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetClassName(nint window, StringBuilder className, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message,
        nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeWindowRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint message, ref AppBarData data);
}
