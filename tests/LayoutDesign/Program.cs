using ClassicDesk;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

internal static class Program
{
    record Result(string Name, bool Passed, string Detail);
    static readonly List<Result> Results = [];
    static void Require(bool value, string detail) { if (!value) throw new Exception(detail); }
    static void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or JsonException or ArgumentException) { return; } throw new Exception("无效输入未被拒绝"); }
    static void Check(string name, Action action) { try { action(); Results.Add(new(name, true, "")); } catch (Exception e) { Results.Add(new(name, false, e.ToString())); } }
    static T Find<T>(DependencyObject parent, string id) where T : DependencyObject => Descendants(parent).OfType<T>().First(c => AutomationProperties.GetAutomationId(c) == id);
    static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>()) foreach (var node in Descendants(child)) yield return node;
    }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void Layout(Window window, int width, int height)
    {
        var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
    }
    static void Capture(Window window, int width, int height, double factor, string path)
    {
        var content = (FrameworkElement)window.Content; var bitmap = new RenderTargetBitmap((int)(width * factor), (int)(height * factor), 96 * factor, 96 * factor, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]), fixture = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string path = Path.Combine(fixture, "design.json");
        Check("缺失设计只读加载不创建文件", () => { Require(TaskbarDesignProfileFile.Load(path).Revision == "missing", "非missing"); Require(!File.Exists(path), "创建了文件"); });
        Check("默认与显式同项目托盘语义相等", () => Require(TaskbarDesignProfileFile.Fingerprint(new()) == TaskbarDesignProfileFile.Fingerprint(new(VisibleTrayItems: ["volume", "input", "clock", "network", "showDesktop"])), "等价项目误认为更改"));
        Check("保存加载导入可往返全部选项", () =>
        {
            var options = new TaskbarDesignOptions("left", "neverWithLabels", "left", 32, 64, 228, 19, true, 13, ["clock", "input", "pen"]);
            var revision = TaskbarDesignProfileFile.Save(path, options, "missing"); Require(TaskbarDesignProfileFile.Load(path).Revision == revision, "修订不匹配");
            Require(TaskbarDesignProfileFile.Fingerprint(TaskbarDesignProfileFile.Import(path)) == TaskbarDesignProfileFile.Fingerprint(options), "往返丢失");
        });
        Check("新保存保留原始字节备份", () => { var bytes = File.ReadAllBytes(path); TaskbarDesignProfileFile.Save(path, new(), TaskbarDesignProfileFile.Revision(path)); Require(bytes.SequenceEqual(File.ReadAllBytes(path + ".previous")), "备份变化"); });
        Check("外部修改拒绝覆盖", () => { var revision = TaskbarDesignProfileFile.Revision(path); File.WriteAllText(path, "{\"Dock\":\"top\"}"); Reject(() => TaskbarDesignProfileFile.Save(path, new(), revision)); Require(File.ReadAllText(path) == "{\"Dock\":\"top\"}", "外部修改被覆盖"); });
        foreach (var (name, json) in new[] { ("未知字段", "{\"Wrong\":true}"), ("重复字段", "{\"Dock\":\"top\",\"Dock\":\"bottom\"}"), ("非法位置", "{\"Dock\":\"float\"}"), ("未核实第五合并模式", "{\"Combine\":\"never\"}"), ("重复托盘", "{\"VisibleTrayItems\":[\"clock\",\"clock\"]}"), ("未知托盘", "{\"VisibleTrayItems\":[\"secret\"]}"), ("尺寸越界", "{\"LabelWidth\":241}"), ("空对象", "{}"), ("损坏JSON", "{") })
            Check(name + "导入拒绝", () => { string input = Path.Combine(fixture, Guid.NewGuid() + ".json"); File.WriteAllText(input, json); Reject(() => TaskbarDesignProfileFile.Import(input)); });
        Check("超大文件拒绝且保留", () => { string input = Path.Combine(fixture, "large.json"); File.WriteAllText(input, new string(' ', 65537)); Reject(() => TaskbarDesignProfileFile.Load(input)); Require(new FileInfo(input).Length == 65537, "原文件变化"); });
        Check("损坏文件即使修订已知也拒绝覆盖", () => { string input = Path.Combine(fixture, "damaged.json"); File.WriteAllText(input, "broken"); Reject(() => TaskbarDesignProfileFile.Save(input, new(), TaskbarDesignProfileFile.Revision(input))); Require(File.ReadAllText(input) == "broken", "覆盖损坏文件"); });
        Check("单项托盘保留首尾绘制留白", () => { foreach (var id in new[] { "clock", "network" }) { var result = TaskbarDesignLayout.Calculate(new(VisibleTrayItems: [id]), [], 1024, 400); double glyph = id == "clock" ? 56 : 22; Require(result.Tray.Width >= glyph + 6, "末项/单项被裁切"); } });
        Check("托盘空间不足明确报告缩略省略", () => { var result = TaskbarDesignLayout.Calculate(new(VisibleTrayItems: TaskbarDesignTray.Items.Select(t => t.Id).ToArray()), [], 640, 400); Require(result.Explanation.Contains("托盘项目在缩略图中省略"), "缩略省略无提示"); });
        Check("始终合并按应用身份收敛", () => { var result = TaskbarDesignLayout.Calculate(new(VisibleTrayItems: []), TaskbarDesignLayout.Examples, 1920, 400); Require(result.Combined && result.Buttons.Count == 4 && result.Buttons.Count(b => b.WindowCount == 2) == 2, "合并错误"); });
        Check("宽屏占满时合并保持六个文字窗口", () => { var result = TaskbarDesignLayout.Calculate(new(Combine: "whenFull", VisibleTrayItems: []), TaskbarDesignLayout.Examples, 1920, 400); Require(!result.Combined && result.Buttons.Count == 6 && result.Buttons.All(b => b.LabelVisible), "宽屏误合并"); });
        Check("窄屏占满时合并收敛四组", () => { var result = TaskbarDesignLayout.Calculate(new(Combine: "whenFull", VisibleTrayItems: []), TaskbarDesignLayout.Examples, 640, 400); Require(result.Combined && result.Buttons.Count == 4 && result.Buttons.All(b => !b.LabelVisible), "窄屏未合并"); });
        Check("从不合并保留同应用多窗口及溢出计数", () => { var result = TaskbarDesignLayout.Calculate(new(Combine: "neverWithLabels", VisibleTrayItems: []), TaskbarDesignLayout.Examples, 640, 400); Require(!result.Combined && result.OverflowCount > 0 && result.Buttons.All(b => b.WindowCount == 1 && b.LabelVisible), "从不合并错误"); });
        Check("隐藏标签仍保留独立窗口", () => { var result = TaskbarDesignLayout.Calculate(new(Combine: "neverNoLabels", VisibleTrayItems: []), TaskbarDesignLayout.Examples, 1024, 400); Require(result.Buttons.Count == 6 && result.Buttons.All(b => !b.LabelVisible && b.WindowCount == 1), "隐藏标签模式丢失窗口"); });
        foreach (var dock in TaskbarDesignOptions.Docks)
            Check("边缘/对齐/容量矩阵 " + dock, () =>
            {
                foreach (var alignment in TaskbarDesignOptions.Alignments) foreach (var combine in TaskbarDesignOptions.CombineModes) foreach (var extent in new[] { 120, 640, 1920 })
                {
                    var options = new TaskbarDesignOptions(Dock: dock, Alignment: alignment, Combine: combine, Height: 64, EdgeMargin: 32, TrayGap: 16, VisibleTrayItems: TaskbarDesignTray.Items.Select(t => t.Id).ToArray());
                    var result = TaskbarDesignLayout.Calculate(options, TaskbarDesignLayout.Examples, extent, extent);
                    var screen = new Rect(0, 0, extent, extent); Require(screen.Contains(result.Bar) && screen.Contains(result.Start) && screen.Contains(result.Tray), "边缘元素超出屏幕");
                    foreach (var button in result.Buttons) Require(result.Bar.Contains(button.Bounds) && !button.Bounds.IntersectsWith(result.Start) && !button.Bounds.IntersectsWith(result.Tray), "按钮重叠或越界");
                    Require(result.OverflowCount >= 0, "溢出计数负值");
                }
            });
        Check("非法NaN/无穷/超大画布拒绝", () => { foreach (var width in new[] { double.NaN, double.PositiveInfinity, 119, 16385 }) Reject(() => TaskbarDesignLayout.Calculate(new(), [], width, 400)); });
        Check("零窗口与零托盘正常布局", () => { var result = TaskbarDesignLayout.Calculate(new(VisibleTrayItems: []), [], 640, 400); Require(result.Buttons.Count == 0 && result.OverflowCount == 0 && result.Tray.Width == 0, "空布局异常"); });
        string uiPath = Path.Combine(fixture, "ui.json"); int startCalls = 0;
        var window = new LayoutWorkbenchWindow(uiPath, _ => startCalls++);
        Check("构造与翻页不写文件或打开菜单", () => { window.SelectPage(1); window.SelectPage(0); Require(!File.Exists(uiPath) && startCalls == 0 && !window.IsDirty, "构造有副作用"); });
        Check("用户点击独立菜单入口才触发回调", () => { Click(Find<Button>(window, "design-start-menu")); Require(startCalls == 1, "回调未触发"); });
        Check("控件编辑联动草稿与保存", () => { ((ComboBox)window.Controls["design-dock"]).SelectedIndex = 1; Require(window.Draft.Dock == "top" && window.IsDirty, "编辑失效"); window.SaveDraft(); Require(!window.IsDirty && TaskbarDesignProfileFile.Load(uiPath).Options.Dock == "top", "保存失效"); });
        Check("保存结果可回读且不影响ShellProfile", () => { Require(TaskbarDesignProfileFile.Load(uiPath).Options.Dock == "top", "保存未回读"); Require(!File.Exists(Path.Combine(fixture, "ShellProfile.json")), "写入既有方案"); window.Change(window.Draft with { Height = 64 }); window.DiscardDraft(); Require(window.Draft.Height == 48 && !window.IsDirty, "撤销失效"); });
        Check("任务栏重置保留托盘草稿", () => { window.Change(window.Draft with { TrayGap = 15, VisibleTrayItems = [], Dock = "right" }); window.SelectPage(0); window.ResetPage(); Require(window.Draft.Dock == "bottom" && window.Draft.TrayGap == 15 && window.Draft.EffectiveTrayItems.Count == 0, "任务栏重置混入托盘"); });
        Check("托盘重置保留任务栏草稿", () => { window.Change(window.Draft with { Dock = "left" }); window.SelectPage(1); window.ResetPage(); Require(window.Draft.Dock == "left" && window.Draft.TrayGap == 6 && window.Draft.EffectiveTrayItems.Count == 5, "托盘重置混入任务栏"); });
        Check("17项托盘逐项改变示意及保存", () => { window.SelectPage(1); Require(window.Controls.Count == 18, "托盘项缺失"); foreach (var item in TaskbarDesignTray.Items) ((CheckBox)window.Controls["design-tray-" + item.Id]).IsChecked = true; Require(window.Draft.EffectiveTrayItems.Count == 17, "开启失效"); window.SaveDraft(); Require(TaskbarDesignProfileFile.Load(uiPath).Options.EffectiveTrayItems.Count == 17, "保存丢项"); });
        Check("导出不清除草稿且保护原文件", () => { window.Change(window.Draft with { EdgeMargin = 20 }); string export = Path.Combine(fixture, "export.json"); window.ExportDraft(export, "missing"); Require(window.IsDirty && TaskbarDesignProfileFile.Import(export).EdgeMargin == 20, "导出改变状态"); Reject(() => window.ExportDraft(uiPath, TaskbarDesignProfileFile.Revision(uiPath))); });
        Check("损坏配置禁写且草稿可导出", () => { string corrupt = Path.Combine(fixture, "corrupt.json"); File.WriteAllText(corrupt, "broken"); var broken = new LayoutWorkbenchWindow(corrupt); broken.Change(broken.Draft with { Dock = "top" }); broken.SaveDraft(); Require(File.ReadAllText(corrupt) == "broken" && broken.IsDirty, "覆盖坏文件"); broken.ExportDraft(Path.Combine(fixture, "rescued.json"), "missing"); });
        Check("取消关闭保留草稿，明确放弃后关闭不写文件", () => {
            bool discard = false, closed = false; int prompts = 0; string closePath = Path.Combine(fixture, "close.json");
            var closing = new LayoutWorkbenchWindow(closePath, confirmDiscard: () => { prompts++; return discard; }); closing.Closed += (_, _) => closed = true;
            closing.Change(closing.Draft with { Dock = "top" }); closing.Close(); Require(!closed && closing.IsDirty && prompts == 1, "取消关闭丢草稿");
            discard = true; closing.Close(); Require(closed && prompts == 2 && !File.Exists(closePath), "确认关闭写文件或未关闭");
        });
        Check("关闭确认失败不丢失草稿", () => { bool closed = false; var closing = new LayoutWorkbenchWindow(Path.Combine(fixture, "close-error.json"), confirmDiscard: () => throw new IOException("合成确认失败")); closing.Closed += (_, _) => closed = true; closing.Change(closing.Draft with { Dock = "top" }); closing.Close(); Require(!closed && closing.IsDirty, "确认异常丢草稿"); });
        Check("主界面布局入口点击及更新阻断", () =>
        {
            bool blocked = false; int calls = 0; ShellSettingsWindow? parent = null;
            parent = new ShellSettingsWindow(path: Path.Combine(fixture, "shell.json"), manageLayouts: _ => { calls++; blocked = parent!.UpdateBlockReason is not null; });
            var button = Find<Button>(parent, "shell-layout-workbench"); Require(WindowChrome.GetIsHitTestVisibleInChrome(button), "标题栏不接收点击"); Click(button); Require(calls == 1 && blocked && parent.UpdateBlockReason is null, "操作gate失效");
        });
        Check("纵向说明与实际预览尺寸一致", () => {
            window.Change(new(Dock: "left", VisibleTrayItems: TaskbarDesignTray.Items.Select(t => t.Id).ToArray())); Layout(window, 720, 600);
            var preview = Descendants(window).OfType<TaskbarDesignPreview>().Single(); Require(Descendants(window).OfType<TextBlock>().Any(t => t.Text.StartsWith(preview.CurrentLayout.Explanation, StringComparison.Ordinal)), "说明使用了不同画布高度");
        });
        foreach (var (width, height, factor) in new[] { (1040, 820, 1.0), (720, 600, 1.25), (720, 600, 1.5), (1040, 820, 2.0) }) foreach (var page in new[] { 0, 1 })
            Check($"布局渲染 {width}x{height} {factor * 100}% page{page}", () =>
            {
                window.Change(new(Combine: "whenFull", Segmented: true));
                window.SelectPage(page); Layout(window, width, height);
                foreach (var button in Descendants(window).OfType<Button>().Where(b => b.Visibility == Visibility.Visible && b.ActualWidth > 0)) { var bounds = button.TransformToAncestor((FrameworkElement)window.Content).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight)); Require(bounds.Left >= -.5 && bounds.Right <= width + .5 && bounds.Top >= -.5 && bounds.Bottom <= height + .5, "操作按钮裁切"); }
                Capture(window, width, height, factor, Path.Combine(output, $"layout-{width}x{height}-{factor * 100}-page{page}.png"));
            });
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { Passed = Results.Count(r => r.Passed), Failed = Results.Count(r => !r.Passed), Tests = Results, WindowsSettingsWritten = false, WindowsShown = false }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in Results.Where(r => !r.Passed)) Console.WriteLine(result.Name + ": " + result.Detail);
        Console.WriteLine($"LayoutDesign: {Results.Count(r => r.Passed)} passed, {Results.Count(r => !r.Passed)} failed"); app.Shutdown(); return Results.Any(r => !r.Passed) ? 1 : 0;
    }
}
