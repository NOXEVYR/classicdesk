using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ClassicDesk;

public sealed record ShellServiceSessionInstallation(string Root, string ReceiptSha256, ShellProfile Profile, bool Pending);
public sealed record ShellServiceExplorerIdentity(uint ProcessId, uint SessionId, long CreationTimeFileTime);
public sealed record ShellServiceSessionReview(bool CanStart, string State, string Detail,
    string? Installation, string? ReceiptSha256, ShellProfile? RegisteredProfile, ShellProfile? CurrentDraft,
    bool? DraftDiffers, bool Pending, TaskbarCheckStatus? TaskbarStatus,
    ShellTaskbarIdentityStatus? ShellTaskbarIdentity, string? CompatibilityState, string? Fingerprint,
    ShellServiceExplorerIdentity? Explorer);
public sealed record ShellServiceSessionRequest(string Installation, string ReceiptSha256, ShellProfile Profile,
    ShellServiceExplorerIdentity Explorer, string Fingerprint);
public sealed record ShellServiceSessionHelperResult(bool Completed, bool Succeeded, int? ExitCode,
    bool Cancelled, string? Error, string? PreparationDetail = null);
public sealed record ShellServiceSessionPostStart(ShellServiceObservation Service, string Installation,
    uint ServiceProcessId, string NativeState, uint NativeProcessId, int NativeError,
    string NativeStartMode, uint NativeSessionId, uint NativeExplorerPid);
public sealed record ShellServiceSessionResult(string State, bool? CurrentSessionStarted, bool? StartupDisabled,
    bool? ServiceRunning, bool? StartupAcceptanceWritten, bool VisualEffectVerified, string Detail,
    string? Installation = null, string? ReceiptSha256 = null, ShellProfile? RegisteredProfile = null,
    uint? ExplorerPid = null, uint? SessionId = null, long? ExplorerCreationTimeFileTime = null,
    string? Fingerprint = null, int? HelperExitCode = null)
{
    public bool ConfirmedStarted => State == ShellServiceSessionOperations.RunningState &&
        CurrentSessionStarted == true && StartupDisabled == true && ServiceRunning == true && !VisualEffectVerified;
}

public interface IShellServiceSessionOperations
{
    Task<ShellServiceSessionReview> ReviewAsync(ShellProfile? currentDraft = null);
    Task<ShellServiceSessionResult> StartAsync(ShellServiceSessionReview review);
    Task<ShellServiceSessionResult> StartCurrentSessionAsync(ShellProfile? currentDraft = null);
}

public interface IShellServiceSessionHost
{
    ShellServiceObservation ReadService();
    Task<ShellServiceSessionInstallation> ReadInstallationAsync(ShellServiceObservation service);
    TaskbarDiagnosticsReport InspectTaskbar();
    ShellServiceExplorerIdentity ReadExplorerIdentity(TaskbarDiagnosticsReport taskbar);
    Task<(string State, string Fingerprint, bool EngineStarted, bool AcceptanceWritten)> InspectCompatibilityAsync(string root);
    Task<ShellServiceSessionHelperResult> StartCurrentSessionAsync(ShellServiceSessionRequest request);
    ShellServiceSessionPostStart ReadPostStart();
}

public sealed class ShellServiceSessionOperations(IShellServiceSessionHost host) : IShellServiceSessionOperations
{
    public const string RunningState = "running-current-session-unverified";
    private static readonly Regex FingerprintPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    public static IShellServiceSessionOperations CreateDefault() => new ShellServiceSessionOperations(new WindowsShellServiceSessionHost());

