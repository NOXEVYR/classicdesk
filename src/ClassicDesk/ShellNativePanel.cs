using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace ClassicDesk;

public sealed record ShellNativeReview(string Title, string Detail, bool CanEnable = false, bool CanRestore = false,
    ActivationConfirmation? EnableTicket = null, ActivationRestoreConfirmation? RestoreTicket = null);

public interface IShellNativeController
{
    Task<ShellNativeReview> ReviewAsync(ShellProfile profile);
    Task<ActivationResult> EnableAsync(ShellNativeReview review);
    Task<ActivationResult> RestoreAsync(ShellNativeReview review);
}

/// <summary>A user-opened review sheet. Construction does not inspect or change the host.</summary>
public sealed class ShellNativePanel : Window
{
    readonly IShellNativeController controller;
    readonly ShellProfile profile;
    readonly TextBlock state = Text("正在检查", 16, true);
    readonly TextBlock detail = Text("", 12);
    readonly Button enable;
    readonly Button restore;
    readonly Button refresh;
    ShellNativeReview? review;
    bool busy, closed, applying;
    int generation;
    public bool IsWorking => busy;
    public string StatusText => state.Text;
    public bool EnableAvailable => enable.IsEnabled;
    public bool RestoreAvailable => restore.IsEnabled;

