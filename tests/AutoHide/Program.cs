using ClassicDesk;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

static class Program
{
    [STAThread] static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var results = new List<object>(); int passed = 0, failed = 0;
        void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
        void Throws(Action run) { try { run(); } catch { return; } throw new Exception("Expected rejection"); }
        void Test(string name, Action<Fake, TaskbarAutoHideController, string> run)
        {
            var path = Path.Combine(output, "fixtures", Guid.NewGuid().ToString("N")); var host = new Fake(); var controller = new TaskbarAutoHideController(host, path);
            try { run(host, controller, path); passed++; results.Add(new { name, passed = true }); }
            catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); }
        }
        Test("construction and inspection are read only; zero is legal", (h,c,p) => { Check(h.Reads == 0 && !Directory.Exists(p)); var r = c.Review(); Check(r.Current.Flags == 0 && r.CanApply && !r.CanRestore && h.Writes == 0 && !Directory.Exists(p)); });
        Test("apply preserves flags, persists intent before write, and restores", (h,c,p) =>
        {
            h.State = new("fixture", 0xA2); h.BeforeWrite = () => Check(File.ReadAllText(Path.Combine(p, "current.json")).Contains(h.Writes == 1 ? "apply-intent" : "restore-intent"));
            c.Apply(c.Review(), true); Check(h.State.Flags == 0xA3); var r = c.Review(); Check(!r.CanApply && r.CanRestore);
            c.Restore(r); Check(h.State.Flags == 0xA2 && c.Review().CanApply && h.Writes == 2);
        });
        Test("unknown apply cannot retry or restore", (h,c,p) => { h.ThrowAfterWrite = true; Throws(() => c.Apply(c.Review(), true)); var r=c.Review(); Check(r.Journal?.Phase == "apply-intent" && !r.CanApply && !r.CanRestore); Throws(() => c.Apply(r, true)); Throws(() => c.Restore(r)); Check(h.Writes == 1); });
        Test("write returning without effective change is not confirmed", (h,c,p) => { h.IgnoreWrite = true; Throws(() => c.Apply(c.Review(), true)); Check(c.Review().Journal?.Phase == "apply-intent" && !c.Review().CanRestore); });
        Test("unknown restore cannot repeat", (h,c,p) => { c.Apply(c.Review(), true); h.ThrowAfterWrite = true; Throws(() => c.Restore(c.Review())); var r=c.Review(); Check(r.Journal?.Phase == "restore-intent" && !r.CanApply && !r.CanRestore); Throws(() => c.Restore(r)); Check(h.Writes == 2); });
        Test("external flag or session changes block restoration", (h,c,p) => { c.Apply(c.Review(), true); h.State = h.State with { Flags = 3 }; Check(!c.Review().CanRestore); h.State = new("new-session",1); Check(!c.Review().CanRestore); Check(h.Writes == 1); });
        Test("stale ticket cannot overwrite changed taskbar", (h,c,p) => { var r=c.Review(); h.State = new("fixture", 2); Throws(() => c.Apply(r,true)); Check(h.Writes==0); });
        Test("failed reads prevent writes", (h,c,p) => { h.FailRead=true; Throws(() => c.Review()); Check(h.Writes==0 && !Directory.Exists(p)); });
        Test("corrupt journal is preserved and blocks writes", (h,c,p) => { Directory.CreateDirectory(p); var file=Path.Combine(p,"current.json"); File.WriteAllText(file,"broken"); Throws(() => c.Review()); Check(File.ReadAllText(file)=="broken" && h.Writes==0); });
        Test("oversize journal rejected without overwrite", (h,c,p) => { Directory.CreateDirectory(p); var file=Path.Combine(p,"current.json"); File.WriteAllText(file,new string('x',65537)); Throws(() => c.Review()); Check(new FileInfo(file).Length==65537 && h.Writes==0); });
        Test("unsupported journal schema is preserved", (h,c,p) => { Directory.CreateDirectory(p); var file=Path.Combine(p,"current.json"); var text=JsonSerializer.Serialize(new TaskbarAutoHideJournal(2,Guid.NewGuid(),"applied",new("fixture",0),1)); File.WriteAllText(file,text); Throws(() => c.Review()); Check(File.ReadAllText(file)==text && h.Writes==0); });
        Test("missing used journal does not silently create fresh history", (h,c,p) => { c.Apply(c.Review(),true); File.Move(Path.Combine(p,"current.json"),Path.Combine(p,"current.saved")); Throws(() => c.Review()); Check(h.Writes==1); });
        Test("journal revision change invalidates ticket", (h,c,p) => { c.Apply(c.Review(),true); var r=c.Review(); File.AppendAllText(Path.Combine(p,"current.json")," "); Throws(() => c.Restore(r)); Check(h.Writes==1); });
        Test("journal write failure prevents host mutation", (h,c,p) => { var r=c.Review(); Directory.CreateDirectory(p); Directory.CreateDirectory(Path.Combine(p,"current.json")); Throws(() => c.Apply(r,true)); Check(h.Writes==0); });
        Test("confirmation save failure retains intent and blocks retry", (h,c,p) =>
        {
            FileStream? held = null;
            try { h.BeforeWrite = () => held = new FileStream(Path.Combine(p,"current.json"),FileMode.Open,FileAccess.Read,FileShare.Read); Throws(() => c.Apply(c.Review(),true)); }
            finally { held?.Dispose(); }
            var r=c.Review(); Check(h.Writes==1 && r.Journal?.Phase=="apply-intent" && !r.CanApply && !r.CanRestore);
        });
        Test("restore confirmation save failure retains restore intent", (h,c,p) =>
        {
            c.Apply(c.Review(),true); FileStream? held=null;
            try { h.BeforeWrite=()=>held=new FileStream(Path.Combine(p,"current.json"),FileMode.Open,FileAccess.Read,FileShare.Read); Throws(()=>c.Restore(c.Review())); }
            finally { held?.Dispose(); }
            var r=c.Review(); Check(h.Writes==2 && r.Journal?.Phase=="restore-intent" && !r.CanApply && !r.CanRestore);
        });
        Test("no-op does not create an intent", (h,c,p) => { c.Apply(c.Review(),false); Check(h.Writes==0 && !File.Exists(Path.Combine(p,"current.json"))); });
        Test("restored history is retained before next transaction", (h,c,p) => { c.Apply(c.Review(),true); c.Restore(c.Review()); var id=c.Review().Journal!.Id; c.Apply(c.Review(),true); Check(File.Exists(Path.Combine(p,$"restored-{id:N}.json")) && c.Review().Journal!.Id!=id); });
        Test("UI construction and plan changes do not write", (h,c,p) =>
        {
            var panel = new TaskbarAutoHidePanel(c); Check(h.Reads==0 && !panel.ApplyAvailable); Await(panel.RefreshAsync()); panel.PlannedEnabled=true;
            Check(panel.ApplyAvailable && h.Writes==0 && !Directory.Exists(p));
            foreach (var (w, height) in new[] { (620,560), (520,440) })
            {
                var element=(FrameworkElement)panel.Content; element.Measure(new Size(w,height)); element.Arrange(new Rect(0,0,w,height)); element.UpdateLayout();
                var bitmap=new RenderTargetBitmap(w,height,96,96,PixelFormats.Pbgra32); bitmap.Render(element); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(output,$"autohide-{w}.png")); encoder.Save(file);
            }
            Await(panel.ApplyAsync()); Check(!panel.ApplyAvailable && h.Writes==1); Await(panel.ApplyAsync()); Check(h.Writes==1); Await(panel.RefreshAsync()); Check(panel.RestoreAvailable); Await(panel.RestoreAsync()); Check(h.Writes==2); panel.Close();
        });
        var report=JsonSerializer.Serialize(new { passed, failed, realHostWrites=0, results },new JsonSerializerOptions { WriteIndented=true }); File.WriteAllText(Path.Combine(output,"report.json"),report); Console.WriteLine(report); return failed==0 ? 0 : 1;
    }
    static void Await(Task task) { if(!task.IsCompleted) { var frame=new DispatcherFrame(); var dispatcher=Dispatcher.CurrentDispatcher; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue=false)); Dispatcher.PushFrame(frame); } task.GetAwaiter().GetResult(); }
    sealed class Fake : ITaskbarAutoHideHost
    {
        public TaskbarAutoHideState State = new("fixture",0); public int Reads,Writes; public bool FailRead,ThrowAfterWrite,IgnoreWrite; public Action? BeforeWrite;
        public TaskbarAutoHideState Read() { Reads++; if(FailRead) throw new IOException("fixture read failure"); return State; }
        public void Write(TaskbarAutoHideState expected,uint flags) { if(expected!=State) throw new IOException("fixture changed"); Writes++; BeforeWrite?.Invoke(); if(!IgnoreWrite) State=State with { Flags=flags }; if(ThrowAfterWrite) throw new IOException("fixture unknown outcome"); }
    }
}
