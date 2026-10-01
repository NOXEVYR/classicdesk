using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using ClassicDesk;

internal static class Program
{
    sealed record Result(string Name, bool Passed, string Detail);
    sealed record Picture(string File, int Width, int Height, double Dpi, string Hash);
    sealed record MenuLabel(string Text) { public override string ToString() => Text; }
    static readonly List<Result> Results = [];
    static readonly List<Picture> Pictures = [];
    static readonly List<ShellSettingsWindow> Windows = [];
    static readonly List<ContextMenu> Menus = [];
    static string output = "", fixture = "";
    static int externalCalls;
    const string LongTip = "这是用于验证长中文提示换行与焦点边界的完整文本。切换预览状态只改变编辑示意，不会保存草稿、修改方案参数或应用到真实 Windows 任务栏。键盘用户仍可使用 Tab 到达控件，并通过菜单箭头、Enter 和 Escape 使用默认导航。这个提示不应变成超出视口的一条长文本，也不应遮住相邻控件的内容。";

    [STAThread]
    public static int Main(string[] args)
    {
        output = Path.GetFullPath(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "ClassicDesk-FocusMenu-" + Guid.NewGuid().ToString("N")) : args[0]);
        Directory.CreateDirectory(output);
        fixture = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));

        Check("容器退出Tab序列而可操作控件保留键盘访问", () =>
        {
            var window = New("focus-policy");
            foreach (int page in new[] { 0, 1, 2, 3, 4, 5 })
            {
                window.SelectPage(page); Layout((FrameworkElement)window.Content, 1080, 820);
                var scroll = Field<ScrollViewer>(window, "scroll");
                Require(!scroll.Focusable && !KeyboardNavigation.GetIsTabStop(scroll) && (scroll.FocusVisualStyle is null || HasThemeFocusVisual(scroll)), "主滚动容器仍可聚焦或沿用系统虚线焦点样式。");
                Require(KeyboardNavigation.GetTabNavigation(scroll) != KeyboardNavigationMode.None, "通过禁用整个滚动区Tab导航掩盖焦点问题。");
                foreach (var control in Logical(window).OfType<Control>().Where(c => c is Button or CheckBox or ComboBox or TextBox))
                {
                    Require(control.Focusable && KeyboardNavigation.GetIsTabStop(control), "可操作控件失去键盘可达性: " + Id(control));
                    Require(control.FocusVisualStyle is null || HasThemeFocusVisual(control), "交互控件仍使用默认系统焦点adorner: " + Id(control));
                    Require(HasFocusTrigger(control.Template) || HasThemeFocusVisual(control), "交互控件缺少主题焦点提示: " + Id(control));
                }
            }
        });
        Check("合成系统Alt和Alt+F4事件未被窗口快捷键拦截", () =>
        {
            var window = New("system-keys"); var before = window.Draft;
            foreach (Key key in new[] { Key.LeftAlt, Key.RightAlt, Key.F4, Key.Left, Key.Right, Key.Up, Key.Down, Key.Enter, Key.Escape })
            {
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, new FixtureInputSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(e); Require(!e.Handled && window.Draft == before, "窗口吞掉默认导航/系统按键: " + key);
            }
            var system = new KeyEventArgs(Keyboard.PrimaryDevice, new FixtureInputSource(), Environment.TickCount, Key.F4) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(system, null);
            Require(system.Key == Key.System && system.SystemKey == Key.F4, "合成系统键夹具无效。");
            window.RaiseEvent(system); Require(!system.Handled, "窗口吞掉System/F4事件。此项仅验证路由处理，不是原生Alt+F4验收。");
        });
        Check("实际场景菜单Opened刷新单选与Click仅切换预览", () =>
        {
            var window = New("scene-click");
            foreach (int page in new[] { 0, 2, 4 })
            {
                window.SelectPage(page); Layout((FrameworkElement)window.Content, 1080, 820);
                var button = Find<Button>(window, page == 4 ? "library-preview-scenarios" : "shell-preview-scenarios");
                var menu = Track(button.ContextMenu!); var draft = window.Draft; var blocked = window.UpdateBlockReason;
                Require(ReferenceEquals(menu.PlacementTarget, button) && menu.Placement == System.Windows.Controls.Primitives.PlacementMode.Bottom, "菜单锚点或方向丢失。");
                Require(menu.Style == window.FindResource("ShellContextMenu") && menu.Items.OfType<MenuItem>().All(i => i.Style == window.FindResource("ShellMenuItem")), "真实菜单未显式使用主题资源。");
                foreach (ShellOverviewMode mode in Enum.GetValues<ShellOverviewMode>())
                {
                    var item = menu.Items.OfType<MenuItem>().Single(i => Id(i) == "overview-mode-" + mode);
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Opened(menu);
                    Require(window.Draft == draft && window.UpdateBlockReason == blocked, "场景选择改变了草稿或未保存状态。");
                    Require(menu.Items.OfType<MenuItem>().Count(i => i.IsChecked) == 1 && item.IsChecked, "Opened后场景未保持单选。");
                    Require(Find<ShellProfileOverview>(window, page == 4 ? "library-current-overview" : "shell-live-overview").Mode == mode, "实际Click未切换对应示意。");
                    Require(!File.Exists(Path.Combine(fixture, "scene-click.json")), "场景选择提前写入方案文件。");
                }
                Require(menu.Items.OfType<MenuItem>().All(i => i.IsCheckable && i.Focusable && KeyboardNavigation.GetIsTabStop(i)), "场景菜单失去勾选语义或键盘访问。");
            }
        });
        Check("已有菜单Opened读取最新皮肤配色", () =>
        {
            var window = New("menu-palette"); var menu = Track(Find<Button>(window, "shell-preview-scenarios").ContextMenu!);
            foreach (int skin in Enumerable.Range(0, ShellSkins.All.Count))
            {
                window.ApplySkin(skin); Opened(menu);
                foreach (string key in new[] { "AccentBrush", "SidebarBrush", "WorkspaceBrush" })
                    Require(((SolidColorBrush)menu.FindResource(key)).Color == ((SolidColorBrush)window.FindResource(key)).Color, "菜单Opened未刷新配色 " + key);
                Require(!menu.IsOpen, "合成Opened意外打开Popup。");
            }
        });
        Check("禁用菜单的自动化Invoke拒绝执行而启用项保留Invoke语义", () =>
        {
            var window = New("disabled-menu"); var style = (Style)window.FindResource("ShellMenuItem"); int invoked = 0;
            var disabled = new MenuItem { Header = "禁用动作", IsEnabled = false, Style = style }; disabled.Click += (_, _) => invoked++;
            var peer = new MenuItemAutomationPeer(disabled); var invoke = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
            bool rejected = false; try { invoke.Invoke(); } catch (ElementNotEnabledException) { rejected = true; }
            Require(rejected && invoked == 0, "禁用菜单Invoke没有拒绝或触发了Click。");
            var enabled = new MenuItem { Header = "启用动作", Style = style };
            Require(new MenuItemAutomationPeer(enabled).GetPattern(PatternInterface.Invoke) is IInvokeProvider && enabled.Focusable, "启用菜单失去Invoke与焦点语义。");
        });
        Check("真实方案更多菜单分组与归档恢复Click保持草稿", () =>
        {
            var window = New("library-menu"); window.SaveToLibrary("键盘与菜单检查方案"); var draft = window.Draft;
            var entry = window.LibraryEntries.Single(); string suffix = entry.Id.ToString("N");
            var menu = Track(Find<Button>(window, "library-more-" + suffix).ContextMenu!);
            Require(menu.Items.OfType<MenuItem>().Count() == 4, "方案菜单动作缺失。");
            menu.Items.OfType<MenuItem>().Single(i => Id(i) == "library-archive-" + suffix).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(window.LibraryEntries.Single().Archived && window.Draft == draft, "归档动作丢失方案或改变当前草稿。");
            var archived = Find<CheckBox>(window, "library-archived"); archived.IsChecked = true; archived.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            menu = Track(Find<Button>(window, "library-more-" + suffix).ContextMenu!);
            Require(menu.Items.OfType<MenuItem>().Count() == 3, "归档项仍暴露更新动作。");
            menu.Items.OfType<MenuItem>().Single(i => Id(i) == "library-archive-" + suffix).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(!window.LibraryEntries.Single().Archived && window.Draft == draft, "恢复归档动作失败或改变草稿。");
        });

        foreach (int skin in Enumerable.Range(0, ShellSkins.All.Count))
            foreach (var (width, height) in new[] { (740, 550), (1080, 820) })
                foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
                    Check($"六皮肤菜单与模拟模板焦点 {skin} {width}x{height} {scale * 100}%", () =>
                    {
                        var window = New("render-" + skin + "-" + width + "-" + scale); window.ApplySkin(skin); window.SelectPage(2);
                        var root = (FrameworkElement)window.Content; Layout(root, width, height);
                        foreach (var button in Logical(root).OfType<Button>().Where(c => c.ActualWidth > 0))
                        {
                            var at = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                            Require(at.Left >= -1 && at.Right <= width + 1, "控件横向越界 " + Id(button) + " " + at);
                        }
                        Save(Render(root, width, height, scale), $"window-{skin}-{width}x{height}-{scale * 100}.png");
                        var menu = Track(Find<Button>(window, "shell-preview-scenarios").ContextMenu!); Opened(menu);
                        var sheet = Sheet(window, menu, width, height, scale); Layout(sheet, width, height);
                        Save(Render(sheet, width, height, scale), $"focus-menu-{skin}-{width}x{height}-{scale * 100}.png");
                        Require(!menu.IsOpen && !window.IsVisible, "离屏检查显示了原生窗口或Popup。");
                    });

        Check("测试未显示窗口创建原生焦点或调用真实宿主", () =>
        {
            Require(externalCalls == 0 && Windows.All(w => !w.IsVisible) && Menus.All(m => !m.IsOpen), "测试越过离屏边界。");
            Require(Windows.All(w => PresentationSource.FromVisual(w) is null), "测试窗口存在原生PresentationSource。");
        });
        Check("生成可审查六皮肤PNG缩略总览", ContactSheets);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            Passed = Results.Count(r => r.Passed), Failed = Results.Count(r => !r.Passed), Tests = Results, Images = Pictures,
            WindowsShown = Windows.Any(w => w.IsVisible), PopupsOpened = Menus.Any(m => m.IsOpen), ExternalHostCalls = externalCalls,
            StaticEvidence = "实际有效 FocusVisualStyle / Focusable / TabStop / KeyboardNavigation 与原模板触发器检查",
            EventEvidence = "真实菜单对象合成 Click / Opened 与 PreviewKeyDown 路由；未发送真实键鼠",
            SimulatedFocusEvidence = "从同一ShellTheme.xaml复制样式并将焦点/高亮触发器替换为Tag；仅核对模板选中态外观和边界，不是原生keyboard focus",
            RenderingScope = "Measure/Arrange/RenderTargetBitmap；菜单和Tooltip独立Visibility.Visible渲染再栅格合成，不添加父级、不打开Popup；100/125/150/200%是输出栅格密度，不等同真实显示器DPI切换",
            NativeAcceptance = "未验收：真实Alt虚线消失、原生Popup位置/焦点、菜单箭头/Enter/Escape交互、Alt+F4、显示器DPI切换"
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in Results.Where(r => !r.Passed)) Console.WriteLine(result.Name + ": " + result.Detail);
        Console.WriteLine($"FocusMenu: {Results.Count(r => r.Passed)} passed, {Results.Count(r => !r.Passed)} failed, {Pictures.Count} images");
        foreach (var window in Windows) window.Close(); app.Shutdown(); return Results.Any(r => !r.Passed) ? 1 : 0;
    }

    static Grid Sheet(ShellSettingsWindow window, ContextMenu actualMenu, int width, int height, double scale)
    {
        var root = new Grid { Background = (Brush)window.FindResource("WorkspaceBrush"), Margin = new Thickness(0) };
        root.Resources = window.Resources;
        var title = new TextBlock { Text = "真实菜单模板 · 模拟焦点/高亮 · 已选/禁用/分组 · 长文本提示", Margin = new Thickness(18, 14, 12, 0), FontSize = 12 };
        root.Children.Add(title);
        actualMenu.Resources = window.Resources; actualMenu.Visibility = Visibility.Visible; actualMenu.ApplyTemplate();
        Require(actualMenu.Template is not null && KeyboardNavigation.GetDirectionalNavigation(actualMenu) != KeyboardNavigationMode.None, "实际菜单模板或箭头导航丢失。");
        var selected = actualMenu.Items.OfType<MenuItem>().Single(i => i.IsChecked); Simulate(selected, highlighted: true);
        Natural(actualMenu, 320); var menuImage = Snapshot(actualMenu, scale); menuImage.HorizontalAlignment = HorizontalAlignment.Left; menuImage.VerticalAlignment = VerticalAlignment.Top; menuImage.Margin = new Thickness(18, 42, 0, 0); root.Children.Add(menuImage);
        var panel = new StackPanel { Width = 360, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 42, 18, 0) }; root.Children.Add(panel);
        var grouped = new ContextMenu { Style = (Style)window.FindResource("ShellContextMenu") }; Track(grouped);
        grouped.Resources = window.Resources;
        var checkedItem = new MenuItem { Header = "已选中 · 菜单状态", IsCheckable = true, IsChecked = true, Style = (Style)window.FindResource("ShellMenuItem") };
        var disabled = new MenuItem { Header = "禁用动作 · 不可执行", IsEnabled = false, Style = checkedItem.Style };
        var submenu = new MenuItem { Header = "分组与子菜单", Style = checkedItem.Style }; submenu.Items.Add(new MenuItem { Header = "保留默认箭头与Enter/Escape" }); submenu.Items.Add(new Separator());
        var nested = new MenuItem { Header = "二级分组" }; nested.Items.Add(new MenuItem { Header = "三级菜单" }); nested.Items.Add(new Separator()); nested.Items.Add("自动生成的三级条目"); submenu.Items.Add(nested);
        var separator = new Separator(); grouped.Items.Add(checkedItem); grouped.Items.Add(separator); grouped.Items.Add(disabled); grouped.Items.Add(submenu);
        grouped.Visibility = Visibility.Visible; Natural(grouped, 310); panel.Children.Add(Snapshot(grouped, scale));
        var mixed = Track(new ContextMenu { Style = grouped.Style, Visibility = Visibility.Visible }); mixed.Resources = window.Resources;
        mixed.Items.Add("普通字符串 · 生成容器"); mixed.Items.Add(new Separator()); mixed.Items.Add(new MenuItem { Header = "MenuItem · 隐式主题样式" }); mixed.Items.Add(new MenuLabel("对象条目 · 生成容器")); Natural(mixed, 320);
        for (int index = 0; index < mixed.Items.Count; index++)
        {
            var container = mixed.ItemContainerGenerator.ContainerFromIndex(index);
            Require(container is MenuItem or Separator, "混合条目容器类型错误。");
            if (container is MenuItem item) Require(HasFocusTrigger(item.Template) && item.FocusVisualStyle is null, "自动生成菜单项未继承主题模板/焦点。");
        }
        var mixedImage = Snapshot(mixed, scale); mixedImage.HorizontalAlignment = HorizontalAlignment.Left; mixedImage.VerticalAlignment = VerticalAlignment.Top; mixedImage.Margin = new Thickness(18, 340, 0, 0); root.Children.Add(mixedImage);
        foreach (var parent in new[] { submenu, nested })
        {
            parent.ApplyTemplate(); var popup = (System.Windows.Controls.Primitives.Popup)parent.Template.FindName("PART_Popup", parent);
            Natural((FrameworkElement)popup.Child, 320);
            Require(!popup.IsOpen && !parent.IsSubmenuOpen, "子菜单检查打开了Popup。");
            for (int index = 0; index < parent.Items.Count; index++)
                if (parent.ItemContainerGenerator.ContainerFromIndex(index) is MenuItem generated)
                    Require(HasFocusTrigger(generated.Template), "自然子菜单条目未继承主题模板。");
        }
        var focus = new Button { Content = "按钮 · 模拟键盘焦点", Style = (Style)window.FindResource("ShellButton"), Margin = new Thickness(8, 16, 8, 8) }; panel.Children.Add(focus); Simulate(focus);
        var check = new CheckBox { Content = "普通复选框 · 已选与模拟焦点", IsChecked = true, Style = (Style)window.FindResource("ShellCheckBox"), Margin = new Thickness(8, 4, 8, 8) }; panel.Children.Add(check); Simulate(check);
        var combo = new ComboBox { Style = (Style)window.FindResource("ShellChoice"), Margin = new Thickness(8, 4, 8, 8) }; combo.Items.Add("下拉控件 · 模拟焦点"); combo.SelectedIndex = 0; panel.Children.Add(combo); Simulate(combo);
        var tooltip = new ToolTip { Content = LongTip, Style = (Style)window.FindResource("ShellToolTip"), Visibility = Visibility.Visible }; tooltip.Resources = window.Resources; Natural(tooltip, 294);
        var tipImage = Snapshot(tooltip, scale); tipImage.Margin = new Thickness(8, 8, 8, 0); panel.Children.Add(tipImage);
        Layout(root, width, height);
        Require(disabled.IsEnabled == false && disabled.Opacity > 0, "禁用菜单状态不可见。");
        Require(XamlWriter.Save(submenu.Template).Contains("PART_Popup", StringComparison.Ordinal), "子菜单PART_Popup丢失。");
        Require(Visual(checkedItem).OfType<System.Windows.Shapes.Path>().Single(p => p.Name == "CheckMark").Visibility == Visibility.Visible, "已选菜单勾选标记未显示。");
        Require(Visual(submenu).OfType<System.Windows.Shapes.Path>().Single(p => p.Name == "SubmenuArrow").Visibility == Visibility.Visible, "子菜单箭头未显示。");
        Require(separator.Template is not null && separator.ActualHeight > 0 && !separator.Focusable, "默认Separator容器未使用可见主题模板或抢占焦点。");
        var wrapped = Visual(tooltip).OfType<TextBlock>().SingleOrDefault(t => t.Text == LongTip);
        Require(wrapped is not null && wrapped.TextWrapping == TextWrapping.Wrap && wrapped.ActualHeight > wrapped.FontSize * 2, "长字符串Tooltip未在实际模板换行。");
        Require(tooltip.ActualWidth <= panel.ActualWidth + 1, "Tooltip宽度溢出面板。");
        var bottom = tipImage.TransformToAncestor(root).TransformBounds(new Rect(0, 0, tipImage.ActualWidth, tipImage.ActualHeight));
        Require(bottom.Right <= width + 1 && bottom.Bottom <= height + 1, "长Tooltip超出离屏最小视口 " + bottom);
        var elementTip = new ToolTip { Content = new TextBlock { Text = "UIElement tooltip", Foreground = Brushes.Purple }, Style = tooltip.Style, Visibility = Visibility.Visible }; elementTip.Resources = window.Resources; Natural(elementTip, 300);
        Require(Visual(elementTip).Contains((DependencyObject)elementTip.Content), "Tooltip替换模板丢失自定义UIElement内容。");
        foreach (Control control in new Control[] { focus, check, combo })
        {
            Require(control.Focusable && KeyboardNavigation.GetIsTabStop(control), "模拟模板损坏控件键盘访问。");
            var visibleRing = Visual(control).OfType<Border>().FirstOrDefault(b => b.Name is "KeyboardRing" or "FocusRing" && b.Visibility == Visibility.Visible);
            if (visibleRing is not null)
            {
                var bounds = visibleRing.TransformToAncestor(root).TransformBounds(new Rect(0, 0, visibleRing.ActualWidth, visibleRing.ActualHeight));
                Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width && bounds.Bottom <= height, "模拟焦点ring越出视口。");
            }
        }
        return root;
    }
    static void Natural(FrameworkElement element, int width)
    {
        element.ApplyTemplate(); element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(new Point(0, 0), element.DesiredSize)); element.UpdateLayout();
    }
    static Image Snapshot(FrameworkElement element, double scale) => new() { Source = Render(element, (int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), scale), Width = element.ActualWidth, Height = element.ActualHeight, Stretch = Stretch.Fill };
    static void Simulate(Control control, bool highlighted = false)
    {
        control.ApplyTemplate(); Require(control.Template is not null, "控件模板为空。");
        string theme = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ClassicDesk/ShellTheme.xaml"));
        var xml = XDocument.Load(theme); bool replaced = false;
        foreach (var trigger in xml.Descendants().Where(e => e.Name.LocalName == "Trigger"))
            if (trigger.Attribute("Property")?.Value.Split('.').Last() is "IsKeyboardFocused" or "IsKeyboardFocusWithin" || highlighted && trigger.Attribute("Property")?.Value.Split('.').Last() == "IsHighlighted")
            { trigger.Elements().Where(e => e.Name.LocalName == "Trigger.Value").Remove(); trigger.SetAttributeValue("Property", "Tag"); trigger.SetAttributeValue("Value", "simulated-focus"); replaced = true; }
        Require(replaced, "原模板无可模拟的主题焦点触发器。");
        var resources = (ResourceDictionary)XamlReader.Parse(xml.ToString());
        string key = control switch { MenuItem => "ShellMenuItem", Button => "ShellButton", CheckBox => "ShellCheckBox", ComboBox => "ShellChoice", _ => throw new InvalidOperationException("未定义模拟控件类型。") };
        control.Style = (Style)resources[key]; control.Tag = "simulated-focus"; control.ApplyTemplate();
    }
    static bool HasFocusTrigger(ControlTemplate? template) => template is not null && template.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsKeyboardFocusedProperty || t.Property == UIElement.IsKeyboardFocusWithinProperty);
    static bool HasThemeFocusVisual(Control control) => control.FocusVisualStyle is { } style && XamlWriter.Save(style).Contains("AccentBrush", StringComparison.Ordinal);
    static ShellSettingsWindow New(string name)
    {
        void Forbidden() { externalCalls++; throw new InvalidOperationException("离屏检查禁止真实宿主调用。"); }
        var window = new ShellSettingsWindow(path: Path.Combine(fixture, name + ".json"), inspectPlan: _ => { Forbidden(); return Task.FromResult(""); },
            manageNative: (_, _) => Forbidden(), manageAutoHide: _ => Forbidden(), openTaskbarSettings: Forbidden, manageUpdates: _ => Forbidden(), manageLayouts: _ => Forbidden());
        Windows.Add(window); return window;
    }
    static ContextMenu Track(ContextMenu menu) { if (!Menus.Contains(menu)) Menus.Add(menu); return menu; }
    static void Opened(ContextMenu menu) { Require(!menu.IsOpen, "不得真实打开菜单。"); menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent)); }
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    static string Id(DependencyObject value) => AutomationProperties.GetAutomationId(value);
    static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Logical(root).OfType<T>().Single(e => Id(e) == id);
    static IEnumerable<DependencyObject> Logical(DependencyObject root) { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var node in Logical(child)) yield return node; }
    static IEnumerable<DependencyObject> Visual(DependencyObject root) { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var node in Visual(VisualTreeHelper.GetChild(root, i))) yield return node; }
    sealed class FixtureInputSource : PresentationSource { public override Visual RootVisual { get; set; } = new DrawingVisual(); public override bool IsDisposed => false; protected override CompositionTarget GetCompositionTargetCore() => null!; }
    static void Check(string name, Action action) { try { action(); Results.Add(new(name, true, "")); } catch (Exception e) { Results.Add(new(name, false, e.ToString())); } }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Layout(FrameworkElement root, int width, int height)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        }
    }
    static RenderTargetBitmap Render(FrameworkElement root, int width, int height, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), scale * 96, scale * 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height)); bitmap.Render(drawing); return bitmap;
    }
    static void Save(BitmapSource bitmap, string name)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        Require(bytes.Where((_, i) => i % 4 == 3).Count(v => v > 0) > bitmap.PixelWidth * bitmap.PixelHeight / 2, "PNG大部分透明。");
        Require(bytes.Where((_, i) => i % 4 != 3).Distinct().Count() > 30, "PNG缺少实际内容。");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(output, name))) encoder.Save(stream);
        Pictures.Add(new(name, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, Convert.ToHexString(SHA256.HashData(bytes))));
    }
    static void ContactSheets()
    {
        foreach (string prefix in new[] { "window", "focus-menu" })
            foreach (var (width, height) in new[] { (740, 550), (1080, 820) })
                foreach (int percent in new[] { 100, 125, 150, 200 })
                {
                    var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 1200));
                        for (int skin = 0; skin < 6; skin++)
                        {
                            string file = Path.Combine(output, $"{prefix}-{skin}-{width}x{height}-{percent}.png"); Require(File.Exists(file), "总览缺少图片 " + file);
                            var image = new BitmapImage(new Uri(file)); dc.DrawImage(image, new Rect(skin % 2 * 500, skin / 2 * 400, 500, 500d * height / width));
                        }
                    }
                    var bitmap = new RenderTargetBitmap(1000, 1200, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); Save(bitmap, $"contact-{prefix}-{width}x{height}-{percent}.png");
                }
    }
}
