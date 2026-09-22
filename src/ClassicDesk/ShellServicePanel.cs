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

/// <summary>Explicit service configuration actions; construction never changes the host.</summary>
public sealed class ShellServicePanel : Window
{
    readonly ShellProfile proposal;
    readonly IShellServiceLayoutOperations operations;
    readonly TextBlock status = Text("正在检查开机方案", 20, true);
    readonly TextBlock detail = Text("", 12);
    readonly TextBlock current = Text("读取中…", 13);
    readonly TextBlock changes = Text("", 12);
    readonly Button apply, disable, refresh;
    ShellServiceLayoutReview? review;
    bool busy, closed, writing;
    public string StatusText => status.Text;
    public bool ApplyAvailable => apply.IsEnabled;
    public bool IsWorking => busy;
    public ShellServicePanel(ShellProfile profile, IShellServiceLayoutOperations? adapter = null)
    {
        profile.Validate(); proposal = profile; operations = adapter ?? new WindowsShellServiceLayoutOperations();
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
        body.Children.Add(status); detail.Margin = new Thickness(0, 10, 0, 20); detail.LineHeight = 21; body.Children.Add(detail);
        body.Children.Add(Card("已保存的开机方案", current));
        body.Children.Add(Card("本次更新方案", Text(Describe(profile), 13)));
        changes.Margin = new Thickness(3, 12, 3, 8); changes.Foreground = Brush("#5D6E9C"); body.Children.Add(changes);
        body.Children.Add(Text("点击更新后由 Windows 请求管理员授权。保存成功后，下次重启自动使用新规则；本次桌面继续使用原布局。方案包含任务栏、资源管理器和右键菜单；界面皮肤只改变 ClassicDesk 外观。", 12));
        Loaded += (_, _) => _ = RefreshAsync();
        Closing += (_, e) => { if (writing) e.Cancel = true; };
        Closed += (_, _) => closed = true;
        SetBusy(false);
    }
    public async Task RefreshAsync()
    {
        if (busy || closed) return; review = null; SetBusy(true); status.Text = "正在检查开机方案";
        try
        {
            var value = await operations.ReviewAsync(proposal);
            if (closed) return;
            review = value; current.Text = Describe(value.ScheduledProfile);
            status.Text = value.Pending ? "已有方案等待重启" : "开机方案已读取";
            detail.Text = value.Pending ? "下方展示的是下次启动使用的方案。再次更新会替换待生效方案，当前桌面保持原布局。" : "可将当前编辑的完整方案保存为开机规则。";
            changes.Text = value.Changes.Count == 0 ? "两套方案一致，无需更新。" : "将更新：" + string.Join("、", value.Changes);
        }
        catch (Exception e) { if (!closed) { status.Text = "暂时无法管理开机方案"; detail.Text = e.Message; } }
        finally { if (!closed) SetBusy(false); }
    }
    public Task ApplyAsync() => ExecuteAsync(false);
    public Task DisableAsync() => ExecuteAsync(true);
    async Task ExecuteAsync(bool stopping)
    {
        if (closed || busy || review is null || (!stopping && review.Changes.Count == 0)) return;
        var captured = review; writing = true; SetBusy(true);
        status.Text = stopping ? "正在停用下次开机增强" : "正在保存开机方案";
        detail.Text = "请完成 Windows 管理员授权，完成后即可关闭此窗口。";
        try
        {
            if (stopping) { await operations.DisableAsync(captured); status.Text = "下次开机增强已停用"; detail.Text = "当前桌面保留，重启后不再自动加载。安装文件和方案记录仍保留。"; }
            else { detail.Text = await operations.StageAsync(captured); status.Text = "开机方案已保存，等待重启"; }
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        { status.Text = "已取消管理员授权"; detail.Text = "本次未提交更新，可重新检查后再试。"; }
        catch (Exception e) { status.Text = "操作未完成，请重新检查"; detail.Text = e.Message; }
        finally { review = null; writing = false; SetBusy(false); }
    }
    void SetBusy(bool value) { busy = value; refresh.IsEnabled = !value; apply.IsEnabled = !value && review is { Changes.Count: > 0 }; disable.IsEnabled = !value && review is not null; }
    static string Describe(ShellProfile p) => $"{ShellPresets.DisplayName(p)} · {ShellSkins.All[ShellSkins.Index(p)].Name}\n" +
        (p.SkipTaskbarLayout ? "任务栏布局增强关闭" : p.StartOnLeft ? "开始靠左 · 应用居中" : "开始与应用居中") +
        $"\n图标 {p.IconSize} / 小图标 {p.SmallIconSize} · 栏高 {p.TaskbarHeight} · 按钮 {p.TaskbarButtonWidth} / 小按钮 {p.SmallTaskbarButtonWidth}" +
        (p.SkipTaskbarSizing ? "（尺寸增强关闭）" : "") +
        $"\n左置：系统按钮 {(p.OtherSystemButtonsOnLeft ? "开" : "关")} / 开始菜单 {(p.StartMenuOnLeft ? "开" : "关")} / 搜索 {(p.SearchMenuOnLeft ? "开" : "关")}" +
        $"\n紧凑托盘 {(p.CompactTray ? "开" : "关")} · " + (p.FollowMaximizedTheme ? "最大化背景不透明，桌面透明" : p.TranslucentTaskbar ? "始终透明" : "系统原生背景") +
        "\n资源管理器：" + (p.ClassicRibbon ? "Win10 功能区（无标签页）" : p.UseClassicNavigationBar ? "经典导航栏（保留标签页）" : "Win11 原生") +
        "\n右键菜单：" + (p.ClassicContextMenu ? "完整菜单" + (p.ClassicMenuWithCtrl ? " · Ctrl 临时新版" : "") : "Win11 原生");
    static TextBlock Text(string text, double size, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.8 };
    static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    static Border Card(string title, TextBlock content)
    {
        var body = new StackPanel { Margin = new Thickness(16) }; body.Children.Add(Text(title, 12, true)); content.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(content);
        return new Border { Child = body, CornerRadius = new CornerRadius(10), Background = Brushes.White, BorderBrush = Brush("#E0E5F0"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
    }
    static Button Action(string label, Func<Task> action)
    { var button = new Button { Content = label, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(8, 0, 0, 0) }; button.Click += async (_, _) => await action(); return button; }
}