    public async Task<ShellServiceSessionReview> ReviewAsync(ShellProfile? currentDraft = null)
    {
        currentDraft?.Validate();
        var service = host.ReadService();
        if (!service.Registered) return Blocked("service-not-registered", "未登记正式系统服务。");
        if (service.StartMode != 4 || service.State != 1)
            return Blocked("service-not-stopped-disabled", "本次启动要求服务为 Stopped 且启动类型 Disabled；本入口不会更改开机启动设置。");

        var installation = await host.ReadInstallationAsync(service).ConfigureAwait(false);
        if (installation.Pending)
            return Blocked("pending-installation-change", "登记安装存在待完成的更新，暂不启动本次会话。请先重新检查开机方案。", installation, currentDraft);

        var taskbar = host.InspectTaskbar();
        string? taskbarProblem = TaskbarProblem(taskbar);
        if (taskbarProblem is not null)
            return Blocked("taskbar-preflight-failed", taskbarProblem, installation, currentDraft, taskbar);

        var explorer = host.ReadExplorerIdentity(taskbar);
        if (!MatchesExplorer(taskbar, explorer))
            return Blocked("explorer-identity-mismatch", "当前 Explorer PID、会话或创建时间与已检查任务栏不一致；本次未启动。", installation, currentDraft, taskbar, explorer);

        var compatibility = await host.InspectCompatibilityAsync(installation.Root).ConfigureAwait(false);
        if (!FingerprintPattern.IsMatch(compatibility.Fingerprint) || compatibility.EngineStarted || compatibility.AcceptanceWritten)
            return Blocked("fingerprint-unavailable", "原生只读检查未返回可信 fingerprint，或报告了引擎/验收写入；本次未启动。", installation, currentDraft, taskbar, explorer, compatibility.State, compatibility.Fingerprint);

        bool? differs = currentDraft is null ? null : currentDraft != installation.Profile;
        var detail = "本次将按安装记录中已登记的开机方案启动。";
        if (differs == true) detail += " 当前草稿与登记方案不同，本次只使用已登记方案，不会应用草稿。";
        detail += $" 原生状态为 {compatibility.State}；fingerprint 只绑定本次启动，不表示兼容验收。启动后仍需检查桌面效果。";
        return new(true, "ready-current-session", detail, installation.Root, installation.ReceiptSha256,
            installation.Profile, currentDraft, differs, false, taskbar.Status, taskbar.ShellTaskbarIdentity,
            compatibility.State, compatibility.Fingerprint, explorer);
    }

    public async Task<ShellServiceSessionResult> StartCurrentSessionAsync(ShellProfile? currentDraft = null)
    {
        var review = await ReviewAsync(currentDraft).ConfigureAwait(false);
        return review.CanStart ? await StartAsync(review).ConfigureAwait(false) : FromReview(review);
    }

    public async Task<ShellServiceSessionResult> StartAsync(ShellServiceSessionReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!review.CanStart) return FromReview(review);
        var current = await ReviewAsync(review.CurrentDraft).ConfigureAwait(false);
        if (!current.CanStart) return FromReview(current with { State = "preflight-changed", Detail = "启动前复核未通过：" + current.Detail });
        if (!SameReview(review, current))
            return FromReview(current with { CanStart = false, State = "preflight-changed",
                Detail = "安装记录、任务栏/Explorer 身份或 fingerprint 已变化；本次未启动，请重新检查。" });

        var request = new ShellServiceSessionRequest(current.Installation!, current.ReceiptSha256!,
            current.RegisteredProfile!, current.Explorer!, current.Fingerprint!);
        ShellServiceSessionHelperResult helper;
        try { helper = await host.StartCurrentSessionAsync(request).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            return MakeResult("start-outcome-unknown", null, null, null, null,
                "启动操作没有得到可核对的结果；未自动重试，请重新检查服务状态和桌面。" + ErrorText(e), current);
        }
        if (helper.Cancelled)
            return MakeResult("administrator-authorization-cancelled", false, null, null, null, "已取消管理员授权，本次未提交启动请求。" + helper.PreparationDetail, current);
        if (!helper.Completed)
            return MakeResult("start-helper-timeout", null, null, null, null, "启动辅助程序等待超时；服务可能仍在处理，本次不会自动重试，请重新检查。" + helper.PreparationDetail, current);
        if (!helper.Succeeded)
            return MakeResult("start-helper-failed", false, null, null, null,
                $"启动辅助程序失败（{helper.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "未知"}）；未报告为成功。" +
                (string.IsNullOrWhiteSpace(helper.Error) ? "" : " " + helper.Error) + helper.PreparationDetail, current);

