using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClassicDesk;

public enum ExplorerFrameKind { Unknown, Ribbon, Modern, Mixed }
public sealed record ExplorerRect(int Left, int Top, int Right, int Bottom)
{
    public bool HasArea => Right > Left && Bottom > Top;
}
public sealed record ExplorerChild(string ClassName, uint ProcessId, bool Visible, ExplorerRect? Bounds);
public sealed record ExplorerWindowObservation(long Hwnd, uint ProcessId, uint ThreadId, int SessionId,
    long ProcessCreationTime, bool IdentityVerified, bool Visible, bool Minimized, bool? Responsive,
    bool ChildrenComplete, ExplorerRect? Bounds, IReadOnlyList<ExplorerChild> Children, string? IdentityError = null,
    int? ResponseWin32Error = null, long? ResponseElapsedMilliseconds = null, int? ResponseTimeoutMilliseconds = null);
public sealed record ExplorerWindowReport(ExplorerWindowObservation Observation, ExplorerFrameKind Frame, string Detail);
public sealed record ExplorerDiagnosticsReport(bool Complete, IReadOnlyList<ExplorerWindowReport> Windows)
{
    public bool ClicksVerified => false;
    public bool DragVerified => false;
    public string Summary => !Complete ? "文件窗口信息不完整，不能确认布局。" : Windows.Count == 0
        ? "当前未发现文件窗口，尚未验证 Win10 布局。"
        : string.Join("\n", Windows.Select(w => $"窗口 {w.Observation.Hwnd:X}：{w.Detail}"));
}

/// <summary>Observes file-window structure, never opens/closes windows or reads folder names.
/// Ribbon structure is only a prerequisite for subsequent mouse and keyboard acceptance.</summary>
public static class ShellExplorerDiagnostics
{
    public static ExplorerDiagnosticsReport Capture() => WindowsExplorerObservationSource.Capture();

