using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassicDesk;

/// <summary>Opening only reads. Apply and restore require distinct explicit clicks.</summary>
public sealed class TaskbarAutoHidePanel : Window
{
    readonly TaskbarAutoHideController controller;
    readonly TextBlock status = Text("尚未检查", 20, true), detail = Text("", 12), current = Text("未读取", 14, true);
    readonly CheckBox choice = new() { Content = "自动隐藏 Windows 任务栏", FontSize = 14, Margin = new Thickness(0, 10, 0, 10) };
    readonly Button check, apply, restore;
    TaskbarAutoHideReview? review;
    bool busy, writing, closed;
    public string StatusText => status.Text;
    public bool ApplyAvailable => apply.IsEnabled;
    public bool RestoreAvailable => restore.IsEnabled;
    public bool PlannedEnabled { get => choice.IsChecked == true; set => choice.IsChecked = value; }
    public TaskbarAutoHidePanel(TaskbarAutoHideController controller)
    {
        this.controller = controller;
        Title = "ClassicDesk · 原生自动隐藏"; Width = 620; Height = 560; MinWidth = 520; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Microsoft YaHei UI");
        Icon = AppIcons.Get("brand");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(TaskbarAutoHidePanel).Assembly.GetName().Name};component/ShellTheme.xaml", UriKind.Relative) });
        choice.Style = (Style)FindResource("ShellFeatureChoice");
        Background = Brush("#F5F7FC"); Foreground = Brush("#26344E"); UseLayoutRounding = true;
        var root = new DockPanel { Margin = new Thickness(24), Background = Background }; Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        check = Button("重新检查", RefreshAsync); restore = Button("恢复原状态", RestoreAsync); apply = Button("应用自动隐藏", ApplyAsync);
        apply.Background = Brush("#637BDC"); apply.Foreground = Brushes.White;
        footer.Children.Add(check); footer.Children.Add(restore); footer.Children.Add(apply);
        AutomationProperties.SetAutomationId(apply, "autohide-apply"); AutomationProperties.SetAutomationId(restore, "autohide-restore");
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var eyebrow = Text("原生任务栏 / 当前桌面", 11, true); eyebrow.Foreground = Brush("#697CAA"); body.Children.Add(eyebrow);
        status.Margin = new Thickness(0, 8, 0, 8); body.Children.Add(status); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        detail.Margin = new Thickness(0, 0, 0, 18); body.Children.Add(detail);
        var settings = new StackPanel(); settings.Children.Add(Text("检查到的状态", 11, true)); current.Margin = new Thickness(0, 6, 0, 14); settings.Children.Add(current);
        settings.Children.Add(new Separator()); settings.Children.Add(choice); settings.Children.Add(Text("勾选仅改变本次计划，点击应用才会修改任务栏。", 11));
        body.Children.Add(new Border { Child = settings, Padding = new Thickness(18), Background = Brushes.White, CornerRadius = new CornerRadius(12), BorderBrush = Brush("#E0E6F1"), BorderThickness = new Thickness(1) });
        var note = Text("此操作独立于布局方案和开机服务，仅管理当前用户桌面的原生自动隐藏状态，不提供逐屏独立设置。不会重启资源管理器，也不会自动重试。\n\nStartAllBack / StartIsBack 仍加载时，本窗口会阻止原生设置操作。状态回读不等于实际效果验收；请检查隐藏、鼠标触边唤出和全屏表现。", 12); note.Foreground = Brush("#68758C"); note.Margin = new Thickness(0, 16, 0, 0); body.Children.Add(note);
        choice.Checked += (_, _) => UpdateButtons(); choice.Unchecked += (_, _) => UpdateButtons();
        Loaded += (_, _) => _ = RefreshAsync();
        Closing += (_, e) => { if (writing) { e.Cancel = true; detail.Text = "正在完成状态与恢复记录，请稍候再关闭。"; } };
        Closed += (_, _) => closed = true;
        UpdateButtons();
    }
    public async Task RefreshAsync()
    {
        if (busy || closed) return; busy = true; review = null; current.Text = "正在读取…"; status.Text = "正在检查任务栏"; detail.Text = "只读检查当前桌面及本地恢复记录。"; UpdateButtons();
        try
        {
            var result = await Task.Run(controller.Review);
            if (closed) return;
            review = result; status.Text = result.Title; detail.Text = result.Detail; current.Text = result.Current.Enabled ? "自动隐藏已开启" : "自动隐藏已关闭";
            choice.IsChecked = result.Current.Enabled;
        }
        catch (Exception e) { if (!closed) { status.Text = "暂时无法管理自动隐藏"; detail.Text = e.Message; current.Text = "未取得可确认的状态"; } }
        finally { busy = false; if (!closed) UpdateButtons(); }
    }
    public Task ApplyAsync() => ExecuteAsync(false);
    public Task RestoreAsync() => ExecuteAsync(true);
    async Task ExecuteAsync(bool restoring)
    {
        if (closed || busy || review is null || (restoring ? !restore.IsEnabled : !apply.IsEnabled)) return;
        var ticket = review; bool enabled = PlannedEnabled; review = null; busy = writing = true; UpdateButtons();
        status.Text = restoring ? "正在恢复原状态" : "正在提交自动隐藏设置"; detail.Text = "先保存恢复记录，再修改并回读任务栏状态。";
        try
        {
            detail.Text = await Task.Run(() => restoring ? controller.Restore(ticket) : controller.Apply(ticket, enabled));
            status.Text = restoring ? "恢复已回读确认" : "设置已回读确认";
            current.Text = "本次操作已完成，继续管理前请重新检查。";
        }
        catch (Exception e) { status.Text = "操作未完成，请重新检查"; detail.Text = e.Message + "\n不会自动重试；已有恢复记录保持保留。"; current.Text = "结果待检查"; }
        finally { busy = writing = false; UpdateButtons(); }
    }
    void UpdateButtons()
    {
        check.IsEnabled = !busy; choice.IsEnabled = !busy && review?.CanApply == true;
        apply.IsEnabled = !busy && review?.CanApply == true && PlannedEnabled != review.Current.Enabled;
        restore.IsEnabled = !busy && review?.CanRestore == true;
    }
    static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    static TextBlock Text(string text, double size, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.65 };
    static Button Button(string label, Func<Task> action) { var button = new Button { Content = label, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(8, 0, 0, 0) }; button.Click += async (_, _) => await action(); return button; }
}
