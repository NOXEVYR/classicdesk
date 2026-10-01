using ClassicDesk;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "reports/start-ui"); Directory.CreateDirectory(output);
        var fixture = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<object>(); int failed = 0, launchCalls = 0, placeCalls = 0, scanCalls = 0;
        string Profile(string key) => Path.Combine(fixture, key + ".json");
        void Test(string name, Action run) { try { run(); checks.Add(new { name, passed = true }); } catch (Exception e) { failed++; checks.Add(new { name, passed = false, error = e.ToString() }); } }
        var programs = Path.Combine(fixture, "programs"); Directory.CreateDirectory(programs); Directory.CreateDirectory(Path.Combine(programs, "工作"));
        File.WriteAllText(Path.Combine(programs, "工作", "代码编辑器.lnk"), "fixture"); File.WriteAllText(Path.Combine(programs, "工作", "便笺.lnk"), "fixture");
        File.WriteAllText(Path.Combine(programs, "非常长的中文程序名称用于确认窄宽度搜索结果不会遮挡固定按钮和常用目录.lnk"), "fixture");
        var catalog = ClassicStartCatalog.Scan(new[] { programs }); var first = catalog.Apps[0].Id;
        ClassicStartMenuWindow Window(string key, ClassicStartOptions? options = null, ClassicStartCatalogSnapshot? snapshot = null,
            Func<ClassicStartCatalogSnapshot>? scanner = null, Action<string>? launcher = null, Func<bool>? discard = null) => new(options, Profile(key), snapshot ?? catalog,
                scanner ?? (() => { scanCalls++; return catalog; }), launcher ?? (_ => launchCalls++), _ => placeCalls++, discard ?? (() => true));

        Test("construction and closing perform no scan, launch, place open or profile write", () => {
            var w = Window("construction"); Assert(!w.IsVisible && !File.Exists(Profile("construction")) && scanCalls == 0 && launchCalls == 0 && placeCalls == 0); w.Close(); Assert(!File.Exists(Profile("construction")));
        });
        Test("default demo displays synthetic apps and rejects launch", () => { var w = new ClassicStartMenuWindow(profilePath: Profile("demo"), launch: _ => throw new Exception("unexpected launch")); Assert(w.VisibleApps.Count == 5); w.LaunchSelected(); Assert(w.StatusText.Contains("未启动")); w.Close(); });
        Test("all programs and category filtering", () => { var w=Window("category"); w.SelectSection("all"); Assert(w.VisibleApps.Count==3); w.SelectSection("category:工作"); Assert(w.VisibleApps.Count==2); w.Close(); });
        Test("search spans categories and names and displays empty feedback", () => { var w=Window("search"); w.Search("工作"); Assert(w.VisibleApps.Count==2); w.Search("编辑"); Assert(w.VisibleApps.Count==1); w.Search("does-not-exist"); Assert(w.VisibleApps.Count==0 && Find<TextBlock>(w,"start-empty").Visibility==Visibility.Visible); w.Close(); });
        Test("pin, unpin and limit are draft only", () => { var w=Window("pin", new(MaxPinned:1)); w.SelectSection("all"); w.TogglePin(first); w.TogglePin(catalog.Apps[1].Id); Assert(w.Options.EffectivePinnedIds.Length==1 && w.StatusText.Contains("上限")); w.SelectSection("pinned"); Assert(w.VisibleApps.Count==1); w.TogglePin(first); Assert(w.VisibleApps.Count==0 && !File.Exists(Profile("pin"))); w.Close(); });
        Test("draft saves explicitly and reloads pinned choices", () => { var w=Window("save"); w.TogglePin(first); Assert(w.HasUnsavedChanges); w.SaveSettings(); Assert(!w.HasUnsavedChanges && File.Exists(Profile("save"))); w.Close(); var second=Window("save"); Assert(second.Options.EffectivePinnedIds.Contains(first)); second.Close(); });
        Test("revert restores saved choices and leaves file untouched", () => { var w=Window("revert"); w.SaveSettings(); var bytes=File.ReadAllBytes(Profile("revert")); w.TogglePin(first); w.RevertSettings(); Assert(!w.HasUnsavedChanges && w.Options.EffectivePinnedIds.Length==0 && bytes.SequenceEqual(File.ReadAllBytes(Profile("revert")))); w.Close(); });
        Test("corrupt profile remains byte identical after rejected save", () => { File.WriteAllText(Profile("corrupt"), "{broken"); var bytes=File.ReadAllBytes(Profile("corrupt")); var w=Window("corrupt"); Assert(w.StatusText.Contains("读取失败")); w.TogglePin(first); w.SaveSettings(); Assert(w.StatusText.Contains("未保存") && bytes.SequenceEqual(File.ReadAllBytes(Profile("corrupt")))); w.Close(); });
        Test("external revision prevents stale window overwriting", () => { var w=Window("race"); var other=ClassicStartProfileFile.Load(Profile("race")); ClassicStartProfileFile.Save(new(Style:"compact"),other.Revision,Profile("race")); var bytes=File.ReadAllBytes(Profile("race")); w.TogglePin(first); w.SaveSettings(); Assert(w.HasUnsavedChanges && bytes.SequenceEqual(File.ReadAllBytes(Profile("race")))); w.Close(); });
        Test("import export previews before save and preserves existing export", () => { var export=Profile("export"); var w=Window("import"); w.TogglePin(first); w.ExportSettings(export); Assert(!File.Exists(Profile("import"))); var original=File.ReadAllBytes(export); w.TogglePin(first); w.ExportSettings(export); Assert(original.SequenceEqual(File.ReadAllBytes(export)) && w.StatusText.Contains("失败")); w.ImportSettings(export); Assert(w.HasUnsavedChanges && w.Options.EffectivePinnedIds.Contains(first)); w.Close(); });
        Test("options change style icons sorting and visibility without writing", () => { var w=Window("options"); Click(w,"start-options"); Assert(w.SettingsVisible); Find<ComboBox>(w,"start-style").SelectedIndex=2; Find<ComboBox>(w,"start-icon-size").SelectedItem=32; Find<CheckBox>(w,"start-sort").IsChecked=false; Find<CheckBox>(w,"start-user-heading").IsChecked=false; Assert(w.Options.Style=="compact" && w.Options.IconSize==32 && !w.Options.SortAscending && !w.Options.ShowUserHeading && !File.Exists(Profile("options"))); w.HandleNavigationKey(Key.Escape); Assert(!w.SettingsVisible); w.Close(); });
        Test("Escape is offered to the editor before window navigation and modified Escape is ignored", () => {
            var w=Window("escape-routing"); w.ShowSettings(true); var choice=Find<ComboBox>(w,"start-style");
            KeyEventArgs Event(RoutedEvent route, bool handled=false) => new(Keyboard.PrimaryDevice,new FixtureInputSource(),Environment.TickCount,Key.Escape) { RoutedEvent=route, Handled=handled };
            var preview=Event(Keyboard.PreviewKeyDownEvent); choice.RaiseEvent(preview); Assert(!preview.Handled && w.SettingsVisible);
            choice.RaiseEvent(Event(Keyboard.KeyDownEvent,true)); Assert(w.SettingsVisible);
            Assert(!w.HandleNavigationKey(Key.Escape,ModifierKeys.Control) && w.SettingsVisible);
            var fallback=Event(Keyboard.KeyDownEvent); choice.RaiseEvent(fallback); Assert(fallback.Handled && !w.SettingsVisible && !File.Exists(Profile("escape-routing"))); w.Close();
        });
        Test("entry icons can be hidden and are independent of program icon size", () => { var w=Window("entry-icons");var place=(StackPanel)Find<Button>(w,"start-place-user").Content;Assert(place.Children.OfType<Image>().Single().Width==18); Find<ComboBox>(w,"start-icon-size").SelectedItem=32;place=(StackPanel)Find<Button>(w,"start-place-user").Content;Assert(place.Children.OfType<Image>().Single().Width==18);Find<CheckBox>(w,"start-entry-icons").IsChecked=false;place=(StackPanel)Find<Button>(w,"start-place-user").Content;Assert(!place.Children.OfType<Image>().Any());w.Close(); });
        Test("equivalent defaults and returning choices to saved values clear draft state", () => { var w=Window("equivalent",new ClassicStartOptions());Assert(!w.HasUnsavedChanges);w.TogglePin(first);Assert(w.HasUnsavedChanges);w.TogglePin(first);Assert(!w.HasUnsavedChanges);Find<ComboBox>(w,"start-style").SelectedIndex=2;Assert(w.HasUnsavedChanges);Find<ComboBox>(w,"start-style").SelectedIndex=0;Assert(!w.HasUnsavedChanges && !File.Exists(Profile("equivalent")));w.Close(); });
        Test("hidden dirty menu rejects close without opening confirmation or writing", () => {
            var w=new ClassicStartMenuWindow(profilePath:Profile("hidden-dirty"),catalog:catalog);bool closed=false;w.Closed+=(_,_)=>closed=true;w.TogglePin(first);w.Close();Assert(!closed && w.HasUnsavedChanges && !w.IsVisible && !File.Exists(Profile("hidden-dirty")));w.HandleNavigationKey(Key.Escape);Assert(!closed && w.StatusText.Contains("尚未保存"));w.RevertSettings();w.Close();Assert(closed);
        });
        Test("close cancel retains draft and confirm discards without automatic save", () => {
            bool accept=false,closed=false;int confirmations=0;var w=Window("discard",discard:()=>{confirmations++;return accept;});w.SaveSettings();var bytes=File.ReadAllBytes(Profile("discard"));w.Closed+=(_,_)=>closed=true;w.TogglePin(first);Click(w,"start-close");Assert(!closed && w.HasUnsavedChanges && confirmations==1 && bytes.SequenceEqual(File.ReadAllBytes(Profile("discard"))));accept=true;w.HandleNavigationKey(Key.Escape);Assert(closed && confirmations==2 && bytes.SequenceEqual(File.ReadAllBytes(Profile("discard"))));
        });
        Test("confirmation failure retains draft and clean menu needs no confirmation", () => {
            var clean=Window("clean-close",discard:()=>throw new Exception("unexpected confirmation"));bool cleanClosed=false;clean.Closed+=(_,_)=>cleanClosed=true;clean.Close();Assert(cleanClosed);var dirty=Window("discard-error",discard:()=>throw new IOException("确认失败"));bool dirtyClosed=false;dirty.Closed+=(_,_)=>dirtyClosed=true;dirty.TogglePin(first);dirty.Close();Assert(!dirtyClosed && dirty.StatusText.Contains("确认失败") && !File.Exists(Profile("discard-error")));dirty.RevertSettings();dirty.Close();
        });
        Test("double click launches only the clicked owned result row", () => {
            int launches=0;string? target=null;var w=Window("double-click",launcher:path=>{launches++;target=path;});w.SelectSection("all");var list=Find<ListBox>(w,"start-results");Assert(list.SelectedItem is ListBoxItem);
            Render(w,output,"double-click-targets",800,640,96);
            DoubleClick(list,list);Assert(launches==0);var scrollBar=VisualWalk(list).OfType<System.Windows.Controls.Primitives.ScrollBar>().First();DoubleClick(list,scrollBar);Assert(launches==0);
            DoubleClick(list,new ListBoxItem{Tag=catalog.Apps[0],Content="foreign"});Assert(launches==0);
            var row=(ListBoxItem)list.Items[1];var surface=(Grid)row.Content;var pin=surface.Children.OfType<Button>().Single();DoubleClick(list,pin);Assert(launches==0);
            var text=surface.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().First();DoubleClick(list,text,MouseButton.Right);Assert(launches==0);DoubleClick(list,text);Assert(launches==1 && target==((ClassicStartApp)row.Tag).SourcePath && ReferenceEquals(list.SelectedItem,row));w.Close();
        });
        Test("custom folder add open remove and invalid path feedback", () => { var w=Window("folder"); var folder=Path.Combine(fixture,"custom");Directory.CreateDirectory(folder); w.AddCustomFolder("工作资料",folder); Assert(w.Options.EffectiveCustomFolders.Length==1); var id=w.Options.EffectiveCustomFolders[0].Id; w.OpenPlace(id); Assert(placeCalls==1); Click(w,"start-folder-remove-"+id); Assert(w.Options.EffectiveCustomFolders.Length==0); w.AddCustomFolder("错误","relative");Assert(w.StatusText.Contains("未添加"));w.Close(); });
        Test("keyboard results selection and Enter execute only injected callback", () => { var w=Window("keyboard"); w.Search("工作"); Assert(w.HandleNavigationKey(Key.Down)); w.HandleNavigationKey(Key.Enter); Assert(launchCalls==1 && w.StatusText.Contains("已请求启动")); w.HandleNavigationKey(Key.Escape); Assert(Find<TextBox>(w,"start-search").Text==""); w.Close(); });
        Test("launch rejects removed shortcut and reports callback failure", () => { var path=Path.Combine(programs,"gone.lnk");File.WriteAllText(path,"fixture");var shot=ClassicStartCatalog.Scan(new[]{programs}); var w=Window("gone",snapshot:shot);w.Search("gone");File.Delete(path);w.LaunchSelected();Assert(w.StatusText.Contains("未启动") && launchCalls==1);w.Close();var bad=Window("launch-error",launcher:_=>throw new IOException("模拟启动失败"));bad.Search("编辑");bad.LaunchSelected();Assert(bad.StatusText.Contains("模拟启动失败"));bad.Close(); });
        Test("refresh is explicit and returns injected read only catalog", () => { var w=Window("refresh"); Complete(w.RefreshCatalogAsync());Assert(scanCalls==1 && !w.IsScanning && w.StatusText.Contains("已读取"));w.Close(); });
        Test("scan failure preserves results and enables retry", () => { var w=Window("scan-error",scanner:()=>throw new IOException("模拟读取失败"));w.SelectSection("all");Complete(w.RefreshCatalogAsync());Assert(w.VisibleApps.Count==3 && !w.IsScanning && Find<Button>(w,"start-refresh").IsEnabled && w.StatusText.Contains("模拟读取失败"));Render(w,output,"scan-error",800,640,96);w.Close(); });
        Test("late scan after close cannot refresh or write", () => { using var gate=new ManualResetEventSlim(); var w=Window("late",scanner:()=>{gate.Wait();return catalog;});var task=w.RefreshCatalogAsync(); w.Close();gate.Set();Complete(task);Assert(!File.Exists(Profile("late"))); });
        Test("ordinary menu renders 800x640 at 96 and 144 dpi without showing", () => { var w=Window("ordinary", new(PinnedIds:catalog.Apps.Select(a=>a.Id).ToArray()));Render(w,output,"ordinary-96",800,640,96);Render(w,output,"ordinary-144",800,640,144);Assert(!w.IsVisible);w.Close(); });
        Test("compact menu renders long Chinese search at 560x520 and 192 dpi", () => { var w=Window("compact",new(Style:"compact",IconSize:16));w.Search("非常长");Render(w,output,"compact-long-96",560,520,96);Render(w,output,"compact-long-192",560,520,192);Assert(!w.IsVisible);w.Close(); });
        Test("Windows 10 tile menu and options are rendered independently", () => { var w=Window("tiles",new(Style:"win10"));w.SelectSection("all");Render(w,output,"win10-tiles",800,640,96);w.ShowSettings(true);Render(w,output,"options",800,640,96);Render(w,output,"options-compact",560,520,96);Find<ScrollViewer>(w,"start-options-view").ScrollToBottom();Render(w,output,"options-compact-bottom",560,520,96);w.Close(); });
        Test("empty menu and no-match results render actionable feedback", () => { var w=Window("empty",snapshot:new ClassicStartCatalogSnapshot(Array.Empty<ClassicStartApp>()));w.SelectSection("all");Render(w,output,"empty",560,520,96);w.Search("不存在");Render(w,output,"no-match",800,640,96);w.Close(); });
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {passed=checks.Count-failed,failed,windowsShown=0,systemMutations=0,realLaunches=0,fixture,checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Start UI: {checks.Count-failed} passed, {failed} failed.");app.Shutdown();return failed==0?0:1;
    }
    static void Complete(Task task) { var frame=new DispatcherFrame(); task.ContinueWith(_=>Application.Current.Dispatcher.BeginInvoke(()=>frame.Continue=false));Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult(); }
    static void Assert(bool value) { if(!value)throw new Exception("Assertion failed"); }
    sealed class FixtureInputSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    static IEnumerable<DependencyObject> Walk(DependencyObject node) { yield return node;foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())foreach(var nested in Walk(child))yield return nested; }
    static T Find<T>(DependencyObject root,string id) where T:DependencyObject => Walk(root).OfType<T>().Single(e=>AutomationProperties.GetAutomationId(e)==id);
    static void Click(DependencyObject root,string id)=>Find<Button>(root,id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void DoubleClick(ListBox list,DependencyObject origin,MouseButton button=MouseButton.Left) { var args=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,button){RoutedEvent=Control.MouseDoubleClickEvent,Source=origin};list.RaiseEvent(args);Assert(ReferenceEquals(args.OriginalSource,origin)); }
    static IEnumerable<DependencyObject> VisualWalk(DependencyObject node) { yield return node;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in VisualWalk(VisualTreeHelper.GetChild(node,i)))yield return child; }
    static void Render(ClassicStartMenuWindow window,string output,string name,int width,int height,double dpi)
    {
        var content=(FrameworkElement)window.Content;
        // Isolate the render surface from Window's non-client sizing. No native handle is created.
        window.Content=null;content.Resources=window.Resources;
        content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,window.FontFamily);
        content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,window.FontSize);
        content.Width=width;content.Height=height;content.InvalidateMeasure();content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        if(Math.Abs(content.ActualWidth-width)>=1 || Math.Abs(content.ActualHeight-height)>=1) throw new Exception($"Unexpected render surface {content.ActualWidth}x{content.ActualHeight}, wanted {width}x{height}");
        var bitmap=new RenderTargetBitmap((int)(width*dpi/96),(int)(height*dpi/96),dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(content);
        var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);Assert(pixels.Where((v,i)=>i%4!=3).Distinct().Count()>20);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);content.Width=double.NaN;content.Height=double.NaN;window.Content=content;Assert(!window.IsVisible);
    }
}
