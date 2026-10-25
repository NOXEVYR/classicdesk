using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassicDesk;

public interface IShellServiceLayoutOperations
{
    Task<ShellServiceLayoutReview> ReviewAsync(ShellProfile proposal);
    Task<string> StageAsync(ShellServiceLayoutReview review);
    Task DisableAsync(ShellServiceLayoutReview review);
}
public sealed class WindowsShellServiceLayoutOperations : IShellServiceLayoutOperations
{
    public Task<ShellServiceLayoutReview> ReviewAsync(ShellProfile proposal) => ShellServiceLayout.ReviewAsync(proposal);
    public Task<string> StageAsync(ShellServiceLayoutReview review) => ShellServiceLayout.StageAsync(review);
    public Task DisableAsync(ShellServiceLayoutReview review) => ShellServiceLayout.DisableAsync(review);
}

/// <summary>Safe default for tests and embedded panels; production entry points opt in explicitly.</summary>
sealed class UnavailableShellServiceSessionOperations : IShellServiceSessionOperations
{
    public Task<ShellServiceSessionReview> ReviewAsync(ShellProfile? currentDraft = null) =>
        Task.FromResult(new ShellServiceSessionReview(false, "session-entry-not-configured",
            "本次会话恢复入口尚未配置。请从 ClassicDesk 主界面重新打开此页面。", null, null, null,
            currentDraft, null, false, null, null, null, null, null));
    public Task<ShellServiceSessionResult> StartAsync(ShellServiceSessionReview review) =>
        Task.FromResult(new ShellServiceSessionResult("session-entry-not-configured", false, null, null, null,
            false, "本次会话恢复入口尚未配置；没有启动服务。"));
    public Task<ShellServiceSessionResult> StartCurrentSessionAsync(ShellProfile? currentDraft = null) =>
        Task.FromResult(new ShellServiceSessionResult("session-entry-not-configured", false, null, null, null,
            false, "本次会话恢复入口尚未配置；没有启动服务。"));
}