        string? explorerProblem = CurrentExplorerProblem(current.Explorer!);
        if (explorerProblem is not null)
            return MakeResult("start-result-unverified", null, null, null, null,
                "启动辅助程序已返回，但" + explorerProblem + "；不能确认本次启动仍对应检查时的 Explorer。请重新检查服务和桌面；不会自动重试。", current);

        ShellServiceSessionPostStart? post = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                post = host.ReadPostStart();
                if (IsConfirmedPostStart(post, current.Installation!, current.Explorer!)) break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            {
                // ServiceMain state is allowed a short, fixed time to become observable.
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        if (post is null || !IsConfirmedPostStart(post, current.Installation!, current.Explorer!))
            return MakeResult("start-result-unverified", null, null, null, null,
                "辅助程序已返回，但未核对到同一安装的 Running+Disabled 及原生当前会话状态/PID；不能确认启动结果，不会重试。", current);

        explorerProblem = CurrentExplorerProblem(current.Explorer!);
        if (explorerProblem is not null)
            return MakeResult("start-result-unverified", null, null, null, null,
                "SCM 与原生状态已报告启动，但" + explorerProblem + "；不能确认当前会话仍绑定到检查时的 Explorer。请重新检查服务和桌面；不会自动重试。", current);

        return MakeResult(RunningState, true, true, true, false,
            "本次增强已启动，开机仍停用。SCM 与原生状态确认当前会话启动，且 Explorer PID、会话和创建时间仍与检查时一致；像素渲染和点击效果仍需检查。" + helper.PreparationDetail, current);
    }

    public static string? TaskbarProblem(TaskbarDiagnosticsReport taskbar)
    {
        if (taskbar.Status != TaskbarCheckStatus.Healthy) return "当前任务栏结构或响应检查未通过；本次启动要求 Healthy。";
        if (taskbar.ShellTaskbarIdentity != ShellTaskbarIdentityStatus.Matched) return "主任务栏与 GetShellWindow 的 PID/session 身份未确认一致。";
        if (!taskbar.CurrentSessionId.HasValue) return "无法确认当前登录会话 ID。";
        var main = taskbar.Taskbars.Where(t => t.ClassName == "Shell_TrayWnd" && t.SessionRelation == TaskbarSessionRelation.Current).ToArray();
        if (main.Length != 1 || main[0].Status != TaskbarCheckStatus.Healthy || main[0].ProcessId == 0 ||
            main[0].ProcessSessionId != taskbar.CurrentSessionId ||
            taskbar.ShellWindow.ProcessId != main[0].ProcessId || taskbar.ShellWindow.ProcessSessionId != taskbar.CurrentSessionId)
            return "当前主任务栏的 PID/session 不匹配或不唯一。";
        return null;
    }

    private static bool MatchesExplorer(TaskbarDiagnosticsReport taskbar, ShellServiceExplorerIdentity explorer) =>
        taskbar.CurrentSessionId.HasValue && explorer.ProcessId != 0 &&
        explorer.SessionId == (uint)taskbar.CurrentSessionId.Value && explorer.CreationTimeFileTime > 0 &&
        taskbar.Taskbars.Count(t => t.ClassName == "Shell_TrayWnd" && t.SessionRelation == TaskbarSessionRelation.Current &&
            t.ProcessId == explorer.ProcessId) == 1;

