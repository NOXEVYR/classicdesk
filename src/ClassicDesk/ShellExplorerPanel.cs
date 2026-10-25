using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClassicDesk;

public enum ShellExplorerOpenRequestOutcome { Rejected, Submitted, Unknown }
public sealed record ShellExplorerOpenRequestResult(ShellExplorerOpenRequestOutcome Outcome, string Detail);

public interface IShellExplorerPanelOperations
{
    ExplorerDiagnosticsReport Capture();
    string? ChooseFolder(Window owner);
    ShellExplorerOpenRequestResult OpenFolder(string folderPath);
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class WindowsShellExplorerPanelOperations : IShellExplorerPanelOperations
{
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(8);

    public ExplorerDiagnosticsReport Capture() => ShellExplorerDiagnostics.Capture();

    public string? ChooseFolder(Window owner)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要打开的本地文件夹",
            Multiselect = false
        };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Runs the open request in a short-lived copy of ClassicDesk so a hung shell call cannot
    /// hold the panel open forever. This method is called only by the helper's early startup path.
    /// </summary>
    public static ShellExplorerOpenRequestResult RequestFolderInBroker(string folderPath, DateTimeOffset? deadline = null)
    {
        string localPath;
        try
        {
            localPath = ShellExplorerFolderPath.NormalizeLocalFolderPath(folderPath);
            if (!Directory.Exists(localPath))
                return new(ShellExplorerOpenRequestOutcome.Rejected, "所选目录不存在或当前无法访问。未发送打开请求。");

            string root = Path.GetPathRoot(localPath) ?? "";
            var driveType = new DriveInfo(root).DriveType;
            if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                return new(ShellExplorerOpenRequestOutcome.Rejected, "只支持本机固定磁盘或可移动磁盘上的目录。未发送打开请求。");
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            return new(ShellExplorerOpenRequestOutcome.Rejected, "无法验证所选本地目录。未发送打开请求。" + ErrorText(e));
        }

        try
        {
            var start = new ProcessStartInfo(localPath)
            {
                UseShellExecute = true,
                Verb = "opennewwindow",
                WindowStyle = ProcessWindowStyle.Normal
            };
            if (deadline.HasValue && DateTimeOffset.UtcNow >= deadline.Value)
                return new(ShellExplorerOpenRequestOutcome.Rejected, "打开期限已到，未发送请求。");
            using var process = Process.Start(start);
            // ShellExecute can successfully delegate to an existing Explorer process and
            // return no process handle. Submission is not proof of a new HWND or location.
            return new(ShellExplorerOpenRequestOutcome.Submitted, process is null
                ? "Windows 已接受打开请求，由现有进程处理；仍需检查窗口、布局和目录位置，不会自动重试。"
                : "已向 Windows 提交打开新窗口请求；仍需检查实际窗口和目录位置。");
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            return new(ShellExplorerOpenRequestOutcome.Unknown,
                "打开请求结果未知；不会自动重试。" + ErrorText(e));
        }
    }

