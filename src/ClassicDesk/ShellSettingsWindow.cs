using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.IO;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace ClassicDesk;

/// <summary>Compact settings editor. Saving a draft never claims a live Shell change.</summary>
public sealed class ShellSettingsWindow : Window
{
    readonly StackPanel settings = new();
    readonly TextBlock heading = Label("", 24, true);
    readonly TextBlock description = Label("", 12);
    readonly TextBlock feedback = Label("方案演示 · 尚未应用到 Windows", 11);
    readonly TextBlock draftBadge = Label("已保存", 11);
    readonly ShellPreview preview = new();
    readonly List<Button> routes = [];
    readonly List<Button> presetTabs = [];
    readonly Button referenceButton;
    readonly Dictionary<int, Button> sidebarRoutes = new();
    readonly FrameworkElement pageTools;
    readonly Border previewStage;
    readonly DesktopBackdrop desktopBackdrop = new();
    readonly ComboBox skinPicker;
    readonly Button animeCard;
    readonly DesktopBackdrop skinThumbnail = new() { Height = 72 };
    readonly TextBlock skinCaption = Label("", 11, true);
    bool syncingScheme;
    readonly TextBlock previewCaption = Label("", 11);
    readonly List<Button> layoutChoices = [];
    readonly Dictionary<string, FrameworkElement> dependentControls = new();
    readonly Button save, undo, inspectButton;
    readonly string profilePath;
    readonly ShellProfileLibrary library;
    ShellLibrarySnapshot? librarySnapshot;
    Guid? librarySelection;
    bool showArchived;
    (ShellProfile Before, ShellProfile Loaded)? libraryLoadUndo;
    readonly Func<ShellProfile, Task<string>> inspect;
    readonly Action<Window, ShellProfile>? manageNative;
    readonly ScrollViewer scroll;
    ShellProfile saved;
    string? sourceRevision;
    int page;
    bool busy, closed, advancedExpanded, compactLayout;
    CancellationTokenSource? inspection;
    public ShellProfile Draft { get; private set; }
    public int ActivePage => page;