    private string? CurrentExplorerProblem(ShellServiceExplorerIdentity expected)
    {
        try
        {
            var taskbar = host.InspectTaskbar();
            string? taskbarProblem = TaskbarProblem(taskbar);
            if (taskbarProblem is not null) return taskbarProblem;
            var observed = host.ReadExplorerIdentity(taskbar);
            if (!MatchesExplorer(taskbar, expected) || observed != expected)
                return "当前 Explorer 的 PID、会话或创建时间已变化";
            return null;
        }
        catch (Exception e)
        {
            return "无法确认当前 Explorer 仍存活且身份一致" + ErrorText(e);
        }
    }

    private static bool SameReview(ShellServiceSessionReview a, ShellServiceSessionReview b) =>
        a.Installation is not null && b.Installation is not null && PathEquals(a.Installation, b.Installation) && a.ReceiptSha256 == b.ReceiptSha256 &&
        a.RegisteredProfile == b.RegisteredProfile && a.Fingerprint == b.Fingerprint &&
        a.CompatibilityState == b.CompatibilityState && a.Explorer == b.Explorer &&
        a.TaskbarStatus == b.TaskbarStatus && a.ShellTaskbarIdentity == b.ShellTaskbarIdentity && !b.Pending;

    public static bool IsConfirmedPostStart(ShellServiceSessionPostStart value, string root,
        ShellServiceExplorerIdentity expectedExplorer) =>
        value.Service.Registered && value.Service.StartMode == 4 && value.Service.State == 4 &&
        PathEquals(value.Installation, root) && value.ServiceProcessId != 0 &&
        value.NativeProcessId == value.ServiceProcessId && value.NativeError == 0 && value.NativeState == RunningState &&
        string.Equals(value.NativeStartMode, "session-only", StringComparison.Ordinal) &&
        value.NativeSessionId == expectedExplorer.SessionId && value.NativeExplorerPid == expectedExplorer.ProcessId;

    private static ShellServiceSessionReview Blocked(string state, string detail, ShellServiceSessionInstallation? install = null,
        ShellProfile? draft = null, TaskbarDiagnosticsReport? taskbar = null, ShellServiceExplorerIdentity? explorer = null,
        string? compatibilityState = null, string? fingerprint = null) =>
        new(false, state, detail, install?.Root, install?.ReceiptSha256, install?.Profile, draft,
            install is null || draft is null ? null : draft != install.Profile, install?.Pending ?? false,
            taskbar?.Status, taskbar?.ShellTaskbarIdentity, compatibilityState, fingerprint, explorer);

    private static ShellServiceSessionResult FromReview(ShellServiceSessionReview review) =>
        MakeResult(review.State, false, null, null, null, review.Detail, review);

    private static ShellServiceSessionResult MakeResult(string state, bool? started, bool? disabled, bool? running,
        bool? acceptanceWritten, string detail, ShellServiceSessionReview review, int? exitCode = null) =>
        new(state, started, disabled, running, acceptanceWritten, false, detail, review.Installation, review.ReceiptSha256,
            review.RegisteredProfile, review.Explorer?.ProcessId, review.Explorer?.SessionId,
            review.Explorer?.CreationTimeFileTime, review.Fingerprint, exitCode);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string ErrorText(Exception e) => string.IsNullOrWhiteSpace(e.Message) ? "" : " " + e.Message;
}

internal sealed class WindowsShellServiceSessionHost : IShellServiceSessionHost
{
    private const int CompatibilityTimeoutMs = 8000;
    private const int StartTimeoutMs = 30000;
    public ShellServiceObservation ReadService() => ShellServiceStatus.Read();