    public ShellExplorerOpenRequestResult OpenFolder(string folderPath)
    {
        string localPath;
        // Filesystem and drive checks run only in the bounded helper. A local path can
        // traverse a junction to an unavailable volume, so even Exists can block.
        try { localPath = ShellExplorerFolderPath.NormalizeLocalFolderPath(folderPath); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            return new(ShellExplorerOpenRequestOutcome.Rejected, "无法验证所选本地目录。未发送打开请求。" + ErrorText(e));
        }

        string helperPath = Path.Combine(AppContext.BaseDirectory, "ClassicDesk.exe");
        if (!File.Exists(helperPath))
            return new(ShellExplorerOpenRequestOutcome.Rejected, "找不到 ClassicDesk 辅助程序；未发送打开请求。请检查程序文件。");

        Process? helper;
        try
        {
            var start = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--request-explorer-folder");
            start.ArgumentList.Add(localPath);
            helper = Process.Start(start);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            return new(ShellExplorerOpenRequestOutcome.Rejected, "无法启动 ClassicDesk 辅助程序；未发送打开请求。" + ErrorText(e));
        }

        if (helper is null)
            return new(ShellExplorerOpenRequestOutcome.Rejected, "Windows 未启动 ClassicDesk 辅助程序；未发送打开请求。");

        using (helper)
        {
            // Drain both small helper streams while waiting so redirected pipes cannot block exit.
            Task<string> stdout = ReadBoundedAsync(helper.StandardOutput, 4096);
            Task<string> stderr = ReadBoundedAsync(helper.StandardError, 1024);
            if (!helper.WaitForExit((int)HelperTimeout.TotalMilliseconds))
            {
                try { helper.Kill(); } catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { helper.WaitForExit(1000); } catch (InvalidOperationException) { }
                try { Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
                return new(ShellExplorerOpenRequestOutcome.Unknown,
                    "辅助程序等待超过 8 秒；只停止了辅助程序，文件夹请求是否已生效未知，可能稍后仍会打开。不会自动重试。");
            }

            try
            {
                if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(1)))
                    return new(ShellExplorerOpenRequestOutcome.Unknown,
                        "辅助程序已退出但输出未完整读取；打开请求是否生效未知。不会自动重试。");
            }
            catch (AggregateException)
            {
                return new(ShellExplorerOpenRequestOutcome.Unknown,
                    "无法读取辅助程序结果；打开请求是否生效未知。不会自动重试。");
            }

            string output = stdout.Result;
            string error = stderr.Result;
            if (TryReadResult(output, out var result)) return result;
            string exitDetail = string.IsNullOrWhiteSpace(error) ? "" : " " + error.Trim();
            return new(ShellExplorerOpenRequestOutcome.Unknown,
                $"辅助程序退出码 {helper.ExitCode}，没有返回有效结果；打开请求是否生效未知。不会自动重试。{exitDetail}");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var result = new StringBuilder(Math.Min(maximumCharacters, 1024));
        var buffer = new char[512];
        bool truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            int keep = Math.Min(read, maximumCharacters - result.Length);
            if (keep > 0) result.Append(buffer, 0, keep);
            if (keep < read) truncated = true;
        }
        if (truncated) result.Append(" [truncated]");
        return result.ToString();
    }

    private static bool TryReadResult(string json, out ShellExplorerOpenRequestResult result)
    {
        result = new(ShellExplorerOpenRequestOutcome.Unknown, "");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            JsonElement outcome = default, detail = default;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("Outcome", StringComparison.OrdinalIgnoreCase)) outcome = property.Value;
                else if (property.Name.Equals("Detail", StringComparison.OrdinalIgnoreCase)) detail = property.Value;
            }
            ShellExplorerOpenRequestOutcome parsed;
            if (outcome.ValueKind == JsonValueKind.String)
            {
                if (!Enum.TryParse(outcome.GetString(), ignoreCase: true, out parsed) || !Enum.IsDefined(parsed)) return false;
            }
            else if (outcome.ValueKind == JsonValueKind.Number && outcome.TryGetInt32(out int value) &&
                Enum.IsDefined(typeof(ShellExplorerOpenRequestOutcome), value))
                parsed = (ShellExplorerOpenRequestOutcome)value;
            else return false;

            if (detail.ValueKind != JsonValueKind.String) return false;
            result = new(parsed, detail.GetString() ?? "");
            return true;
        }
        catch (JsonException) { return false; }
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    private static string ErrorText(Exception e) => string.IsNullOrWhiteSpace(e.Message) ? "" : " " + e.Message;
}

/// <summary>Pure path syntax check shared by the panel and its local Windows adapter.</summary>
public static class ShellExplorerFolderPath
{
    public static string NormalizeLocalFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentException("请先选择一个本地文件夹。", nameof(folderPath));
        if (folderPath.StartsWith("\\\\", StringComparison.Ordinal) || folderPath.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("不支持网络共享或设备路径；请选择本机磁盘上的普通文件夹。", nameof(folderPath));
        if (folderPath.Length < 3 || !char.IsAsciiLetter(folderPath[0]) || folderPath[1] != ':' ||
            folderPath[2] is not ('\\' or '/'))
            throw new ArgumentException("请选择带本机盘符的完整文件夹路径。", nameof(folderPath));
        if (!Path.IsPathFullyQualified(folderPath))
            throw new ArgumentException("请选择完整的本机文件夹路径。", nameof(folderPath));

        string fullPath = Path.GetFullPath(folderPath);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("只支持本机磁盘上的普通文件夹。", nameof(folderPath));
        return fullPath;
    }
}

