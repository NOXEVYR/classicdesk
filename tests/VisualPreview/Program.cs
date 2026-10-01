using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassicDesk;

internal static class Program
{
    sealed record Result(string Name, bool Passed, string Detail);
    sealed record Picture(string File, int Width, int Height, double Dpi, string Hash, int Colors, int OpaquePixels);
    static readonly List<Result> Results = [];
    static readonly List<Picture> Pictures = [];
    static readonly List<Window> Windows = [];
    static string output = "", fixture = "";
    static int externalCalls;

    [STAThread]
    public static int Main(string[] args)
    {
        output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        fixture = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var baseline = new ShellProfile();
        void Difference(string name, ShellProfile before, ShellProfile after, ShellOverviewMode mode = ShellOverviewMode.Desktop)
        {
            Check(name, () =>
            {
                before.Validate(); after.Validate();
                var view = new ShellProfileOverview { Profile = before, Mode = mode };
                Layout(view, 800, 420); var first = Pixels(Render(view, 800, 420));
                view.Profile = after; Layout(view, 800, 420); var second = Pixels(Render(view, 800, 420));
                Require(view.Profile == after && view.Mode == mode, "预览丢失输入方案或场景。");
                Require(DifferentPixels(first, second, 800 * 70 * 4) > 16, "设置改变后仅顶部文字变化，未形成可见绘图差异。");
                Require(!string.IsNullOrWhiteSpace(view.Summary) && !string.IsNullOrWhiteSpace(view.Explanation), "预览缺少状态摘要或场景说明。");
                Save(Render(view, 800, 420), "field-" + name + ".png");
            });
        }

        Difference("开始靠左与居中", baseline, baseline with { StartOnLeft = false });
        Difference("应用全靠左", baseline, baseline with { LeftAlignedApps = true });
        Difference("应用全靠左与集中布局", baseline with { LeftAlignedApps = true }, baseline with { StartOnLeft = false });
        Difference("图标大小", baseline, baseline with { IconSize = 32 });
        Difference("任务栏高度", baseline, baseline with { TaskbarHeight = 64 });
        Difference("按钮宽度", baseline, baseline with { TaskbarButtonWidth = 64 });
        Difference("小图标尺寸", baseline, baseline with { SmallIconSize = 28 }, ShellOverviewMode.SmallIcons);
        Difference("小按钮宽度", baseline, baseline with { SmallTaskbarButtonWidth = 60 }, ShellOverviewMode.SmallIcons);
        Difference("系统按钮位置", baseline, baseline with { OtherSystemButtonsOnLeft = false });
        Difference("开始菜单位置", baseline, baseline with { StartMenuOnLeft = false }, ShellOverviewMode.StartMenu);
        Difference("搜索面板位置", baseline, baseline with { SearchMenuOnLeft = true }, ShellOverviewMode.Search);
        Difference("Explorer现代工具栏", baseline, baseline with { ClassicRibbon = false });
        Difference("Explorer经典导航栏", baseline with { ClassicRibbon = false }, baseline with { ClassicRibbon = false, UseClassicNavigationBar = true });
        Difference("完整右键菜单", baseline, baseline with { ClassicContextMenu = false }, ShellOverviewMode.ContextMenu);
        Difference("Ctrl切换右键菜单", baseline, baseline with { ClassicMenuWithCtrl = false }, ShellOverviewMode.CtrlContextMenu);
        Difference("紧凑托盘", baseline, baseline with { CompactTray = true });
        Difference("透明任务栏", baseline, baseline with { TranslucentTaskbar = true });
        Difference("最大化时不透明", baseline with { TranslucentTaskbar = true }, baseline with { TranslucentTaskbar = true, FollowMaximizedTheme = true }, ShellOverviewMode.Maximized);
        Difference("暂停布局调整", baseline with { LeftAlignedApps = true }, baseline with { LeftAlignedApps = true, SkipTaskbarLayout = true });
        Difference("暂停布局同时暂停背景托盘", baseline with { CompactTray = true, TranslucentTaskbar = true, FollowMaximizedTheme = true }, baseline with { CompactTray = true, TranslucentTaskbar = true, FollowMaximizedTheme = true, SkipTaskbarLayout = true });
        Difference("暂停尺寸调整", baseline with { IconSize = 32, TaskbarHeight = 64, TaskbarButtonWidth = 60 }, baseline with { IconSize = 32, TaskbarHeight = 64, TaskbarButtonWidth = 60, SkipTaskbarSizing = true });
        Check("布局方案标识只改变名称而不伪造独立布局参数", () =>
        {
            var view = new ShellProfileOverview { Profile = baseline }; Layout(view, 800, 420); var first = Pixels(Render(view, 800, 420));
            view.Profile = baseline with { Appearance = "win11" }; Layout(view, 800, 420); var second = Pixels(Render(view, 800, 420));
            Require(DifferentPixels(first, second) > 16 && DifferentPixels(first, second, 800 * 70 * 4) == 0, "方案名标识改变了未修改的功能参数或没有可见标识。");
            Save(Render(view, 800, 420), "field-布局方案标识.png");
        });
        foreach (var skin in ShellSkins.All.Skip(1)) Difference("界面皮肤-" + skin.Id, baseline, baseline with { Skin = skin.Id });
        foreach (var mode in Enum.GetValues<ShellOverviewMode>())
            Check("完整场景与缩略图非空 " + mode, () =>
            {
                foreach (bool thumbnail in new[] { false, true })
                {
                    int width = thumbnail ? 260 : 800, height = thumbnail ? 146 : 420;
                    var view = new ShellProfileOverview { Profile = baseline, Mode = mode, Thumbnail = thumbnail };
                    Layout(view, width, height); var bitmap = Render(view, width, height);
                    AssertPicture(bitmap); Require(view.Profile == baseline && view.Thumbnail == thumbnail, "渲染改变了方案或缩略设置。");
                    Save(bitmap, "scene-" + mode + (thumbnail ? "-thumbnail" : "") + ".png");
                }
            });
        Check("未成立依赖不伪造效果", () =>
        {
            Same(baseline, baseline with { FollowMaximizedTheme = true }, ShellOverviewMode.Maximized, "未透明时跟随设置伪造不透明差异");
            Same(baseline, baseline with { SmallIconSize = 32, SmallTaskbarButtonWidth = 64 }, ShellOverviewMode.Desktop, "普通场景偷偷应用小图标参数");
            Same(baseline with { StartOnLeft = false }, baseline with { StartOnLeft = false, StartMenuOnLeft = false }, ShellOverviewMode.StartMenu, "开始居中时仍应用弹层靠左设置");
            Same(baseline with { StartMenuOnLeft = false }, baseline with { StartMenuOnLeft = false, SearchMenuOnLeft = true }, ShellOverviewMode.Search, "开始菜单未靠左时搜索选项伪造变化");
            Same(baseline with { ClassicContextMenu = false }, baseline with { ClassicContextMenu = false, ClassicMenuWithCtrl = false }, ShellOverviewMode.CtrlContextMenu, "未启用完整菜单时Ctrl设置伪造变化");
            Same(baseline with { LeftAlignedApps = true }, baseline with { LeftAlignedApps = true, StartMenuOnLeft = false }, ShellOverviewMode.StartMenu, "全靠左时开始弹层选项伪造变化");
        });
        foreach (var (name, before, after) in new[] {
            ("紧凑托盘", baseline with { SkipTaskbarLayout = true }, baseline with { SkipTaskbarLayout = true, CompactTray = true }),
            ("透明任务栏", baseline with { SkipTaskbarLayout = true }, baseline with { SkipTaskbarLayout = true, TranslucentTaskbar = true }),
            ("最大化不透明", baseline with { SkipTaskbarLayout = true, TranslucentTaskbar = true }, baseline with { SkipTaskbarLayout = true, TranslucentTaskbar = true, FollowMaximizedTheme = true }) })
            Check("布局未启用时保留但不模拟 " + name, () =>
            {
                foreach (var mode in new[] { ShellOverviewMode.Desktop, ShellOverviewMode.Maximized }) Same(before, after, mode, "布局未启用时仍模拟" + name);
                var view = new ShellProfileOverview { Profile = after };
                Require(view.Profile == after, "预览依赖过滤改写了保留参数。");
                Require(view.Explanation.Contains("布局", StringComparison.Ordinal) && view.Explanation.Contains("未启用", StringComparison.Ordinal)
                    && view.Explanation.Contains("透明", StringComparison.Ordinal) && view.Explanation.Contains("托盘", StringComparison.Ordinal), "依赖说明没有解释布局关闭时背景与托盘保留但不生效。");
            });

        var window = New("editor.json");
        Check("草稿编辑切页保存撤销均同步当前大图", () =>
        {
            window.SelectPage(4); AssertCurrent(window);
            window.ApplyPreset(1); window.ApplySkin(3); AssertCurrent(window);
            for (int page = 0; page < 6; page++) { window.SelectPage(page); AssertCurrent(window); }
            window.SelectPage(4); window.SaveDraft(); AssertCurrent(window);
            var saved = window.Draft; window.ApplyPreset(2); window.ApplySkin(5); AssertCurrent(window);
            Click(Find<Button>(window, "shell-undo")); Require(window.Draft == saved, "撤销没有恢复已保存方案。"); AssertCurrent(window);
            Require(ShellProfileFile.Load(Path.Combine(fixture, "editor.json")).Profile == saved, "保存方案未回读一致。");
        });
        Check("导入载入撤销载入后当前大图同步且不提前保存", () =>
        {
            var subject = New("import.json"); subject.SelectPage(4); subject.SaveToLibrary("中文方案 · 紧凑办公");
            var stored = subject.LibraryEntries.Single();
            var imported = new ShellProfile(StartOnLeft: false, IconSize: 28, ClassicRibbon: false, UseClassicNavigationBar: true, Skin: "cloud", CompactTray: true);
            string path = Path.Combine(fixture, "import-source.json"); ShellProfileFile.Save(path, imported, "missing");
            subject.ImportDraft(path); Require(subject.Draft == imported, "导入丢失参数。"); AssertCurrent(subject);
            Click(Find<Button>(subject, "library-load-" + stored.Id.ToString("N"))); Require(subject.Draft == stored.Profile, "卡片载入入口未载入所选完整方案。"); AssertCurrent(subject);
            subject.UndoLibraryLoad(); Require(subject.Draft == imported, "撤销载入丢失导入草稿。"); AssertCurrent(subject);
            Require(!File.Exists(Path.Combine(fixture, "import.json")), "导入/载入操作提前写入当前保存文件。");
        });
        Check("页面与场景选择联动到当前草稿且不调用系统宿主", () =>
        {
            var subject = New("scenarios.json"); subject.ApplySkin(2);
            foreach (var mode in Enum.GetValues<ShellOverviewMode>())
            {
                subject.SelectPage(0);
                var button = Find<Button>(subject, "shell-preview-scenarios");
                var item = button.ContextMenu!.Items.OfType<MenuItem>().Single(i => AutomationProperties.GetAutomationId(i) == "overview-mode-" + mode);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var view = Find<ShellProfileOverview>(subject, "shell-live-overview");
                Require(view.Mode == mode && view.Profile == subject.Draft, "任务栏场景选择未同步当前完整方案。");
                subject.SelectPage(4); var current = Find<ShellProfileOverview>(subject, "library-current-overview");
                Require(current.Mode == mode && current.Profile == subject.Draft, "方案大图未沿用选择场景。");
            }
            subject.SelectPage(2); Require(Find<ShellProfileOverview>(subject, "shell-live-overview").Mode == ShellOverviewMode.ContextMenu, "菜单页未默认右键场景。");
        });
        void EditControl(string name, int page, ShellOverviewMode expectedMode, Action<ShellSettingsWindow> edit, Func<ShellProfile, bool> changed)
        {
            Check("用户控件自动选择对应预览 " + name, () =>
            {
                var subject = New("automatic-" + Guid.NewGuid().ToString("N") + ".json"); subject.SelectPage(page);
                if (page == 0) Find<Expander>(subject, "shell-advanced").IsExpanded = true;
                subject.SetOverviewMode(expectedMode == ShellOverviewMode.CtrlContextMenu ? ShellOverviewMode.Search : ShellOverviewMode.CtrlContextMenu);
                Layout((FrameworkElement)subject.Content, 1080, 820); var before = subject.Draft;
                edit(subject); Layout((FrameworkElement)subject.Content, 1080, 820);
                var view = Find<ShellProfileOverview>(subject, "shell-live-overview");
                Require(subject.Draft != before && changed(subject.Draft), "直接控件事件未写入对应草稿参数。");
                Require(view.Mode == expectedMode && view.Profile == subject.Draft, "控件编辑后没有自动显示对应场景和完整草稿。");
                Require(subject.UpdateBlockReason?.Contains("草稿", StringComparison.Ordinal) == true, "直接编辑没有保持未保存草稿状态。");
                var modeButton = Find<Button>(subject, "shell-preview-scenarios");
                Require(modeButton.Content?.ToString() is { Length: > 0 } text && text != "预览状态", "场景按钮没有显示自动选择后的场景名称。");
                if (expectedMode == ShellOverviewMode.SmallIcons)
                {
                    string caption = Field<TextBlock>(subject, "previewCaption").Text;
                    Require(caption.Contains("小图标", StringComparison.Ordinal) && caption.Contains(subject.Draft.SmallIconSize + " px", StringComparison.Ordinal)
                        && caption.Contains(subject.Draft.SmallTaskbarButtonWidth + " px", StringComparison.Ordinal), "小图标场景的大标签仍显示普通图标或普通按钮参数。");
                }
                Save(Render((FrameworkElement)subject.Content, 1080, 820), "automatic-" + name + ".png");
                subject.SelectPage(4); var current = Find<ShellProfileOverview>(subject, "library-current-overview");
                Require(current.Mode == expectedMode && current.Profile == subject.Draft, "放大到方案页后丢失控件自动选中的场景。");
            });
        }
        ComboBox Choice(ShellSettingsWindow subject, string name) => Descendants(Field<StackPanel>(subject, "settings")).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == name);
        CheckBox Toggle(ShellSettingsWindow subject, string name) => Descendants(Field<StackPanel>(subject, "settings")).OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == name);
        EditControl("图标大小", 0, ShellOverviewMode.Desktop, w => Choice(w, "图标大小").SelectedItem = "32 px", p => p.IconSize == 32);
        EditControl("任务栏高度", 0, ShellOverviewMode.Desktop, w => Choice(w, "任务栏高度").SelectedItem = "64 px", p => p.TaskbarHeight == 64);
        EditControl("按钮宽度", 0, ShellOverviewMode.Desktop, w => Choice(w, "按钮宽度").SelectedItem = "64 px", p => p.TaskbarButtonWidth == 64);
        EditControl("小图标尺寸", 0, ShellOverviewMode.SmallIcons, w => Choice(w, "小图标尺寸").SelectedItem = "28 px", p => p.SmallIconSize == 28);
        EditControl("小按钮宽度", 0, ShellOverviewMode.SmallIcons, w => Choice(w, "小按钮宽度").SelectedItem = "60 px", p => p.SmallTaskbarButtonWidth == 60);
        EditControl("开始按钮位置", 0, ShellOverviewMode.Desktop, w => Choice(w, "开始按钮位置").SelectedIndex = 1, p => !p.StartOnLeft && !p.LeftAlignedApps);
        EditControl("启用布局调整", 0, ShellOverviewMode.Desktop, w => Toggle(w, "启用布局调整").IsChecked = false, p => p.SkipTaskbarLayout);
        EditControl("启用尺寸调整", 0, ShellOverviewMode.Desktop, w => Toggle(w, "启用尺寸调整").IsChecked = false, p => p.SkipTaskbarSizing);
        EditControl("透明任务栏", 0, ShellOverviewMode.Desktop, w => Toggle(w, "透明任务栏").IsChecked = true, p => p.TranslucentTaskbar);
        EditControl("最大化或全屏时不透明", 0, ShellOverviewMode.Maximized, w => Toggle(w, "最大化或全屏时不透明").IsChecked = true, p => p.FollowMaximizedTheme && p.TranslucentTaskbar);
        EditControl("紧凑系统托盘", 0, ShellOverviewMode.Desktop, w => Toggle(w, "紧凑系统托盘").IsChecked = true, p => p.CompactTray);
        EditControl("其他系统按钮靠左", 0, ShellOverviewMode.Desktop, w => Toggle(w, "其他系统按钮靠左").IsChecked = false, p => !p.OtherSystemButtonsOnLeft);
        EditControl("开始菜单靠左展开", 0, ShellOverviewMode.StartMenu, w => Toggle(w, "开始菜单靠左展开").IsChecked = false, p => !p.StartMenuOnLeft);
        EditControl("所有入口的搜索都靠左", 0, ShellOverviewMode.Search, w => Toggle(w, "所有入口的搜索都靠左").IsChecked = true, p => p.SearchMenuOnLeft);
        EditControl("Explorer现代命令栏", 1, ShellOverviewMode.Desktop, w => Choice(w, "工具区样式").SelectedIndex = 0, p => !p.ClassicRibbon && !p.UseClassicNavigationBar);
        EditControl("Explorer经典导航栏", 1, ShellOverviewMode.Desktop, w => Choice(w, "工具区样式").SelectedIndex = 2, p => !p.ClassicRibbon && p.UseClassicNavigationBar);
        EditControl("完整右键菜单", 2, ShellOverviewMode.ContextMenu, w => Toggle(w, "完整右键菜单").IsChecked = false, p => !p.ClassicContextMenu);
        EditControl("按Ctrl临时使用新版", 2, ShellOverviewMode.CtrlContextMenu, w => Toggle(w, "按 Ctrl 临时使用新版").IsChecked = false, p => !p.ClassicMenuWithCtrl);
        EditControl("集中布局卡片", 0, ShellOverviewMode.Desktop, w => Click(Find<Button>(w, "shell-layout-1")), p => !p.StartOnLeft && !p.LeftAlignedApps);
        EditControl("全靠左布局卡片", 0, ShellOverviewMode.Desktop, w => Click(Find<Button>(w, "shell-layout-2")), p => p.StartOnLeft && p.LeftAlignedApps);
        Check("手动选择小图标场景同步标签切回普通与原生参考读数", () =>
        {
            var subject = New("manual-small-caption.json"); subject.SelectPage(0);
            void SelectMode(ShellOverviewMode mode)
            {
                var item = Find<Button>(subject, "shell-preview-scenarios").ContextMenu!.Items.OfType<MenuItem>().Single(i => AutomationProperties.GetAutomationId(i) == "overview-mode-" + mode);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            SelectMode(ShellOverviewMode.SmallIcons);
            string caption = Field<TextBlock>(subject, "previewCaption").Text;
            Require(caption.Contains("小图标", StringComparison.Ordinal) && caption.Contains(subject.Draft.SmallIconSize + " px", StringComparison.Ordinal)
                && caption.Contains(subject.Draft.SmallTaskbarButtonWidth + " px", StringComparison.Ordinal), "手动切到小图标场景没有刷新参数标签。");
            Layout((FrameworkElement)subject.Content, 1080, 820); Save(Render((FrameworkElement)subject.Content, 1080, 820), "manual-small-caption.png");
            SelectMode(ShellOverviewMode.Desktop); caption = Field<TextBlock>(subject, "previewCaption").Text;
            Require(caption.Contains(subject.Draft.IconSize + " px", StringComparison.Ordinal) && !caption.Contains("小图标", StringComparison.Ordinal), "切回普通场景后仍显示小图标读数。");
            Click(Find<Button>(subject, "shell-reference")); SelectMode(ShellOverviewMode.SmallIcons);
            Require(Field<TextBlock>(subject, "previewCaption").Text.StartsWith("Windows 11 参考", StringComparison.Ordinal), "原生参考误显示草稿尺寸。");
        });
        Check("非控件草稿操作保留手动选择场景", () =>
        {
            var subject = New("manual-mode-retained.json"); subject.SelectPage(4); subject.SetOverviewMode(ShellOverviewMode.Search);
            void Preserved() { Require(Find<ShellProfileOverview>(subject, "library-current-overview").Mode == ShellOverviewMode.Search, "非控件草稿操作覆盖了手动场景。"); AssertCurrent(subject); }
            subject.ApplyPreset(2); Preserved(); subject.ApplySkin(3); Preserved(); subject.SaveDraft(); Preserved();
            subject.SaveToLibrary("手动场景方案"); var entry = subject.LibraryEntries.Single(); Preserved();
            string source = Path.Combine(fixture, "manual-mode-import.json"); ShellProfileFile.Save(source, new ShellProfile(Skin: "moon"), "missing");
            subject.ImportDraft(source); Preserved(); subject.LoadFromLibrary(entry.Id); Preserved(); subject.UndoLibraryLoad(); Preserved();
            Click(Find<Button>(subject, "shell-undo")); Preserved();
        });
        Check("重复点击同一方案仍能撤销回到载入前导入草稿", () =>
        {
            var subject = New("repeat-load.json"); subject.SelectPage(4); subject.SaveToLibrary("中文命名 · 自定义办公方案");
            var stored = subject.LibraryEntries.Single();
            var imported = new ShellProfile(IconSize: 32, TaskbarHeight: 64, ClassicRibbon: false, UseClassicNavigationBar: true, Skin: "moon", TranslucentTaskbar: true);
            string source = Path.Combine(fixture, "repeat-source.json"); ShellProfileFile.Save(source, imported, "missing"); subject.ImportDraft(source);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Click(Find<Button>(subject, "library-load-" + stored.Id.ToString("N"))); Require(subject.Draft == stored.Profile, "重复载入改变了保存方案参数。"); AssertCurrent(subject);
                Require(Descendants(subject).OfType<TextBlock>().Any(t => t.Text.Contains(stored.Name, StringComparison.Ordinal)), "当前自定义方案名称未显示。");
            }
            Click(Find<Button>(subject, "library-undo-load")); Require(subject.Draft == imported, "重复点击覆盖了载入前草稿，导致撤销丢失导入参数。"); AssertCurrent(subject);
            Require(!File.Exists(Path.Combine(fixture, "repeat-load.json")), "重复载入/撤销提前保存了当前方案文件。");
        });
        Check("外部重命名后刷新同时更新当前标题与保存卡片", () =>
        {
            var subject = New("refresh-title.json"); subject.SelectPage(4); subject.SaveToLibrary("刷新前 · 自定义中文方案");
            var entry = subject.LibraryEntries.Single(); subject.LoadFromLibrary(entry.Id); var draft = subject.Draft;
            var external = new ShellProfileLibrary(Path.Combine(fixture, "refresh-title.json.library.json"));
            const string newName = "刷新后 · 新名称办公方案"; external.Rename(external.Read(), entry.Id, newName);
            Click(Find<Button>(subject, "library-refresh")); AssertCurrent(subject);
            Require(Descendants(subject).OfType<TextBlock>().Any(t => t.Text == "正在编辑 · " + newName), "刷新后当前图标题仍是旧方案名。");
            var card = Find<FrameworkElement>(subject, "library-entry-" + entry.Id.ToString("N"));
            Require(Descendants(card).OfType<TextBlock>().Any(t => t.Text == newName) && subject.LibraryEntries.Single().Name == newName, "刷新后卡片或快照仍保留旧名称。");
            Require(subject.Draft == draft && subject.LibraryEntries.Single().Profile == draft, "只刷新名称却改变了方案参数。");
        });
        Check("空方案库无空选择框或禁用动作墙", () =>
        {
            var empty = New("empty.json"); empty.SelectPage(4); Layout((FrameworkElement)empty.Content, 1080, 820);
            var settings = Field<StackPanel>(empty, "settings");
            Require(!Descendants(settings).OfType<ComboBox>().Any(c => AutomationProperties.GetAutomationId(c) == "library-picker" || c.Items.Count == 0), "空库仍显示空下拉。");
            Require(!Descendants(settings).OfType<Button>().Any(b => Rendered(b) && !b.IsEnabled), "空库仍显示禁用库动作。");
            Require(Find<Button>(empty, "library-add").IsEnabled, "空库不能新增当前草稿。");
            AssertCurrent(empty); Require(!File.Exists(Path.Combine(fixture, "empty.json.library.json")), "空库查看写入文件。");
            Save(Render((FrameworkElement)empty.Content, 1080, 820), "library-empty-1080x820.png");
        });
        var cards = New("cards.json"); cards.SelectPage(4);
        string longName = "这是一个包含较长中文名称的桌面方案用于核对缩略图与完整名称提示";
        cards.ApplyPreset(2); cards.ApplySkin(4); cards.SaveToLibrary(longName);
        cards.ApplyPreset(1); cards.ApplySkin(5); cards.SaveToLibrary("月夜 · 日常使用");
        cards.ApplyPreset(3); cards.ApplySkin(3); cards.SaveToLibrary("宽松布局 · 樱月");
        Check("已有方案卡片名称截断带完整提示缩略图保留保存参数", () =>
        {
            Layout((FrameworkElement)cards.Content, 1080, 820);
            foreach (var entry in cards.LibraryEntries)
            {
                var card = Find<FrameworkElement>(cards, "library-entry-" + entry.Id.ToString("N"));
                var name = Descendants(card).OfType<TextBlock>().Single(t => t.Text == entry.Name);
                Require(name.TextTrimming == TextTrimming.CharacterEllipsis && name.ToolTip?.ToString() == entry.Name, "卡片名称没有单行省略或完整名称提示。");
                var preview = Find<ShellProfileOverview>(card, "library-thumbnail-" + entry.Id.ToString("N"));
                Require(preview.Profile == entry.Profile && preview.Thumbnail && preview.ActualWidth > 0 && preview.ActualHeight > 0, "卡片缩略图丢失保存参数或为空。");
                var load = Find<Button>(card, "library-load-" + entry.Id.ToString("N")); Require(load.IsEnabled && load.Visibility == Visibility.Visible, "卡片载入入口不可用。");
                AssertPicture(Render(preview, (int)Math.Ceiling(preview.ActualWidth), (int)Math.Ceiling(preview.ActualHeight)));
            }
        });
        foreach (var (width, height) in new[] { (740, 550), (1080, 820) })
            Check($"每个保存方案载入按钮通过滚动可达 {width}x{height}", () =>
            {
                cards.SelectPage(4); Layout((FrameworkElement)cards.Content, width, height);
                var scroll = Field<ScrollViewer>(cards, "scroll");
                foreach (var entry in cards.LibraryEntries)
                {
                    var load = Find<Button>(cards, "library-load-" + entry.Id.ToString("N"));
                    load.BringIntoView(); Layout((FrameworkElement)cards.Content, width, height);
                    var at = load.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, load.ActualWidth, load.ActualHeight));
                    Require(Rendered(load) && load.IsEnabled && at.Top >= -1 && at.Bottom <= scroll.ActualHeight + 1, "滚动后方案载入按钮仍无法访问。");
                }
                Save(Render((FrameworkElement)cards.Content, width, height), $"library-accessible-{width}x{height}.png"); scroll.ScrollToTop();
            });
        foreach (var (width, height) in new[] { (740, 550), (1080, 820) })
            foreach (double factor in new[] { 1d, 1.25, 1.5, 2d })
                foreach (int page in new[] { 0, 1, 2, 4 })
                    Check($"离屏尺寸DPI {width}x{height} {factor * 100}% page{page}", () =>
                    {
                        cards.SelectPage(page); Layout((FrameworkElement)cards.Content, width, height); AssertCurrent(cards);
                        Bounds(cards, width, height);
                        Save(Render((FrameworkElement)cards.Content, width, height, factor), $"window-{width}x{height}-{factor * 100}-page{page}.png");
                        if (page == 4)
                        {
                            var scroll = Field<ScrollViewer>(cards, "scroll"); scroll.ScrollToEnd(); Layout((FrameworkElement)cards.Content, width, height);
                            Require(scroll.ScrollableHeight == 0 || scroll.VerticalOffset > 0, "方案卡片超出内容区但滚动不可用。");
                            Save(Render((FrameworkElement)cards.Content, width, height, factor), $"window-{width}x{height}-{factor * 100}-page4-bottom.png"); scroll.ScrollToTop();
                        }
                    });
        Check("整个测试未显示窗口或调用系统宿主", () =>
        {
            Require(externalCalls == 0 && Windows.All(w => !w.IsVisible), "离屏预览测试触发外部宿主或显示窗口。");
        });
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new {
            Passed = Results.Count(r => r.Passed), Failed = Results.Count(r => !r.Passed), Tests = Results, Images = Pictures,
            WindowsShown = Windows.Any(w => w.IsVisible), ExternalHostCalls = externalCalls,
            RenderingScope = "离屏 Measure/Arrange/RenderTargetBitmap；DPI仅为输出栅格密度，不等同实际显示器DPI切换"
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in Results.Where(r => !r.Passed)) Console.WriteLine(result.Name + ": " + result.Detail);
        Console.WriteLine($"VisualPreview: {Results.Count(r => r.Passed)} passed, {Results.Count(r => !r.Passed)} failed, {Pictures.Count} images");
        foreach (var item in Windows) item.Close(); app.Shutdown(); return Results.Any(r => !r.Passed) ? 1 : 0;
    }

    static ShellSettingsWindow New(string name)
    {
        void Forbidden() { externalCalls++; throw new InvalidOperationException("离屏测试禁止外部宿主调用。"); }
        var result = new ShellSettingsWindow(path: Path.Combine(fixture, name), inspectPlan: _ => { Forbidden(); return Task.FromResult(""); },
            manageNative: (_, _) => Forbidden(), manageAutoHide: _ => Forbidden(), openTaskbarSettings: Forbidden,
            manageUpdates: _ => Forbidden(), manageLayouts: _ => Forbidden());
        Windows.Add(result); return result;
    }
    static void AssertCurrent(ShellSettingsWindow window)
    {
        var current = window.ActivePage == 4 ? Find<ShellProfileOverview>(window, "library-current-overview")
            : Descendants(window).OfType<ShellProfileOverview>().SingleOrDefault(v => AutomationProperties.GetAutomationId(v) == "shell-live-overview");
        if (window.ActivePage < 3 || window.ActivePage == 4) Require(current is not null, "当前页面缺少完整方案实时预览。");
        if (current is not null) Require(current.Profile == window.Draft, "当前图未同步完整草稿方案。");
    }
    static void Same(ShellProfile before, ShellProfile after, ShellOverviewMode mode, string message)
    {
        var view = new ShellProfileOverview { Profile = before, Mode = mode }; Layout(view, 800, 420);
        var first = Pixels(Render(view, 800, 420)); view.Profile = after; Layout(view, 800, 420);
        Require(first.SequenceEqual(Pixels(Render(view, 800, 420))), message);
    }
    static void Bounds(Window window, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        foreach (var button in Descendants(root).OfType<Button>().Where(Rendered))
        {
            var at = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Require(at.Left >= -1 && at.Right <= width + 1, $"按钮横向裁切: {AutomationProperties.GetAutomationId(button)} {at}");
            if (AutomationProperties.GetAutomationId(button) is "shell-save" or "shell-inspect") Require(at.Top >= 0 && at.Bottom <= height + 1, "底部保存/检查动作裁切。");
        }
        foreach (var card in Descendants(root).OfType<FrameworkElement>().Where(e => AutomationProperties.GetAutomationId(e).StartsWith("library-entry-")))
        { var at = card.TransformToAncestor(root).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight)); Require(at.Left >= -1 && at.Right <= width + 1, "方案卡片横向裁切。"); }
    }
    static void Check(string name, Action action) { try { action(); Results.Add(new(name, true, "")); } catch (Exception e) { Results.Add(new(name, false, e.ToString())); } }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static T Field<T>(object value, string name) => (T)(value.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(value)!);
    static T Find<T>(DependencyObject parent, string id) where T : DependencyObject => Descendants(parent).OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Descendants(child)) yield return item; }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static bool Rendered(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? item = element; item is not null; item = VisualTreeHelper.GetParent(item))
            if (item is UIElement visual && visual.Visibility != Visibility.Visible) return false;
        return true;
    }
    static void Layout(FrameworkElement root, int width, int height)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        }
    }
    static RenderTargetBitmap Render(FrameworkElement element, int width, int height, double factor = 1)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * factor), (int)Math.Ceiling(height * factor), 96 * factor, 96 * factor, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        bitmap.Render(drawing); return bitmap;
    }
    static byte[] Pixels(BitmapSource bitmap) { var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes; }
    static int DifferentPixels(byte[] first, byte[] second, int offset = 0)
    { int count = 0; for (int i = offset; i < first.Length; i += 4) if (!first.AsSpan(i, 4).SequenceEqual(second.AsSpan(i, 4))) count++; return count; }
    static (int Colors, int Opaque) Stats(BitmapSource bitmap)
    { var bytes = Pixels(bitmap); var colors = new HashSet<int>(); int opaque = 0; for (int i = 0; i < bytes.Length; i += 4) { colors.Add(BitConverter.ToInt32(bytes, i)); if (bytes[i + 3] > 0) opaque++; } return (colors.Count, opaque); }
    static void AssertPicture(BitmapSource bitmap)
    { var stats = Stats(bitmap); Require(stats.Opaque > bitmap.PixelWidth * bitmap.PixelHeight / 2 && stats.Colors > 50, "图片为空白、透明或没有足够可见内容。"); }
    static void Save(BitmapSource bitmap, string name)
    {
        AssertPicture(bitmap); var stats = Stats(bitmap); string path = Path.Combine(output, name);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream);
        var decoded = new BitmapImage(); decoded.BeginInit(); decoded.CacheOption = BitmapCacheOption.OnLoad; decoded.UriSource = new Uri(path); decoded.EndInit();
        Require(decoded.PixelWidth == bitmap.PixelWidth && decoded.PixelHeight == bitmap.PixelHeight, "写入PNG尺寸不一致。");
        Pictures.Add(new(name, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, Convert.ToHexString(SHA256.HashData(Pixels(bitmap))), stats.Colors, stats.Opaque));
    }
}
