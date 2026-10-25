using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassicDesk;

public sealed class ShellExplorerRebuildPanel : Window
{
    readonly IShellExplorerRebuildHost host;
    readonly ListBox choices = new() { MinHeight = 160, MaxHeight = 260, DisplayMemberPath = "Label" };
    readonly TextBlock state = new() { Text = "尚未读取窗口", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly CheckBox closeConsent = new() { Content = "替代窗口的目录和 Win10 结构核对一致后，关闭所选旧窗口", Margin = new Thickness(0, 10, 0, 10) };
    readonly Button inspect = new() { Content = "读取现有窗口", Padding = new Thickness(12, 8, 12, 8) };
    readonly Button rebuild = new() { Content = "重建所选窗口", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(12, 0, 0, 0) };
    ExplorerRebuildSnapshot? snapshot; CancellationTokenSource? operation; bool closed;
    public string StatusText => state.Text;
    public bool RebuildAvailable => rebuild.IsEnabled;
    public int WindowCount => choices.Items.Count;
    public ShellExplorerRebuildPanel(IShellExplorerRebuildHost? host = null)
    {
        this.host = host ?? new WindowsExplorerRebuildHost();
        Title = "ClassicDesk · 重建旧文件窗口"; Width = 760; Height = 520; MinWidth = 600; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        var body = new StackPanel { Margin = new Thickness(24) }; Content = body;
        body.Children.Add(new TextBlock { Text = "保留目录，重建 Win10 文件窗口", FontSize = 21, FontWeight = FontWeights.SemiBold });
        body.Children.Add(state); body.Children.Add(choices); body.Children.Add(closeConsent);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(inspect); buttons.Children.Add(rebuild); body.Children.Add(buttons);
        body.Children.Add(new TextBlock { Text = "仅处理可核对的单标签本地文件夹。请先取消文件选择、结束窗口内操作。多标签、虚拟目录、隐藏窗口或信息不完整时保留原窗口。新窗口尚未确认前不会关闭旧窗口；不重启资源管理器。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });
        inspect.Click += async (_, _) => await InspectAsync(); rebuild.Click += async (_, _) => await RebuildAsync();
        choices.SelectionChanged += (_, _) => { closeConsent.IsChecked = false; Update(); };
        closeConsent.Checked += (_, _) => Update(); closeConsent.Unchecked += (_, _) => Update();
        Closed += (_, _) => { closed = true; operation?.Cancel(); }; Update();
    }
    public async Task InspectAsync()
    {
        if (closed || operation is not null) return;
        snapshot = null; choices.Items.Clear(); closeConsent.IsChecked = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)); operation = cancellation; Update();
        try
        {
            var current = await host.ObserveAsync(DateTimeOffset.UtcNow.AddSeconds(10), cancellation.Token);
            if (closed) return; snapshot = current;
            foreach (var w in current.Windows)
                choices.Items.Add(new Row(w, $"窗口 {w.Id.Handle:X} · {(w.RibbonStructureVerified ? "Win10" : "待重建/未确认")} · {w.CanonicalLocation ?? "位置未确认"}"));
            state.Text = current.CompleteWindowInventory ? "请选择一个旧窗口；只有标签、目录和身份完整确认时才能重建。" : "窗口清单不完整，本次不会重建。";
        }
        catch (Exception e) { if (!closed) state.Text = "读取未完成，所有窗口保留。" + DescribeFailure(e); }
        finally { operation = null; Update(); }
    }
    public void SelectWindow(int index, bool consent)
    { if (operation is not null || closed) return; choices.SelectedIndex = index; closeConsent.IsChecked = consent; Update(); }
    public async Task RebuildAsync()
    {
        if (closed || operation is not null || !rebuild.IsEnabled || snapshot is null || choices.SelectedItem is not Row row) return;
        var plan = ShellExplorerRebuildCoordinator.Prepare(snapshot, row.Window.Id); if (plan is null) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; Update();
        try
        {
            state.Text = "正在核对旧窗口并打开替代窗口；原窗口暂时保留。";
            var result = await new ShellExplorerRebuildCoordinator(host).RebuildAsync(plan, TimeSpan.FromSeconds(30), cancellation.Token);
            if (!closed) state.Text = result.Detail;
        }
        catch (Exception e) { if (!closed) state.Text = "重建结果未确认，不自动重试。" + DescribeFailure(e); }
        finally { snapshot = null; closeConsent.IsChecked = false; operation = null; Update(); }
    }
    void Update()
    {
        bool idle = !closed && operation is null; inspect.IsEnabled = idle; choices.IsEnabled = idle; closeConsent.IsEnabled = idle;
        rebuild.IsEnabled = idle && closeConsent.IsChecked == true && snapshot is not null && choices.SelectedItem is Row row &&
            !row.Window.RibbonStructureVerified && ShellExplorerRebuildCoordinator.Prepare(snapshot, row.Window.Id) is not null;
    }
    static string DescribeFailure(Exception error) => error switch
    {
        ExplorerRebuildAccessException => error.Message,
        ExplorerRebuildBrokerException => error.Message,
        OperationCanceledException or TimeoutException => "窗口检查超时或已取消，请稍后重新读取。",
        _ => "辅助程序未完成读取（" + error.GetType().Name + "）。请保留当前窗口后重试读取。"
    };
    sealed record Row(ExplorerRebuildWindow Window, string Label);
}
