using ClassicDesk;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length == 1 ? args[0] : "reports/updates-ui"); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<object>(); int failures = 0;
        void Test(string name, Action run) { try { run(); checks.Add(new { name, passed = true }); } catch (Exception e) { failures++; checks.Add(new { name, passed = false, error = e.Message }); } }
        Test("coordinator preserves preview revision channel", () => { Assert(FrontendUpdateCoordinator.ChannelForVersion("0.11.17-preview") == "preview" && FrontendUpdateCoordinator.ChannelForVersion("0.11.17-preview.1") == "preview" && FrontendUpdateCoordinator.ChannelForVersion("0.11.17") == "stable"); });
        Test("coordinator rejects malformed version channel", () => { bool rejected=false;try{FrontendUpdateCoordinator.ChannelForVersion("0.11.17-preview\n");}catch(InvalidDataException){rejected=true;}Assert(rejected); });
        Test("opening update panel does not check download or install", () => { var fake = new Fake(); var panel = new FrontendUpdatePanel(fake); Assert(fake.Checks == 0 && fake.Downloads == 0 && fake.Installs == 0); panel.Close(); });
        Test("manual check remains accessible when automatic checks disabled", () => { var fake = new Fake { Enabled = false }; var panel = new FrontendUpdatePanel(fake); Click(panel, "update-check"); Assert(fake.Checks == 1); panel.Close(); });
        foreach (var original in new[] { true, false }) Test("failed preference write restores actual checkbox state " + original, () => {
            var fake = new Fake { Enabled = original, FailPreference = true }; var panel = new FrontendUpdatePanel(fake);
            var checkbox = Walk(panel).OfType<CheckBox>().Single(); checkbox.IsChecked = !original;
            Assert(checkbox.IsChecked == original && fake.Enabled == original && fake.PreferenceCalls == 1 && panel.StatusText.Contains("未保存更新偏好")); panel.Close();
        });
        Test("cancel large download performs zero transfers", () => { var fake = new Fake { DownloadBytes = FrontendUpdateFiles.AutoDownloadLimit + 1 }; var panel = new FrontendUpdatePanel(fake, _ => false); Click(panel, "update-download"); Assert(fake.Downloads == 0); panel.Close(); });
        Test("large download passes explicit approval", () => { var fake = new Fake { DownloadBytes = FrontendUpdateFiles.AutoDownloadLimit + 1 }; var panel = new FrontendUpdatePanel(fake, _ => true); Click(panel, "update-download"); Assert(fake.Downloads == 1 && fake.Approved); panel.Close(); });
        Test("small manual download is bounded without large-download approval", () => { var fake = new Fake { DownloadBytes = 1024 }; var panel = new FrontendUpdatePanel(fake, _ => throw new Exception("Unexpected confirmation")); Click(panel, "update-download"); Assert(fake.Downloads == 1 && !fake.Approved); panel.Close(); });
        Test("cancel installation does not invoke installer", () => { var fake = new Fake { CanInstall = true }; var panel = new FrontendUpdatePanel(fake, _ => false); Click(panel, "update-install"); Assert(fake.Installs == 0); panel.Close(); });
        Test("installation requires ready and explicit choice", () => { var fake = new Fake { CanInstall = true }; var panel = new FrontendUpdatePanel(fake, _ => true); Click(panel, "update-install"); Assert(fake.Installs == 1); panel.Close(); });
        Test("busy updates disable installation", () => { var fake = new Fake { CanInstall = true, IsBusy = true }; var panel = new FrontendUpdatePanel(fake, _ => true); Assert(!panel.InstallAvailable); Click(panel, "update-install"); Assert(fake.Installs == 0); panel.Close(); });
        Test("closing panel unsubscribes late results", () => { var fake = new Fake(); var panel = new FrontendUpdatePanel(fake); panel.Close(); fake.StatusText = "late"; fake.Notify(); Assert(panel.StatusText != "late"); });
        Test("unsaved draft prevents updater closing the settings window", () => { var window = new ShellSettingsWindow(path: Path.Combine(output, "dirty.json")); window.ApplyPreset(2); Assert(window.UpdateBlockReason?.Contains("草稿") == true && !window.TryCloseForUpdate()); Assert(!File.Exists(Path.Combine(output,"dirty.json"))); window.Close(); });
        Test("saved draft allows updater close without discarding data", () => { var path = Path.Combine(output, "saved.json"); var window = new ShellSettingsWindow(path: path); window.ApplyPreset(2); window.SaveDraft(); var bytes = File.ReadAllBytes(path); Assert(window.UpdateBlockReason is null && window.TryCloseForUpdate()); Assert(File.ReadAllBytes(path).SequenceEqual(bytes)); });
        Test("native operation blocks update reentry", () => { bool blocked = false; var window = new ShellSettingsWindow(path: Path.Combine(output,"busy.json"), manageNative:(owner,_) => { var w=(ShellSettingsWindow)owner; blocked=w.UpdateBlockReason?.Contains("操作") == true && !w.TryCloseForUpdate(); }); Click(window,"shell-inspect"); Assert(blocked && window.UpdateBlockReason is null); window.Close(); });
        Test("native operation exception releases update gate", () => { var window = new ShellSettingsWindow(path: Path.Combine(output,"exception.json"), manageNative:(_,_) => throw new IOException("fake failure")); try { Click(window,"shell-inspect"); } catch(IOException) {} Assert(window.UpdateBlockReason is null); window.Close(); });
        Test("software update entry invokes only the injected handler and enables titlebar hit testing", () => { int calls=0; var window = new ShellSettingsWindow(path:Path.Combine(output,"entry.json"),manageUpdates:_=>calls++); Assert(WindowChrome.GetIsHitTestVisibleInChrome(Walk(window).OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="shell-updates"))); Click(window,"shell-updates"); Assert(calls==1 && !File.Exists(Path.Combine(output,"entry.json"))); window.Close(); });
        Test("update panel renders offline at compact width", () => { var panel = new FrontendUpdatePanel(new Fake { StatusText="0.11.18-preview 已暂存并校验，尚未安装。\n请先保存设置草稿。" }); var root=(FrameworkElement)panel.Content; root.Measure(new Size(500,430)); root.Arrange(new Rect(0,0,500,430)); root.UpdateLayout(); var visual=new DrawingVisual(); using(var dc=visual.RenderOpen()){dc.DrawRectangle(panel.Background,null,new Rect(0,0,500,430));dc.DrawRectangle(new VisualBrush(root),null,new Rect(0,0,500,430));} var bitmap=new RenderTargetBitmap(500,430,96,96,PixelFormats.Pbgra32); bitmap.Render(visual); var pixels=new byte[500*430*4];bitmap.CopyPixels(pixels,500*4,0);Assert(pixels.Where((v,i)=>i%4!=3).Distinct().Count()>16); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file=File.Create(Path.Combine(output,"software-update.png")); png.Save(file); Assert(!panel.IsVisible); panel.Close(); });
        File.WriteAllText(Path.Combine(output,"report.json"), JsonSerializer.Serialize(new {passed=checks.Count-failures,failed=failures,windowsShown=0,realInstallationsChanged=0,checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Updates UI: {checks.Count-failures} passed, {failures} failed."); app.Shutdown(); return failures==0?0:1;
    }
    static void Assert(bool condition) { if(!condition)throw new Exception("assertion failed"); }
    static IEnumerable<DependencyObject> Walk(DependencyObject node) { yield return node; foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) foreach(var nested in Walk(child)) yield return nested; }
    static void Click(DependencyObject root,string id) => Walk(root).OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)==id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    sealed class Fake : IFrontendUpdateActions
    {
        public string StatusText {get;set;}="尚未检查"; public bool IsBusy{get;set;} public bool Enabled{get;set;}=true; public bool CanDownload{get;set;}=true; public bool CanInstall{get;set;} public long DownloadBytes{get;set;}
        public int Checks,Downloads,Installs,PreferenceCalls; public bool Approved,FailPreference; public event Action? Changed;
        public Task CheckAsync(bool manual){Checks++;return Task.CompletedTask;} public Task DownloadAsync(bool approved){Downloads++;Approved=approved;return Task.CompletedTask;}
        public void SetEnabled(bool enabled) { PreferenceCalls++; if(FailPreference) throw new IOException("fixture write failure"); Enabled=enabled; } public void Install()=>Installs++; public void Notify()=>Changed?.Invoke();
    }
}
