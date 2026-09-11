using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClassicDesk;

internal static class FrontendChecks
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    record Result(string Name, bool Passed, string Detail);
    static readonly List<Result> Results = new();
    static readonly List<Window> Windows = new();
    static readonly List<string> Screenshots = new();
    static string Output = "", Workspace = "";
    static int InspectCalls;

    [STAThread]
    public static int Main(string[] args)
    {
        Workspace = Path.GetFullPath(args[0]); Output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Workspace); Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        string run = Path.Combine(Workspace, "isolated-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        string path = Path.Combine(run, "profile.json");
        ShellSettingsWindow New(string file)
        {
            var result = new ShellSettingsWindow(path: file, inspectPlan: draft => { InspectCalls++; return Task.FromException<string>(new IOException("隔离测试：只检查错误反馈，不打开检查对话框。")); }); Windows.Add(result); return result;
        }
        var desk = New(path);
        Check("缺失方案只读加载默认值，不创建文件", () =>
        {
            var loaded = ShellProfileFile.Load(path); Require(loaded.Revision == "missing" && loaded.Profile == new ShellProfile(), "缺失文件默认值或修订错误。");
            Require(!File.Exists(path), "只读加载创建了方案文件。"); Require(!Field<Button>(desk, "save").IsEnabled, "未修改时保存仍可用。");
            Require(Field<TextBlock>(desk, "draftBadge").Text == "默认方案" && Field<Button>(desk, "undo").Visibility == Visibility.Collapsed, "默认方案误称已保存，或未修改仍出现撤销占位。");
        });
        Check("主导航仅三项且使用真实矢量资源", () =>
        {
            Layout(desk, 820, 610); var routes = Field<List<Button>>(desk, "routes"); var keys = new[] { "taskbar", "folder", "context-menu" };
            Require(routes.Count == 3, "主导航不是三项。"); Require(ReferenceEquals(desk.Icon, AppIcons.Get("brand")), "窗口品牌资源错误。");
            for (int i = 0; i < 3; i++) Require(ReferenceEquals(Logical<Image>(routes[i]).Single().Source, AppIcons.Get(keys[i])), "导航图标与路由不对应。");
            Require(!Logical<TextBlock>(desk).Any(t => t.Text.Contains("显示隐藏文件", StringComparison.Ordinal) || t.Text.Contains("显示文件扩展名", StringComparison.Ordinal)), "前端混入普通系统开关。");
        });
        foreach (var size in new[] { (Width: 820, Height: 610), (Width: 740, Height: 550) })
            for (int page = 0; page < 3; page++)
            {
                int route = page;
                Check($"三页离屏布局 {route} · {size.Width}×{size.Height}", () =>
                {
                    Click(Field<List<Button>>(desk, "routes")[route]); Layout(desk, size.Width, size.Height);
                    Require(desk.ActivePage == route && Field<ShellPreview>(desk, "preview").Kind == (ShellPreviewKind)route, "路由与演示类型未联动。");
                    Bounds(desk, size.Width, size.Height);
                    Capture(desk, size.Width, size.Height, new[] { "任务栏", "资源管理器", "右键菜单" }[route] + $"-{size.Width}x{size.Height}.png");
                });
            }
        Check("默认客户区任务栏四项与说明无需滚动", () =>
        {
            desk.SelectPage(0); Layout(desk, 820, 610);
            var scroll = Visuals<ScrollViewer>((FrameworkElement)desk.Content).Single(s => s.Content is StackPanel panel && Logical<ShellPreview>(panel).Any());
            Require(scroll.ScrollableHeight <= 1, $"默认任务栏页仍需滚动 {scroll.ScrollableHeight:0.#} DIP。");
        });
        Check("新 ComboBox 模板实际载入，选择框有圆角且标签可见", () =>
        {
            desk.SelectPage(0); Layout(desk, 820, 610);
            foreach (var combo in Logical<ComboBox>(Field<StackPanel>(desk, "settings")).Where(c => c.ActualWidth > 0))
            {
                Require(combo.Template is not null && combo.ActualHeight >= 29, "新选择框模板未载入。");
                Require(Visuals<Border>(combo).Any(b => b.CornerRadius.TopLeft >= 6), "选择框没有现代圆角表面。");
                Require(combo.SelectedItem?.ToString() is { Length: > 0 }, "选择框没有选中标签。");
            }
        });
        Check("窄屏任务栏滚动后第四项与说明完整可访问", () =>
        {
            desk.SelectPage(0); Layout(desk, 740, 550);
            var scroll = Visuals<ScrollViewer>((FrameworkElement)desk.Content).Single(s => s.Content is StackPanel panel && Logical<ShellPreview>(panel).Any());
            scroll.ScrollToEnd(); Layout(desk, 740, 550);
            var control = Choice(desk, "任务栏高度"); var at = control.TransformToAncestor(scroll).Transform(new Point());
            Require(at.Y >= 0 && at.Y + control.ActualHeight <= scroll.ActualHeight + 1, "滚动后任务栏高度选项仍不可访问。");
            Require(scroll.ScrollableHeight == 0 || scroll.VerticalOffset > 0, "可滚动内容没有移动。");
            Capture(desk, 740, 550, "任务栏-740x550-下方设置.png"); scroll.ScrollToTop();
        });
        Check("任务栏四个 ComboBox 事件写入草稿并重绘对应参数", () =>
        {
            desk.SelectPage(0); Layout(desk, 820, 610); var preview = Field<ShellPreview>(desk, "preview"); var old = ImageHash(preview);
            Choice(desk, "开始按钮位置").SelectedIndex = 1; Choice(desk, "图标大小").SelectedItem = "32 px"; Choice(desk, "任务栏高度").SelectedItem = "64 px"; Choice(desk, "按钮宽度").SelectedItem = "60 px"; Layout(desk, 820, 610);
            Require(!desk.Draft.StartOnLeft && desk.Draft.IconSize == 32 && desk.Draft.TaskbarHeight == 64 && desk.Draft.TaskbarButtonWidth == 60, "任务栏事件未改变草稿。");
            Require(preview.Options == desk.Draft.PreviewOptions && ImageHash(preview) != old, "任务栏草稿没有改变预览。"); Require(!File.Exists(path), "编辑控件提前写入方案。");
        });
        Check("Explorer ComboBox 事件与右键菜单 Switch 事件分别联动草稿", () =>
        {
            desk.SelectPage(1); Choice(desk, "工具区样式").SelectedIndex = 0; Require(!desk.Draft.ClassicRibbon && !Field<ShellPreview>(desk, "preview").Options.ClassicRibbon, "Ribbon 选择未联动。");
            desk.SelectPage(2); var toggle = Toggle(desk, "完整右键菜单"); toggle.IsChecked = false;
            Require(!desk.Draft.ClassicContextMenu && !Field<ShellPreview>(desk, "preview").Options.ClassicContextMenu, "完整菜单开关未联动。");
            Require(Field<Button>(desk, "save").IsEnabled && !File.Exists(path), "草稿事件写入了磁盘或未启用保存。");
        });
        Check("跨页返回保留三个页面的草稿与控件值", () =>
        {
            var prior = desk.Draft;
            desk.SelectPage(0); Require(Choice(desk, "图标大小").SelectedItem?.ToString() == "32 px" && Choice(desk, "开始按钮位置").SelectedIndex == 1, "任务栏草稿跨页丢失。");
            desk.SelectPage(1); Require(Choice(desk, "工具区样式").SelectedIndex == 0, "Ribbon 草稿跨页丢失。");
            desk.SelectPage(2); Require(Toggle(desk, "完整右键菜单").IsChecked == false && desk.Draft == prior, "菜单草稿跨页丢失。");
        });
        Check("前后切换只改变演示；修改控件自动回到我的方案", () =>
        {
            desk.SelectPage(0); var preview = Field<ShellPreview>(desk, "preview"); var prior = desk.Draft;
            Click(Field<List<Button>>(desk, "comparisons")[0]); Layout(desk, 820, 610); var before = ImageHash(preview);
            Require(preview.Before && desk.Draft == prior, "前态演示改变了草稿。"); Click(Field<List<Button>>(desk, "comparisons")[1]); Layout(desk, 820, 610);
            Require(!preview.Before && ImageHash(preview) != before && desk.Draft == prior, "前后演示状态或内容不正确。");
            desk.SetComparison(true); Choice(desk, "图标大小").SelectedItem = "20 px";
            Require(!preview.Before && desk.Draft.IconSize == 20, "编辑后没有回到我的方案。");
            Capture(desk, 820, 610, "任务栏-已编辑方案.png");
        });
        Check("高级布局五项编辑保留；开始/搜索依赖按底层语义生效", () =>
        {
            var window = New(Path.Combine(run, "advanced.json")); Layout(window, 820, 610);
            var advanced = Logical<Expander>(window).Single(); Require(!advanced.IsExpanded, "高级选项默认未折叠。"); advanced.IsExpanded = true;
            Choice(window, "小图标尺寸").SelectedItem = "20 px"; Choice(window, "小按钮宽度").SelectedItem = "40 px";
            Toggle(window, "其他系统按钮靠左").IsChecked = false;
            Require(Toggle(window, "所有入口的搜索都靠左").IsEnabled, "搜索错误依赖其他系统按钮。");
            Toggle(window, "所有入口的搜索都靠左").IsChecked = true; Toggle(window, "开始菜单靠左展开").IsChecked = false;
            Require(!Toggle(window, "所有入口的搜索都靠左").IsEnabled, "搜索在开始菜单居中时仍可设置。");
            Choice(window, "开始按钮位置").SelectedIndex = 1;
            Require(!Toggle(window, "其他系统按钮靠左").IsEnabled && !Toggle(window, "开始菜单靠左展开").IsEnabled, "开始居中未禁用左侧高级项。");
            Require(window.Draft.SmallIconSize == 20 && window.Draft.SmallTaskbarButtonWidth == 40 && !window.Draft.OtherSystemButtonsOnLeft && !window.Draft.StartMenuOnLeft && window.Draft.SearchMenuOnLeft, "高级事件未完整保存草稿。");
            Require(Field<ShellPreview>(window, "preview").Options == window.Draft.PreviewOptions, "高级选项未传到预览。");
            window.SelectPage(1); window.SelectPage(0); Require(Logical<Expander>(window).Single().IsExpanded && Choice(window, "小图标尺寸").SelectedItem?.ToString() == "20 px", "高级展开/值跨页丢失。");
            Choice(window, "开始按钮位置").SelectedIndex = 0; Toggle(window, "开始菜单靠左展开").IsChecked = true;
            Require(Toggle(window, "所有入口的搜索都靠左").IsEnabled, "恢复前置条件没有重新启用搜索设置。");
            Capture(window, 820, 610, "任务栏-高级布局-820x610.png");
        });
        Check("最小窗口展开高级后所有选项可滚动到达，底部动作始终可用", () =>
        {
            var window = New(Path.Combine(run, "advanced-scroll.json")); Logical<Expander>(window).Single().IsExpanded = true;
            Layout(window, 740, 550); var scroll = Field<ScrollViewer>(window, "scroll"); scroll.ScrollToEnd(); Layout(window, 740, 550);
            var last = Toggle(window, "所有入口的搜索都靠左"); var at = last.TransformToAncestor(scroll).Transform(new Point());
            Require(scroll.VerticalOffset > 0 && at.Y >= 0 && at.Y + last.ActualHeight <= scroll.ActualHeight + 1, "最后高级项无法滚动到可见范围。");
            Bounds(window, 740, 550); Capture(window, 740, 550, "任务栏-高级布局-740x550-底部.png");
        });
        Check("Explorer 三种样式均有独立真实映射、说明与不同预览", () =>
        {
            var window = New(Path.Combine(run, "explorer-modes.json")); window.SelectPage(1); var hashes = new HashSet<string>();
            for (int mode = 0; mode < 3; mode++)
            {
                Choice(window, "工具区样式").SelectedIndex = mode; Layout(window, 820, 610);
                Require(window.Draft.ClassicRibbon == (mode == 1) && window.Draft.UseClassicNavigationBar == (mode == 2), "Explorer 三态映射不正确。");
                var text = Logical<TextBlock>(window).Single(t => AutomationProperties.GetAutomationId(t) == "explorer-mode-note").Text;
                Require(mode == 1 ? text.Contains("不保留") : mode == 2 ? text.Contains("保留标签页") : text.Contains("当前 Windows 11"), "Explorer 说明未随模式更新。");
                hashes.Add(ImageHash(Field<ShellPreview>(window, "preview"))); Capture(window, 820, 610, $"资源管理器-样式{mode}-820x610.png");
            }
            Require(hashes.Count == 3 && !File.Exists(Path.Combine(run, "explorer-modes.json")), "三态预览相同或编辑提前写文件。");
        });
        Check("完整菜单及 Ctrl 行事件与禁用依赖保留草稿", () =>
        {
            var window = New(Path.Combine(run, "menu-options.json")); window.SelectPage(2);
            Toggle(window, "按 Ctrl 临时使用新版").IsChecked = false; Require(!window.Draft.ClassicMenuWithCtrl, "Ctrl 开关未写草稿。");
            Toggle(window, "完整右键菜单").IsChecked = false; Require(!Toggle(window, "按 Ctrl 临时使用新版").IsEnabled, "完整菜单关闭后 Ctrl 行未禁用。");
            Toggle(window, "完整右键菜单").IsChecked = true; Require(Toggle(window, "按 Ctrl 临时使用新版").IsEnabled && Toggle(window, "按 Ctrl 临时使用新版").IsChecked == false, "依赖恢复丢失原 Ctrl 选择。");
            Require(Field<ShellPreview>(window, "preview").Options == window.Draft.PreviewOptions, "两个菜单参数未传到预览。");
        });
        Check("重置本页不丢其他页修改；撤销回到最近保存的完整方案", () =>
        {
            var window = New(Path.Combine(run, "reset-undo.json")); Choice(window, "按钮宽度").SelectedItem = "56 px";
            window.SelectPage(1); Choice(window, "工具区样式").SelectedIndex = 2; window.SelectPage(2); Toggle(window, "完整右键菜单").IsChecked = false;
            window.SelectPage(0); Click(Logical<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "shell-reset-page"));
            Require(window.Draft.TaskbarButtonWidth == 44 && window.Draft.UseClassicNavigationBar && !window.Draft.ClassicContextMenu, "本页重置改动了其他页。");
            window.SaveDraft(); var saved = window.Draft; Choice(window, "图标大小").SelectedItem = "32 px";
            Click(Field<Button>(window, "undo")); Require(window.Draft == saved && !Field<Button>(window, "save").IsEnabled && !Field<Button>(window, "undo").IsEnabled, "撤销未恢复保存基线。");
        });
        Check("原生管理回调只有检查点击才调用并收到当前冻结方案", () =>
        {
            int calls = 0; ShellProfile? received = null; Window? owner = null;
            var window = new ShellSettingsWindow(path: Path.Combine(run, "native-callback.json"), manageNative: (w, p) => { calls++; owner = w; received = p; }); Windows.Add(window);
            window.SelectPage(1); Choice(window, "工具区样式").SelectedIndex = 2; window.SaveDraft(); Require(calls == 0, "构造、切页或保存隐式发起了原生操作。");
            Click(Logical<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "shell-inspect"));
            Require(calls == 1 && owner == window && received == window.Draft && !window.IsVisible, "检查委托未准确接收当前方案和 owner。");
        });
        Check("窗口控制为矢量按钮且保留 Windows 调整边框；未创建 HWND", () =>
        {
            var window = New(Path.Combine(run, "chrome.json")); Layout(window, 820, 610);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window); Require(chrome is not null && chrome.CaptionHeight == 42 && chrome.ResizeBorderThickness.Left >= 6, "窗口拖动/调整边框契约丢失。");
            var buttons = Logical<Button>(window).Where(b => AutomationProperties.GetAutomationId(b).StartsWith("window-", StringComparison.Ordinal)).ToArray();
            Require(buttons.Length == 3 && buttons.All(b => b.Content is System.Windows.Shapes.Path && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(b)), "系统控制按钮不是三枚可命中矢量按钮。");
            Require(PresentationSource.FromVisual(window) is null && !window.IsVisible, "离屏创建了真实窗口源。");
        });
        Check("独立主题覆盖交互控件并允许原生面板设置按钮配色", () =>
        {
            var window = New(Path.Combine(run, "theme.json"));
            foreach (string key in new[] { "ShellButton", "ShellPrimaryButton", "ShellQuietButton", "ShellChoice", "ShellSwitch", "ShellSegment", "ShellPreviewSegment", "ShellExpander" }) Require(window.FindResource(key) is Style, "主题资源缺失：" + key);
            var button = new Button { Content = "测试", Background = Brushes.DarkRed, Foreground = Brushes.White, Style = (Style)window.FindResource(typeof(Button)) }; button.Measure(new Size(120, 40)); button.Arrange(new Rect(0, 0, 120, 40)); button.UpdateLayout();
            Require(Visuals<Border>(button).Any(b => b.Background == Brushes.DarkRed) && button.Foreground == Brushes.White, "共享 Button 模板忽略调用方配色。");
            window.SelectPage(2); Layout(window, 820, 610); Require(Visuals<Border>(Toggle(window, "完整右键菜单")).Any(b => b.CornerRadius.TopLeft >= 10), "开关没有精细圆角轨道。");
        });
        Check("保存按钮只写指定隔离文件并记录返回修订", () =>
        {
            Click(Field<Button>(desk, "save")); var snapshot = ShellProfileFile.Load(path);
            Require(snapshot.Profile == desk.Draft && snapshot.Revision == Hash(path), "保存内容或修订错误。");
            Require(Field<string?>(desk, "sourceRevision") == snapshot.Revision && !Field<Button>(desk, "save").IsEnabled, "保存后基线未更新。");
            Require(Field<TextBlock>(desk, "feedback").Text.Contains("Windows 尚未修改", StringComparison.Ordinal), "保存反馈伪装为系统应用。");
        });
        Check("再次保存保留上一份方案的精确字节备份", () =>
        {
            var previous = File.ReadAllBytes(path); desk.SelectPage(0); Choice(desk, "图标大小").SelectedItem = "24 px"; desk.SaveDraft();
            Require(File.ReadAllBytes(path + ".previous").AsSpan().SequenceEqual(previous), ".previous 不等于上一次原始字节。"); Require(ShellProfileFile.Read(path) == desk.Draft, "保存后草稿与文件不一致。");
        });
        Check("外部修订冲突拒绝覆盖并保留当前编辑", () =>
        {
            var concurrent = New(path); concurrent.SelectPage(0); Choice(concurrent, "图标大小").SelectedItem = "28 px"; var pending = concurrent.Draft;
            var loaded = ShellProfileFile.Load(path); ShellProfileFile.Save(path, loaded.Profile with { TaskbarHeight = 40 }, loaded.Revision); var external = Hash(path);
            concurrent.SaveDraft(); Require(Hash(path) == external && concurrent.Draft == pending, "冲突覆盖了外部文件或清除了草稿。");
            Require(Field<TextBlock>(concurrent, "feedback").Text.Contains("保存失败", StringComparison.Ordinal), "冲突没有显示失败。");
        });
        Check("预览后新建同名文件与删除源文件均拒绝旧修订覆盖", () =>
        {
            string created = Path.Combine(run, "created.json"); var missing = ShellProfileFile.Load(created); File.WriteAllText(created, "{}"); var existing = Hash(created);
            Throws<IOException>(() => ShellProfileFile.Save(created, new(), missing.Revision)); Require(Hash(created) == existing, "缺失基线覆盖了后来新建文件。");
            string deleted = Path.Combine(run, "deleted.json"); ShellProfileFile.Save(deleted, new(), "missing"); var loaded = ShellProfileFile.Load(deleted); File.Delete(deleted);
            Throws<IOException>(() => ShellProfileFile.Save(deleted, new(), loaded.Revision)); Require(!File.Exists(deleted), "删除冲突后意外重新创建源文件。");
        });
        foreach (var corruption in new[] { (Name: "损坏 JSON", Data: "{this is not json"), (Name: "非法尺寸", Data: "{\"IconSize\":17}"), (Name: "异常大文件", Data: new string('x', 65537)) })
            Check(corruption.Name + "加载失败后禁止保存覆盖原字节", () =>
            {
                var badPath = Path.Combine(run, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(badPath, corruption.Data, new UTF8Encoding(false)); var badHash = Hash(badPath);
                var bad = New(badPath); Require(Field<string?>(bad, "sourceRevision") is null, "坏文件产生了可保存的修订基线。");
                bad.SelectPage(0); Choice(bad, "图标大小").SelectedItem = "16 px"; Require(!Field<Button>(bad, "save").IsEnabled, "坏文件开启了保存按钮。");
                bad.SaveDraft(); Require(Hash(badPath) == badHash && !File.Exists(badPath + ".previous"), "保存覆盖了坏文件或生成了误导备份。");
                Require(Field<TextBlock>(bad, "feedback").Text.Contains("已保护原文件", StringComparison.Ordinal), "坏文件保存缺少保护说明。");
            });
        Check("非法方案在写入前拒绝；所有成功/失败路径清理临时文件", () =>
        {
            var snapshot = ShellProfileFile.Load(path); var original = Hash(path);
            Throws<InvalidDataException>(() => ShellProfileFile.Save(path, new ShellProfile(IconSize: 17), snapshot.Revision)); Require(Hash(path) == original, "非法方案改变了文件。");
            Require(Directory.GetFiles(run, ".shell-profile-*.tmp", SearchOption.AllDirectories).Length == 0, "保存后留下了临时文件。");
        });
        Check("检查失败仅更新反馈，不创建显示窗口", () =>
        {
            var inspectButton = Logical<Button>(desk).Single(b => AutomationProperties.GetAutomationId(b) == "shell-inspect"); Click(inspectButton); Pump();
            Require(InspectCalls == 1 && Field<TextBlock>(desk, "feedback").Text.StartsWith("检查未完成", StringComparison.Ordinal), "检查失败回调未呈现。");
            Require(Windows.All(w => !w.IsVisible), "检查测试显示了窗口。");
        });
        Check("异步检查期间允许关闭；取消后不创建窗口或写回反馈", () =>
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new ShellSettingsWindow(path: Path.Combine(run, "busy-close.json"), inspectPlan: _ => completion.Task); Windows.Add(window);
            var task = Inspect(window); Require(Field<bool>(window, "busy"), "检查未进入忙状态。"); var text = Field<TextBlock>(window, "feedback").Text;
            window.Close(); Await(task); Require(Field<bool>(window, "closed") && !Field<bool>(window, "busy"), "忙状态拦截了关闭或取消未完成。");
            completion.SetResult("late result"); Pump(); Require(Field<TextBlock>(window, "feedback").Text == text && !window.IsVisible, "已关闭窗口收到了迟到写回。");
        });
        Check("异步检查冻结草稿并拒绝过期结果，忙时重复检查不重入", () =>
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); ShellProfile? captured = null; int calls = 0;
            var window = new ShellSettingsWindow(path: Path.Combine(run, "stale-inspect.json"), inspectPlan: profile => { calls++; captured = profile; return completion.Task; }); Windows.Add(window);
            var task = Inspect(window); Await(Inspect(window)); Require(calls == 1, "忙时重复发起检查。");
            window.SelectPage(0); Choice(window, "图标大小").SelectedItem = "16 px"; Require(captured != window.Draft, "检查未冻结初始草稿。");
            completion.SetResult("stale success"); Await(task);
            Require(Field<TextBlock>(window, "feedback").Text == "方案已变化，请重新检查应用条件。" && !window.IsVisible, "过期检查仍进入应用结果。");
        });
        Check("未显示窗口检查成功仅更新反馈，不调用对话框", () =>
        {
            var window = new ShellSettingsWindow(path: Path.Combine(run, "successful-inspect.json"), inspectPlan: _ => Task.FromResult("isolated success")); Windows.Add(window);
            int count = app.Windows.Count; Await(Inspect(window));
            Require(app.Windows.Count == count && !window.IsVisible && Field<TextBlock>(window, "feedback").Text == "检查完成 · Windows 尚未修改", "离屏成功分支创建了窗口。");
        });
        Check("异步检查八秒超时后释放忙状态并忽略迟到结果", () =>
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new ShellSettingsWindow(path: Path.Combine(run, "timeout-inspect.json"), inspectPlan: _ => completion.Task); Windows.Add(window);
            var watch = Stopwatch.StartNew(); Await(Inspect(window), 12000); watch.Stop();
            Require(watch.Elapsed.TotalSeconds >= 7 && !Field<bool>(window, "busy") && Field<TextBlock>(window, "feedback").Text.StartsWith("检查超时", StringComparison.Ordinal), "检查没有执行八秒超时或未释放忙状态。");
            string text = Field<TextBlock>(window, "feedback").Text; completion.SetResult("late after timeout"); Pump();
            Require(Field<TextBlock>(window, "feedback").Text == text && !window.IsVisible, "超时后迟到结果仍修改了界面。");
        });
        Check("清理窗口不触发未保存对话框且无可见窗口", () =>
        {
            Require(Windows.All(w => !w.IsVisible), "离屏验收意外显示窗口。");
            foreach (var window in Windows.OfType<ShellSettingsWindow>()) { SetField(window, "saved", window.Draft); window.Close(); }
        });
        Check("未显示的 MainWindow 关闭后 Application.Run 正常结束", () =>
        {
            var main = New(Path.Combine(run, "lifecycle.json")); app.MainWindow = main; app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            bool closed = false; main.Closed += (_, _) => closed = true;
            app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { Require(!main.IsVisible, "生命周期窗口被显示。"); main.Close(); }));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; bool forced = false;
            timer.Tick += (_, _) => { forced = true; timer.Stop(); app.Shutdown(99); }; timer.Start(); var code = app.Run(); timer.Stop();
            Require(closed && !forced && code == 0 && !main.IsVisible, "关闭未显示主窗口没有正常结束消息循环。");
        });
        var files = Directory.GetFiles(AppContext.BaseDirectory).Select(p => new { name = Path.GetFileName(p), bytes = new FileInfo(p).Length }).ToArray();
        File.WriteAllText(Path.Combine(Output, "新前端-离屏验收.json"), JsonSerializer.Serialize(new
        {
            passed = Results.Count(r => r.Passed), failed = Results.Count(r => !r.Passed), results = Results,
            screenshotClientAreas = new[] { "820x610", "740x550" }, screenshots = Screenshots,
            sourceFiles = new[] { "AppIcons.cs", "ShellPreview.cs", "ShellProfile.cs", "ShellSettingsWindow.cs", "ShellTheme.xaml" },
            realSystemWrites = 0, realUserProfileWrites = 0, visibleWindows = 0, mouseKeyboardActions = 0,
            boundary = "离屏控件事件/布局和work内方案保存检查；不包含原生Shell底层、真正鼠键交互、安装或应用验收。",
            isolatedDirectory = run, checkerBuildBytes = files.Sum(f => f.bytes), checkerFiles = files,
            footprintNote = "这是独立验收器构建目录的实测字节，不是产品运行包的占用。"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Passed {Results.Count(r => r.Passed)}, Failed {Results.Count(r => !r.Passed)}; {Screenshots.Count} screenshots.");
        foreach (var failure in Results.Where(r => !r.Passed)) Console.WriteLine(failure.Name + ": " + failure.Detail);
        return Results.All(r => r.Passed) ? 0 : 1;
    }

    static void Check(string name, Action action) { try { action(); Results.Add(new(name, true, "通过")); } catch (Exception e) { Results.Add(new(name, false, e.ToString())); } }
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("预期拒绝为" + typeof(T).Name); }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static T Field<T>(object value, string name) => (T)(value.GetType().GetField(name, Members) ?? throw new MissingFieldException(name)).GetValue(value)!;
    static void SetField(object value, string name, object data) => (value.GetType().GetField(name, Members) ?? throw new MissingFieldException(name)).SetValue(value, data);
    static void Click(Button value) => value.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static CheckBox Toggle(ShellSettingsWindow window, string name) => Logical<CheckBox>(Field<StackPanel>(window, "settings")).Single(c => AutomationProperties.GetName(c) == name);
    static ComboBox Choice(ShellSettingsWindow window, string name) => Logical<ComboBox>(Field<StackPanel>(window, "settings")).Single(c => AutomationProperties.GetName(c) == name);
    static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    { foreach (var item in LogicalTreeHelper.GetChildren(root)) if (item is DependencyObject child) { if (child is T match) yield return match; foreach (var nested in Logical<T>(child)) yield return nested; } }
    static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Visuals<T>(child)) yield return nested; } }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    static Task Inspect(ShellSettingsWindow window) => (Task)(typeof(ShellSettingsWindow).GetMethod("InspectAsync", Members) ?? throw new MissingMethodException("InspectAsync")).Invoke(window, null)!;
    static void Await(Task task, int milliseconds = 4000)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        }
        Require(task.IsCompleted, "异步检查未在测试期限内完成。"); task.GetAwaiter().GetResult();
    }
    static void Layout(Window window, int width, int height)
    { var root = (FrameworkElement)window.Content; for (int i = 0; i < 3; i++) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); Pump(); } }
    static void Bounds(Window window, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        foreach (var item in Visuals<FrameworkElement>(root).Where(i => i is Button or ComboBox or CheckBox or ShellPreview))
        {
            if (item.Visibility != Visibility.Visible || item.ActualWidth <= 0) continue; var at = item.TransformToAncestor(root).Transform(new Point());
            Require(at.X >= -2 && at.X + item.ActualWidth <= width + 2, $"{item.GetType().Name}横向越界 {at.X}+{item.ActualWidth}。");
            if (item is Button b && AutomationProperties.GetAutomationId(b) is "shell-save" or "shell-inspect") Require(at.Y >= 0 && at.Y + item.ActualHeight <= height + 1, "底部动作超出可访问范围。");
        }
    }
    static string ImageHash(FrameworkElement element)
    {
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32); bmp.Render(element);
        var bytes = new byte[bmp.PixelWidth * bmp.PixelHeight * 4]; bmp.CopyPixels(bytes, bmp.PixelWidth * 4, 0); return Convert.ToHexString(SHA256.HashData(bytes));
    }
    static void Capture(Window window, int width, int height, string name)
    {
        Layout(window, width, height); var root = (FrameworkElement)window.Content;
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        { var area = new Rect(0, 0, width, height); dc.DrawRectangle(window.Background ?? Brushes.White, null, area); dc.DrawRectangle(new VisualBrush(root), null, area); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(Output, name)); encoder.Save(stream); Screenshots.Add(name);
    }
}