    public async Task<ShellServiceSessionInstallation> ReadInstallationAsync(ShellServiceObservation service)
    {
        string root = ShellServiceLayout.Installation(service);
        byte[] receipt = WindowsShellActivationHost.ReadBounded(Path.Combine(root, "install-record.json"), 2 * 1024 * 1024);
        string hash = Convert.ToHexString(SHA256.HashData(receipt));
        using var doc = JsonDocument.Parse(receipt);
        string owner = doc.RootElement.GetProperty("OwnerSid").GetString() ?? throw new InvalidDataException("安装记录缺少 OwnerSid。");
        if (!string.Equals(owner, WindowsIdentity.GetCurrent().User?.Value, StringComparison.Ordinal))
            throw new InvalidDataException("开机安装属于其他 Windows 用户。");
        ShellProfile profile = ShellServiceLayout.CurrentProfile(doc.RootElement);
        var verified = await ShellServiceLayout.ReviewAsync(profile).ConfigureAwait(false);
        if (!PathEquals(verified.Installation, root) || verified.ReceiptSha256 != hash || verified.ScheduledProfile != profile)
            throw new IOException("登记安装或方案在复核期间发生变化。");
        return new(root, hash, profile, verified.Pending);
    }

    public TaskbarDiagnosticsReport InspectTaskbar() => ShellTaskbarDiagnostics.Capture();