    public static bool HasSingleClassicFolderTopology(ExplorerWindowObservation w)
    {
        if (!w.IdentityVerified || !w.ChildrenComplete || !w.Visible || w.Minimized ||
            w.Responsive != true || w.Bounds is not { HasArea: true }) return false;
        var owned = w.Children.Where(c => c.ProcessId == w.ProcessId).ToArray();
        if (owned.Any(c => c.Visible && c.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge" &&
            c.Bounds is not { HasArea: true })) return false;
        var visible = owned.Where(c => c.Visible && c.Bounds is { HasArea: true }).ToArray();
        int? contentTop = visible.Where(c => c.ClassName == "SHELLDLL_DefView").Select(c => (int?)c.Bounds!.Top).Min();
        if (!contentTop.HasValue || contentTop <= w.Bounds.Top) return false;
        bool InTop(ExplorerChild c) => c.Bounds!.Top >= w.Bounds.Top && c.Bounds.Top < contentTop &&
            c.Bounds.Bottom <= contentTop && c.Bounds.Left < w.Bounds.Right && c.Bounds.Right > w.Bounds.Left;
        // The details pane uses the same XAML bridge class as the modern header.
        // Only verified top-frame geometry can disqualify a classic single-folder window.
        return ShellExplorerTabEvidence.IsSingleClassicFolder(true,
            owned.Count(c => c.ClassName == "ShellTabWindowClass"),
            visible.Any(c => c.ClassName == "UIRibbonCommandBar" && InTop(c)),
            visible.Any(c => c.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge" && InTop(c)),
            owned.Any(c => c.ClassName == "TITLE_BAR_SCAFFOLDING_WINDOW_CLASS"));
    }

    public static ExplorerWindowReport Analyze(ExplorerWindowObservation w)
    {
        if (!w.IdentityVerified || !w.ChildrenComplete || !w.Visible || w.Minimized || w.Bounds is not { HasArea: true })
            return new(w, ExplorerFrameKind.Unknown, "身份或窗口结构未确认，隐藏/最小化窗口不计为布局通过。");
        if (w.Responsive != true)
            return new(w, ExplorerFrameKind.Unknown, "窗口响应未确认，不能确认布局可用。");

        var children = w.Children.Where(c => c.Visible && c.ProcessId == w.ProcessId && c.Bounds is { HasArea: true }).ToArray();
        var contentTop = children.Where(c => c.ClassName == "SHELLDLL_DefView")
            .Select(c => (int?)c.Bounds!.Top).Min();
        if (!contentTop.HasValue || contentTop <= w.Bounds.Top)
            return new(w, ExplorerFrameKind.Unknown, "文件内容区域未确认，不能把空白窗口算作已重建。");
        // A XAML details pane also has a DesktopChildSiteBridge. Only a bridge
        // strictly above the file view is evidence of a modern top frame.
        bool InTop(ExplorerChild c) => c.Bounds!.Top >= w.Bounds.Top && c.Bounds.Top < contentTop &&
            c.Bounds.Bottom <= contentTop && c.Bounds.Left < w.Bounds.Right && c.Bounds.Right > w.Bounds.Left;
        bool ribbon = children.Any(c => c.ClassName == "UIRibbonCommandBar" && InTop(c));
        bool address = children.Any(c => c.ClassName == "Address Band Root" && InTop(c));
        bool travel = children.Any(c => c.ClassName == "TravelBand" && InTop(c));
        bool search = children.Any(c => c.ClassName == "UniversalSearchBand" && InTop(c));
        bool modern = children.Any(c => c.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge" && InTop(c));
        if (ribbon && modern) return new(w, ExplorerFrameKind.Mixed, "同时检测到 Ribbon 和现代顶栏，布局仍需重建。");
        if (modern) return new(w, ExplorerFrameKind.Modern, "仍为现代顶栏，未达到 Win10 功能区目标。");
        if (ribbon && (!address || !travel || !search))
        {
            string[] required = ["Address Band Root", "TravelBand", "UniversalSearchBand"];
            int existing = required.Count(name => w.Children.Any(c => c.ProcessId == w.ProcessId && c.ClassName == name));
            return new(w, ExplorerFrameKind.Unknown, existing == 0
                ? "Win10 功能区仍在，但当前窗口不存在地址栏、导航按钮和搜索栏控件；不能按隐藏或重绘故障处理，不能计为通过。"
                : "Win10 功能区仍在，但地址栏、导航按钮或搜索栏部分缺失/隐藏或位置异常；不能计为通过。");
        }
        if (ribbon && address && travel && search)
            return new(w, ExplorerFrameKind.Ribbon, "Win10 功能区、地址栏和导航结构已出现；拖动和顶部点击尚未验证。");
        return new(w, ExplorerFrameKind.Unknown, "顶部结构不完整或无法识别，未判定为 Win10 布局。");
    }
}

internal static class WindowsExplorerObservationSource
{
    const int MaxWindows = 64, MaxChildren = 4096, BudgetMs = 2000;
    internal static ExplorerDiagnosticsReport Capture()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint session))
            return new(false, []);
        var handles = new List<nint>();
        bool complete = true;
        var clock = Stopwatch.StartNew();
        if (!EnumWindows((h, _) =>
        {
            string name = Class(h);
            if (name is "CabinetWClass" or "ExploreWClass")
            {
                if (handles.Count == MaxWindows) { complete = false; return false; }
                handles.Add(h);
            }
            return true;
        }, 0)) complete = false;

