using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassicDesk;

public interface IFrontendUpdateActions
{
    string StatusText { get; }
    bool IsBusy { get; }
    bool Enabled { get; }
    bool CanDownload { get; }
    bool CanInstall { get; }
    long DownloadBytes { get; }
    event Action? Changed;
    Task CheckAsync(bool manual);
    Task DownloadAsync(bool approvedLargeDownload);
    void SetEnabled(bool enabled);
    void Install();
}

public sealed class FrontendUpdatePanel : Window
{
    readonly IFrontendUpdateActions actions;
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 20, 0, 20) };
    readonly CheckBox automatic = new() { Content = "启动后自动检查并暂存小更新", Margin = new Thickness(0, 10, 0, 16) };
    readonly Button check, download, install;
    readonly Func<string, bool> confirm;
    bool syncing, closed;
    public string StatusText => status.Text;
    public bool InstallAvailable => install.IsEnabled;
    public FrontendUpdatePanel(IFrontendUpdateActions actions, Func<string, bool>? confirm = null)
    {
        this.actions = actions;
        this.confirm = confirm ?? (text => IsVisible && MessageBox.Show(this, text, "ClassicDesk · 软件更新", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK);
        Title = "ClassicDesk · 软件更新"; Width = 600; Height = 430; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Microsoft YaHei UI"); Icon = AppIcons.Get("brand");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(FrontendUpdatePanel).Assembly.GetName().Name};component/ShellTheme.xaml", UriKind.Relative) });
        var root = new StackPanel { Margin = new Thickness(28) };
        root.Children.Add(new TextBlock { Text = "前端软件更新", FontSize = 23, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "来自官方 NOXEVYR/classicdesk · Windows x64\n增强引擎独立管理，更新不会启用或修改系统外观。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) });
        AutomationProperties.SetAutomationId(status, "update-status"); root.Children.Add(status);
        automatic.Style = (Style)FindResource("ShellFeatureChoice"); root.Children.Add(automatic);
        root.Children.Add(new TextBlock { Text = "每日至多自动检查一次，小于等于 50 MiB 的差异文件可自动暂存。安装始终由你选择；未保存草稿和外观操作会阻止退出。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 20) });
        var buttons = new WrapPanel(); root.Children.Add(buttons);
        Button Add(string caption, string id, Action click)
        { var b = new Button { Content = caption, Margin = new Thickness(0, 0, 10, 8) }; AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => click(); buttons.Children.Add(b); return b; }
        check = Add("检查更新", "update-check", () => _ = InvokeAsync(() => actions.CheckAsync(true)));
        download = Add("下载更新", "update-download", () => _ = DownloadAsync());
        install = Add("安装并重新打开", "update-install", InstallExplicitly); install.Style = (Style)FindResource("ShellPrimaryButton");
        automatic.Checked += (_, _) => SetEnabled(true); automatic.Unchecked += (_, _) => SetEnabled(false);
        Content = new ScrollViewer { Content = root, Background = (Brush)FindResource("WorkspaceBrush"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Background = (Brush)FindResource("WorkspaceBrush");
        actions.Changed += Refresh;
        Closed += (_, _) => { closed = true; actions.Changed -= Refresh; };
        Refresh();
    }
    void SetEnabled(bool value)
    {
        if (syncing || closed) return;
        try { actions.SetEnabled(value); Refresh(); }
        catch (Exception e) {
            syncing = true;
            try { automatic.IsChecked = actions.Enabled; }
            finally { syncing = false; }
            status.Text = "未保存更新偏好：" + e.Message;
        }
    }
    void Refresh()
    {
        if (closed) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(Refresh); return; }
        syncing = true; automatic.IsChecked = actions.Enabled; syncing = false;
        status.Text = actions.StatusText; automatic.IsEnabled = !actions.IsBusy;
        check.IsEnabled = !actions.IsBusy; download.IsEnabled = !actions.IsBusy && actions.CanDownload; install.IsEnabled = !actions.IsBusy && actions.CanInstall;
    }
    async Task InvokeAsync(Func<Task> task)
    { try { await task(); } catch (Exception e) { if (!closed) status.Text = "更新未完成：" + e.Message; } }
    async Task DownloadAsync()
    {
        bool large = actions.DownloadBytes > FrontendUpdateFiles.AutoDownloadLimit;
        if (large && !confirm($"本次差异文件共 {actions.DownloadBytes / 1048576d:F1} MiB，超过自动下载上限。现在下载？")) return;
        await InvokeAsync(() => actions.DownloadAsync(large));
    }
    void InstallExplicitly()
    {
        if (!actions.CanInstall || actions.IsBusy || !confirm("保存或撤销草稿后，将关闭此设置程序，安装已校验的前端更新并重新打开。系统增强继续保持现状。现在安装？")) return;
        try { actions.Install(); } catch (Exception e) { status.Text = "未开始安装：" + e.Message; }
    }
}