    public ShellSettingsWindow(ShellProfile? initial = null, string? path = null, Func<ShellProfile, Task<string>>? inspectPlan = null, Action<Window, ShellProfile>? manageNative = null)
    {
        profilePath = path ?? ShellProfileFile.DefaultPath; this.manageNative = manageNative;
        library = new ShellProfileLibrary(profilePath + ".library.json");
        string? loadError = null;
        try { var snapshot = ShellProfileFile.Load(profilePath); saved = initial ?? snapshot.Profile; saved.Validate(); sourceRevision = snapshot.Revision; }
        catch (Exception e) { saved = new(); loadError = "原方案读取失败，暂用演示方案：" + e.Message; }
        Draft = saved;
        inspect = inspectPlan ?? (_ => Task.FromResult("原生增强底层尚未就绪。当前操作只编辑方案，不会修改 Windows。"));
        Title = "ClassicDesk · 桌面设置"; Width = 1080; Height = 820; MinWidth = 740; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Icon = AppIcons.Get("brand");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Background = Brush("#F5F5F7"); Foreground = Brush("#252A33");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
        UseLayoutRounding = true; SnapsToDevicePixels = true; TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClassicDesk;component/ShellTheme.xaml", UriKind.Relative) });
        var root = new Grid(); root.SetResourceReference(Panel.BackgroundProperty, "WorkspaceBrush"); root.RowDefinitions.Add(new() { Height = new GridLength(42) }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = new Border { Background = Background, BorderBrush = Brush("#DDE2E9"), BorderThickness = new Thickness(1), Child = root }; root.Children.Add(BuildCaption());
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(186) }); body.ColumnDefinitions.Add(new()); Grid.SetRow(body, 1); root.Children.Add(body);
        var navigation = new DockPanel { Margin = new Thickness(14, 24, 14, 20) };
        var status = new StackPanel { Margin = new Thickness(12, 16, 8, 0) }; DockPanel.SetDock(status, Dock.Bottom);
        animeCard = ActionButton("选择界面皮肤", () => SelectPage(5)); animeCard.Padding = new Thickness(0); animeCard.Margin = new Thickness(-8,0,-2,16); animeCard.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var animeStack=new StackPanel(); animeStack.Children.Add(skinThumbnail); skinCaption.Margin=new Thickness(8); skinCaption.Foreground=Brush("#8172B7"); animeStack.Children.Add(skinCaption); animeCard.Content=animeStack; status.Children.Add(animeCard);
        var mode = Label("本地方案", 12, true); mode.Foreground = Brush("#46526E"); status.Children.Add(mode); var hint = Label("保存后可继续编辑\n系统启用需单独检查", 10); hint.Foreground = Brush("#83869A"); hint.LineHeight = 19; hint.Margin = new Thickness(0, 6, 0, 14); status.Children.Add(hint); draftBadge.FontSize = 10; status.Children.Add(draftBadge); navigation.Children.Add(status);
        var segments = new StackPanel(); navigation.Children.Add(segments);
        var section = Label("桌面个性化", 10, true); section.Foreground = Brush("#83869A"); section.Margin = new Thickness(12, 0, 0, 10); segments.Children.Add(section);
        Button Nav(string name, string icon, int index)
        {
            var button = ActionButton(name, () => SelectPage(index)); button.Style = (Style)FindResource("ShellNavigation"); button.Margin = new Thickness(0, 0, 0, 5);
            var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(AppIcons.View(icon, 21)); var text = Label(name, 13); text.Margin = new Thickness(10, 0, 0, 0); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text); button.Content = row;
            AutomationProperties.SetAutomationId(button, "shell-nav-" + index); sidebarRoutes[index] = button; segments.Children.Add(button); return button;
        }
        Nav("布局方案", "appearance", 3);
        Nav("界面皮肤", "picture", 5);
        var names = new[] { "任务栏", "资源管理器", "右键菜单" }; var icons = new[] { "taskbar", "folder", "context-menu" };
        for (int i = 0; i < names.Length; i++)
        {
            routes.Add(Nav(names[i], icons[i], i));
        }
        segments.Children.Add(new Border { Height = 1, Background = Brush("#E0E5F1"), Margin = new Thickness(12, 15, 12, 18) }); Nav("方案管理", "history", 4);
        var sidebar = new Border { BorderBrush = Brush("#E4E7F2"), BorderThickness = new Thickness(0,1,1,0), Child = navigation }; sidebar.SetResourceReference(BackgroundProperty, "SidebarBrush"); body.Children.Add(sidebar);
        var workspace = new Grid { Margin = new Thickness(28, 24, 24, 16) }; workspace.RowDefinitions.Add(new() { Height = GridLength.Auto }); workspace.RowDefinitions.Add(new()); Grid.SetColumn(workspace, 1); body.Children.Add(workspace);
        var content = new StackPanel { Margin = new Thickness(0, 0, 4, 0) }; workspace.Children.Add(content);
        settings.Margin = new Thickness(0, 0, 4, 8); scroll = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); workspace.Children.Add(scroll);
        var pageHeader = new Grid(); pageHeader.ColumnDefinitions.Add(new()); pageHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var words = new StackPanel(); words.Children.Add(heading); description.Foreground = Brush("#777985"); description.Margin = new Thickness(0, 4, 0, 0); words.Children.Add(description); pageHeader.Children.Add(words);
        skinPicker = Choice(ShellSkins.All.Select(p => p.Name).ToArray(), ShellSkins.Index(Draft), i => { if (!syncingScheme) ApplySkin(i); }, 158);
        skinPicker.VerticalAlignment = VerticalAlignment.Center; skinPicker.Margin = new Thickness(12,0,0,0); skinPicker.ToolTip = "界面皮肤：只切换配色和背景，保留布局参数。"; AutomationProperties.SetAutomationId(skinPicker,"shell-skin-picker"); AutomationProperties.SetName(skinPicker,"界面皮肤"); Grid.SetColumn(skinPicker,1);
        var skinHeader=new StackPanel { VerticalAlignment=VerticalAlignment.Center }; var skinLabel=Label("界面皮肤",10); skinLabel.Foreground=Brush("#8B91A2");skinLabel.Margin=new Thickness(14,0,0,4);skinHeader.Children.Add(skinLabel);skinHeader.Children.Add(skinPicker);Grid.SetColumn(skinHeader,1);pageHeader.Children.Add(skinHeader);
        var tools = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) }; pageTools = tools; var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < ShellPresets.All.Count; i++) { int index = i; var tab = ActionButton(ShellPresets.All[i].Name, () => ApplyPreset(index)); tab.Style = (Style)FindResource("ShellPreviewSegment"); tab.ToolTip = "载入" + ShellPresets.All[i].Name + "参数，保留当前皮肤；保存前可撤销。"; AutomationProperties.SetAutomationId(tab,"shell-preset-tab-"+i); presetTabs.Add(tab); tabs.Children.Add(tab); }
        tools.Children.Add(new Border { Background = Brush("#E9EDF2"), CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Child = tabs });
        var reset = ActionButton("重置本页", ResetPage); reset.Style = (Style)FindResource("ShellQuietButton"); reset.Margin = new Thickness(9, 0, 0, 0); reset.ToolTip = "重置此页方案；保存后才写入方案文件。"; AutomationProperties.SetAutomationId(reset, "shell-reset-page"); tools.Children.Add(reset); content.Children.Add(pageHeader); content.Children.Add(tools);
        var stageGrid = new Grid(); stageGrid.Children.Add(desktopBackdrop);
        var previewHeader = new Grid { Margin = new Thickness(18, 14, 18, 0), VerticalAlignment = VerticalAlignment.Top }; previewHeader.ColumnDefinitions.Add(new()); previewHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var previewLabel = Label("桌面效果预览", 12, true); previewLabel.Foreground = Brush("#445276"); previewHeader.Children.Add(previewLabel);
        previewCaption.Foreground = Brush("#52617C"); var previewInfo=new StackPanel { Orientation=Orientation.Horizontal }; previewCaption.VerticalAlignment=VerticalAlignment.Center;previewInfo.Children.Add(previewCaption);
        referenceButton=ActionButton("原生对比",()=>SetComparison(!preview.Before)); referenceButton.Style=(Style)FindResource("ShellQuietButton"); referenceButton.Foreground=Brush("#52618B"); referenceButton.FontSize=10;referenceButton.Padding=new Thickness(7,1,0,1);referenceButton.MinHeight=16;referenceButton.ToolTip="只查看 Windows 11 原生布局参考，不改变草稿。";AutomationProperties.SetAutomationId(referenceButton,"shell-reference");previewInfo.Children.Add(referenceButton); Grid.SetColumn(previewInfo,1);previewHeader.Children.Add(previewInfo);stageGrid.Children.Add(previewHeader);
        preview.Margin = new Thickness(14, 45, 14, 12); preview.VerticalAlignment = VerticalAlignment.Bottom; stageGrid.Children.Add(preview);
        previewStage = new Border { Height = 176, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 14, 0, 2), Child = stageGrid }; content.Children.Add(previewStage);
        root.SizeChanged += (_, e) => { compactLayout = e.NewSize.Height < 660; RefreshPreviewLayout(); };
        var footer = new Grid { Margin = new Thickness(24, 12, 24, 13) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        feedback.VerticalAlignment = VerticalAlignment.Center; feedback.Foreground = Brush("#7F8997"); feedback.Margin = new Thickness(0, 0, 12, 0); AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite); footer.Children.Add(feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        undo = ActionButton("撤销", DiscardDraft); undo.Style = (Style)FindResource("ShellQuietButton"); undo.IsEnabled = false; undo.Margin = new Thickness(0, 0, 8, 0); AutomationProperties.SetAutomationId(undo, "shell-undo"); actions.Children.Add(undo);
        save = ActionButton("保存方案", SaveDraft); save.IsEnabled = false; save.Margin = new Thickness(0, 0, 8, 0); AutomationProperties.SetAutomationId(save, "shell-save"); actions.Children.Add(save);
        save.Style = (Style)FindResource("ShellPrimaryButton");
        inspectButton = ActionButton("系统应用检查", () => { if (this.manageNative is null) _ = InspectAsync(); else this.manageNative(this, Draft); }); AutomationProperties.SetAutomationId(inspectButton, "shell-inspect"); actions.Children.Add(inspectButton);
        var footerBorder = new Border { Background = Brush("#FCFCFD"), BorderBrush = Brush("#E4E7ED"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer }; Grid.SetRow(footerBorder, 2); root.Children.Add(footerBorder);
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveDraft(); e.Handled = true; }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { PickImport(); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { PickExport(); e.Handled = true; }
        };
        Closing += (_, e) => { if (IsVisible && Draft != saved && MessageBox.Show(this, "方案尚未保存。关闭后放弃本次编辑？", "ClassicDesk", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) e.Cancel = true; };
        Closed += (_, _) => { closed = true; inspection?.Cancel(); };
        SelectPage(0); SetComparison(false); UpdateSavedState(); if (loadError is not null) feedback.Text = loadError;
    }
    FrameworkElement BuildCaption()
    {
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(23, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; brand.Children.Add(AppIcons.View("brand", 23)); var name = Label("ClassicDesk", 14, true); name.VerticalAlignment = VerticalAlignment.Center; name.Margin = new Thickness(8, 0, 0, 0); brand.Children.Add(name); var version = Label(typeof(ShellSettingsWindow).Assembly.GetName().Version?.ToString(2) ?? "", 10); version.Foreground = Brush("#A4ACB8"); version.VerticalAlignment = VerticalAlignment.Center; version.Margin = new Thickness(8, 1, 0, 0); brand.Children.Add(version); name.Foreground = Brush("#303A53"); version.Foreground = Brush("#7E8BA3"); header.Children.Add(new Border { Width = 186, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#F1F3FB"), Child = brand });
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
        if (index < 0 || index > 5) throw new ArgumentOutOfRangeException(nameof(index)); page = index;
        foreach (var (i, route) in sidebarRoutes) { route.Tag = i == index ? "selected" : null; var text = ((StackPanel)route.Content).Children.OfType<TextBlock>().Single(); text.Foreground = Brush(i == index ? "#5268C8" : "#69748C"); text.FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal; }
        pageTools.Visibility = previewStage.Visibility = preview.Visibility = index < 3 ? Visibility.Visible : Visibility.Collapsed;
        if (index >= 3) { settings.Children.Clear(); scroll.ScrollToTop(); if (index == 3) BuildPresets(); else if(index == 5) BuildSkins(); else BuildLibrary(); RefreshPreviewLayout(); return; }
        settings.Children.Clear(); dependentControls.Clear(); layoutChoices.Clear(); scroll.ScrollToTop(); preview.Kind = (ShellPreviewKind)index; preview.Height = index switch { 0 => 108, 1 => 176, _ => 174 };
        previewStage.Height = index == 0 ? 176 : 224;
        heading.Text = new[] { "任务栏布局", "资源管理器样式", "菜单体验" }[index]; description.Text = new[] { "调整开始按钮、图标和按钮尺寸", "在同一个资源管理器中切换工具区", "保留熟悉的完整菜单和快捷操作" }[index];
        if (index == 0)
        {
            Section("布局方式");
            var layoutGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) }; layoutGrid.ColumnDefinitions.Add(new()); layoutGrid.ColumnDefinitions.Add(new());
            for (int n = 0; n < 2; n++) {
                bool left = n == 0; var card = ActionButton(left ? "分开布局" : "集中布局", () => { Change(Draft with { StartOnLeft = left }); SelectPage(0); });
                card.Style = (Style)FindResource("ShellLayoutCard"); card.HorizontalContentAlignment = HorizontalAlignment.Stretch; card.Margin = new Thickness(n == 0 ? 0 : 5, 0, n == 0 ? 5 : 0, 0);
                var stack = new StackPanel(); var mini = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 9) }; mini.Children.Add(new Border { Height = 20, Background = Brush("#E8E9EF"), CornerRadius = new CornerRadius(4) });
                if (left) mini.Children.Add(new Border { Width = 8, Height = 8, Background = Brush("#303D8D"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 0), CornerRadius = new CornerRadius(2) });
                var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; for (int k=0; k < (left ? 3 : 4); k++) dots.Children.Add(new Border { Width = 8, Height = 8, Margin = new Thickness(3,0,3,0), Background = Brush("#727589"), CornerRadius = new CornerRadius(2) }); mini.Children.Add(dots); stack.Children.Add(mini);
                stack.Children.Add(Label(left ? "开始靠左，应用居中" : "开始与应用一起居中", 12, true)); card.Content = stack; AutomationProperties.SetAutomationId(card, "shell-layout-" + n); layoutChoices.Add(card); Grid.SetColumn(card,n); layoutGrid.Children.Add(card);
            }
            settings.Children.Add(layoutGrid);
            Section("图标与尺寸");
            var sizeTiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            void Tile(string name, string help, ComboBox control, string icon) {
                var panel = new StackPanel(); var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,9) }; title.Children.Add(AppIcons.View(icon,17)); var label=Label(name,12,true); label.Margin=new Thickness(8,0,0,0); title.Children.Add(label); panel.Children.Add(title);
                control.Width = double.NaN; control.HorizontalAlignment = HorizontalAlignment.Stretch; AutomationProperties.SetName(control,name); AutomationProperties.SetHelpText(control,help); control.ToolTip=help; panel.Children.Add(control);
                sizeTiles.Children.Add(new Border { Background=Brushes.White, BorderBrush=Brush("#DEE0E8"), BorderThickness=new Thickness(1), CornerRadius=new CornerRadius(10), Padding=new Thickness(14,11,14,11), Margin=new Thickness(0,0,8,8), Child=panel });
            }
            Tile("开始按钮位置", "应用图标保持居中。", Choice(["靠左", "居中"], Draft.StartOnLeft ? 0 : 1, i => Change(Draft with { StartOnLeft = i == 0 })), "layout");
            Tile("图标大小", "任务栏应用图标的尺寸。", PixelChoice(ShellProfile.IconSizes, Draft.IconSize, value => Change(Draft with { IconSize = value })), "appearance");
            Tile("任务栏高度", "任务栏的整体高度。", PixelChoice(ShellProfile.TaskbarHeights, Draft.TaskbarHeight, value => Change(Draft with { TaskbarHeight = value })), "taskbar");
            Tile("按钮宽度", "按钮宽度控制图标两侧留白；与图标大小分别设置。", PixelChoice(ShellProfile.ButtonWidths, Draft.TaskbarButtonWidth, value => Change(Draft with { TaskbarButtonWidth = value })), "system");
            settings.Children.Add(sizeTiles);
            var advancedRows = new StackPanel();
            AddRow(advancedRows, "启用布局调整", "关闭时保留当前位置参数与设计预览。", Toggle(!Draft.SkipTaskbarLayout, value => Change(Draft with { SkipTaskbarLayout = !value })));
            AddRow(advancedRows, "启用尺寸调整", "关闭时保留图标、栏高和按钮尺寸参数。", Toggle(!Draft.SkipTaskbarSizing, value => Change(Draft with { SkipTaskbarSizing = !value })));
            AddRow(advancedRows, "小图标尺寸", "Windows 使用小图标模式时的尺寸。", PixelChoice(ShellProfile.IconSizes, Draft.SmallIconSize, value => Change(Draft with { SmallIconSize = value })));
            AddRow(advancedRows, "小按钮宽度", "Windows 使用小图标模式时的按钮宽度。", PixelChoice(ShellProfile.ButtonWidths, Draft.SmallTaskbarButtonWidth, value => Change(Draft with { SmallTaskbarButtonWidth = value })));
            var system = Toggle(Draft.OtherSystemButtonsOnLeft, value => Change(Draft with { OtherSystemButtonsOnLeft = value })); dependentControls["otherButtons"] = system; AddRow(advancedRows, "其他系统按钮靠左", "搜索、任务视图和小组件；开始按钮居中时不生效。", system);
            var start = Toggle(Draft.StartMenuOnLeft, value => Change(Draft with { StartMenuOnLeft = value })); dependentControls["startMenu"] = start; AddRow(advancedRows, "开始菜单靠左展开", "开始按钮居中时不生效。", start);
            var search = Toggle(Draft.SearchMenuOnLeft, value => Change(Draft with { SearchMenuOnLeft = value })); dependentControls["searchMenu"] = search; AddRow(advancedRows, "所有入口的搜索都靠左", "包含 Win+S 和任务栏搜索；需要开始菜单靠左。", search, true);
            var advanced = new Expander { Header = "高级布局", Content = advancedRows, IsExpanded = advancedExpanded, Style = (Style)FindResource("ShellExpander"), Margin = new Thickness(0, 10, 0, 0) }; AutomationProperties.SetAutomationId(advanced, "shell-advanced"); advanced.Expanded += (_, _) => advancedExpanded = true; advanced.Collapsed += (_, _) => advancedExpanded = false; settings.Children.Add(advanced);
        }
        else if (index == 1)
        {
            Section("工具区", "保留熟悉的操作习惯，选择适合自己的工具栏。");
            var group = Group(); int style = Draft.ClassicRibbon ? 1 : Draft.UseClassicNavigationBar ? 2 : 0;
            AddRow(group, "工具区样式", "功能区与经典导航栏是不同模式。", Choice(["Windows 11 命令栏", "Windows 10 功能区", "经典导航栏"], style, i => Change(Draft with { ClassicRibbon = i == 1, UseClassicNavigationBar = i == 2 }), 210), true);
            AddNote(Draft.ClassicRibbon ? "主页、共享、查看完整展开；此模式不保留 Windows 11 标签页。" : Draft.UseClassicNavigationBar ? "采用旧版 Windows 11 导航布局，保留标签页。" : "使用当前 Windows 11 命令栏与标签页。", "explorer-mode-note");
        }
        else
        {
            Section("菜单行为");
            var group = Group(); AddRow(group, "完整右键菜单", "直接展开完整的 Shell 命令。", Toggle(Draft.ClassicContextMenu, value => Change(Draft with { ClassicContextMenu = value })));
            var ctrl = Toggle(Draft.ClassicMenuWithCtrl, value => Change(Draft with { ClassicMenuWithCtrl = value })); dependentControls["ctrlMenu"] = ctrl; AddRow(group, "按 Ctrl 临时使用新版", "开启完整菜单时，按住 Ctrl 可临时切回新版菜单。", ctrl, true);
            AddNote("菜单命令由文件类型与已安装的扩展决定。", "context-note");
        }
        preview.Options = Draft.PreviewOptions; UpdateDependencies(); UpdatePreviewDetails(); RefreshPreviewLayout();
    }
    StackPanel Group()
    { var rows = new StackPanel(); settings.Children.Add(new Border { Background = Brushes.White, BorderBrush = Brush("#E1E6EE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Child = rows }); return rows; }
    void Section(string title, string? subtitle = null)
    {
        var label = Label(title, 14, true); label.Margin = new Thickness(1, 16, 0, 9); settings.Children.Add(label);
        if (subtitle is not null) { var detail = Label(subtitle, 11); detail.Foreground = Brush("#7D8A9D"); detail.Margin = new Thickness(1, 0, 0, 12); settings.Children.Add(detail); }
    }
    void BuildPresets()
    {
        heading.Text = "选择熟悉的操作方式"; description.Text = "布局决定操作习惯，皮肤决定界面外观。";
        Section("布局方案", "切换任务栏、工具栏与右键菜单参数，保留已选皮肤。Win10 为经典操作方案，并非读取当前系统。 ");
        var gallery = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        for(int i=0;i<ShellPresets.All.Count;i++) {
            int index=i;var preset=ShellPresets.All[i]; bool selected = Draft.Appearance==preset.Profile.Appearance;
            var panel=new StackPanel(); panel.Children.Add(new DesktopBackdrop { Variant=i,Thumbnail=true,Height=136 });
            var text=new StackPanel { Margin=new Thickness(14,12,14,14) };
            var line=new Grid();line.ColumnDefinitions.Add(new());line.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var title=Label(preset.Name,15,true);line.Children.Add(title);
            var mark=Label(selected?"已选择":"↗",11);mark.Foreground=Brush("#7C85C7");Grid.SetColumn(mark,1);line.Children.Add(mark);text.Children.Add(line);
            var detail=Label(preset.Description,11);detail.Foreground=Brush("#858DA1");detail.Margin=new Thickness(0,7,0,0);detail.LineHeight=18;detail.MinHeight=54;text.Children.Add(detail);panel.Children.Add(text);
            var button=ActionButton(preset.Name,()=>ApplyPreset(index));button.Content=panel;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.Padding=new Thickness(0);button.Margin=new Thickness(0,0,12,12);
            if(selected){button.Background=Brush("#F9FAFF");button.BorderBrush=Brush("#A6B4EC");} AutomationProperties.SetAutomationId(button,"shell-preset-"+i);gallery.Children.Add(button);
        }
        settings.Children.Add(gallery);
    }
    public void ApplyPreset(int index)
    {
        if (index < 0 || index >= ShellPresets.All.Count) throw new ArgumentOutOfRangeException(nameof(index));
        Change(ShellPresets.All[index].Profile with { Skin = Draft.Skin }); SelectPage(page); feedback.Text = "已载入「" + ShellPresets.All[index].Name + "」· 保留当前皮肤";
    }
    public void ApplySkin(int index)
    {
        if(index < 0 || index >= ShellSkins.All.Count) throw new ArgumentOutOfRangeException(nameof(index));
        Change(Draft with { Skin = ShellSkins.All[index].Id });
        if(page >= 3) SelectPage(page);
        feedback.Text = "皮肤已切换为「" + ShellSkins.All[index].Name + "」· 布局参数已保留";
    }
    void BuildSkins()
    {
        heading.Text = "给桌面换一种心情"; description.Text = "保留原版图标，让背景与配色表达你的风格。";
        Section("皮肤资料库", "可与任意布局组合，例如 Win10 方案 + 星空二次元。皮肤仅用于软件界面与预览。");
        var gallery = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        for(int i=0;i<ShellSkins.All.Count;i++) {
            int index=i;var skin=ShellSkins.All[i];bool selected=Draft.Skin==skin.Id;
            var panel=new StackPanel();panel.Children.Add(new DesktopBackdrop { Variant=skin.Backdrop,Height=124 });
            var text=new StackPanel { Margin=new Thickness(14,12,14,14) };var title=Label(skin.Name+(selected?"  ·  已选择":""),14,true);text.Children.Add(title);
            var detail=Label(skin.Description,11);detail.Margin=new Thickness(0,7,0,0);detail.Foreground=Brush("#8590A3");detail.MinHeight=38;text.Children.Add(detail);
            var colors=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0) };
            foreach(var color in new[]{skin.Accent,skin.Sidebar,skin.Workspace}) colors.Children.Add(new Border { Width=20,Height=6,CornerRadius=new CornerRadius(3),Background=Brush(color),Margin=new Thickness(0,0,5,0) });
            text.Children.Add(colors);panel.Children.Add(text);
            var button=ActionButton(skin.Name,()=>ApplySkin(index));button.Content=panel;button.Padding=new Thickness(0);button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.Margin=new Thickness(0,0,12,12);
            if(selected)button.BorderBrush=Brush(skin.Accent);AutomationProperties.SetAutomationId(button,"shell-skin-"+i);gallery.Children.Add(button);
        }
        settings.Children.Add(gallery);
    }
    void BuildLibrary()
    {
        heading.Text = "方案与备份"; description.Text = "保存、迁移和找回设置，让每一次调整都有来处。";
        BuildSavedLibrary();
        Section("当前方案", Draft == saved ? "草稿与本次打开或最近保存的方案一致。" : "以下修改尚未保存，其他页面的编辑也会一并保留。");
        var summary = Group();
        AddRow(summary, "布局与皮肤", "", Label(ShellPresets.DisplayName(Draft) + "\n" + ShellSkins.All[ShellSkins.Index(Draft)].Name, 11));
        AddRow(summary, "任务栏", "", Label((Draft.StartOnLeft ? "开始靠左 · 应用居中" : "开始与应用居中") + $"\n{Draft.IconSize} px 图标 / {Draft.TaskbarHeight} px 高度", 11));
        AddRow(summary, "资源管理器", "", Label(Draft.ClassicRibbon ? "Windows 10 功能区" : Draft.UseClassicNavigationBar ? "经典导航栏" : "Windows 11 命令栏", 11));
        AddRow(summary, "右键菜单", "", Label(Draft.ClassicContextMenu ? "完整菜单" : "Windows 11 菜单", 11), true);
        var changed = ShellPresets.Changes(saved, Draft);
        if (changed.Count > 0) AddNote("已修改 " + changed.Count + " 项：" + string.Join("、", changed), "shell-change-summary");
        Section("导入与备份"); var files = Group();
        var import = ActionButton("导入…", PickImport); AutomationProperties.SetAutomationId(import, "shell-import"); AddRow(files, "导入方案", "载入 JSON 到草稿，保存前不覆盖当前方案。  Ctrl+O", import);
        var export = ActionButton("导出…", PickExport); AutomationProperties.SetAutomationId(export, "shell-export"); AddRow(files, "导出当前草稿", "包含还未保存的参数，方便备份和迁移。  Ctrl+Shift+S", export);
        var restore = ActionButton("载入上一份", () => { try { RestorePreviousDraft(); } catch (Exception e) { feedback.Text = "恢复未完成：" + e.Message; } });
        restore.IsEnabled = File.Exists(profilePath + ".previous"); AutomationProperties.SetAutomationId(restore, "shell-restore-previous"); AddRow(files, "上一份保存", "载入上次保存的备份，确认后再保存。", restore, true);
        AddNote("本页只管理 ClassicDesk 的方案文件。系统效果请通过右下角的应用检查确认。", "shell-library-note");
    }
    void BuildSavedLibrary()
    {
        Section("本地方案库", "保存多套布局与皮肤组合。载入只进入草稿，归档后仍可恢复。");
        var group = Group(); var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) }; group.Children.Add(panel);
        var toolbar = new WrapPanel(); panel.Children.Add(toolbar);
        var archiveFilter = new CheckBox { Content = "查看归档", IsChecked = showArchived, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 8) };
        AutomationProperties.SetAutomationId(archiveFilter, "library-archived"); toolbar.Children.Add(archiveFilter);
        archiveFilter.Click += (_, _) => { showArchived = archiveFilter.IsChecked == true; librarySelection = null; SelectPage(4); };
        var refresh = ActionButton("刷新", () => SelectPage(4)); refresh.Margin = new Thickness(0, 0, 0, 8); AutomationProperties.SetAutomationId(refresh, "library-refresh"); toolbar.Children.Add(refresh);
        try { librarySnapshot = library.Read(); }
        catch (Exception e) { librarySnapshot = null; panel.Children.Add(Label("方案库读取失败，原文件已保留：" + e.Message, 11)); return; }
        var entries = librarySnapshot.Entries.Where(e => e.Archived == showArchived).OrderByDescending(e => e.UpdatedUtc).ToArray();
        if (!entries.Any(e => e.Id == librarySelection)) librarySelection = entries.FirstOrDefault()?.Id;
        var selected = entries.FirstOrDefault(e => e.Id == librarySelection);
        var picker = new ComboBox { ItemsSource = entries.Select(e => e.Name).ToArray(), SelectedIndex = Array.FindIndex(entries, e => e.Id == librarySelection), HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)FindResource("ShellChoice"), Margin = new Thickness(0, 0, 0, 10), IsEnabled = entries.Length > 0 };
        AutomationProperties.SetAutomationId(picker, "library-picker"); AutomationProperties.SetName(picker, "已保存方案"); panel.Children.Add(picker);
        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex >= 0) { librarySelection = entries[picker.SelectedIndex].Id; SelectPage(4); } };
        panel.Children.Add(Label(selected is null ? (showArchived ? "暂无归档方案。" : "还没有保存到方案库，可将当前草稿新增为一套方案。") :
            $"{ShellPresets.DisplayName(selected.Profile)} · {ShellSkins.All[ShellSkins.Index(selected.Profile)].Name}\n{selected.Profile.IconSize} px 图标 / {selected.Profile.TaskbarHeight} px 高度 · {selected.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}", 11));
        var nameLabel = Label("方案名称", 11); nameLabel.Margin = new Thickness(0, 12, 0, 5); panel.Children.Add(nameLabel);
        var name = new TextBox { Text = selected?.Name ?? ShellPresets.DisplayName(Draft) + " · " + ShellSkins.All[ShellSkins.Index(Draft)].Name, MaxLength = 40, Padding = new Thickness(10, 7, 10, 7), BorderBrush = Brush("#DCE2EC"), BorderThickness = new Thickness(1), Background = Brushes.White, Foreground = Brush("#29313D"), Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetAutomationId(name, "library-name"); AutomationProperties.SetName(name, "方案名称"); panel.Children.Add(name);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        void Action(string title, string id, System.Action action, bool enabled = true)
        {
            var button = ActionButton(title, () => { try { action(); } catch (Exception e) { feedback.Text = "方案库操作未完成：" + e.Message; } });
            button.IsEnabled = enabled; button.Margin = new Thickness(0, 0, 8, 8); AutomationProperties.SetAutomationId(button, id); actions.Children.Add(button);
        }
        Action("新增当前草稿", "library-add", () => SaveToLibrary(name.Text));
        Action("载入预览", "library-load", () => LoadFromLibrary(selected!.Id), selected is not null && !showArchived);
        Action("更新为当前草稿", "library-update", () => UpdateLibraryEntry(selected!.Id), selected is not null && !showArchived);
        Action("复制", "library-copy", () => DuplicateLibraryEntry(selected!.Id), selected is not null);
        Action("重命名", "library-rename", () => RenameLibraryEntry(selected!.Id, name.Text), selected is not null);
        Action(showArchived ? "恢复方案" : "归档", "library-archive", () => ArchiveLibraryEntry(selected!.Id, !showArchived), selected is not null);
        Action("撤销载入", "library-undo-load", UndoLibraryLoad, libraryLoadUndo is { } prior && Draft == prior.Loaded);
    }
    public IReadOnlyList<ShellLibraryEntry> LibraryEntries => (librarySnapshot ??= library.Read()).Entries;
    public void SaveToLibrary(string name)
    {
        librarySnapshot = library.Add(librarySnapshot ?? library.Read(), name, Draft);
        librarySelection = librarySnapshot.Entries.Last().Id; showArchived = false; SelectPage(4);
        feedback.Text = "已新增到方案库 · 当前草稿与 Windows 状态不变";
    }
    public void LoadFromLibrary(Guid id)
    {
        var entry = ShellProfileLibrary.Find(librarySnapshot ??= library.Read(), id);
        if (entry.Archived) throw new InvalidOperationException("请先恢复已归档方案。");
        libraryLoadUndo = (Draft, entry.Profile); librarySelection = id; Change(entry.Profile); SelectPage(4);
        feedback.Text = "已载入预览 · 可撤销载入；确认后再保存当前方案";
    }
    public void UndoLibraryLoad()
    {
        if (libraryLoadUndo is not { } prior || Draft != prior.Loaded) throw new InvalidOperationException("载入后已有新的编辑，无法直接撤销载入。");
        Change(prior.Before); libraryLoadUndo = null; SelectPage(4); feedback.Text = "已回到载入前的草稿";
    }
    public void UpdateLibraryEntry(Guid id)
    {
        librarySnapshot = library.Update(librarySnapshot ?? library.Read(), id, Draft); SelectPage(4); feedback.Text = "方案库条目已更新 · Windows 尚未修改";
    }
    public void DuplicateLibraryEntry(Guid id)
    {
        librarySnapshot = library.Duplicate(librarySnapshot ?? library.Read(), id); librarySelection = librarySnapshot.Entries.Last().Id; showArchived = false; SelectPage(4); feedback.Text = "已复制方案，原方案保留";
    }
    public void RenameLibraryEntry(Guid id, string name)
    {
        librarySnapshot = library.Rename(librarySnapshot ?? library.Read(), id, name); SelectPage(4); feedback.Text = "方案已重命名";
    }
    public void ArchiveLibraryEntry(Guid id, bool archived)
    {
        librarySnapshot = library.SetArchived(librarySnapshot ?? library.Read(), id, archived); librarySelection = null; SelectPage(4); feedback.Text = archived ? "方案已归档，可在“查看归档”中恢复" : "方案已恢复到列表";
    }
    public void ImportDraft(string path)
    {
        var imported = ShellProfileFile.Import(path); Change(imported); SelectPage(page); feedback.Text = "方案已导入草稿 · 保存前可撤销";
    }
    public void ExportDraft(string path, string expectedRevision)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(profilePath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(profilePath + ".previous"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(profilePath + ".library.json"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("请另选备份文件名，不能覆盖当前方案、上一份记录或方案库。");
        ShellProfileFile.Save(path, Draft, expectedRevision); feedback.Text = "草稿已导出 · 当前保存状态不变";
    }
    public void RestorePreviousDraft()
    {
        if (!File.Exists(profilePath + ".previous")) throw new FileNotFoundException("还没有上一份保存记录。");
        var previous = ShellProfileFile.Load(profilePath + ".previous").Profile; Change(previous); SelectPage(page); feedback.Text = "上一份方案已载入草稿 · 确认后再保存";
    }
    void PickImport()
    {
        var picker = new OpenFileDialog { Title = "导入 ClassicDesk 方案", Filter = "ClassicDesk 方案 (*.json)|*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try { ImportDraft(picker.FileName); } catch (Exception e) { feedback.Text = "导入失败，原草稿保留：" + e.Message; }
    }
    void PickExport()
    {
        var picker = new SaveFileDialog { Title = "导出当前草稿", Filter = "ClassicDesk 方案 (*.json)|*.json", FileName = "ClassicDesk-方案.json", AddExtension = true, DefaultExt = ".json", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        try { ExportDraft(picker.FileName, ShellProfileFile.Revision(picker.FileName)); } catch (Exception e) { feedback.Text = "导出失败：" + e.Message; }
    }
    ComboBox PixelChoice(int[] values, int selected, Action<int> changed) => Choice(values.Select(value => value + " px").ToArray(), Array.IndexOf(values, selected), i => changed(values[i]));
    ComboBox Choice(string[] items, int selected, Action<int> changed, double width = 146)
    { var combo = new ComboBox { ItemsSource = items, SelectedIndex = selected, Width = width, Style = (Style)FindResource("ShellChoice") }; combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); }; return combo; }
    CheckBox Toggle(bool initial, Action<bool> changed)
    { var toggle = new CheckBox { IsChecked = initial, Style = (Style)FindResource("ShellSwitch") }; toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false); return toggle; }
    void AddRow(StackPanel parent, string title, string detail, FrameworkElement control, bool last = false)
    {
        var row = new Grid { Margin = new Thickness(16, 10, 16, 10), MinHeight = 38 }; row.ColumnDefinitions.Add(new() { Width = new GridLength(37) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var iconKey = title.Contains("图标") ? "appearance" : title.Contains("菜单") ? "context-menu" : title.Contains("导") || title.Contains("保存") ? "history" : title.Contains("工具") ? "folder" : "layout";
        row.Children.Add(new Border { Width = 27, Height = 27, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(7), Background = Brush("#F0F1F5"), Child = AppIcons.View(iconKey,18) });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) }; words.Children.Add(Label(title, 12));
        if (!string.IsNullOrWhiteSpace(detail)) { var caption = Label(detail, 10); caption.Foreground = Brush("#8792A3"); caption.Margin = new Thickness(0, 4, 0, 0); words.Children.Add(caption); } Grid.SetColumn(words,1); row.Children.Add(words); control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 2); row.Children.Add(control);
        AutomationProperties.SetName(control, title); AutomationProperties.SetHelpText(control, detail); control.ToolTip = detail; ToolTipService.SetShowOnDisabled(control, true); parent.Children.Add(new Border { BorderBrush = Brush("#EDF0F4"), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Child = row });
    }
    void AddNote(string text, string id)
    { var note = Label(text, 11); note.Foreground = Brush("#8B95A3"); note.Margin = new Thickness(2, 12, 2, 0); AutomationProperties.SetAutomationId(note, id); settings.Children.Add(note); }
    void UpdateDependencies()
    {
        if (dependentControls.TryGetValue("otherButtons", out var system)) system.IsEnabled = Draft.StartOnLeft && !Draft.SkipTaskbarLayout;
        if (dependentControls.TryGetValue("startMenu", out var start)) start.IsEnabled = Draft.StartOnLeft && !Draft.SkipTaskbarLayout;
        if (dependentControls.TryGetValue("searchMenu", out var search)) search.IsEnabled = Draft.StartOnLeft && Draft.StartMenuOnLeft && !Draft.SkipTaskbarLayout;
        if (dependentControls.TryGetValue("ctrlMenu", out var ctrl)) ctrl.IsEnabled = Draft.ClassicContextMenu;
        if (page == 1)
        { var note = settings.Children.OfType<TextBlock>().SingleOrDefault(t => AutomationProperties.GetAutomationId(t) == "explorer-mode-note"); if (note is not null) note.Text = Draft.ClassicRibbon ? "主页、共享、查看完整展开；此模式不保留 Windows 11 标签页。" : Draft.UseClassicNavigationBar ? "采用旧版 Windows 11 导航布局，保留标签页。" : "使用当前 Windows 11 命令栏与标签页。"; }
    }
    void UpdatePreviewDetails()
    {
        int style = ShellPresets.Index(Draft);
        bool modified = Draft != (ShellPresets.All[style].Profile with { Skin = Draft.Skin });
        syncingScheme = true; skinPicker.SelectedIndex = ShellSkins.Index(Draft); syncingScheme = false;
        for(int i=0;i<presetTabs.Count;i++) { presetTabs[i].Tag=i==style?"selected":null;AutomationProperties.SetName(presetTabs[i],ShellPresets.All[i].Name+(i==style&&modified?" · 已调整":"")); }
        var skin=ShellSkins.All[ShellSkins.Index(Draft)];
        int backdrop=skin.Id=="classic" ? (style==0?0:1) : skin.Backdrop;
        desktopBackdrop.Variant=backdrop;skinThumbnail.Variant=backdrop;skinCaption.Text=skin.Name+"  ↗";
        Resources["AccentBrush"] = Brush(skin.Accent); Resources["SidebarBrush"] = Brush(skin.Sidebar); Resources["WorkspaceBrush"] = Brush(skin.Workspace);
        referenceButton.Content=preview.Before?"返回方案":"原生对比";
        previewCaption.Text = preview.Before ? "Windows 11 参考 · 示意" : page switch { 0 => $"{Draft.IconSize} px 图标  /  {Draft.TaskbarHeight} px 高度", 1 => Draft.ClassicRibbon ? "经典功能区 · 示意" : Draft.UseClassicNavigationBar ? "经典导航栏 · 示意" : "Windows 11 命令栏 · 示意", _ => Draft.ClassicContextMenu ? "完整菜单 · 示意" : "Windows 11 菜单 · 示意" };
        if (page == 0 && !preview.Before && (Draft.SkipTaskbarLayout || Draft.SkipTaskbarSizing)) previewCaption.Text = "设计预览 · 部分增强未选择";
        for (int i=0; i<layoutChoices.Count; i++) layoutChoices[i].Tag = (i == 0) == Draft.StartOnLeft ? "selected" : null;
    }
    void RefreshPreviewLayout()
    {
        heading.FontSize = compactLayout ? 20 : 24;
        foreach(var route in sidebarRoutes.Values) { route.Padding=new Thickness(12,compactLayout?7:14,12,compactLayout?7:14);route.MinHeight=compactLayout?34:49;route.Margin=new Thickness(0,0,0,compactLayout?2:5); }
        animeCard.Visibility = compactLayout ? Visibility.Collapsed : Visibility.Visible;
        previewStage.Height = compactLayout ? 148 : page == 0 ? 176 : 224;
        preview.Height = compactLayout ? (page == 0 ? 92 : 102) : page switch { 0 => 108, 1 => 176, _ => 174 };
        preview.Margin = compactLayout ? new Thickness(10, 32, 10, 8) : new Thickness(14, 45, 14, 12);
    }
    void Change(ShellProfile value)
    { value.Validate(); Draft = value; preview.Options = value.PreviewOptions; SetComparison(false); UpdateDependencies(); UpdateSavedState(); UpdatePreviewDetails(); feedback.Text = Draft != saved ? "方案已编辑 · 尚未应用到 Windows" : "方案演示 · 尚未应用到 Windows"; }
    void UpdateSavedState()
    { bool dirty = Draft != saved; save.IsEnabled = dirty && sourceRevision is not null; undo.IsEnabled = dirty; undo.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed; draftBadge.Text = sourceRevision is null ? "原方案受保护" : dirty ? "有未保存更改" : sourceRevision == "missing" ? "默认方案" : "已保存"; draftBadge.Foreground = Brush(dirty ? "#6576CF" : "#7F8A9E"); }
    public void SetComparison(bool before)
    { preview.Before = before; UpdatePreviewDetails(); }
    void ResetPage()
    {
        var defaults = ShellPresets.All[ShellPresets.Index(Draft)].Profile;
        var value = page switch
        {
            0 => Draft with { StartOnLeft = defaults.StartOnLeft, IconSize = defaults.IconSize, TaskbarHeight = defaults.TaskbarHeight, TaskbarButtonWidth = defaults.TaskbarButtonWidth, SmallIconSize = defaults.SmallIconSize, SmallTaskbarButtonWidth = defaults.SmallTaskbarButtonWidth, OtherSystemButtonsOnLeft = defaults.OtherSystemButtonsOnLeft, StartMenuOnLeft = defaults.StartMenuOnLeft, SearchMenuOnLeft = defaults.SearchMenuOnLeft, SkipTaskbarLayout = defaults.SkipTaskbarLayout, SkipTaskbarSizing = defaults.SkipTaskbarSizing },
            1 => Draft with { ClassicRibbon = defaults.ClassicRibbon, UseClassicNavigationBar = defaults.UseClassicNavigationBar },
            _ => Draft with { ClassicContextMenu = defaults.ClassicContextMenu, ClassicMenuWithCtrl = defaults.ClassicMenuWithCtrl }
        };
        Change(value); SelectPage(page); feedback.Text = "本页已恢复为所选风格 · 尚未应用到 Windows";
    }
    void DiscardDraft() { Change(saved); SelectPage(page); feedback.Text = "已撤销未保存的更改 · Windows 未修改"; }
    public void SaveDraft()
    { try { if (sourceRevision is null) throw new InvalidOperationException("原方案读取失败，已保护原文件；请先处理文件错误。"); sourceRevision = ShellProfileFile.Save(profilePath, Draft, sourceRevision); saved = Draft; UpdateSavedState(); if (page == 4) SelectPage(page); feedback.Text = "方案已保存 · Windows 尚未修改"; } catch (Exception e) { feedback.Text = "保存失败，编辑仍保留：" + e.Message; } }
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