    public ShellServiceExplorerIdentity ReadExplorerIdentity(TaskbarDiagnosticsReport taskbar)
    {
        string? problem = ShellServiceSessionOperations.TaskbarProblem(taskbar);
        if (problem is not null) throw new InvalidOperationException(problem);
        var main = taskbar.Taskbars.Single(t => t.ClassName == "Shell_TrayWnd" && t.SessionRelation == TaskbarSessionRelation.Current);
        // Keep one minimally privileged handle for the complete identity read.
        // Managed Process metadata previously failed on a live Explorer on the
        // affected host; neither process name nor a later PID lookup is identity.
        using var process = OpenProcess(0x1000, false, main.ProcessId);
        if (process.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 Explorer 的只读身份句柄。");
        if (!ProcessIdToSessionId(main.ProcessId, out uint session))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法通过 Win32 确认 Explorer 的登录会话。");
        if (!taskbar.CurrentSessionId.HasValue || session != (uint)taskbar.CurrentSessionId.Value)
            throw new InvalidOperationException("Explorer 已不属于检查时的当前会话。");
        var image = new StringBuilder(32768);
        uint imageSize = (uint)image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref imageSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对 Explorer 完整映像路径。");
        if (!string.Equals(image.ToString(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前任务栏进程不是 Windows 目录中的 Explorer。");
        if (!GetProcessTimes(process, out long creation, out long exitTime, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对 Explorer 创建时间。");
        if (!GetExitCodeProcess(process, out uint exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认 Explorer 存活状态。");
        if (creation <= 0 || exitTime != 0 || exitCode != 259)
            throw new InvalidOperationException("Explorer 已退出或创建时间未确认。");
        return new(main.ProcessId, session, creation);
    }

    public async Task<(string State, string Fingerprint, bool EngineStarted, bool AcceptanceWritten)> InspectCompatibilityAsync(string root)
    {
        string exe = Path.Combine(root, "ClassicDeskShell.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("安装目录缺少原生检查程序。");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--inspect-compatibility");
        using var process = Process.Start(start) ?? throw new IOException("无法启动原生 fingerprint 只读检查。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(CompatibilityTimeoutMs)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("原生 fingerprint 检查超时；没有启动引擎。");
        }
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (output.Length > 128 * 1024 || process.ExitCode != 0)
            throw new IOException("原生 fingerprint 只读检查失败。" + (string.IsNullOrWhiteSpace(error) ? "" : " " + error.Trim()));
        using var doc = JsonDocument.Parse(output);
        var result = doc.RootElement;
        return (result.GetProperty("state").GetString() ?? "",
            result.GetProperty("fingerprint").GetString() ?? "",
            result.GetProperty("engineStarted").GetBoolean(),
            result.GetProperty("acceptanceWritten").GetBoolean());
    }

    public async Task<ShellServiceSessionHelperResult> StartCurrentSessionAsync(ShellServiceSessionRequest request)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "Install-ShellService.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("缺少配套的开机管理脚本。");
        static string Q(string value) => !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n')
            ? "\"" + value + "\"" : throw new InvalidDataException("参数包含无效字符。");
        string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Q(script) +
            " -Action StartCurrentSession -Installation " + Q(request.Installation) +
            " -ExpectedExplorerPid " + request.Explorer.ProcessId.ToString(CultureInfo.InvariantCulture) +
            " -ExpectedSessionId " + request.Explorer.SessionId.ToString(CultureInfo.InvariantCulture) +
            " -ExpectedExplorerCreationTime " + request.Explorer.CreationTimeFileTime.ToString(CultureInfo.InvariantCulture) +
            " -ExpectedFingerprint " + Q(request.Fingerprint);
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        string? preparation = null;
        if (request.Profile.ClassicRibbon)
        {
            try { preparation = ExplorerPrelaunchController.Current().EnsureRibbon(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or JsonException)
            { return new(true, false, null, false, "Win10 窗口准备未完成，未调用启动辅助程序。" + e.Message); }
        }
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(powershell, args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })
                ?? throw new IOException("无法启动提权的会话恢复辅助程序。");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        { return new(true, false, null, true, "管理员授权已取消。", preparation); }
        using (process)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(StartTimeoutMs)).ConfigureAwait(false); }
            catch (TimeoutException) { return new(false, false, null, false, "提权辅助程序等待超时；可能仍在处理。", preparation); }
            return new(true, process.ExitCode == 0, process.ExitCode, false,
                process.ExitCode == 0 ? null : "提权辅助程序未确认当前会话启动。", preparation);
        }
    }

    public ShellServiceSessionPostStart ReadPostStart()
    {
        var service = ShellServiceStatus.Read();
        string root = ShellServiceLayout.Installation(service);
        string path = Path.Combine(root, "service-state.ini");
        WindowsShellActivationHost.RejectReparse(path);
        byte[] bytes = WindowsShellActivationHost.ReadBounded(path, 32 * 1024);
        var fields = ReadServiceSection(Encoding.UTF8.GetString(bytes));
        uint nativePid = uint.TryParse(fields.GetValueOrDefault("ProcessId"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPid) ? parsedPid : 0;
        int error = int.TryParse(fields.GetValueOrDefault("Error"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedError) ? parsedError : int.MinValue;
        uint sessionId = uint.TryParse(fields.GetValueOrDefault("SessionId"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSession) ? parsedSession : 0;
        uint explorerPid = uint.TryParse(fields.GetValueOrDefault("ExplorerPid"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedExplorer) ? parsedExplorer : 0;
        return new(service, root, ReadServiceProcessId(), fields.GetValueOrDefault("State") ?? "", nativePid, error,
            fields.GetValueOrDefault("StartMode") ?? "", sessionId, explorerPid);
    }

    private static Dictionary<string, string> ReadServiceSection(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool service = false;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            { service = string.Equals(line[1..^1], "Service", StringComparison.OrdinalIgnoreCase); continue; }
            if (!service) continue;
            int separator = line.IndexOf('=');
            if (separator > 0) result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        if (!result.ContainsKey("State") || !result.ContainsKey("ProcessId") || !result.ContainsKey("Error"))
            throw new InvalidDataException("原生服务状态记录缺少 State、ProcessId 或 Error。");
        return result;
    }

    private static uint ReadServiceProcessId()
    {
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法查询 SCM 服务 PID。");
        try
        {
            nint service = OpenService(manager, ShellServiceStatus.ServiceName, 4);
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 ClassicDesk SCM 服务。");
            try
            {
                if (!QueryServiceStatusEx(service, 0, out ServiceStatusProcess status,
                    (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 SCM 服务 PID。");
                return status.ProcessId;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(nint service, int infoLevel, out ServiceStatusProcess status, uint bufferSize, out uint bytesNeeded);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process,
        uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