/// <summary>Explicitly opens one chosen folder and observes Explorer structure without closing old windows.</summary>
public sealed class ShellExplorerPanel : Window
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const string ManualCheck = "仅验证结构；确认位置后测试拖动、主页/共享/查看、地址栏等，旧窗口需自行确认关闭。";

    private readonly IShellExplorerPanelOperations operations;
    private readonly TextBlock status = MakeText("尚未检查", 20, true);
    private readonly TextBlock detail = MakeText("检查只读取文件窗口结构。请选择单独的操作来检查或打开文件夹。", 12);
    private readonly TextBox folder = new() { IsReadOnly = true, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox windows = new() { MinHeight = 112, MaxHeight = 220 };
    private readonly Button inspectButton, chooseButton, openButton;
    private readonly TextBlock resultHeading = MakeText("观察到的文件窗口", 12, true);
    private string? selectedFolder;
    private CancellationTokenSource? activeObservation;
    private bool busy, closed;

    public string StatusText => status.Text;
    public string DetailText => detail.Text;
    public string SelectedFolderPath => selectedFolder ?? "";
    public IReadOnlyList<string> WindowRows => windows.Items.Cast<string>().ToArray();
    public bool IsWorking => busy;
    public bool InspectAvailable => inspectButton.IsEnabled;
    public bool ChooseFolderAvailable => chooseButton.IsEnabled;
    public bool OpenFolderAvailable => openButton.IsEnabled;

    public ShellExplorerPanel(IShellExplorerPanelOperations? operations = null)
    {
        this.operations = operations ?? new WindowsShellExplorerPanelOperations();
        Title = "ClassicDesk · 打开并检查资源管理器";
        Width = 720; Height = 650; MinWidth = 560; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Background = Brush("#F5F7FC"); Foreground = Brush("#26344E");
        Icon = AppIcons.Get("folder"); UseLayoutRounding = true;

        var root = new DockPanel { Margin = new Thickness(24), Background = Background };
        Content = root;
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        inspectButton = MakeButton("检查现有窗口", InspectAsync);
        inspectButton.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(inspectButton, Dock.Left);
        footer.Children.Add(inspectButton);
        chooseButton = MakeButton("选择文件夹…", () => { ChooseFolder(); return Task.CompletedTask; });
        footer.Children.Add(chooseButton);
        openButton = MakeButton("打开并检查资源管理器", OpenSelectedFolderAsync);
        openButton.Background = Brush("#637BDC"); openButton.Foreground = Brushes.White;
        footer.Children.Add(openButton);

        AutomationProperties.SetAutomationId(inspectButton, "explorer-inspect-existing");
        AutomationProperties.SetAutomationId(chooseButton, "explorer-choose-folder");
        AutomationProperties.SetAutomationId(openButton, "explorer-open-selected-folder");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var eyebrow = MakeText("资源管理器 / 当前桌面", 11, true); eyebrow.Foreground = Brush("#697CAA");
        body.Children.Add(eyebrow);
        status.Margin = new Thickness(0, 8, 0, 8); body.Children.Add(status);
        detail.Margin = new Thickness(0, 0, 0, 16); detail.LineHeight = 20; body.Children.Add(detail);

        var folderBox = new StackPanel { Margin = new Thickness(16) };
        folderBox.Children.Add(MakeText("要打开的本地文件夹", 12, true));
        folder.Margin = new Thickness(0, 8, 0, 7); folderBox.Children.Add(folder);
        folderBox.Children.Add(MakeText("选择后点击“打开并检查资源管理器”。每次只发送一次打开请求。", 11));
        body.Children.Add(new Border
        {
            Child = folderBox, Background = Brushes.White, CornerRadius = new CornerRadius(10),
            BorderBrush = Brush("#E0E6F1"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 14)
        });

        var windowBox = new StackPanel { Margin = new Thickness(16) };
        windowBox.Children.Add(resultHeading);
        windows.Margin = new Thickness(0, 8, 0, 0); windowBox.Children.Add(windows);
        body.Children.Add(new Border
        {
            Child = windowBox, Background = Brushes.White, CornerRadius = new CornerRadius(10),
            BorderBrush = Brush("#E0E6F1"), BorderThickness = new Thickness(1)
        });

        var note = MakeText("检查只读取窗口身份与界面结构，不读取窗口中的文件夹路径。观察到新窗口也不能证明它显示的是所选文件夹或交互已经通过。\n\n" + ManualCheck, 11);
        note.Foreground = Brush("#68758C"); note.LineHeight = 18; note.Margin = new Thickness(2, 16, 2, 0);
        body.Children.Add(note);

        var rebuildOld = MakeButton("重建旧窗口…", () =>
        {
            if (!busy && !closed) new ShellExplorerRebuildPanel { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        });
        body.Children.Add(rebuildOld);

        Closed += (_, _) =>
        {
            closed = true;
            try { activeObservation?.Cancel(); } catch (ObjectDisposedException) { }
        };
        UpdateButtons();
    }

    public async Task InspectAsync()
    {
        if (closed || busy) return;
        var cancellation = BeginOperation();
        status.Text = "正在检查现有文件窗口";
        detail.Text = "只读检查当前登录会话中的文件窗口，包括隐藏和最小化窗口。";
        try
        {
            var report = await CaptureAsync(cancellation.Token, CaptureTimeout);
            if (closed) return;
            ShowRows(report);
            if (!TryCreateAnchor(report, out _, out string problem))
            {
                status.Text = "检查结果不完整";
                detail.Text = problem + " 不会据此确认新窗口。";
                return;
            }

            status.Text = report.Windows.Count == 0 ? "未发现现有文件窗口" : $"已检查 {report.Windows.Count} 个现有文件窗口";
            detail.Text = report.Windows.Count == 0 ? "本次检查建立了空窗口基线。" : report.Summary;
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception e)
        {
            if (!closed) { status.Text = "检查未完成"; detail.Text = "无法读取文件窗口信息。" + ErrorText(e); }
        }
        finally { EndOperation(cancellation); }
    }

    public void ChooseFolder()
    {
        if (closed || busy) return;
        try
        {
            string? chosen = operations.ChooseFolder(this);
            if (closed || string.IsNullOrWhiteSpace(chosen)) return;
            selectedFolder = chosen;
            folder.Text = chosen;
            status.Text = "已选择文件夹";
            detail.Text = "打开前会重新读取现有窗口，打开后只观察新窗口身份和界面结构。";
        }
        catch (Exception e)
        {
            if (!closed) { status.Text = "无法选择文件夹"; detail.Text = ErrorText(e); }
        }
        UpdateButtons();
    }

    public async Task OpenSelectedFolderAsync()
    {
        if (closed || busy || string.IsNullOrWhiteSpace(selectedFolder)) return;
        string normalizedPath;
        try { normalizedPath = ShellExplorerFolderPath.NormalizeLocalFolderPath(selectedFolder); }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            status.Text = "无法打开所选文件夹"; detail.Text = ErrorText(e); return;
        }

        var cancellation = BeginOperation();
        folder.Text = selectedFolder = normalizedPath;
        status.Text = "正在保存打开前的窗口状态";
        detail.Text = "先读取所有现有窗口，再发送一次打开请求。";
        try
        {
            ExplorerDiagnosticsReport before;
            try { before = await CaptureAsync(cancellation.Token, CaptureTimeout); }
            catch (OperationCanceledException) when (closed) { return; }
            catch (Exception e)
            {
                status.Text = "无法确认打开前状态";
                detail.Text = "读取现有窗口失败，未发送打开请求。" + ErrorText(e);
                return;
            }
            if (closed) return;
            ShowRows(before);
            if (!TryCreateAnchor(before, out var anchor, out string problem))
            {
                status.Text = "无法确认打开前状态";
                detail.Text = problem + " 未发送打开请求；请重新检查。";
                return;
            }

            // The open request is issued exactly once and is deliberately not canceled by closing this panel.
            status.Text = "正在提交一次打开请求";
            var open = await Task.Run(() => operations.OpenFolder(normalizedPath)).WaitAsync(cancellation.Token);
            if (closed) return;
            if (open.Outcome == ShellExplorerOpenRequestOutcome.Rejected)
            {
                status.Text = "打开请求未发送"; detail.Text = open.Detail;
                return;
            }

            status.Text = open.Outcome == ShellExplorerOpenRequestOutcome.Submitted
                ? "打开请求已提交，正在检查新窗口" : "打开请求结果未知，正在检查新窗口";
            detail.Text = open.Detail + " 不会自动重试。";
            await ObserveAfterOpenAsync(anchor, before, open, cancellation.Token);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception e)
        {
            if (!closed) { status.Text = "打开结果未能确认"; detail.Text = "操作停止，未自动重试。" + ErrorText(e); }
        }
        finally { EndOperation(cancellation); }
    }

    private async Task ObserveAfterOpenAsync(ExplorerSnapshotAnchor before, ExplorerDiagnosticsReport initial,
        ShellExplorerOpenRequestResult open,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = operations.UtcNow + ObservationTimeout;
        bool sawUnverifiedFreshWindow = false;
        bool sawHiddenWindowBecomeVisible = false;
        var hidden = initial.Windows.Where(w => !w.Observation.Visible && !w.Observation.Minimized)
            .Select(w => w.Observation.Hwnd).ToHashSet();
        ExplorerWindowObservation? observedCandidate = null;
        while (!closed && !cancellationToken.IsCancellationRequested && operations.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - operations.UtcNow;
            await operations.DelayAsync(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
            if (closed || cancellationToken.IsCancellationRequested) return;
            remaining = deadline - operations.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            ExplorerDiagnosticsReport report;
            try { report = await CaptureAsync(cancellationToken, remaining); }
            catch (TimeoutException) { break; }
            if (closed) return;
            if (operations.UtcNow >= deadline) break;
            ShowRows(report);
            if (!TryCreateAnchor(report, out var after, out _))
            {
                detail.Text = "窗口清单暂时不完整；继续只读观察，不会重复发送打开请求。";
                continue;
            }

            if (!KnownWindowsUnchanged(before, after, out string change))
            {
                status.Text = "窗口变化导致结果未确认";
                detail.Text = change + " 保留所有窗口；请人工检查。";
                return;
            }

            // Retain the full observation budget: a formerly hidden window can be
            // shown by an unrelated action while the requested fresh HWND is pending.
            sawHiddenWindowBecomeVisible |= report.Windows.Any(w => hidden.Contains(w.Observation.Hwnd) &&
                w.Observation.Visible && !w.Observation.Minimized);

            var fresh = report.Windows.Where(w => !before.Windows.ContainsKey(w.Observation.Hwnd)).ToArray();
            if (fresh.Length > 1)
            {
                status.Text = "观察到多个新窗口，结果有歧义";
                detail.Text = "无法确定哪个窗口对应这次请求。保留所有窗口，不会关闭或重试；请人工检查。";
                return;
            }
            if (fresh.Length == 1)
            {
                var candidate = fresh[0];
                if (!ValidCandidate(candidate, before.SessionId))
                {
                    sawUnverifiedFreshWindow = true;
                    detail.Text = "观察到一个新窗口候选，但其 Explorer 身份尚未完整确认；继续只读观察。";
                    continue;
                }

                var current = candidate.Observation;
                if (observedCandidate is not null &&
                    (current.Hwnd != observedCandidate.Hwnd || current.ProcessId != observedCandidate.ProcessId ||
                     current.ThreadId != observedCandidate.ThreadId || current.SessionId != observedCandidate.SessionId ||
                     current.ProcessCreationTime != observedCandidate.ProcessCreationTime))
                {
                    status.Text = "新窗口身份变化，结果未确认";
                    detail.Text = "等待初始化期间新窗口被替换；保留所有窗口，不再把后来的窗口归入本次请求。";
                    return;
                }
                observedCandidate = current;
                // A newly allocated HWND can precede the frame and message loop
                // becoming ready. Keep observing the same identity, within the
                // existing budget; never turn an early timeout into acceptance.
                if (!current.Visible || current.Minimized || current.Responsive != true ||
                    !current.ChildrenComplete || current.Bounds is not { HasArea: true } ||
                    candidate.Frame is ExplorerFrameKind.Unknown or ExplorerFrameKind.Mixed)
                {
                    detail.Text = "新窗口已出现，正在等待同一窗口的响应和顶部结构就绪；不会再次打开或关闭窗口。";
                    continue;
                }

                status.Text = open.Outcome == ShellExplorerOpenRequestOutcome.Submitted
                    ? "观察到一个新的文件窗口" : "观察到一个新窗口，请人工确认来源";
                string relation = open.Outcome == ShellExplorerOpenRequestOutcome.Submitted
                    ? "打开请求之后观察到一个身份已确认的新窗口。"
                    : "打开请求结果未知；观察到的新窗口不一定由该请求产生。";
                detail.Text = relation + $" 顶部结构：{FrameName(candidate.Frame)}。这里只验证窗口身份和结构，不能证明窗口显示的是所选路径。\n\n" + ManualCheck;
                return;
            }
            if (observedCandidate is not null)
            {
                status.Text = "新窗口已消失，结果未确认";
                detail.Text = "等待初始化期间新窗口消失；保留原窗口，不会重新打开或改认其他窗口。";
                return;
            }
        }

        if (closed || cancellationToken.IsCancellationRequested) return;
        status.Text = observedCandidate is null && sawHiddenWindowBecomeVisible ? "原有隐藏窗口已显示，未确认新建窗口" : "无法确认新窗口";
        detail.Text = observedCandidate is not null
            ? "新窗口已出现，但在 5 秒观察期内响应或顶部结构仍未就绪；不能确认 Win10 布局可用。保留所有窗口，不会自动重试。"
            : sawHiddenWindowBecomeVisible
            ? "打开前已有的隐藏窗口变为可见，可能复用了预加载窗口；它不是新建窗口，原有 Win11 布局不会因钩子加载而重建。未确认此窗口对应所选目录，保留所有窗口，不会自动重试。"
            : sawUnverifiedFreshWindow
            ? "在观察期限内未能确认新窗口的 Explorer 身份。请求不会自动重试；请检查现有窗口后再决定是否手动重试。"
            : "5 秒内没有观察到唯一且身份已确认的新窗口；请求结果不代表窗口已打开。请检查现有窗口后再决定是否手动重试。";
        // Preserve the broker's reason: a later observation timeout must not hide
        // an earlier helper timeout, launch failure, or unknown submission result.
        detail.Text += "\n\n打开请求结果：" + open.Detail;
    }

    private async Task<ExplorerDiagnosticsReport> CaptureAsync(CancellationToken cancellationToken, TimeSpan timeout)
    {
        // The real diagnostics capture is itself capped at about two seconds. WaitAsync also
        // protects this panel from a stalled native observation implementation or test double.
        return await Task.Run(operations.Capture).WaitAsync(timeout, cancellationToken);
    }

    private static bool TryCreateAnchor(ExplorerDiagnosticsReport report, out ExplorerSnapshotAnchor anchor, out string problem)
    {
        anchor = ExplorerSnapshotAnchor.Empty;
        if (report is null || !report.Complete || report.Windows is null)
        {
            problem = "文件窗口清单未完整读取。";
            return false;
        }
        var byHandle = new Dictionary<long, ExplorerWindowIdentity>();
        var byProcess = new Dictionary<uint, ExplorerProcessIdentity>();
        int? session = null;
        foreach (var window in report.Windows)
        {
            var value = window?.Observation;
            if (value is null || value.Hwnd == 0 || !value.IdentityVerified || value.ProcessId == 0 || value.ThreadId == 0 ||
                value.SessionId <= 0 || value.ProcessCreationTime <= 0)
            {
                problem = "至少一个窗口的 Explorer 身份未确认。";
                return false;
            }
            var identity = new ExplorerWindowIdentity(value.ProcessId, value.SessionId, value.ProcessCreationTime, value.ThreadId);
            if (!byHandle.TryAdd(value.Hwnd, identity))
            {
                problem = "窗口清单包含重复的窗口句柄。";
                return false;
            }
            var process = new ExplorerProcessIdentity(value.SessionId, value.ProcessCreationTime);
            if (byProcess.TryGetValue(value.ProcessId, out var prior) && prior != process)
            {
                problem = "同一进程 PID 对应的会话或创建时间不一致。";
                return false;
            }
            byProcess[value.ProcessId] = process;
            if (session.HasValue && session.Value != value.SessionId)
            {
                problem = "窗口清单包含不同登录会话。";
                return false;
            }
            session = value.SessionId;
        }
        anchor = new(byHandle, byProcess, session);
        problem = "";
        return true;
    }

    private static bool KnownWindowsUnchanged(ExplorerSnapshotAnchor before, ExplorerSnapshotAnchor after, out string problem)
    {
        foreach (var (handle, identity) in before.Windows)
        {
            if (!after.Windows.TryGetValue(handle, out var current))
            {
                problem = "检查期间一个原有文件窗口消失。";
                return false;
            }
            if (current != identity)
            {
                problem = "检查期间一个原有窗口的 PID、会话、创建时间或线程身份变化。";
                return false;
            }
        }
        foreach (var (pid, identity) in before.Processes)
        {
            if (after.Processes.TryGetValue(pid, out var current) && current != identity)
            {
                problem = "检查期间一个已知 Explorer PID 的会话或创建时间变化。";
                return false;
            }
        }
        if (before.SessionId.HasValue && after.SessionId.HasValue && before.SessionId != after.SessionId)
        {
            problem = "检查期间文件窗口所属登录会话变化。";
            return false;
        }
        problem = "";
        return true;
    }

    private static bool ValidCandidate(ExplorerWindowReport candidate, int? expectedSession)
    {
        var value = candidate.Observation;
        return value.IdentityVerified && value.Hwnd != 0 && value.ProcessId != 0 && value.ThreadId != 0 &&
            value.SessionId > 0 && value.ProcessCreationTime > 0 &&
            (!expectedSession.HasValue || expectedSession.Value == value.SessionId);
    }

    private void ShowRows(ExplorerDiagnosticsReport report)
    {
        var rows = report.Windows.Select(w =>
        {
            var x = w.Observation;
            string visibility = x.Minimized ? "最小化" : x.Visible ? "显示" : "隐藏";
            return $"窗口 0x{x.Hwnd:X} · PID {x.ProcessId} / 会话 {x.SessionId} · {FrameName(w.Frame)} · {visibility}";
        }).ToArray();
        windows.ItemsSource = rows;
        resultHeading.Text = report.Windows.Count == 0 ? "未发现文件窗口" : $"观察到的文件窗口（{report.Windows.Count}）";
    }

    private CancellationTokenSource BeginOperation()
    {
        busy = true;
        var cancellation = new CancellationTokenSource();
        activeObservation = cancellation;
        UpdateButtons();
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(activeObservation, cancellation)) activeObservation = null;
        cancellation.Dispose();
        if (!closed) { busy = false; UpdateButtons(); }
    }

    private void UpdateButtons()
    {
        inspectButton.IsEnabled = chooseButton.IsEnabled = !busy && !closed;
        openButton.IsEnabled = !busy && !closed && !string.IsNullOrWhiteSpace(selectedFolder);
    }

    private static string FrameName(ExplorerFrameKind frame) => frame switch
    {
        ExplorerFrameKind.Ribbon => "Win10 功能区结构",
        ExplorerFrameKind.Modern => "现代顶栏结构",
        ExplorerFrameKind.Mixed => "混合顶栏结构",
        _ => "结构未确认"
    };

    private static string ErrorText(Exception e) => string.IsNullOrWhiteSpace(e.Message) ? "" : e.Message;
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static TextBlock MakeText(string text, double size, bool bold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.65
    };
    private static Button MakeButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(8, 0, 0, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private sealed record ExplorerWindowIdentity(uint ProcessId, int SessionId, long CreationTimeFileTime, uint ThreadId);
    private sealed record ExplorerProcessIdentity(int SessionId, long CreationTimeFileTime);
    private sealed record ExplorerSnapshotAnchor(Dictionary<long, ExplorerWindowIdentity> Windows,
        Dictionary<uint, ExplorerProcessIdentity> Processes, int? SessionId)
    {
        public static ExplorerSnapshotAnchor Empty => new([], [], null);
    }
}
