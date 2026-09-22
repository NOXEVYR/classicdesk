using ClassicDesk;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Program
{
    [STAThread] static int Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var checks = new List<object>();
        void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
        void Test(string name, Action run)
        { try { run(); checks.Add(new { name, passed = true, error = "" }); } catch (Exception e) { checks.Add(new { name, passed = false, error = e.ToString() }); } }
        Test("construction does not inspect or write", () => { var host = new Fake(); var panel = new ShellServicePanel(new(), host); Check(host.Reads == 0 && host.Writes == 0 && !panel.ApplyAvailable); panel.Close(); });
        Test("matching profile cannot stage", () => { var host = new Fake { Same = true }; var panel = new ShellServicePanel(new(), host); panel.RefreshAsync().GetAwaiter().GetResult(); panel.ApplyAsync().GetAwaiter().GetResult(); Check(!panel.ApplyAvailable && host.Writes == 0); panel.Close(); });
        Test("review enables update only after success", () => { var host = new Fake(); var panel = new ShellServicePanel(new(), host); panel.RefreshAsync().GetAwaiter().GetResult(); Check(panel.ApplyAvailable && host.Writes == 0); panel.Close(); });
        Test("successful update says next reboot and disables repeat write", () => { var host = new Fake(); var panel = new ShellServicePanel(new(), host); panel.RefreshAsync().GetAwaiter().GetResult(); panel.ApplyAsync().GetAwaiter().GetResult(); panel.ApplyAsync().GetAwaiter().GetResult(); Check(host.Writes == 1 && panel.StatusText.Contains("等待重启") && !panel.ApplyAvailable); panel.Close(); });
        Test("cancelled UAC never reports saved", () => { var host = new Fake { Cancel = true }; var panel = new ShellServicePanel(new(), host); panel.RefreshAsync().GetAwaiter().GetResult(); panel.ApplyAsync().GetAwaiter().GetResult(); Check(panel.StatusText.Contains("取消") && !panel.ApplyAvailable && !panel.IsWorking); panel.Close(); });
        Test("failed inspection leaves update unavailable", () => { var host = new Fake { FailRead = true }; var panel = new ShellServicePanel(new(), host); panel.RefreshAsync().GetAwaiter().GetResult(); panel.ApplyAsync().GetAwaiter().GetResult(); Check(host.Writes == 0 && !panel.ApplyAvailable && panel.StatusText.Contains("无法")); panel.Close(); });
        Test("pending update displays staged state", () => { var host = new Fake { Pending = true }; var panel = new ShellServicePanel(new(), host); panel.RefreshAsync().GetAwaiter().GetResult(); Check(panel.StatusText.Contains("等待重启")); panel.Close(); });
        Test("disable is explicit and does not claim current engine stopped", () => { var host = new Fake(); var panel = new ShellServicePanel(new(), host); panel.RefreshAsync().GetAwaiter().GetResult(); panel.DisableAsync().GetAwaiter().GetResult(); Check(host.Disables == 1 && panel.StatusText.Contains("下次开机") && !panel.ApplyAvailable); panel.Close(); });
        Test("review comparison renders at standard and minimum size", () =>
        {
            foreach (var (w,h) in new[] { (740,690), (610,500) })
            {
                var panel = new ShellServicePanel(new ShellProfile(CompactTray:true, TranslucentTaskbar:true, FollowMaximizedTheme:true), new Fake());
                panel.RefreshAsync().GetAwaiter().GetResult();
                var content = (FrameworkElement)panel.Content; content.Measure(new Size(w,h)); content.Arrange(new Rect(0,0,w,h)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output,$"service-panel-{w}.png")); encoder.Save(file); panel.Close();
            }
        });
        var failed = checks.Count(c => !(bool)c.GetType().GetProperty("passed")!.GetValue(c)!);
        var report = JsonSerializer.Serialize(new { passed = checks.Count - failed, failed, realServiceWrites = 0, checks }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output,"report.json"),report); Console.WriteLine(report); return failed == 0 ? 0 : 1;
    }
    sealed class Fake : IShellServiceLayoutOperations
    {
        public int Reads, Writes, Disables;
        public bool Same, Cancel, FailRead, Pending;
        public Task<ShellServiceLayoutReview> ReviewAsync(ShellProfile p)
        { Reads++; if (FailRead) throw new IOException("Fixture inspection failure"); return Task.FromResult(new ShellServiceLayoutReview("fixture", "hash", p with { IconSize = 28 }, p, Pending, Same ? [] : ["图标大小"])); }
        public Task<string> StageAsync(ShellServiceLayoutReview review)
        { Writes++; if (Cancel) throw new System.ComponentModel.Win32Exception(1223); return Task.FromResult("下次重启生效，当前桌面保持原布局。"); }
        public Task DisableAsync(ShellServiceLayoutReview review) { Disables++; return Task.CompletedTask; }
    }
}
