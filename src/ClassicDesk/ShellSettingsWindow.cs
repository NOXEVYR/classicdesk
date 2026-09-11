using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace ClassicDesk;

/// <summary>Compact settings editor. Saving a draft never claims a live Shell change.</summary>
public sealed class ShellSettingsWindow : Window
{
    readonly StackPanel settings = new();
    readonly TextBlock heading = Label("", 18, true);
    readonly TextBlock description = Label("", 11);
    readonly TextBlock feedback = Label("方案演示 · 尚未应用到 Windows", 11);
    readonly TextBlock draftBadge = Label("已保存", 11);
    readonly ShellPreview preview = new();
    readonly List<Button> routes = [];
    readonly List<Button> comparisons = [];
    readonly Dictionary<string, FrameworkElement> dependentControls = new();
    readonly Button save, undo, inspectButton;
    readonly string profilePath;
    readonly Func<ShellProfile, Task<string>> inspect;
    readonly Action<Window, ShellProfile>? manageNative;
    readonly ScrollViewer scroll;
    ShellProfile saved;
    string? sourceRevision;
    int page;
    bool busy, closed, advancedExpanded;
    CancellationTokenSource? inspection;
    public ShellProfile Draft { get; private set; }
    public int ActivePage => page;

    public ShellSettingsWindow(ShellProfile? initial = null, string? path = null, Func<ShellProfile, Task<string>>? inspectPlan = null, Action<Window, ShellProfile>? manageNative = null)
    {
        profilePath = path ?? ShellProfileFile.DefaultPath; this.manageNative = manageNative;
        string? loadError = null;
        try { var snapshot = ShellProfileFile.Load(profilePath); saved = initial ?? snapshot.Profile; saved.Validate(); sourceRevision = snapshot.Revision; }
        catch (Exception e) { saved = new(); loadError = "原方案读取失败，暂用演示方案：" + e.Message; }
        Draft = saved;
        inspect = inspectPlan ?? (_ => Task.FromResult("原生增强底层尚未就绪。当前操作只编辑方案，不会修改 Windows。"));
        Title = "ClassicDesk · 桌面设置"; Width = 820; Height = 610; MinWidth = 740; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Icon = AppIcons.Get("brand");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Background = Brush("#F6F7F9"); Foreground = Brush("#252A33");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
        UseLayoutRounding = true; SnapsToDevicePixels = true; TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClassicDesk;component/ShellTheme.xaml", UriKind.Relative) });
        var root = new Grid { Background = Background }; root.RowDefinitions.Add(new() { Height = new GridLength(42) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = new Border { Background = Background, BorderBrush = Brush("#DDE2E9"), BorderThickness = new Thickness(1), Child = root }; root.Children.Add(BuildCaption());
        var navigation = new Grid { Margin = new Thickness(24, 5, 24, 13) }; navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); navigation.ColumnDefinitions.Add(new());
        var segments = new Grid { Margin = new Thickness(3) }; for (int i = 0; i < 3; i++) segments.ColumnDefinitions.Add(new() { Width = new GridLength(128) });
        var names = new[] { "任务栏", "资源管理器", "右键菜单" }; var icons = new[] { "taskbar", "folder", "context-menu" };
        for (int i = 0; i < names.Length; i++)
        {
            int index = i; var route = ActionButton(names[i], () => SelectPage(index)); route.Style = (Style)FindResource("ShellSegment");
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center }; row.Children.Add(AppIcons.View(icons[i], 18)); var text = Label(names[i], 12); text.Margin = new Thickness(7, 0, 0, 0); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text); route.Content = row;
            Grid.SetColumn(route, i); AutomationProperties.SetAutomationId(route, "shell-nav-" + i); routes.Add(route); segments.Children.Add(route);
        }
        navigation.Children.Add(new Border { Background = Brush("#E9EDF2"), CornerRadius = new CornerRadius(9), Child = segments });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }; meta.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = Brush("#A3ADBA"), Margin = new Thickness(0, 0, 7, 0) }); draftBadge.Foreground = Brush("#89929F"); meta.Children.Add(draftBadge); Grid.SetColumn(meta, 1); navigation.Children.Add(meta); Grid.SetRow(navigation, 1); root.Children.Add(navigation);
        var content = new StackPanel { Margin = new Thickness(24, 0, 24, 16) }; scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var pageHeader = new Grid(); pageHeader.ColumnDefinitions.Add(new()); pageHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var words = new StackPanel(); words.Children.Add(heading); description.Foreground = Brush("#8A929E"); description.Margin = new Thickness(0, 4, 0, 0); words.Children.Add(description); pageHeader.Children.Add(words);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 2; i++) { int index = i; var comparison = ActionButton(i == 0 ? "Win11 参考" : "我的方案", () => SetComparison(index == 0)); comparison.Style = (Style)FindResource("ShellPreviewSegment"); comparison.ToolTip = i == 0 ? "Windows 11 默认样式示意，不是当前系统截图。" : "根据下方参数绘制的目标方案；保存不会直接应用。"; comparisons.Add(comparison); tabs.Children.Add(comparison); }
        tools.Children.Add(new Border { Background = Brush("#E9EDF2"), CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Child = tabs });
        var reset = ActionButton("重置本页", ResetPage); reset.Style = (Style)FindResource("ShellQuietButton"); reset.Margin = new Thickness(9, 0, 0, 0); reset.ToolTip = "重置此页方案；保存后才写入方案文件。"; AutomationProperties.SetAutomationId(reset, "shell-reset-page"); tools.Children.Add(reset); Grid.SetColumn(tools, 1); pageHeader.Children.Add(tools); content.Children.Add(pageHeader);
        preview.Margin = new Thickness(0, 7, 0, 12); content.Children.Add(preview); content.Children.Add(settings);
        var footer = new Grid { Margin = new Thickness(24, 12, 24, 13) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        feedback.VerticalAlignment = VerticalAlignment.Center; feedback.Foreground = Brush("#7F8997"); feedback.Margin = new Thickness(0, 0, 12, 0); AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite); footer.Children.Add(feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        undo = ActionButton("撤销", DiscardDraft); undo.Style = (Style)FindResource("ShellQuietButton"); undo.IsEnabled = false; undo.Margin = new Thickness(0, 0, 8, 0); AutomationProperties.SetAutomationId(undo, "shell-undo"); actions.Children.Add(undo);
        save = ActionButton("保存方案", SaveDraft); save.IsEnabled = false; save.Margin = new Thickness(0, 0, 8, 0); AutomationProperties.SetAutomationId(save, "shell-save"); actions.Children.Add(save);
        inspectButton = ActionButton("检查应用", () => { if (this.manageNative is null) _ = InspectAsync(); else this.manageNative(this, Draft); }, true); AutomationProperties.SetAutomationId(inspectButton, "shell-inspect"); actions.Children.Add(inspectButton);
        var footerBorder = new Border { Background = Brush("#FCFCFD"), BorderBrush = Brush("#E4E7ED"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer }; Grid.SetRow(footerBorder, 3); root.Children.Add(footerBorder);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveDraft(); e.Handled = true; } };
        Closing += (_, e) => { if (IsVisible && Draft != saved && MessageBox.Show(this, "方案尚未保存。关闭后放弃本次编辑？", "ClassicDesk", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) e.Cancel = true; };
        Closed += (_, _) => { closed = true; inspection?.Cancel(); };
        SelectPage(0); SetComparison(false); UpdateSavedState(); if (loadError is not null) feedback.Text = loadError;
    }
    FrameworkElement BuildCaption()
    {
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(23, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; brand.Children.Add(AppIcons.View("brand", 23)); var name = Label("ClassicDesk", 14, true); name.VerticalAlignment = VerticalAlignment.Center; name.Margin = new Thickness(8, 0, 0, 0); brand.Children.Add(name); var version = Label("0.6", 9); version.Foreground = Brush("#A4ACB8"); version.VerticalAlignment = VerticalAlignment.Center; version.Margin = new Thickness(8, 1, 0, 0); brand.Children.Add(version); header.Children.Add(brand);
        var system = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(system, 1); header.Children.Add(system);
        system.Children.Add(CaptionButton("最小化", "M 0,5 L 10,5", () => SystemCommands.MinimizeWindow(this)));
        var maximize = CaptionButton("最大化", "M 0,0 L 9,0 L 9,9 L 0,9 Z", () => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }); system.Children.Add(maximize);
        StateChanged += (_, _) => AutomationProperties.SetName(maximize, WindowState == WindowState.Maximized ? "还原窗口" : "最大化");
        var close = CaptionButton("关闭", "M 0,0 L 9,9 M 0,9 L 9,0", Close); close.Style = (Style)FindResource("ShellCloseButton"); system.Children.Add(close); return header;
    }
    Button CaptionButton(string title, string geometry, Action action)
    {
        var button = ActionButton(title, action); button.Style = (Style)FindResource("ShellCaptionButton");
        var shape = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 10, Height = 10, StrokeThickness = 1.1, Stretch = Stretch.Uniform, SnapsToDevicePixels = true }; shape.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = button }); button.Content = shape;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true); AutomationProperties.SetAutomationId(button, "window-" + title); return button;
    }
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    static TextBlock Label(string text, double size = 12, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontFamily = new FontFamily("Microsoft YaHei UI"), Foreground = Brush("#29313D"), TextWrapping = TextWrapping.Wrap };
    Button ActionButton(string text, Action action, bool primary = false)
    { var button = new Button { Content = text, Style = (Style)FindResource(primary ? "ShellPrimaryButton" : "ShellButton") }; AutomationProperties.SetName(button, text); button.Click += (_, _) => action(); return button; }
    public void SelectPage(int index)
    {
        if (index < 0 || index > 2) throw new ArgumentOutOfRangeException(nameof(index)); page = index;
        for (int i = 0; i < routes.Count; i++) { routes[i].Tag = i == index ? "selected" : null; var text = ((StackPanel)routes[i].Content).Children.OfType<TextBlock>().Single(); text.Foreground = Brush(i == index ? "#28364B" : "#7A8798"); text.FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal; }
        settings.Children.Clear(); dependentControls.Clear(); scroll.ScrollToTop(); preview.Kind = (ShellPreviewKind)index; preview.Height = index switch { 0 => 108, 1 => 176, _ => 174 };
        heading.Text = new[] { "任务栏布局", "资源管理器样式", "菜单体验" }[index]; description.Text = new[] { "调整开始按钮、图标和按钮尺寸", "在同一个资源管理器中切换工具区", "保留熟悉的完整菜单和快捷操作" }[index];
        if (index == 0)
        {
            var primary = Group();
            AddRow(primary, "开始按钮位置", "应用图标保持居中。", Choice(["靠左", "居中"], Draft.StartOnLeft ? 0 : 1, i => Change(Draft with { StartOnLeft = i == 0 })));
            AddRow(primary, "图标大小", "任务栏应用图标的尺寸。", PixelChoice(ShellProfile.IconSizes, Draft.IconSize, value => Change(Draft with { IconSize = value })));
            AddRow(primary, "任务栏高度", "任务栏的整体高度。", PixelChoice(ShellProfile.TaskbarHeights, Draft.TaskbarHeight, value => Change(Draft with { TaskbarHeight = value })));
            AddRow(primary, "按钮宽度", "按钮宽度控制图标两侧留白；与图标大小分别设置。", PixelChoice(ShellProfile.ButtonWidths, Draft.TaskbarButtonWidth, value => Change(Draft with { TaskbarButtonWidth = value })), true);
            var advancedRows = new StackPanel();
            AddRow(advancedRows, "小图标尺寸", "Windows 使用小图标模式时的尺寸。", PixelChoice(ShellProfile.IconSizes, Draft.SmallIconSize, value => Change(Draft with { SmallIconSize = value })));
            AddRow(advancedRows, "小按钮宽度", "Windows 使用小图标模式时的按钮宽度。", PixelChoice(ShellProfile.ButtonWidths, Draft.SmallTaskbarButtonWidth, value => Change(Draft with { SmallTaskbarButtonWidth = value })));
            var system = Toggle(Draft.OtherSystemButtonsOnLeft, value => Change(Draft with { OtherSystemButtonsOnLeft = value })); dependentControls["otherButtons"] = system; AddRow(advancedRows, "其他系统按钮靠左", "搜索、任务视图和小组件；开始按钮居中时不生效。", system);
            var start = Toggle(Draft.StartMenuOnLeft, value => Change(Draft with { StartMenuOnLeft = value })); dependentControls["startMenu"] = start; AddRow(advancedRows, "开始菜单靠左展开", "开始按钮居中时不生效。", start);
            var search = Toggle(Draft.SearchMenuOnLeft, value => Change(Draft with { SearchMenuOnLeft = value })); dependentControls["searchMenu"] = search; AddRow(advancedRows, "所有入口的搜索都靠左", "包含 Win+S 和任务栏搜索；需要开始菜单靠左。", search, true);
            var advanced = new Expander { Header = "高级布局", Content = advancedRows, IsExpanded = advancedExpanded, Style = (Style)FindResource("ShellExpander"), Margin = new Thickness(0, 10, 0, 0) }; AutomationProperties.SetAutomationId(advanced, "shell-advanced"); advanced.Expanded += (_, _) => advancedExpanded = true; advanced.Collapsed += (_, _) => advancedExpanded = false; settings.Children.Add(advanced);
        }
        else if (index == 1)
        {
            var group = Group(); int style = Draft.ClassicRibbon ? 1 : Draft.UseClassicNavigationBar ? 2 : 0;
            AddRow(group, "工具区样式", "功能区与经典导航栏是不同模式。", Choice(["Windows 11 命令栏", "Windows 10 功能区", "经典导航栏"], style, i => Change(Draft with { ClassicRibbon = i == 1, UseClassicNavigationBar = i == 2 }), 210), true);
            AddNote(Draft.ClassicRibbon ? "主页、共享、查看完整展开；此模式不保留 Windows 11 标签页。" : Draft.UseClassicNavigationBar ? "采用旧版 Windows 11 导航布局，保留标签页。" : "使用当前 Windows 11 命令栏与标签页。", "explorer-mode-note");
        }
        else
        {
            var group = Group(); AddRow(group, "完整右键菜单", "直接展开完整的 Shell 命令。", Toggle(Draft.ClassicContextMenu, value => Change(Draft with { ClassicContextMenu = value })));
            var ctrl = Toggle(Draft.ClassicMenuWithCtrl, value => Change(Draft with { ClassicMenuWithCtrl = value })); dependentControls["ctrlMenu"] = ctrl; AddRow(group, "按 Ctrl 临时使用新版", "开启完整菜单时，按住 Ctrl 可临时切回新版菜单。", ctrl, true);
            AddNote("菜单命令由文件类型与已安装的扩展决定。", "context-note");
        }
        preview.Options = Draft.PreviewOptions; UpdateDependencies();
    }
    StackPanel Group()
    { var rows = new StackPanel(); settings.Children.Add(new Border { Background = Brushes.White, BorderBrush = Brush("#E1E6EE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Child = rows }); return rows; }
    ComboBox PixelChoice(int[] values, int selected, Action<int> changed) => Choice(values.Select(value => value + " px").ToArray(), Array.IndexOf(values, selected), i => changed(values[i]));
    ComboBox Choice(string[] items, int selected, Action<int> changed, double width = 146)
    { var combo = new ComboBox { ItemsSource = items, SelectedIndex = selected, Width = width, Style = (Style)FindResource("ShellChoice") }; combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); }; return combo; }
    CheckBox Toggle(bool initial, Action<bool> changed)
    { var toggle = new CheckBox { IsChecked = initial, Style = (Style)FindResource("ShellSwitch") }; toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false); return toggle; }
    void AddRow(StackPanel parent, string title, string detail, FrameworkElement control, bool last = false)
    {
        var row = new Grid { Margin = new Thickness(14, 7, 14, 7), MinHeight = 30 }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = Label(title, 12); text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Thickness(0, 0, 18, 0); row.Children.Add(text); control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); row.Children.Add(control);
        AutomationProperties.SetName(control, title); AutomationProperties.SetHelpText(control, detail); control.ToolTip = detail; ToolTipService.SetShowOnDisabled(control, true); parent.Children.Add(new Border { BorderBrush = Brush("#EDF0F4"), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Child = row });
    }
    void AddNote(string text, string id)
    { var note = Label(text, 11); note.Foreground = Brush("#8B95A3"); note.Margin = new Thickness(2, 12, 2, 0); AutomationProperties.SetAutomationId(note, id); settings.Children.Add(note); }
    void UpdateDependencies()
    {
        if (dependentControls.TryGetValue("otherButtons", out var system)) system.IsEnabled = Draft.StartOnLeft;
        if (dependentControls.TryGetValue("startMenu", out var start)) start.IsEnabled = Draft.StartOnLeft;
        if (dependentControls.TryGetValue("searchMenu", out var search)) search.IsEnabled = Draft.StartOnLeft && Draft.StartMenuOnLeft;
        if (dependentControls.TryGetValue("ctrlMenu", out var ctrl)) ctrl.IsEnabled = Draft.ClassicContextMenu;
        if (page == 1)
        { var note = settings.Children.OfType<TextBlock>().SingleOrDefault(t => AutomationProperties.GetAutomationId(t) == "explorer-mode-note"); if (note is not null) note.Text = Draft.ClassicRibbon ? "主页、共享、查看完整展开；此模式不保留 Windows 11 标签页。" : Draft.UseClassicNavigationBar ? "采用旧版 Windows 11 导航布局，保留标签页。" : "使用当前 Windows 11 命令栏与标签页。"; }
    }
    void Change(ShellProfile value)
    { value.Validate(); Draft = value; preview.Options = value.PreviewOptions; SetComparison(false); UpdateDependencies(); UpdateSavedState(); feedback.Text = Draft != saved ? "方案已编辑 · 尚未应用到 Windows" : "方案演示 · 尚未应用到 Windows"; }
    void UpdateSavedState()
    { bool dirty = Draft != saved; save.IsEnabled = dirty && sourceRevision is not null; undo.IsEnabled = dirty; undo.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed; draftBadge.Text = sourceRevision is null ? "原方案受保护" : dirty ? "有未保存更改" : sourceRevision == "missing" ? "默认方案" : "已保存"; draftBadge.Foreground = Brush(dirty ? "#4A7BC4" : "#98A1AE"); }
    public void SetComparison(bool before)
    { preview.Before = before; for (int i = 0; i < comparisons.Count; i++) comparisons[i].Tag = before == (i == 0) ? "selected" : null; }
    void ResetPage()
    {
        var defaults = new ShellProfile();
        var value = page switch
        {
            0 => Draft with { StartOnLeft = defaults.StartOnLeft, IconSize = defaults.IconSize, TaskbarHeight = defaults.TaskbarHeight, TaskbarButtonWidth = defaults.TaskbarButtonWidth, SmallIconSize = defaults.SmallIconSize, SmallTaskbarButtonWidth = defaults.SmallTaskbarButtonWidth, OtherSystemButtonsOnLeft = defaults.OtherSystemButtonsOnLeft, StartMenuOnLeft = defaults.StartMenuOnLeft, SearchMenuOnLeft = defaults.SearchMenuOnLeft },
            1 => Draft with { ClassicRibbon = defaults.ClassicRibbon, UseClassicNavigationBar = defaults.UseClassicNavigationBar },
            _ => Draft with { ClassicContextMenu = defaults.ClassicContextMenu, ClassicMenuWithCtrl = defaults.ClassicMenuWithCtrl }
        };
        Change(value); SelectPage(page); feedback.Text = "本页已重置到默认方案 · 尚未应用到 Windows";
    }
    void DiscardDraft() { Change(saved); SelectPage(page); feedback.Text = "已撤销未保存的更改 · Windows 未修改"; }
    public void SaveDraft()
    { try { if (sourceRevision is null) throw new InvalidOperationException("原方案读取失败，已保护原文件；请先处理文件错误。"); sourceRevision = ShellProfileFile.Save(profilePath, Draft, sourceRevision); saved = Draft; UpdateSavedState(); feedback.Text = "方案已保存 · Windows 尚未修改"; } catch (Exception e) { feedback.Text = "保存失败，编辑仍保留：" + e.Message; } }
    async Task InspectAsync()
    {
        if (busy || closed) return; busy = true; var captured = Draft; inspection = new CancellationTokenSource(); inspectButton.IsEnabled = false; feedback.Text = "正在检查原生底层与兼容性…";
        try
        {
            var result = await inspect(captured).WaitAsync(TimeSpan.FromSeconds(8), inspection.Token); if (closed) return;
            if (captured != Draft) { feedback.Text = "方案已变化，请重新检查应用条件。"; return; }
            if (!IsVisible) { feedback.Text = "检查完成 · Windows 尚未修改"; return; }
            var dialog = new Window { Owner = this, Title = "ClassicDesk · 应用检查", Width = 580, Height = 460, MinWidth = 480, MinHeight = 360, FontFamily = FontFamily, FontSize = 12, Background = Background, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClassicDesk;component/ShellTheme.xaml", UriKind.Relative) });
            var panel = new DockPanel { Margin = new Thickness(22) }; dialog.Content = panel; var close = ActionButton("返回设置", () => dialog.Close(), true); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 14, 0, 0); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
            var resultText = Label(result, 11); resultText.LineHeight = 21; panel.Children.Add(new ScrollViewer { Content = resultText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); dialog.ShowDialog(); feedback.Text = "检查完成 · Windows 尚未修改";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (TimeoutException) { if (!closed) feedback.Text = "检查超时，未修改 Windows；可继续编辑或关闭。"; }
        catch (Exception e) { if (!closed) feedback.Text = "检查未完成：" + e.Message; }
        finally { busy = false; inspection?.Dispose(); inspection = null; if (!closed) inspectButton.IsEnabled = true; }
    }
}