    public ShellNativePanel(ShellProfile proposal, IShellNativeController operations)
    {
        profile = proposal; profile.Validate(); controller = operations;
        Title = "ClassicDesk · 启用与恢复"; Width = 620; Height = 550; MinWidth = 520; MinHeight = 460;
        Background = Color("#F6F7F9"); Foreground = Color("#20232A");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12; Icon = AppIcons.Get("brand");
        UseLayoutRounding = true; SnapsToDevicePixels = true; TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClassicDesk;component/ShellTheme.xaml", UriKind.Relative) });
        var root = new Grid { Background = Background };
        root.RowDefinitions.Add(new() { Height = new GridLength(42) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = new Border { Background = Background, BorderBrush = Color("#DDE2E9"), BorderThickness = new Thickness(1), Child = root }; root.Children.Add(Caption());
        var title = new StackPanel { Margin = new Thickness(24, 9, 24, 0) }; title.Children.Add(Text("启用原生增强", 18, true));
        var intro = Text("先核对这次方案，再开始切换。", 11); intro.Foreground = Color("#7F8997"); intro.Margin = new Thickness(0, 5, 0, 15); title.Children.Add(intro); Grid.SetRow(title, 1); root.Children.Add(title);
        var body = new StackPanel { Margin = new Thickness(24, 0, 24, 12) }; var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var summary = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };
        SummaryRow(summary, "任务栏", profile.StartOnLeft ? "开始靠左，应用居中" : "开始与应用居中");
        SummaryRow(summary, "尺寸", $"图标 {profile.IconSize} · 栏高 {profile.TaskbarHeight} · 按钮宽 {profile.TaskbarButtonWidth} px");
        SummaryRow(summary, "资源管理器", profile.ClassicRibbon ? "Windows 10 功能区 · 无标签页" : profile.UseClassicNavigationBar ? "经典导航栏" : "Windows 11 当前布局");
        SummaryRow(summary, "右键菜单", profile.ClassicContextMenu ? "完整菜单" + (profile.ClassicMenuWithCtrl ? " · Ctrl 临时打开新版" : "") : "Windows 11 菜单");
        body.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(9), BorderBrush = Color("#E1E6EE"), BorderThickness = new Thickness(1), Child = summary });
        var statusBox = new StackPanel { Margin = new Thickness(2, 20, 2, 0) }; state.FontSize = 15; statusBox.Children.Add(state); detail.Margin = new Thickness(0, 7, 0, 0); detail.Foreground = Color("#656C77"); detail.LineHeight = 20; statusBox.Children.Add(detail); body.Children.Add(statusBox);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        var note = Text("首次切换尚未实机验收。增强效果需要后台引擎运行，设置窗口可以退出。", 11); note.Foreground = Color("#8A7351"); note.LineHeight = 18; note.Margin = new Thickness(2, 17, 2, 8); body.Children.Add(note);
        var actions = new Grid { Margin = new Thickness(24, 12, 24, 13) }; actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var footer = new Border { Background = Color("#FCFCFD"), BorderBrush = Color("#E4E7ED"), BorderThickness = new Thickness(0, 1, 0, 0), Child = actions }; Grid.SetRow(footer, 3); root.Children.Add(footer);
        refresh = Button("重新检查", () => _ = RefreshAsync()); refresh.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(refresh);
        var right = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(right, 1); actions.Children.Add(right);
        restore = Button("恢复原设置", () => _ = RestoreAsync()); restore.Margin = new Thickness(8, 0, 8, 0); restore.IsEnabled = false; right.Children.Add(restore);
        enable = Button("启用此方案", () => _ = EnableAsync()); enable.Background = Color("#337CE9"); enable.Foreground = Brushes.White; enable.BorderBrush = Color("#2C73DE"); enable.IsEnabled = false; right.Children.Add(enable);
        AutomationProperties.SetAutomationId(enable, "native-enable"); AutomationProperties.SetAutomationId(restore, "native-restore");
        Loaded += (_, _) => _ = RefreshAsync();
        Closing += (_, e) => { if (applying) { e.Cancel = true; state.Text = "正在保存切换结果"; detail.Text = "完成后即可关闭。恢复记录会保留在本机。"; } };
        Closed += (_, _) => { closed = true; generation++; };
    }

    FrameworkElement Caption()
    {
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(23, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; brand.Children.Add(AppIcons.View("brand", 23));
        var name = Text("ClassicDesk", 14, true); name.VerticalAlignment = VerticalAlignment.Center; name.Margin = new Thickness(8, 0, 0, 0); brand.Children.Add(name);
        var context = Text("启用与恢复", 11); context.Foreground = Color("#89929F"); context.Margin = new Thickness(11, 1, 0, 0); context.VerticalAlignment = VerticalAlignment.Center; brand.Children.Add(context); header.Children.Add(brand);
        var close = Button("关闭", Close); close.Style = (Style)FindResource("ShellCloseButton"); close.Padding = new Thickness(0);
        var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L9,9 M0,9 L9,0"), Width = 10, Height = 10, Stretch = Stretch.Uniform, StrokeThickness = 1.1, SnapsToDevicePixels = true };
        glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = close }); close.Content = glyph;
        WindowChrome.SetIsHitTestVisibleInChrome(close, true); AutomationProperties.SetAutomationId(close, "native-close"); AutomationProperties.SetName(close, "关闭"); Grid.SetColumn(close, 1); header.Children.Add(close); return header;
    }

    public async Task RefreshAsync()
    {
        if (busy || closed) return; SetBusy(true); review = null; int request = ++generation;
        state.Text = "正在检查"; detail.Text = "核对增强组件、现有工具和恢复记录…";
        try
        {
            var result = await controller.ReviewAsync(profile).WaitAsync(TimeSpan.FromSeconds(10));
            if (closed || request != generation) return;
            review = result; state.Text = result.Title; detail.Text = result.Detail;
        }
        catch (TimeoutException) { if (!closed) { state.Text = "检查超时"; detail.Text = "稍后可重试，本次没有进行切换。"; } }
        catch (Exception e) { if (!closed) { state.Text = "暂时无法切换"; detail.Text = e.Message; } }
        finally { if (!closed) SetBusy(false); }
    }

    public Task EnableAsync() => ExecuteAsync(false);
    public Task RestoreAsync() => ExecuteAsync(true);
    async Task ExecuteAsync(bool restoring)
    {
        if (busy || closed || review is null || (restoring ? !review.CanRestore : !review.CanEnable)) return;
        var captured = review; SetBusy(true); applying = true;
        state.Text = restoring ? "正在恢复" : "正在启用";
        detail.Text = "正在核对文件修订，并保存恢复记录。此过程不会重启资源管理器。";
        try
        {
            // A host mutation must finish its durable journal; do not detach it on a UI timeout.
            var result = await (restoring ? controller.RestoreAsync(captured) : controller.EnableAsync(captured));
            review = null;
            state.Text = result.State switch { ShellActivationState.Active => "增强引擎已启动", ShellActivationState.Restored => "原设置已恢复", ShellActivationState.RolledBack => "切换未完成，已回退", _ => "需要检查恢复记录" };
            detail.Text = result.State switch {
                ShellActivationState.Active => "配置与进程已回读。请检查任务栏与资源管理器的实际效果；实机效果尚未验收。",
                ShellActivationState.Restored => "已恢复本次事务拥有的原始设置，并确认自己的引擎退出。请检查实际桌面。",
                _ => result.Error ?? "已保留记录，未把不确定状态当作成功。" };
            detail.Text += "\n记录：" + result.JournalId.ToString("N");
        }
        catch (Exception e) { review = null; state.Text = "操作未完成"; detail.Text = e.Message + "\n重新检查后再继续，当前错误不会触发自动重试。"; }
        finally { applying = false; SetBusy(false); }
    }
    void SetBusy(bool value) { busy = value; refresh.IsEnabled = !value; enable.IsEnabled = !value && review?.CanEnable == true; restore.IsEnabled = !value && review?.CanRestore == true; }
    static SolidColorBrush Color(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static TextBlock Text(string value, double size, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    static Button Button(string text, Action action) { var b = new Button { Content = text, Padding = new Thickness(13, 8, 13, 8) }; b.Click += (_, _) => action(); return b; }
    static void SummaryRow(Panel panel, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(95) }); row.ColumnDefinitions.Add(new());
        var name = Text(label, 12); name.Foreground = Color("#777C86"); row.Children.Add(name); var content = Text(value, 12); Grid.SetColumn(content, 1); row.Children.Add(content); panel.Children.Add(row);
    }
}