        nint previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) return new(false, []);
        var windows = new List<ExplorerWindowReport>();
        try
        {
            foreach (var handle in handles)
            {
                if (clock.ElapsedMilliseconds >= BudgetMs) { complete = false; break; }
                uint tid = GetWindowThreadProcessId(handle, out uint pid);
                int windowSession = -1;
                long creation = 0;
                bool identity = false;
                string? identityError = null;
                string identityStage = "process-open-limited";
                using var process = OpenProcess(0x1000, false, pid);
                try
                {
                    if (process.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    identityStage = "process-session";
                    // Query the PID directly: managed Process.SessionId can report a
                    // live Explorer as exited on this host. Never infer the session.
                    if (!ProcessIdToSessionId(pid, out uint observedSession))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if (observedSession > int.MaxValue) throw new InvalidOperationException("Unsupported session identifier.");
                    windowSession = (int)observedSession;
                    if (observedSession != session) continue;
                    identityStage = "process-creation-and-image";
                    if (!GetProcessTimes(process, out creation, out _, out _, out _))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    var image = new StringBuilder(32768);
                    uint imageSize = (uint)image.Capacity;
                    if (!QueryFullProcessImageName(process, 0, image, ref imageSize))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    identity = creation > 0 && string.Equals(image.ToString(),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                        StringComparison.OrdinalIgnoreCase) && GetExitCodeProcess(process, out uint exitCode) && exitCode == 259;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
                { complete = false; identityError = identityStage + ":" + e.GetType().Name +
                    (e is System.ComponentModel.Win32Exception nativeError ? $":{nativeError.NativeErrorCode}" : ""); }

                var bounds = Rect(handle);
                bool visible = IsWindowVisible(handle), minimized = IsIconic(handle);
                var children = new List<ExplorerChild>();
                bool childrenComplete = true;
                _ = EnumChildWindows(handle, (h, _) =>
                {
                    if (children.Count >= MaxChildren || clock.ElapsedMilliseconds >= BudgetMs)
                    { childrenComplete = false; return false; }
                    var name = Class(h);
                    uint childThread = GetWindowThreadProcessId(h, out uint childPid);
                    var rect = Rect(h);
                    if (name.Length == 0 || childThread == 0 || childPid != pid || rect is null)
                        childrenComplete = false;
                    children.Add(new(name, childPid, IsWindowVisible(h), rect));
                    return true;
                }, 0);
                int remaining = BudgetMs - (int)clock.ElapsedMilliseconds;
                bool? responsive = null;
                int? responseError = null, responseTimeout = null;
                long? responseElapsed = null;
                if (remaining > 0)
                {
                    responseTimeout = Math.Min(200, remaining);
                    var responseClock = Stopwatch.StartNew();
                    Marshal.SetLastPInvokeError(0);
                    responsive = SendMessageTimeout(handle, 0, 0, 0,
                        0x23, (uint)responseTimeout.Value, out _) != 0;
                    responseError = responsive == true ? null : Marshal.GetLastPInvokeError();
                    responseElapsed = responseClock.ElapsedMilliseconds;
                }
                uint afterTid = GetWindowThreadProcessId(handle, out uint afterPid);
                identity = identity && tid != 0 && tid == afterTid && pid == afterPid && IsWindow(handle) &&
                    GetExitCodeProcess(process, out uint finalExitCode) && finalExitCode == 259;
                complete &= identity && childrenComplete;
                windows.Add(ShellExplorerDiagnostics.Analyze(new(handle.ToInt64(), pid, tid, windowSession, creation,
                    identity, visible, minimized, responsive, childrenComplete, bounds, children, identityError,
                    responseError, responseElapsed, responseTimeout)));
            }
        }
        finally { if (SetThreadDpiAwarenessContext(previousDpi) == 0) complete = false; }
        return new(complete, windows);
    }

    static string Class(nint h) { var s = new StringBuilder(256); return GetClassName(h, s, s.Capacity) > 0 ? s.ToString() : ""; }
    static ExplorerRect? Rect(nint h) => GetWindowRect(h, out var r) ? new(r.Left, r.Top, r.Right, r.Bottom) : null;
    delegate bool Enumerate(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumWindows(Enumerate callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumChildWindows(nint parent, Enumerate callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder value, int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryFullProcessImageName(SafeProcessHandle process,
        uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SendMessageTimeout(nint window, uint message, nuint wparam, nint lparam, uint flags, uint milliseconds, out nuint result);
}
