using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClassicDesk;

/// <summary>A manually opened, independent launcher. Construction never scans, launches or saves.</summary>
public sealed class ClassicStartMenuWindow : Window
{
    readonly string profilePath;
    readonly Func<ClassicStartCatalogSnapshot> scan;
    readonly Action<string> launch, openPlace;
    readonly Func<bool> confirmDiscard;
    readonly Grid root = new(), body = new();
    readonly StackPanel navigation = new(), places = new();
    readonly TextBox search = new(), folderName = new(), folderPath = new();
    readonly ListBox results = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock status = new(), heading = new(), count = new(), empty = new();
    readonly ScrollViewer optionsView = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
    readonly ComboBox style = new(), icons = new(), maxPinned = new();
    readonly CheckBox ascending = new() { Content = "按名称升序" }, entryIcons = new() { Content = "显示程序图标" }, userHeading = new() { Content = "显示用户目录标题" };
    readonly Button refresh;
    readonly List<CheckBox> placeChoices = new();
    readonly StackPanel customFolders = new();
    ClassicStartOptions options, saved;
    ClassicStartCatalogSnapshot catalog;
    string revision = "", section = "pinned";
    bool syncing, scanning, closed, settingsVisible, demoCatalog;
    public ClassicStartOptions Options => options.Freeze();
    public IReadOnlyList<ClassicStartApp> VisibleApps { get; private set; } = Array.Empty<ClassicStartApp>();
    public string StatusText => status.Text;
    public bool HasUnsavedChanges { get; private set; }
    public bool SettingsVisible => settingsVisible;
    public bool IsScanning => scanning;