/// <summary>Explicit service configuration actions; construction never changes the host.</summary>
public sealed class ShellServicePanel : Window
{
    readonly ShellProfile proposal;
    readonly IShellServiceLayoutOperations operations;
    readonly TextBlock status = Text("正在检查开机方案", 20, true);
    readonly TextBlock detail = Text("", 12);
    readonly TextBlock current = Text("读取中…", 13);
    readonly TextBlock currentHeading = Text("已保存的开机方案", 12, true);
    readonly TextBlock changes = Text("", 12);
    readonly Button apply, disable, refresh, diagnose, startSession;
    readonly Func<TaskbarDiagnosticsReport> taskbarDiagnostics;
    readonly IShellServiceSessionOperations sessionOperations;
    readonly TextBlock sessionDetail = Text("正在读取本次会话启动条件。", 12);
    ShellServiceLayoutReview? review;
    ShellServiceSessionReview? sessionReview;
    bool busy, closed, writing;
    public string StatusText => status.Text;
    public string SessionDetailText => sessionDetail.Text;
    public bool ApplyAvailable => apply.IsEnabled;
    public bool StartSessionAvailable => startSession.IsEnabled;
    public bool IsWorking => busy;
    public string ScheduledSummary => current.Text;
    public string ScheduledHeading => currentHeading.Text;
    public string ChangesText => changes.Text;
    public ShellServicePanel(ShellProfile profile, IShellServiceLayoutOperations? adapter = null,
        Func<TaskbarDiagnosticsReport>? diagnostics = null, IShellServiceSessionOperations? sessions = null)
    {
        profile.Validate(); proposal = profile; operations = adapter ?? new WindowsShellServiceLayoutOperations();
        taskbarDiagnostics = diagnostics ?? ShellTaskbarDiagnostics.Capture;
        sessionOperations = sessions ?? new UnavailableShellServiceSessionOperations();
        Title = "ClassicDesk · 开机方案"; Width = 740; Height = 690; MinWidth = 610; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Icon = AppIcons.Get("brand");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13; Background = Brush("#F5F6FA");
        Foreground = Brush("#202B40"); UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClassicDesk;component/ShellTheme.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new Thickness(24), Background = Background }; Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        refresh = Action("重新检查", async () => await RefreshAsync()); footer.Children.Add(refresh);
        disable = Action("停用下次开机增强", async () => await DisableAsync()); footer.Children.Add(disable);
        apply = Action("更新开机方案", async () => await ApplyAsync()); apply.Background = Brush("#637BDC"); apply.Foreground = Brushes.White; footer.Children.Add(apply);
        AutomationProperties.SetAutomationId(apply, "service-layout-apply"); AutomationProperties.SetAutomationId(disable, "service-disable-next-boot");
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var eyebrow = Text("系统配置 / 开机方案", 11, true); eyebrow.Foreground = Brush("#6576A6"); eyebrow.Margin = new Thickness(0, 0, 0, 6); body.Children.Add(eyebrow);
        body.Children.Add(status); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); detail.Margin = new Thickness(0, 8, 0, 18); detail.LineHeight = 21; body.Children.Add(detail);
        var boundary = Text("本地草稿  →  保存开机规则  →  增强兼容验收", 12, true); boundary.Foreground = Brush("#4F6095");
        body.Children.Add(new Border { Child = boundary, Background = Brush("#EAF0FF"), CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 0, 16) });
        diagnose = Action("检查当前任务栏", DiagnoseAsync); diagnose.HorizontalAlignment = HorizontalAlignment.Left;
        diagnose.Margin = new Thickness(0, 0, 0, 14); body.Children.Add(diagnose);
        AutomationProperties.SetAutomationId(diagnose, "service-taskbar-diagnostics");
        startSession = Action("本次启用已保存方案", StartSessionAsync);
        startSession.HorizontalAlignment = HorizontalAlignment.Left; startSession.Margin = new Thickness(0, 0, 0, 14);
        AutomationProperties.SetAutomationId(startSession, "service-start-current-session");
        var sessionBody = new StackPanel { Margin = new Thickness(16) };
        var sessionHeading = Text("当前会话恢复", 12, true); sessionHeading.Foreground = Brush("#637296");
        sessionBody.Children.Add(sessionHeading); sessionDetail.Margin = new Thickness(0, 8, 0, 8);
        sessionBody.Children.Add(sessionDetail); sessionBody.Children.Add(startSession);
        body.Children.Add(new Border { Child = sessionBody, CornerRadius = new CornerRadius(10), Background = Brushes.White,
            BorderBrush = Brush("#E0E5F0"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(Card(currentHeading, current));
        body.Children.Add(Card(Text("本次提交的草稿", 12, true), Text(Describe(profile), 13)));
        changes.Margin = new Thickness(3, 12, 3, 8); changes.Foreground = Brush("#5D6E9C"); body.Children.Add(changes);
        body.Children.Add(Text("检查当前任务栏只读取窗口和模块状态。保存方案需要管理员授权；停用状态会保留，修改后的系统与方案须通过增强兼容验收才能自动加载。方案包含任务栏、资源管理器和右键菜单，界面皮肤只改变 ClassicDesk 外观。", 12));
        Loaded += (_, _) => _ = RefreshAsync();
        Closing += (_, e) => { if (writing) e.Cancel = true; };
        Closed += (_, _) => closed = true;
        SetBusy(false);
    }
    public async Task RefreshAsync()
    {
        if (busy || closed) return; review = null; sessionReview = null; SetBusy(true); status.Text = "正在检查开机方案";
        currentHeading.Text = "已保存的开机方案"; current.Text = "正在读取，尚未取得本次检查结果。"; changes.Text = ""; detail.Text = "读取已登记的方案，不改变当前桌面。";
        sessionDetail.Text = "正在检查系统、已保存方案与当前任务栏状态。";
        try
        {
            var value = await operations.ReviewAsync(proposal);
            if (closed) return;
            review = value; current.Text = Describe(value.ScheduledProfile);
            status.Text = value.Pending ? "已有方案等待兼容验收" : "开机方案已读取";
            detail.Text = value.Pending ? "下方展示的是已保存方案。当前桌面保持原布局，增强兼容性需单独核验。" : "可将当前编辑的完整方案保存为开机规则。";
            changes.Text = value.Changes.Count == 0 ? "两套方案一致，无需更新。" : "将更新：" + string.Join("、", value.Changes);
            sessionReview = await sessionOperations.ReviewAsync(proposal);
            if (closed) return;
            sessionDetail.Text = sessionReview.CanStart
                ? "已保存方案和当前任务栏状态通过本次检查。" +
                    (sessionReview.DraftDiffers == true ? " 当前草稿不同，本次仍使用已保存方案。" : "") +
                    " 本次启动后开机仍停用，实际效果需检查。"
                : sessionReview.Detail;
            startSession.ToolTip = sessionReview.DraftDiffers == true
                ? "本次只加载安装记录中的已保存开机方案，不应用当前草稿。"
                : "只在当前登录会话启用；服务下次开机仍保持停用。";
        }
        catch (Exception e) { if (!closed) { status.Text = "暂时无法管理开机方案"; detail.Text = e.Message; current.Text = "未取得可核对的开机方案，请重新检查。"; changes.Text = ""; sessionReview = null; sessionDetail.Text = "本次会话恢复条件未能确认，请重新检查。"; } }
        finally { if (!closed) SetBusy(false); }
    }
    public Task ApplyAsync() => ExecuteAsync(false);
    public Task DisableAsync() => ExecuteAsync(true);
    public async Task StartSessionAsync()
    {
        if (closed || busy || sessionReview is not { CanStart: true }) return;
        var captured = sessionReview; writing = true; SetBusy(true);
        status.Text = "正在启动本次登录会话增强";
        sessionDetail.Text = "正在重新检查系统、已保存方案与当前任务栏状态；开机启动保持停用。";
        try
        {
            var result = await sessionOperations.StartAsync(captured);
            if (closed) return;
            sessionReview = null; review = null;
            if (result.ConfirmedStarted)
            {
                status.Text = "本次增强已启动，开机仍停用";
                sessionDetail.Text = result.Detail;
            }
            else
            {
                status.Text = result.CurrentSessionStarted == false ? "本次增强未启动" : "启动结果需要重新检查";
                sessionDetail.Text = result.Detail;
            }
        }
        catch (Exception e)
        {
            sessionReview = null; review = null;
            status.Text = "启动结果需要重新检查";
            sessionDetail.Text = "本次操作没有可确认的结果；未自动重试。请重新检查服务状态和当前桌面。 " + e.Message;
        }
        finally { writing = false; SetBusy(false); }
    }
    public async Task DiagnoseAsync()
    {
        if (closed || busy) return;
        SetBusy(true);status.Text = "正在检查当前任务栏";
        try {
            var report = await Task.Run(taskbarDiagnostics);
            if (closed) return;
            status.Text = report.Status == TaskbarCheckStatus.Healthy ? "任务栏结构与响应检查通过" : "任务栏诊断需要关注";
            detail.Text = report.Summary + "\n" + string.Join("\n", report.Taskbars.Select((bar, index) =>
                $"任务栏 {index + 1}：进程 {bar.ProcessId}，子窗口 {bar.ChildCount}，响应 " +
                (bar.Responsive == true ? "正常" : bar.Responsive == false ? "超时" : "未知") +
                (bar.Modules.Status == ProcessModuleReadStatus.Available ? $"，检测到增强模块 {bar.Modules.ClassicDeskModules.Concat(bar.Modules.WindhawkModules).DistinctBy(module => module.FullPath, StringComparer.OrdinalIgnoreCase).Count()} 项。" : "，模块信息读取不完整。")));
        } catch (Exception e) { if (!closed) { status.Text = "任务栏诊断未完成"; detail.Text = e.Message; } }
        finally { if (!closed) SetBusy(false); }
    }
    async Task ExecuteAsync(bool stopping)
    {
        if (closed || busy || review is null || (!stopping && review.Changes.Count == 0)) return;
        var captured = review; writing = true; SetBusy(true);
        status.Text = stopping ? "正在停用下次开机增强" : "正在保存开机方案";
        detail.Text = "请完成 Windows 管理员授权，完成后即可关闭此窗口。";
        try
        {
            if (stopping) { await operations.DisableAsync(captured); status.Text = "下次开机增强已停用"; detail.Text = "当前桌面保留，重启后不再自动加载。安装文件和方案记录仍保留。"; currentHeading.Text = "停用前保存的方案 · 留存快照"; changes.Text = "自动加载已停用；重新检查后可继续管理。"; }
            else { detail.Text = await operations.StageAsync(captured); status.Text = "开机方案已保存，等待兼容验收"; currentHeading.Text = "本次已提交的开机方案"; current.Text = Describe(captured.Proposal); changes.Text = "本次更新已提交。继续管理前请重新检查；当前桌面效果尚未验证。"; }
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        { status.Text = "已取消管理员授权"; detail.Text = "本次未提交更新，可重新检查后再试。"; }
        catch (Exception e) { status.Text = "操作未完成，请重新检查"; detail.Text = e.Message; }
        finally { review = null; writing = false; SetBusy(false); }
    }
    void SetBusy(bool value)
    {
        busy = value; refresh.IsEnabled = !value; diagnose.IsEnabled = !value;
        apply.IsEnabled = !value && review is { Changes.Count: > 0 };
        disable.IsEnabled = !value && review is not null;
        startSession.IsEnabled = !value && sessionReview is { CanStart: true };
    }
    static string Describe(ShellProfile p) => $"{ShellPresets.DisplayName(p)} · {ShellSkins.All[ShellSkins.Index(p)].Name}\n" +
        (p.SkipTaskbarLayout ? "任务栏布局增强关闭" : ShellPresets.TaskbarLayoutName(p)) +
        $"\n图标 {p.IconSize} / 小图标 {p.SmallIconSize} · 栏高 {p.TaskbarHeight} · 按钮 {p.TaskbarButtonWidth} / 小按钮 {p.SmallTaskbarButtonWidth}" +
        (p.UseNativeTaskbarSizing ? "（已保存的自定义数值；实际尺寸由 Windows 管理）" : p.SkipTaskbarSizing ? "（尺寸增强关闭）" : "") +
        (p.LeftAlignedApps ? "\n定位：Windows 原生左对齐，不启用开始/搜索定位模块" : $"\n左置：系统按钮 {(p.OtherSystemButtonsOnLeft ? "开" : "关")} / 开始菜单 {(p.StartMenuOnLeft ? "开" : "关")} / 搜索 {(p.SearchMenuOnLeft ? "开" : "关")}") +
        $"\n紧凑托盘 {(p.CompactTray ? "开" : "关")} · " + (p.FollowMaximizedTheme ? "最大化背景不透明，桌面透明" : p.TranslucentTaskbar ? "始终透明" : "系统原生背景") +
        "\n资源管理器：" + (p.ClassicRibbon ? "Win10 功能区（无标签页）" : p.UseClassicNavigationBar ? "经典导航栏（保留标签页）" : "Win11 原生") +
        "\n右键菜单：" + (p.ClassicContextMenu ? "完整菜单" + (p.ClassicMenuWithCtrl ? " · Ctrl 临时新版" : "") : "Win11 原生");
    static TextBlock Text(string text, double size, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.8 };
    static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    static Border Card(TextBlock title, TextBlock content)
    {
        var body = new StackPanel { Margin = new Thickness(16) }; title.Foreground = Brush("#637296"); body.Children.Add(title); content.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(content);
        return new Border { Child = body, CornerRadius = new CornerRadius(10), Background = Brushes.White, BorderBrush = Brush("#E0E5F0"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
    }
    static Button Action(string label, Func<Task> action)
    { var button = new Button { Content = label, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(8, 0, 0, 0) }; button.Click += async (_, _) => await action(); return button; }
}
