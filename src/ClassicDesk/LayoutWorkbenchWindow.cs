using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace ClassicDesk;

/// <summary>Editor for independent layout designs. No shell host, registry or process APIs.</summary>
public sealed class LayoutWorkbenchWindow : Window
{
    readonly string profilePath;
    readonly Action<Window>? openStart;
    readonly StackPanel rows = new();
    readonly TextBlock status = Text("布局草稿 · Windows 未修改", 11);
    readonly TextBlock changes = Text("", 11);
    readonly TextBlock explanation = Text("", 11);
    readonly TextBlock title = Text("任务栏布局", 23, true);
    readonly Button save, undo;
    readonly TaskbarDesignPreview preview = new();
    readonly Dictionary<string, FrameworkElement> controls = new();
    TaskbarDesignOptions saved;
    string? revision;
    int page;
    public TaskbarDesignOptions Draft { get; private set; }
    public bool IsDirty => TaskbarDesignProfileFile.Fingerprint(Draft) != TaskbarDesignProfileFile.Fingerprint(saved);
    public int ActivePage => page;
    public IReadOnlyDictionary<string, FrameworkElement> Controls => controls;

    public LayoutWorkbenchWindow(string? path = null, Action<Window>? openStartMenu = null, Func<bool>? confirmDiscard = null)
    {
        profilePath = path ?? TaskbarDesignProfileFile.DefaultPath; openStart = openStartMenu;
        string? error = null;
        try { var loaded = TaskbarDesignProfileFile.Load(profilePath); saved = loaded.Options; revision = loaded.Revision; }
        catch (Exception e) { saved = new(); error = "原布局文件读取失败，已保护原文件：" + e.Message; }
        Draft = saved;
        Closing += (_, e) => {
            if (!IsDirty) return;
            try { if (!(confirmDiscard?.Invoke() ?? (IsVisible && MessageBox.Show(this, "布局草稿尚未保存，是否放弃更改并关闭？", "ClassicDesk", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes))) e.Cancel = true; }
            catch (Exception ex) { e.Cancel = true; status.Text = "关闭确认未完成，草稿仍保留：" + ex.Message; }
        };
        Title = "ClassicDesk · 布局与开始菜单"; Width = 1040; Height = 820; MinWidth = 720; MinHeight = 600;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12; Foreground = Ink; Background = Surface;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; UseLayoutRounding = true; Icon = AppIcons.Get("brand");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClassicDesk;component/ShellTheme.xaml", UriKind.Relative) });
        var root = new Grid { Margin = new Thickness(24, 18, 24, 18), Background = Surface };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(216) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = new Border { Background = Surface, Child = root };
        preview.SizeChanged += (_, _) => Refresh();
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); root.Children.Add(header);
        var words = new StackPanel(); words.Children.Add(title);
        words.Children.Add(Text("对照常用桌面布局选项。开始菜单可独立使用；以下任务栏和托盘只保存设计预览。", 11)); header.Children.Add(words);
        var menu = Button("打开独立开始菜单", "design-start-menu", () => { try { openStart?.Invoke(this); } catch (Exception e) { status.Text = "菜单未打开：" + e.Message; } }, true);
        menu.IsEnabled = openStart is not null; menu.Margin = new Thickness(16, 0, 0, 0); menu.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(menu, 1); header.Children.Add(menu);
        var stage = new Grid { Margin = new Thickness(0, 16, 0, 8) }; stage.RowDefinitions.Add(new()); stage.RowDefinitions.Add(new() { Height = GridLength.Auto });
        stage.Children.Add(preview); explanation.Margin = new Thickness(4, 4, 0, 0); Grid.SetRow(explanation, 1); stage.Children.Add(explanation); Grid.SetRow(stage, 1); root.Children.Add(stage);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 6, 0, 12) };
        toolbar.Children.Add(Button("任务栏布局", "design-page-taskbar", () => SelectPage(0)));
        toolbar.Children.Add(Button("托盘项目", "design-page-tray", () => SelectPage(1)));
        toolbar.Children.Add(Button("重置本页", "design-reset-page", ResetPage));
        var size = new ComboBox { ItemsSource = new[] { "640 px 预览", "1024 px 预览", "1440 px 预览", "1920 px 预览" }, SelectedIndex = 1, Width = 142, Margin = new Thickness(12, 0, 0, 0), Style = (Style)FindResource("ShellChoice") };
        AutomationProperties.SetAutomationId(size, "design-screen-width"); AutomationProperties.SetName(size, "模拟屏幕宽度");
        size.SelectionChanged += (_, _) => { preview.ScreenWidth = new[] { 640, 1024, 1440, 1920 }[size.SelectedIndex]; Refresh(); }; toolbar.Children.Add(size);
        Grid.SetRow(toolbar, 2); root.Children.Add(toolbar);
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 3); root.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) }; changes.Margin = new Thickness(0, 0, 0, 5); footer.Children.Add(changes); footer.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        save = Button("保存布局草稿", "design-save", SaveDraft, true); undo = Button("撤销更改", "design-undo", DiscardDraft);
        actions.Children.Add(save); actions.Children.Add(undo);
        actions.Children.Add(Button("导入", "design-import", () => { var picker = new OpenFileDialog { Filter = "布局草稿 (*.json)|*.json", Title = "导入布局草稿" }; if (picker.ShowDialog(this) == true) Try(() => ImportDraft(picker.FileName)); }));
        actions.Children.Add(Button("导出", "design-export", () => { var picker = new SaveFileDialog { Filter = "布局草稿 (*.json)|*.json", FileName = "ClassicDesk-布局草稿.json", Title = "导出布局草稿", OverwritePrompt = true }; if (picker.ShowDialog(this) == true) Try(() => ExportDraft(picker.FileName, TaskbarDesignProfileFile.Revision(picker.FileName))); }));
        footer.Children.Add(actions); Grid.SetRow(footer, 4); root.Children.Add(footer);
        AutomationProperties.SetAutomationId(status, "design-status"); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(changes, "design-changes");
        SelectPage(0); if (error is not null) status.Text = error;
    }
    static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(42, 50, 68));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(99, 115, 139));
    static readonly Brush Surface = new SolidColorBrush(Color.FromRgb(246, 247, 251));
    static TextBlock Text(string value, double size = 12, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Foreground = bold ? Ink : Muted, Margin = new Thickness(0, 4, 0, 4) };
    Button Button(string text, string id, Action action, bool primary = false)
    {
        var result = new Button { Content = text, Style = (Style)FindResource(primary ? "ShellPrimaryButton" : "ShellButton"), Margin = new Thickness(0, 0, 7, 0) };
        AutomationProperties.SetAutomationId(result, id); AutomationProperties.SetName(result, text); result.Click += (_, _) => action(); return result;
    }
    void Try(Action action) { try { action(); } catch (Exception e) { status.Text = "操作未完成，草稿与原文件保留：" + e.Message; } }
    void Row(string name, string detail, string id, FrameworkElement control)
    {
        controls[id] = control; AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); AutomationProperties.SetHelpText(control, detail);
        var row = new Grid { Margin = new Thickness(16, 9, 16, 9) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(215) });
        var words = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; words.Children.Add(Text(name, 12, true)); words.Children.Add(Text(detail, 10)); row.Children.Add(words);
        control.VerticalAlignment = VerticalAlignment.Center; control.HorizontalAlignment = HorizontalAlignment.Stretch; Grid.SetColumn(control, 1); row.Children.Add(control);
        rows.Children.Add(new Border { Child = row, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(227, 231, 242)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 0, 7) });
    }
    ComboBox Choice(string[] names, int selected, Action<int> action)
    {
        var result = new ComboBox { ItemsSource = names, SelectedIndex = selected, Style = (Style)FindResource("ShellChoice") };
        result.SelectionChanged += (_, _) => { if (result.SelectedIndex >= 0) Try(() => action(result.SelectedIndex)); }; return result;
    }
    CheckBox Toggle(bool selected, Action<bool> action)
    {
        var result = new CheckBox { IsChecked = selected, Style = (Style)FindResource("ShellSwitch"), HorizontalAlignment = HorizontalAlignment.Right };
        result.Checked += (_, _) => Try(() => action(true)); result.Unchecked += (_, _) => Try(() => action(false)); return result;
    }
    public void SelectPage(int index)
    {
        if (index is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(index));
        page = index; rows.Children.Clear(); controls.Clear(); title.Text = index == 0 ? "任务栏布局" : "托盘与系统图标";
        if (page == 0)
        {
            Row("停靠位置", "预览上、下、左、右四个边缘。", "design-dock", Choice(["底部", "顶部", "左侧", "右侧"], Array.IndexOf(TaskbarDesignOptions.Docks, Draft.Dock), i => Change(Draft with { Dock = TaskbarDesignOptions.Docks[i] })));
            Row("合并按钮与文字标签", "容量变化使用六个合成窗口演示，不读取真实窗口标题。", "design-combine", Choice(["始终合并 · 隐藏标签", "任务栏占满时合并", "从不合并 · 显示标签", "从不合并 · 隐藏标签"], Array.IndexOf(TaskbarDesignOptions.CombineModes, Draft.Combine), i => Change(Draft with { Combine = TaskbarDesignOptions.CombineModes[i] })));
            Row("开始与应用位置", "分开布局、一起居中、全靠左。", "design-alignment", Choice(["开始靠左 · 应用居中", "开始与应用居中", "开始与应用全靠左"], Array.IndexOf(TaskbarDesignOptions.Alignments, Draft.Alignment), i => Change(Draft with { Alignment = TaskbarDesignOptions.Alignments[i] })));
            Row("图标大小", "尺寸独立于按钮标签宽度。", "design-icon-size", Choice(ShellProfile.IconSizes.Select(n => n + " px").ToArray(), Array.IndexOf(ShellProfile.IconSizes, Draft.IconSize), i => Change(Draft with { IconSize = ShellProfile.IconSizes[i] })));
            Row("任务栏厚度", "竖向布局也保留此尺寸。", "design-height", Choice(ShellProfile.TaskbarHeights.Select(n => n + " px").ToArray(), Array.IndexOf(ShellProfile.TaskbarHeights, Draft.Height), i => Change(Draft with { Height = ShellProfile.TaskbarHeights[i] })));
            var labelWidth = new Slider { Minimum = 80, Maximum = 240, Value = Draft.LabelWidth, TickFrequency = 4, IsSnapToTickEnabled = true };
            labelWidth.ValueChanged += (_, _) => Change(Draft with { LabelWidth = (int)Math.Round(labelWidth.Value) });
            Row("文字按钮宽度", "80–240 px；合并或隐藏标签时保留设计值。", "design-label-width", labelWidth);
            var margin = new Slider { Minimum = 0, Maximum = 32, Value = Draft.EdgeMargin, TickFrequency = 1, IsSnapToTickEnabled = true };
            margin.ValueChanged += (_, _) => Change(Draft with { EdgeMargin = (int)Math.Round(margin.Value) }); Row("边缘留白", "任务栏两端额外留白，与托盘间距分开。", "design-edge-margin", margin);
            Row("分段背景", "开始、应用和托盘分别显示背景。", "design-segmented", Toggle(Draft.Segmented, value => Change(Draft with { Segmented = value })));
            rows.Children.Add(Text("原工具第五个“从不合并”档位的具体差异尚未实测，本版不把它合并为已完成项。", 10));
        }
        else
        {
            var gap = new Slider { Minimum = 0, Maximum = 16, Value = Draft.TrayGap, TickFrequency = 1, IsSnapToTickEnabled = true };
            gap.ValueChanged += (_, _) => Change(Draft with { TrayGap = (int)Math.Round(gap.Value) }); Row("托盘项目间距", "0–16 px；可与应用图标大小分别编辑。", "design-tray-gap", gap);
            rows.Children.Add(Text("显示项目 · 仅控制上方示意图", 14, true));
            rows.Children.Add(Text("对应本地工具的 17 个入口。设备是否存在、图标权限和系统可用性仍需以后实机核对。", 11));
            var list = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (var item in TaskbarDesignTray.Items)
            {
                var check = new CheckBox { Content = item.Name, IsChecked = Draft.EffectiveTrayItems.Contains(item.Id), Margin = new Thickness(12, 9, 8, 9), FontSize = 12 };
                controls["design-tray-" + item.Id] = check; AutomationProperties.SetAutomationId(check, "design-tray-" + item.Id); AutomationProperties.SetName(check, item.Name);
                void Set(bool value) => Change(Draft with { VisibleTrayItems = TaskbarDesignTray.Items.Where(t => t.Id == item.Id ? value : Draft.EffectiveTrayItems.Contains(t.Id)).Select(t => t.Id).ToArray() });
                check.Checked += (_, _) => Set(true); check.Unchecked += (_, _) => Set(false); list.Children.Add(check);
            }
            rows.Children.Add(new Border { Child = list, Background = Brushes.White, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 5, 0, 0) });
        }
        Refresh();
    }
    public void Change(TaskbarDesignOptions value) { value.Validate(); Draft = value; Refresh(); }
    void Refresh()
    {
        preview.Options = Draft; preview.InvalidateVisual();
        var layout = preview.CurrentLayout;
        explanation.Text = $"{layout.Explanation} · {Draft.IconSize} px 图标 / {Draft.Height} px 厚度 / {Draft.EdgeMargin} px 边距 / {Draft.TrayGap} px 托盘间距";
        save.IsEnabled = IsDirty && revision is not null; undo.IsEnabled = IsDirty;
        var changed = typeof(TaskbarDesignOptions).GetProperties().Where(p => p.Name != nameof(TaskbarDesignOptions.EffectiveTrayItems))
            .Count(p => p.Name == nameof(TaskbarDesignOptions.VisibleTrayItems) ? !Draft.EffectiveTrayItems.SequenceEqual(saved.EffectiveTrayItems) : !Equals(p.GetValue(saved), p.GetValue(Draft)));
        changes.Text = IsDirty ? $"草稿有 {changed} 项更改 · 仅保存到本地布局方案" : revision == "missing" ? "默认布局 · 尚未创建文件" : "布局草稿已保存 · 系统效果未应用";
    }
    public void ResetPage()
    {
        var defaults = new TaskbarDesignOptions(); Change(page == 1 ? Draft with { TrayGap = defaults.TrayGap, VisibleTrayItems = defaults.VisibleTrayItems } : defaults with { TrayGap = Draft.TrayGap, VisibleTrayItems = Draft.VisibleTrayItems });
        SelectPage(page); status.Text = "本页已重置 · Windows 未修改";
    }
    public void DiscardDraft() { Change(saved); SelectPage(page); status.Text = "草稿已撤销 · Windows 未修改"; }
    public void SaveDraft() => Try(() =>
    {
        if (revision is null) throw new IOException("原布局文件损坏，不能覆盖；请另行导出草稿。");
        revision = TaskbarDesignProfileFile.Save(profilePath, Draft, revision); saved = Draft; Refresh(); status.Text = "布局草稿已保存 · Windows 未修改";
    });
    public void ImportDraft(string path) { Change(TaskbarDesignProfileFile.Import(path)); SelectPage(page); status.Text = "已导入草稿 · 保存前可撤销"; }
    public void ExportDraft(string path, string expectedRevision)
    {
        string destination = Path.GetFullPath(path);
        if (new[] { profilePath, profilePath + ".previous", profilePath + ".save.lock" }.Any(p => string.Equals(Path.GetFullPath(p), destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("不能覆盖当前布局方案、备份或保存锁。");
        TaskbarDesignProfileFile.Save(path, Draft, expectedRevision); status.Text = "草稿已导出 · 当前保存状态不变";
    }
}

public sealed class TaskbarDesignPreview : FrameworkElement
{
    public TaskbarDesignOptions Options { get; set; } = new();
    public int ScreenWidth { get; set; } = 1024;
    double Scale => ActualWidth > 18 && ActualHeight > 14 ? Math.Min((ActualWidth - 18) / ScreenWidth, (ActualHeight - 14) / 120) : 0;
    public TaskbarDesignResult CurrentLayout => TaskbarDesignLayout.Calculate(Options, TaskbarDesignLayout.Examples, ScreenWidth, Scale > 0 ? Math.Max(120, (ActualHeight - 14) / Scale) : 400);
    static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (ActualWidth < 1 || ActualHeight < 1) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        dc.DrawRoundedRectangle(Brush("#E9EDF8"), null, new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
        // A screen frame uses DIP geometry; render scaling never writes display settings.
        double scale = Scale;
        if (scale <= 0) { dc.Pop(); return; }
        double logicalHeight = Math.Max(120, (ActualHeight - 14) / scale);
        var layout = CurrentLayout;
        dc.PushTransform(new TranslateTransform((ActualWidth - ScreenWidth * scale) / 2, 7)); dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawRoundedRectangle(Brush("#DCE5FA"), null, new Rect(0, 0, ScreenWidth, logicalHeight), 12, 12);
        var sample = new Rect(ScreenWidth * .25, 18, ScreenWidth * .5, Math.Max(36, logicalHeight - Options.Height - 40)); dc.DrawRoundedRectangle(Brush("#F5F7FD"), new Pen(Brush("#D1DCF2"), 1), sample, 12, 12);
        DrawText(dc, "布局示意 · 合成窗口", sample.X + 20, sample.Y + 12, 14, sample.Width - 40);
        for (int i = 0; i < 4 && 43 + i * 20 + 9 < sample.Height; i++) dc.DrawRoundedRectangle(Brush(i == 0 ? "#D8E1F7" : "#E7ECF7"), null, new Rect(sample.X + 20, sample.Y + 43 + i * 20, sample.Width - 40 - i * 15, 7), 4, 4);
        void Background(Rect rect) { if (rect.Width > 0 && rect.Height > 0) dc.DrawRoundedRectangle(Brush("#F4F6FD"), new Pen(Brush("#D5DCF0"), 1), rect, Options.Segmented ? 9 : 0, Options.Segmented ? 9 : 0); }
        if (Options.Segmented) { Background(layout.Start); Background(layout.Applications); Background(layout.Tray); } else Background(layout.Bar);
        var start = layout.Start; var center = new Point(start.X + start.Width / 2, start.Y + start.Height / 2);
        for (int i = 0; i < 4; i++) dc.DrawRectangle(Brush("#637EE0"), null, new Rect(center.X - 9 + (i % 2) * 10, center.Y - 9 + (i / 2) * 10, 8, 8));
        string[] colors = ["#D6B15B", "#658ED9", "#8D7ECB", "#67A49B"]; int n = 0;
        foreach (var button in layout.Buttons)
        {
            var rect = button.Bounds; double icon = Math.Min(Options.IconSize, Math.Min(rect.Width, rect.Height) - 8), ix = rect.X + (button.LabelVisible ? 10 : (rect.Width - icon) / 2), iy = rect.Y + (rect.Height - icon) / 2;
            dc.DrawRoundedRectangle(Brush(colors[n++ % colors.Length]), null, new Rect(ix, iy, icon, icon), 5, 5);
            if (button.LabelVisible) DrawText(dc, button.Title, ix + icon + 8, rect.Y + (rect.Height - 16) / 2, 12, Math.Max(1, rect.Width - icon - 28));
            if (button.WindowCount > 1) DrawText(dc, button.WindowCount.ToString(), ix + icon - 3, iy - 6, 10, 15);
            dc.DrawRoundedRectangle(Brush("#6582DC"), null, new Rect(rect.X + rect.Width / 2 - 7, rect.Bottom - 5, 14, 2), 1, 1);
        }
        bool vertical = Options.Dock is "left" or "right"; double at = vertical ? layout.Tray.Y + 4 : layout.Tray.X + 4;
        foreach (var id in Options.EffectiveTrayItems)
        {
            var item = TaskbarDesignTray.Items.Single(t => t.Id == id); double length = !vertical && id == "clock" ? 56 : 22;
            if (at + length > (vertical ? layout.Tray.Bottom : layout.Tray.Right) - 2) break;
            if (vertical) DrawText(dc, id == "clock" ? "12:30" : item.Glyph, layout.Tray.X + 4, at, 11, Math.Max(1, layout.Tray.Width - 8));
            else DrawText(dc, item.Glyph, at, layout.Tray.Y + (layout.Tray.Height - 16) / 2, 12, length);
            at += length + Options.TrayGap;
        }
        dc.Pop(); dc.Pop(); dc.Pop();
    }
    void DrawText(DrawingContext dc, string value, double x, double y, double size, double width)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brush("#4C5B7B"), VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new Point(x, y));
    }
}