    public ClassicStartMenuWindow(ClassicStartOptions? options = null, string? profilePath = null,
        ClassicStartCatalogSnapshot? catalog = null, Func<ClassicStartCatalogSnapshot>? scan = null,
        Action<string>? launch = null, Action<string>? openPlace = null, Func<bool>? confirmDiscard = null)
    {
        this.profilePath = profilePath ?? ClassicStartProfileFile.DefaultPath;
        this.scan = scan ?? (() => ClassicStartCatalog.Scan());
        this.launch = launch ?? StartTarget; this.openPlace = openPlace ?? StartTarget;
        this.confirmDiscard = confirmDiscard ?? (() => IsVisible && MessageBox.Show(this, "菜单选项尚未保存。放弃本次修改并关闭？", "ClassicDesk · 未保存的菜单选项", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        this.catalog = catalog ?? DemoCatalog();
        demoCatalog = catalog is null; if (demoCatalog) section = "all";
        string initialStatus = catalog is null ? "演示程序 · 点击“刷新本机应用”后可启动本机程序。" : "已载入指定程序列表。";
        try { var file = ClassicStartProfileFile.Load(this.profilePath); saved = file.Options; revision = file.Revision; this.options = options ?? saved; }
        catch (Exception e) { saved = new(); this.options = options ?? saved; initialStatus = "设置读取失败，原文件保留：" + e.Message; }
        this.options = this.options.Freeze(); this.options.Validate();
        HasUnsavedChanges = !SameOptions(this.options, saved);
        Title = "ClassicDesk · 独立开始菜单"; Width = 800; Height = 640; MinWidth = 560; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
        UseLayoutRounding = true; SnapsToDevicePixels = true; Icon = AppIcons.Get("brand");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClassicDesk;component/ShellTheme.xaml", UriKind.Relative) });
        Background = (Brush)FindResource("WorkspaceBrush"); Foreground = Brush("#29374B");
        root.Margin = new Thickness(18); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(Button("选项", "start-options", () => ShowSettings(!settingsVisible)));
        actions.Children.Add(Button("关闭", "start-close", Close)); top.Children.Add(actions);
        var title = new StackPanel { Orientation = Orientation.Horizontal }; title.Children.Add(AppIcons.View("start", 28));
        title.Children.Add(new TextBlock { Text = "开始", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0) }); top.Children.Add(title); root.Children.Add(top);
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        refresh = Button("刷新本机应用", "start-refresh", () => _ = RefreshCatalogAsync()); DockPanel.SetDock(refresh, Dock.Right); searchRow.Children.Add(refresh);
        search.ToolTip = "搜索程序名称、分类；↑↓ 选择，Enter 打开，Escape 清空或关闭"; Id(search, "start-search");
        AutomationProperties.SetName(search, "搜索程序名称或分类");
        var searchSurface = new Grid(); searchSurface.Children.Add(search);
        var searchHint = new TextBlock { Text = "搜索程序名称或分类", Foreground = Brush("#7B879A"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchSurface.Children.Add(searchHint);
        search.TextChanged += (_, _) => { searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; if (!syncing) RenderResults(); }; searchRow.Children.Add(searchSurface); Grid.SetRow(searchRow, 1); root.Children.Add(searchRow);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(126) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(174) });
        navigation.Margin = new Thickness(0, 0, 12, 0);
        var navScroll = new ScrollViewer { Content = navigation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(navScroll);
        var center = new Grid { Margin = new Thickness(0, 0, 12, 0) }; center.RowDefinitions.Add(new() { Height = GridLength.Auto }); center.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); center.RowDefinitions.Add(new() { Height = GridLength.Auto });
        count.FontWeight = FontWeights.SemiBold; count.Margin = new Thickness(8, 4, 0, 10); Id(count, "start-count"); center.Children.Add(count);
        var listSurface = new Grid(); Grid.SetRow(listSurface, 1); center.Children.Add(listSurface); Id(results, "start-results"); listSurface.Children.Add(results);
        empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(18); empty.Foreground = Brush("#687991"); empty.VerticalAlignment = VerticalAlignment.Center; Id(empty, "start-empty"); listSurface.Children.Add(empty);
        results.MouseDoubleClick += (_, e) => {
            if (e.ChangedButton != MouseButton.Left) return;
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != results; node = InputParent(node)) {
                if (node is System.Windows.Controls.Primitives.ButtonBase) return;
                if (node is not ListBoxItem item) continue;
                if (!results.Items.Contains(item) || item.Tag is not ClassicStartApp) return;
                results.SelectedItem = item; LaunchSelected(); e.Handled = true; return;
            }
        }; Grid.SetColumn(center, 1); body.Children.Add(center);
        var openSelected = Button("打开所选程序", "start-launch", LaunchSelected); openSelected.HorizontalAlignment = HorizontalAlignment.Left; openSelected.Margin = new Thickness(4, 8, 0, 0); Grid.SetRow(openSelected, 2); center.Children.Add(openSelected);
        places.Margin = new Thickness(12, 8, 0, 0); var placesScroll = new ScrollViewer { Content = places, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(placesScroll, 2); body.Children.Add(placesScroll);
        Grid.SetRow(body, 2); root.Children.Add(body); BuildSettings(); Grid.SetRow(optionsView, 2); root.Children.Add(optionsView);
        status.TextWrapping = TextWrapping.Wrap; status.FontSize = 11; status.Foreground = Brush("#59697F"); status.Margin = new Thickness(0, 12, 0, 0); status.Text = initialStatus; Id(status, "start-status"); Grid.SetRow(status, 3); root.Children.Add(status);
        Content = new Border { Background = Background, Child = root };
        PreviewKeyDown += (_, e) => { if (e.Key != Key.Escape && HandleNavigationKey(e.Key, Keyboard.Modifiers)) e.Handled = true; };
        // Let a drop-down, popup or editor consume Escape before falling back to menu navigation.
        KeyDown += (_, e) => { if (e.Key == Key.Escape && HandleNavigationKey(e.Key, Keyboard.Modifiers)) e.Handled = true; };
        Loaded += (_, _) => search.Focus(); Closed += (_, _) => closed = true;
        Closing += (_, e) => {
            if (!HasUnsavedChanges) return;
            try { if (this.confirmDiscard()) return; }
            catch (Exception error) { status.Text = "未关闭菜单：" + error.Message; e.Cancel = true; return; }
            e.Cancel = true; status.Text = "菜单选项尚未保存，请保存或撤销修改后关闭。";
        };
        ApplyOptions(); RenderNavigation(); RenderPlaces(); RenderResults();
    }

    static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    static DependencyObject? InputParent(DependencyObject node) => (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);
    static void Id(DependencyObject element, string id) => AutomationProperties.SetAutomationId(element, id);
    Button Button(string text, string id, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 7, 10, 7) };
        Id(button, id); button.Click += (_, _) => action(); return button;
    }
    static void StartTarget(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    static ClassicStartCatalogSnapshot DemoCatalog() => new(new[] {
        new ClassicStartApp("app:" + new string('1', 64), "便笺 · 演示", "工作", "demo"), new ClassicStartApp("app:" + new string('2', 64), "浏览器 · 演示", "网络", "demo"),
        new ClassicStartApp("app:" + new string('3', 64), "文件管理 · 演示", "工具", "demo"), new ClassicStartApp("app:" + new string('4', 64), "影音播放器 · 演示", "影音", "demo"),
        new ClassicStartApp("app:" + new string('5', 64), "代码编辑器 · 演示", "工作", "demo") });

    void BuildSettings()
    {
        Id(optionsView, "start-options-view");
        var panel = new StackPanel { Margin = new Thickness(4, 0, 4, 0) }; optionsView.Content = panel;
        panel.Children.Add(new TextBlock { Text = "菜单选项", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "调整立即预览。点击保存设置后才写入独立配置。", Margin = new Thickness(0, 6, 0, 14), TextWrapping = TextWrapping.Wrap });
        var choices = new WrapPanel(); panel.Children.Add(choices);
        void Choice(string caption, ComboBox combo, string id, params object[] items) {
            var field = new StackPanel { Margin = new Thickness(0, 0, 16, 12) }; field.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 5) });
            combo.Width = 140; foreach (var item in items) combo.Items.Add(item); Id(combo, id); AutomationProperties.SetName(combo, caption); field.Children.Add(combo); choices.Children.Add(field); combo.SelectionChanged += (_, _) => ReadControls();
        }
        Choice("风格", style, "start-style", "Windows 7", "Windows 10", "紧凑"); Choice("图标尺寸", icons, "start-icon-size", 16, 24, 32); Choice("固定程序上限", maxPinned, "start-max-pinned", 6, 12, 18, 24);
        var checks = new WrapPanel(); panel.Children.Add(checks);
        foreach (var (check, id) in new[] { (ascending, "start-sort"), (entryIcons, "start-entry-icons"), (userHeading, "start-user-heading") }) {
            check.Style = (Style)FindResource("ShellFeatureChoice"); check.Margin = new Thickness(0, 0, 18, 12); Id(check, id); checks.Children.Add(check); check.Checked += (_, _) => ReadControls(); check.Unchecked += (_, _) => ReadControls();
        }
        panel.Children.Add(new TextBlock { Text = "右侧常用位置", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) });
        var placePanel = new WrapPanel(); panel.Children.Add(placePanel);
        foreach (var place in ClassicStartPlaces.All) {
            var check = new CheckBox { Content = place.Name, Tag = place.Id, Margin = new Thickness(0, 0, 15, 8), Style = (Style)FindResource("ShellFeatureChoice") };
            Id(check, "start-place-option-" + place.Id); check.Checked += (_, _) => ReadControls(); check.Unchecked += (_, _) => ReadControls(); placeChoices.Add(check); placePanel.Children.Add(check);
        }
        panel.Children.Add(new TextBlock { Text = "自定义文件夹", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) }); panel.Children.Add(customFolders);
        var folderRow = new Grid(); folderRow.ColumnDefinitions.Add(new() { Width = new GridLength(125) }); folderRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); folderRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Id(folderName, "start-folder-name"); folderName.ToolTip = "文件夹显示名称"; folderName.Margin = new Thickness(0, 0, 8, 0);
        var nameField = new StackPanel(); nameField.Children.Add(new TextBlock { Text = "显示名称", Margin = new Thickness(0, 0, 0, 4) }); nameField.Children.Add(folderName); folderRow.Children.Add(nameField);
        Id(folderPath, "start-folder-path"); folderPath.ToolTip = "本地文件夹完整路径";
        var pathField = new StackPanel(); pathField.Children.Add(new TextBlock { Text = "本地文件夹路径", Margin = new Thickness(0, 0, 0, 4) }); pathField.Children.Add(folderPath); Grid.SetColumn(pathField, 1); folderRow.Children.Add(pathField);
        var add = Button("添加", "start-folder-add", () => AddCustomFolder(folderName.Text, folderPath.Text)); add.VerticalAlignment = VerticalAlignment.Bottom; Grid.SetColumn(add, 2); folderRow.Children.Add(add); panel.Children.Add(folderRow);
        var files = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) }; panel.Children.Add(files);
        var save = Button("保存设置", "start-save", SaveSettings); save.Style = (Style)FindResource("ShellPrimaryButton"); files.Children.Add(save);
        files.Children.Add(Button("撤销修改", "start-revert", RevertSettings)); files.Children.Add(Button("导入…", "start-import", () => {
            var dialog = new OpenFileDialog { Filter = "开始菜单配置 (*.json)|*.json", Title = "导入菜单选项（先预览，再保存）" }; if (dialog.ShowDialog(this) == true) ImportSettings(dialog.FileName);
        }));
        files.Children.Add(Button("导出…", "start-export", () => {
            var dialog = new SaveFileDialog { Filter = "开始菜单配置 (*.json)|*.json", FileName = "classicdesk-start.json", Title = "导出菜单选项（请选择新文件）", OverwritePrompt = false }; if (dialog.ShowDialog(this) == true) ExportSettings(dialog.FileName);
        })); files.Children.Add(Button("返回菜单", "start-return", () => ShowSettings(false)));
    }

    void ApplyOptions()
    {
        syncing = true; style.SelectedIndex = options.Style switch { "win10" => 1, "compact" => 2, _ => 0 }; icons.SelectedItem = options.IconSize;
        if (!maxPinned.Items.Contains(options.MaxPinned)) maxPinned.Items.Add(options.MaxPinned); maxPinned.SelectedItem = options.MaxPinned;
        ascending.IsChecked = options.SortAscending; entryIcons.IsChecked = options.ShowEntryIcons; userHeading.IsChecked = options.ShowUserHeading;
        foreach (var check in placeChoices) check.IsChecked = options.EffectiveVisiblePlaces.Contains((string)check.Tag);
        syncing = false;
        bool compact = options.Style == "compact"; body.ColumnDefinitions[0].Width = new GridLength(compact ? 94 : 126); body.ColumnDefinitions[2].Width = new GridLength(compact ? 136 : 174);
        root.Margin = new Thickness(compact ? 12 : 18); results.FontSize = compact ? 11 : 12;
        var panel = new FrameworkElementFactory(options.Style == "win10" ? typeof(WrapPanel) : typeof(StackPanel));
        if (options.Style == "win10") panel.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
        results.ItemsPanel = new ItemsPanelTemplate(panel); ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        RenderCustomFolders();
    }
    void ReadControls()
    {
        if (syncing || icons.SelectedItem is not int size || maxPinned.SelectedItem is not int limit) return;
        options = options with { Style = style.SelectedIndex switch { 1 => "win10", 2 => "compact", _ => "win7" }, IconSize = size, MaxPinned = limit,
            SortAscending = ascending.IsChecked == true, ShowEntryIcons = entryIcons.IsChecked == true, ShowUserHeading = userHeading.IsChecked == true,
            VisiblePlaces = placeChoices.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray() };
        Dirty(); ApplyOptions(); RenderPlaces(); RenderResults();
    }
    static bool SameOptions(ClassicStartOptions a, ClassicStartOptions b) => a.Style == b.Style && a.IconSize == b.IconSize && a.MaxPinned == b.MaxPinned && a.SortAscending == b.SortAscending && a.ShowEntryIcons == b.ShowEntryIcons && a.ShowUserHeading == b.ShowUserHeading && a.EffectivePinnedIds.SequenceEqual(b.EffectivePinnedIds) && a.EffectiveCustomFolders.SequenceEqual(b.EffectiveCustomFolders) && a.EffectiveVisiblePlaces.SequenceEqual(b.EffectiveVisiblePlaces);
    void Dirty() { HasUnsavedChanges = !SameOptions(options, saved); status.Text = HasUnsavedChanges ? "菜单选项已修改 · 尚未保存。" : "本次选项与已保存设置一致。"; }
    public void ShowSettings(bool show) { settingsVisible = show; optionsView.Visibility = show ? Visibility.Visible : Visibility.Collapsed; body.Visibility = show ? Visibility.Collapsed : Visibility.Visible; }
    public void Search(string query) => search.Text = query;
    public void SelectSection(string value) { section = value; RenderNavigation(); RenderResults(); }
    void RenderNavigation()
    {
        navigation.Children.Clear();
        void Add(string name, string key) { var b = Button(name, "start-section-" + key, () => SelectSection(key)); b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Style = (Style)FindResource("ShellSegment"); b.Tag = section == key ? "selected" : ""; navigation.Children.Add(b); }
        Add("固定程序", "pinned"); Add("所有程序", "all");
        navigation.Children.Add(new TextBlock { Text = "分类", Foreground = Brush("#687991"), Margin = new Thickness(8, 16, 0, 8) });
        foreach (var category in catalog.Apps.Select(a => a.Category).Distinct().OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)) Add(category, "category:" + category);
    }
    void RenderResults()
    {
        var previous = (results.SelectedItem as ListBoxItem)?.Tag as ClassicStartApp;
        IEnumerable<ClassicStartApp> apps = ClassicStartCatalog.Query(catalog, options, search.Text);
        if (string.IsNullOrWhiteSpace(search.Text)) {
            if (section == "pinned") apps = ClassicStartCatalog.Pinned(catalog, options);
            else if (section.StartsWith("category:", StringComparison.Ordinal)) apps = apps.Where(a => a.Category == section[9..]);
        }
        VisibleApps = apps.ToArray(); results.Items.Clear();
        count.Text = (string.IsNullOrWhiteSpace(search.Text) ? section == "pinned" ? "固定程序" : section == "all" ? "所有程序" : section[9..] : "搜索结果") + "  ·  " + VisibleApps.Count;
        foreach (var app in VisibleApps) {
            var row = new Grid { Margin = new Thickness(2, 3, 2, 3) }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            if (options.ShowEntryIcons) { var icon = AppIcons.View(app.Category.Contains("影音") ? "video" : "document", options.IconSize); icon.Margin = new Thickness(4, 0, 9, 0); row.Children.Add(icon); }
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; label.Children.Add(new TextBlock { Text = app.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            if (options.Style != "compact") label.Children.Add(new TextBlock { Text = app.Category, FontSize = 10, Foreground = Brush("#687991"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var pinned = options.EffectivePinnedIds.Contains(app.Id); var pin = Button(pinned ? "取消固定" : "固定", "start-pin-" + app.Id, () => TogglePin(app.Id)); pin.Padding = new Thickness(6, 4, 6, 4); pin.FontSize = 10; pin.ToolTip = pinned ? "取消固定这个程序" : "将程序固定到菜单"; Grid.SetColumn(pin, 2); row.Children.Add(pin);
            var item = new ListBoxItem { Content = row, Tag = app, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = app.Name + "\n" + app.SourcePath };
            if (options.Style == "win10") { item.Width = 164; item.MinHeight = 92; item.Background = Brush("#EAF0FC"); item.Margin = new Thickness(3); }
            AutomationProperties.SetName(item, app.Name); results.Items.Add(item);
        }
        if (results.Items.Count > 0) results.SelectedIndex = previous is null ? 0 : Math.Max(0, VisibleApps.ToList().FindIndex(a => a.Id == previous.Id));
        empty.Visibility = VisibleApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Text = !string.IsNullOrWhiteSpace(search.Text) ? "没有匹配的程序。试试更短的名称或分类。" : section == "pinned" ? "还没有固定程序。\n在“所有程序”中点击固定。" : "没有可用的程序。\n点击“刷新本机应用”读取开始菜单快捷方式。";
    }
    void RenderPlaces()
    {
        places.Children.Clear(); heading.Text = "用户目录"; heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 12); heading.Visibility = options.ShowUserHeading ? Visibility.Visible : Visibility.Collapsed; places.Children.Add(heading);
        foreach (var place in ClassicStartPlaces.Visible(options)) {
            var targetId = place.Id; var button = Button(place.Name, "start-place-" + targetId, () => OpenPlace(targetId)); button.HorizontalContentAlignment = HorizontalAlignment.Left;
            var content = new StackPanel { Orientation = Orientation.Horizontal }; if (options.ShowEntryIcons) content.Children.Add(AppIcons.View("folder", 18)); content.Children.Add(new TextBlock { Text = place.Name, Margin = new Thickness(options.ShowEntryIcons ? 8 : 0, 0, 0, 0), MaxWidth = options.Style == "compact" ? 80 : 115, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content; button.ToolTip = place.Name; places.Children.Add(button);
        }
    }
    public void OpenPlace(string id) { try { openPlace(ClassicStartPlaces.Resolve(options, id)); status.Text = "已请求打开位置。"; } catch (Exception e) { status.Text = "位置未打开：" + e.Message; } }
    public void TogglePin(string id)
    {
        if (!catalog.Apps.Any(a => a.Id == id)) { status.Text = "程序已不在当前列表，请刷新。"; return; }
        var ids = options.EffectivePinnedIds.ToList(); if (!ids.Remove(id)) { if (ids.Count >= options.MaxPinned) { status.Text = "已达到固定程序上限，请取消一个固定程序或调整选项。"; return; } ids.Add(id); }
        options = options with { PinnedIds = ids.ToArray() }; Dirty(); RenderResults();
    }
    public void AddCustomFolder(string name, string path)
    {
        try {
            var folder = new ClassicStartFolder("folder:" + Guid.NewGuid().ToString("N"), name.Trim(), path.Trim()); var candidate = options with { CustomFolders = options.EffectiveCustomFolders.Append(folder).ToArray() }; candidate.Validate();
            _ = ClassicStartPlaces.Resolve(candidate, folder.Id); options = candidate; folderName.Clear(); folderPath.Clear(); Dirty(); RenderCustomFolders(); RenderPlaces();
        } catch (Exception e) { status.Text = "文件夹未添加：" + e.Message; }
    }
    void RenderCustomFolders()
    {
        customFolders.Children.Clear(); foreach (var folder in options.EffectiveCustomFolders) {
            var row = new DockPanel(); var remove = Button("移除", "start-folder-remove-" + folder.Id, () => { options = options with { CustomFolders = options.EffectiveCustomFolders.Where(f => f.Id != folder.Id).ToArray() }; Dirty(); RenderCustomFolders(); RenderPlaces(); }); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = folder.Name + " · " + folder.Path, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder.Path, VerticalAlignment = VerticalAlignment.Center }); customFolders.Children.Add(row);
        }
    }
    public async Task RefreshCatalogAsync()
    {
        if (scanning || closed) return; scanning = true; refresh.IsEnabled = false; status.Text = "正在只读扫描开始菜单快捷方式…";
        try {
            var next = await Task.Run(scan).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => { if (closed) return; catalog = next; demoCatalog = false; RenderNavigation(); RenderResults(); status.Text = next.Errors.Count == 0 ? $"已读取 {next.Apps.Count} 个本机程序。" : $"已读取 {next.Apps.Count} 个程序；扫描提示：" + string.Join("；", next.Errors.Take(3)); });
        }
        catch (Exception e) { await Dispatcher.InvokeAsync(() => { if (!closed) status.Text = "扫描失败，保留原列表：" + e.Message; }); }
        finally { await Dispatcher.InvokeAsync(() => { scanning = false; if (!closed) refresh.IsEnabled = true; }); }
    }
    public void LaunchSelected()
    {
        if (results.SelectedItem is not ListBoxItem { Tag: ClassicStartApp app }) { status.Text = "请选择一个程序。"; return; }
        if (demoCatalog) { status.Text = "演示程序未启动，请先点击“刷新本机应用”读取真实程序。"; return; }
        try { var target = ClassicStartCatalog.ResolveLaunch(catalog, app.Id); launch(target); status.Text = "已请求启动：" + app.Name + "。"; }
        catch (Exception e) { status.Text = "程序未启动：" + e.Message; }
    }
    public bool HandleNavigationKey(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (key == Key.Escape && modifiers == ModifierKeys.None) { if (settingsVisible) ShowSettings(false); else if (search.Text.Length > 0) Search(""); else Close(); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.F) { search.Focus(); search.SelectAll(); return true; }
        if (settingsVisible || modifiers != ModifierKeys.None) return false;
        // Only intercept navigation from the search field or result list, never another button/control.
        var focused = Keyboard.FocusedElement;
        if (focused is not null && focused != search && focused != results && focused is not ListBoxItem) return false;
        if (key == Key.Down || key == Key.Up) { if (results.Items.Count > 0) { results.SelectedIndex = Math.Clamp(results.SelectedIndex + (key == Key.Down ? 1 : -1), 0, results.Items.Count - 1); results.ScrollIntoView(results.SelectedItem); } return true; }
        if (key == Key.Enter) { LaunchSelected(); return true; } return false;
    }
    public void SaveSettings()
    {
        try { var file = ClassicStartProfileFile.Save(options, revision, profilePath); saved = options = file.Options; revision = file.Revision; HasUnsavedChanges = false; status.Text = "菜单设置已保存。"; }
        catch (Exception e) { status.Text = "设置未保存，原文件保留：" + e.Message; }
    }
    public void RevertSettings() { options = saved; HasUnsavedChanges = false; ApplyOptions(); RenderPlaces(); RenderResults(); status.Text = "已撤销本次修改。"; }
    public void ImportSettings(string path) { try { options = ClassicStartProfileFile.Import(path); Dirty(); ApplyOptions(); RenderPlaces(); RenderResults(); status.Text = "已导入选项草稿，请检查后保存。"; } catch (Exception e) { status.Text = "导入失败，当前选项保留：" + e.Message; } }
    public void ExportSettings(string path) { try { ClassicStartProfileFile.Export(path, options); status.Text = "已导出菜单选项：" + path; } catch (Exception e) { status.Text = "导出失败：" + e.Message; } }
}
