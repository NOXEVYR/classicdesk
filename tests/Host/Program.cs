using ClassicDesk;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
var checks = new List<object>();
var root = Path.Combine(AppContext.BaseDirectory, "isolated-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException or ArgumentException) { return; } throw new Exception("expected rejection"); }
void Test(string name, Action<Fixture> action) { if(args.Length > 1 && !System.Text.RegularExpressions.Regex.IsMatch(name,args[1])) return; var f = new Fixture(Path.Combine(root, checks.Count.ToString())); try { action(f); checks.Add(new { name, passed=true }); } catch(Exception e) { checks.Add(new { name, passed=false, error=e.ToString() }); } }
Test("package check verifies assets and creates no ownership directory", f => { var p=f.Check(); Assert(p.VerifiedAssets==17 && !Directory.Exists(Path.Combine(f.PackageRoot,".classicdesk-activation")) && f.Registry.Reads==0 && f.Process.Starts==0); });
Test("resume creates new process ownership and preserves all original settings", f => {
    var original=f.Originals(); var active=f.Activate(); var bytes=File.ReadAllBytes(Path.Combine(f.Logs,active.JournalId.ToString("N")+".json"));
    f.Process.Running=false; f.Environment.Revision="next-login";
    var resumed=f.Core.Resume(f.Core.CaptureResumeConfirmation(active.JournalId));
    Assert(resumed.State==ShellActivationState.Active && resumed.Error is null && f.Process.Starts==2 && f.Registry.Writes==1);
    var old=JsonSerializer.Deserialize<ActivationJournal>(bytes)!; var next=JsonSerializer.Deserialize<ActivationJournal>(File.ReadAllBytes(Path.Combine(f.Logs,active.JournalId.ToString("N")+".json")))!;
    Assert(JsonSerializer.Serialize(old.Steps)==JsonSerializer.Serialize(next.Steps) && old.Daemon!=next.Daemon);
    Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(active.JournalId)).State==ShellActivationState.Restored && f.Process.Stops==1);
    Assert(original.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
});
Test("resume ownership still rejects altered engine configuration", f => { var r=f.Activate(); f.Process.Running=false; File.AppendAllText(Path.Combine(f.PackageRoot,"AppData/settings.ini"),";changed",Encoding.Unicode); Reject(()=>f.Core.CaptureResumeConfirmation(r.JournalId)); Assert(f.Process.Starts==1&&f.Registry.Writes==1); });
Test("next login tolerates unrelated key timestamp and restores original DWORD", f => {
    var r=f.Activate(); var receipts=File.ReadAllText(Path.Combine(f.Logs,r.JournalId.ToString("N")+".json"));
    f.Process.Running=false; f.Environment.Revision="new-login"; f.Registry.State=f.Registry.State with {Revision="unrelated-key-write"};
    Assert(f.Core.Resume(f.Core.CaptureResumeConfirmation(r.JournalId)).State==ShellActivationState.Active && f.Registry.Writes==1);
    var after=JsonSerializer.Deserialize<ActivationJournal>(File.ReadAllText(Path.Combine(f.Logs,r.JournalId.ToString("N")+".json")))!;
    Assert(JsonSerializer.Serialize(after.Steps)==JsonSerializer.Serialize(JsonSerializer.Deserialize<ActivationJournal>(receipts)!.Steps));
    Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.Restored && f.Registry.State.Data=="0");
});
Test("changed alignment is preserved and blocks resume", f => { var r=f.Activate(); f.Process.Running=false; f.Registry.State=new(true,"0","user-change"); Reject(()=>f.Core.CaptureResumeConfirmation(r.JournalId)); Assert(f.Registry.State.Data=="0"&&f.Process.Starts==1); });
Test("last moment metadata revision is still a strict start gate", f => { var r=f.Activate(); f.Process.Running=false; var c=f.Core.CaptureResumeConfirmation(r.JournalId); f.Process.BeforeStart=()=>f.Registry.State=f.Registry.State with {Revision="late-key-edit"}; var result=f.Core.Resume(c); Assert(result.Error is not null&&f.Process.Starts==1&&f.Registry.Writes==1); });
Test("preboot identity differs from a reused current PID", f => { var now=new DateTime(2026,9,20,1,0,0,DateTimeKind.Utc); Assert(WindowsActivationProcesses.PredatesCurrentBoot(now.AddHours(-1).Ticks,now,600000)); Assert(!WindowsActivationProcesses.PredatesCurrentBoot(now.AddMinutes(-5).Ticks,now,600000)); Assert(!WindowsActivationProcesses.PredatesCurrentBoot(now.AddMinutes(-10).AddSeconds(-3).Ticks,now,600000)); });
Test("invalid uptime never grants process ownership", f => { Assert(!WindowsActivationProcesses.PredatesCurrentBoot(1,DateTime.UtcNow,-1)); Assert(!WindowsActivationProcesses.PredatesCurrentBoot(0,DateTime.UtcNow,100)); Assert(!WindowsActivationProcesses.PredatesCurrentBoot(1,DateTime.UtcNow,long.MaxValue)); });
ShellLoginRegistration Login(Fixture f) { var folder=Path.GetDirectoryName(f.Logs)!; var exe=Path.Combine(folder,"ClassicDesk.exe"); File.WriteAllText(exe,"isolated stub"); return new(Path.Combine(folder,"login"),Path.Combine(folder,"startup"),exe,f.PackageRoot); }
Test("no login opt in does not call shell or create files", f => { var l=Login(f); int calls=0; var r=l.RunOnceAsync(()=> { calls++; return Task.FromResult("unexpected"); },()=> { calls++; return Task.CompletedTask; }).GetAwaiter().GetResult(); Assert(r==0&&calls==0&&!File.Exists(l.EntryPath)&&!File.Exists(l.ReportPath)); });
Test("owned login launcher can enable twice and disable exactly", f => { var l=Login(f); l.SetEnabled(true); var bytes=File.ReadAllBytes(l.EntryPath); l.SetEnabled(true); Assert(l.Enabled&&File.ReadAllBytes(l.EntryPath).SequenceEqual(bytes)); l.SetEnabled(false); Assert(!l.Enabled&&!File.Exists(l.EntryPath)); });
Test("foreign startup file is not overwritten", f => { var l=Login(f); Directory.CreateDirectory(Path.GetDirectoryName(l.EntryPath)!); File.WriteAllText(l.EntryPath,"foreign"); Reject(()=>l.SetEnabled(true)); Assert(File.ReadAllText(l.EntryPath)=="foreign"); });
Test("changed startup file is neither overwritten nor deleted", f => { var l=Login(f); l.SetEnabled(true); File.AppendAllText(l.EntryPath,"changed"); var bytes=File.ReadAllBytes(l.EntryPath); Reject(()=>l.SetEnabled(false)); Assert(File.ReadAllBytes(l.EntryPath).SequenceEqual(bytes)); });
Test("another installation cannot take login ownership", f => { var l=Login(f); l.SetEnabled(true); var folder=Path.GetDirectoryName(f.Logs)!; var other=new ShellLoginRegistration(Path.Combine(folder,"login"),Path.Combine(folder,"startup"),Path.Combine(folder,"other.exe"),f.PackageRoot); Reject(()=>other.SetEnabled(false)); Assert(l.Enabled); });
Test("missing launcher means login is not enabled", f => { var l=Login(f); l.SetEnabled(true); File.Delete(l.EntryPath); Assert(!l.Enabled); });
Test("login checks once and reports completion", f => { var l=Login(f); l.SetEnabled(true); int calls=0; var code=l.RunOnceAsync(()=> { calls++; return Task.FromResult("existing transaction"); },()=>Task.CompletedTask).GetAwaiter().GetResult(); Assert(code==0&&calls==1&&l.LastResult().Contains("existing transaction")); });
Test("failed login does not retry or silently report success", f => { var l=Login(f); l.SetEnabled(true); int calls=0; var code=l.RunOnceAsync(()=> { calls++; throw new IOException("external change"); },()=>Task.CompletedTask).GetAwaiter().GetResult(); Assert(code==1&&calls==1&&l.LastResult().Contains("未继续运行")); });
Test("unstable shell never reaches resume", f => { var l=Login(f); l.SetEnabled(true); int calls=0; var code=l.RunOnceAsync(()=> { calls++; return Task.FromResult("unexpected"); },()=>throw new IOException("shell unstable")).GetAwaiter().GetResult(); Assert(code==1&&calls==0); });
Test("disabled login ignores an old successful report", f => { var l=Login(f); l.SetEnabled(true); l.Report("已确认运行","old"); l.SetEnabled(false); int calls=0; Assert(l.RunOnceAsync(()=> { calls++; return Task.FromResult("unexpected"); },()=>Task.CompletedTask).GetAwaiter().GetResult()==0&&calls==0); });
Test("capture is read only and desired INI preserves BOM", f => { var c=f.Capture(); Assert(c.Preparation.Changes.Length==7 && !Directory.Exists(f.Logs)); foreach(var change in c.Preparation.Changes.Skip(1)) { var b=Convert.FromBase64String(change.DesiredData); Assert(b[0]==255 && b[1]==254); } });
Test("real file writes fake registry and process activate then restore exact bytes", f => { var originals=f.Originals(); var r=f.Activate(); Assert(r.State==ShellActivationState.Active && f.Registry.Writes==1 && f.Process.Starts==1); Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.Restored); Assert(f.Registry.State.Data=="0" && f.Process.Stops==1); Assert(originals.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value))); });
Test("owned modules still unloading after daemon exit do not interrupt restore", f => { var original=f.Originals(); f.Environment.RequireStoppedIdentity=true; var active=f.Activate(); Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(active.JournalId)).State==ShellActivationState.Restored); Assert(f.Process.Stops==1&&original.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value))); });
Test("recreated host restores saved ownership after process restart", f => { var r=f.Activate(); var host=new WindowsShellActivationHost(f.Registry,f.Environment,f.Process); var core=new ShellActivationCoordinator(host,new FileActivationJournalStore(f.Logs)); Assert(core.Restore(core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.Restored); });
Test("three complete activation restore cycles ignore old stopped records", f => { for(var i=0;i<3;i++) { var r=f.Activate(); Assert(r.State==ShellActivationState.Active); Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.Restored); } Assert(f.Process.Starts==3&&f.Process.Stops==3&&f.Registry.Writes==6); });
Test("expanded profile maps all exact mod parameters", f => { f.Profile=new(StartOnLeft:true,IconSize:28,TaskbarHeight:56,ClassicRibbon:false,ClassicContextMenu:true,TaskbarButtonWidth:60,SmallIconSize:20,SmallTaskbarButtonWidth:40,OtherSystemButtonsOnLeft:false,StartMenuOnLeft:false,SearchMenuOnLeft:true,ClassicMenuWithCtrl:false,UseClassicNavigationBar:true); var c=f.Capture(); string Text(ActivationTarget t)=>Encoding.Unicode.GetString(Convert.FromBase64String(c.Preparation.Changes.Single(x=>x.Target==t).DesiredData)); Assert(Text(ActivationTarget.IconSizeMod).Contains("TaskbarButtonWidth=60")&&Text(ActivationTarget.IconSizeMod).Contains("IconSizeSmall=20")); Assert(Text(ActivationTarget.StartButtonMod).Contains("otherSystemButtonsOnTheLeft=0")&&Text(ActivationTarget.StartButtonMod).Contains("searchMenuPositionInAllCases=1")); Assert(Text(ActivationTarget.ExplorerFrameMod).Contains("explorerStyle=classicNavigationBar")&&Text(ActivationTarget.ContextMenuMod).Contains("overrideWithCtrl=0")); });
Test("off choices produce Disabled1 while both safety modes explicitly enabled", f => { f.Profile=new(StartOnLeft:false,ClassicRibbon:false,ClassicContextMenu:false); var c=f.Capture(); foreach(var t in new[]{ActivationTarget.StartButtonMod,ActivationTarget.ExplorerFrameMod,ActivationTarget.ContextMenuMod}) Assert(Encoding.Unicode.GetString(Convert.FromBase64String(c.Preparation.Changes.Single(x=>x.Target==t).DesiredData)).Contains("Disabled=1")); foreach(var t in new[]{ActivationTarget.MainSettings,ActivationTarget.EngineSettings}) Assert(Encoding.Unicode.GetString(Convert.FromBase64String(c.Preparation.Changes.Single(x=>x.Target==t).DesiredData)).Contains("SafeMode=0")); });
Test("deterministic capture does not use changing wall clock timestamp", f => { var first=f.Capture(); var second=f.Capture(); Assert(first.Fingerprint==second.Fingerprint); });
Test("manifest hash mismatch rejected before host reads", f => { Reject(()=>WindowsShellActivationHost.CheckPackage(f.PackageRoot,new string('F',64))); Assert(f.Registry.Reads==0); });
Test("changed binary rejected", f => { File.AppendAllText(Path.Combine(f.PackageRoot,"windhawk.exe"),"external"); Reject(()=>f.Check()); });
Test("same-size binary hash corruption rejected", f => { var p=Path.Combine(f.PackageRoot,"windhawk.exe"); var b=File.ReadAllBytes(p); b[0]^=1; File.WriteAllBytes(p,b); Reject(()=>f.Check()); });
Test("missing dependency rejected", f => { File.Delete(Path.Combine(f.PackageRoot,"AppData/Engine/Mods/64/libunwind.whl")); Reject(()=>f.Check()); });
Test("extra unknown mod INI blocks", f => { File.WriteAllText(Path.Combine(f.PackageRoot,"AppData/Engine/Mods/foreign.ini"),"[Mod]\nDisabled=0"); Reject(()=>f.Check()); });
Test("extra DLL in runtime root blocks", f => { File.WriteAllText(Path.Combine(f.PackageRoot,"foreign.dll"),"foreign"); Reject(()=>f.Check()); });
Test("extra library under mod runtime blocks", f => { File.WriteAllText(Path.Combine(f.PackageRoot,"AppData/Engine/Mods/64/foreign.whl"),"foreign"); Reject(()=>f.Check()); });
Test("duplicate manifest is ambiguous and rejected", f => { File.Copy(Path.Combine(f.PackageRoot,"runtime-assets-manifest.json"),Path.Combine(f.PackageRoot,"runtime-manifest.json")); Reject(()=>f.Check()); });
Test("asset traversal rejected", f => { f.ChangeManifest(doc=>doc["assets"]![0]!["path"]="../foreign.exe"); Reject(()=>f.Check()); });
Test("duplicate asset path rejected", f => { f.ChangeManifest(doc=>doc["assets"]!.AsArray().Add(doc["assets"]![0]!.DeepClone())); Reject(()=>f.Check()); });
Test("unknown engine version rejected", f => { f.ChangeManifest(doc=>doc["engineVersion"]="9.9"); Reject(()=>f.Check()); });
Test("external storage root rejected", f => { var p=Path.Combine(f.PackageRoot,"windhawk.ini"); File.WriteAllText(p,File.ReadAllText(p).Replace("AppDataPath=AppData","AppDataPath=C:\\outside"),Encoding.Unicode); Reject(()=>f.Check()); });
Test("duplicate INI storage key rejected", f => { File.AppendAllText(Path.Combine(f.PackageRoot,"windhawk.ini"),"AppDataPath=AppData\r\n",Encoding.Unicode); Reject(()=>f.Check()); });
Test("preconfirmation SAB conflict does no host writes", f => { f.Environment.Sab=ActivationPresence.Present; Reject(()=>f.Activate()); Assert(f.Registry.Writes==0&&f.Process.Starts==0&&!Directory.Exists(f.Logs)); });
Test("unknown environment conflict rejects", f => { f.Environment.Sab=ActivationPresence.Unknown; Reject(()=>f.Activate()); Assert(f.Registry.Writes==0); });
Test("unknown other engine rejects", f => { f.Environment.Other=ActivationPresence.Unknown; Reject(()=>f.Activate()); Assert(f.Process.Starts==0); });
Test("file revision drift prevents stale activation", f => { var c=f.Capture(); File.AppendAllText(Path.Combine(f.PackageRoot,"AppData/settings.ini"),";external\r\n",Encoding.Unicode); Reject(()=>f.Core.Activate(c)); Assert(f.Registry.Writes==0); });
Test("registry revision drift prevents stale activation", f => { var c=f.Capture(); f.Registry.State=f.Registry.State with {Revision="external"}; Reject(()=>f.Core.Activate(c)); Assert(f.Process.Starts==0); });
Test("writes require explicit package lease", f => { var c=f.Capture(); Reject(()=>f.Host.Write(c.Package,c.Preparation.Changes[0],c.Preparation.Guards)); Assert(f.Registry.Writes==0); });
Test("package file lock excludes another host instance", f => { var p=f.Check().Package; using var lease=f.Host.AcquirePackageLease(p); var second=new WindowsShellActivationHost(f.Registry,f.Environment,f.Process); Reject(()=>second.AcquirePackageLease(p)); });
Test("midapply registry failure remains unknown without launching", f => { f.Registry.ThrowAfterWrite=true; var r=f.Activate(); Assert(r.State==ShellActivationState.ManualReview&&f.Process.Starts==0); });
Test("definite start rejection restores files and registry", f => { var originals=f.Originals(); f.Process.Behavior="reject"; var r=f.Activate(); Assert(r.State==ShellActivationState.RolledBack&&f.Registry.State.Data=="0"); Assert(originals.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value))); });
Test("unknown launch does not stop or compensate", f => { f.Process.Behavior="unknown"; var r=f.Activate(); Assert(r.State==ShellActivationState.ManualReview&&f.Registry.State.Data=="1"&&f.Process.Stops==0); });
Test("foreign process identity never stopped", f => { var r=f.Activate(); f.Process.Foreign=true; var result=f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)); Assert(result.State==ShellActivationState.ManualReview&&f.Process.Stops==0); });
Test("unknown stop preserves enabled config for manual review", f => { var r=f.Activate(); f.Process.StopUnknown=true; Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.ManualReview); Assert(f.Registry.State.Data=="1"); });
Test("external config after activation rejects before process stop", f => { var r=f.Activate(); File.AppendAllText(Path.Combine(f.PackageRoot,"AppData/settings.ini"),";external\r\n",Encoding.Unicode); Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.ManualReview&&f.Process.Stops==0); });
Test("ownership token tamper prevents overwrite", f => { var r=f.Activate(); var path=Directory.GetFiles(Path.Combine(f.PackageRoot,".classicdesk-activation"),"write-*.json").First(); var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; node["Token"]=Guid.NewGuid().ToString("N"); File.WriteAllText(path,node.ToJsonString()); Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.ManualReview); });
Test("process ownership tamper prevents any stop", f => { var r=f.Activate(); var path=Directory.GetFiles(Path.Combine(f.PackageRoot,".classicdesk-activation"),"daemon-*.json").Single(); var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; node["Identity"]!["ProcessId"]=999; File.WriteAllText(path,node.ToJsonString()); Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.ManualReview&&f.Process.Stops==0); });
Test("final commit gate rejects changed earlier module before actual process start", f => { f.Process.BeforeStart=()=> { var p=Path.Combine(f.PackageRoot,"AppData/Engine/Mods/taskbar-start-button-position.ini"); File.WriteAllText(p,File.ReadAllText(p).Replace("Include=explorer.exe","Include=foreign.exe"),Encoding.Unicode); }; var r=f.Activate(); Assert(r.State==ShellActivationState.ManualReview&&f.Process.Starts==0&&f.Registry.State.Data=="0"); });
Test("final commit gate rechecks TaskbarAl", f => { f.Process.BeforeStart=()=>f.Registry.State=f.Registry.State with {Revision="foreign-registry-before-start"}; var r=f.Activate(); Assert(r.State==ShellActivationState.ManualReview&&f.Process.Starts==0); });
Test("complete absent module observation allows installed but inactive StartAllBack", f => Assert(WindowsActivationEnvironment.ClassifyStartAllBack("not-detected")==ActivationPresence.Absent));
Test("loaded StartAllBack blocks", f => Assert(WindowsActivationEnvironment.ClassifyStartAllBack("detected")==ActivationPresence.Present));
Test("incomplete StartAllBack observation remains unknown", f => Assert(WindowsActivationEnvironment.ClassifyStartAllBack("unknown")==ActivationPresence.Unknown));
Test("unrecognized module observation never grants absence", f => Assert(WindowsActivationEnvironment.ClassifyStartAllBack("")==ActivationPresence.Unknown));
Test("owned files can restore after Explorer session changed and daemon exited", f => { var r=f.Activate(); f.Process.Running=false; f.Environment.Revision="next-session"; Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.Restored&&f.Process.Stops==0&&f.Registry.State.Data=="0"); });
Test("Explorer changes again after restore confirmation blocks all changes", f => { var r=f.Activate(); f.Environment.Revision="new-session"; var c=f.Core.CaptureRestoreConfirmation(r.JournalId); f.Environment.Revision="newest-session"; Assert(f.Core.Restore(c).State==ShellActivationState.ManualReview&&f.Process.Stops==0&&f.Registry.State.Data=="1"); });
for (var mask = 0; mask < 16; mask++)
{
    var selection = new ShellFeatureSelection((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
    Test("independent feature mapping " + mask, f => {
        f.Profile = selection.Apply(new ShellProfile()); var c = f.Capture();
        var expected = new Dictionary<ActivationTarget, bool> { [ActivationTarget.StartButtonMod] = selection.Layout, [ActivationTarget.IconSizeMod] = selection.Sizing, [ActivationTarget.ExplorerFrameMod] = selection.Explorer, [ActivationTarget.ContextMenuMod] = selection.ContextMenu };
        foreach (var pair in expected) Assert(Encoding.Unicode.GetString(Convert.FromBase64String(c.Preparation.Changes.Single(x => x.Target == pair.Key).DesiredData)).Contains("Disabled=" + (pair.Value ? "0" : "1") + "\r\n"));
        var alignment = c.Preparation.Changes.Single(x => x.Target == ActivationTarget.TaskbarAlignment);
        Assert(alignment.DesiredData == (selection.Layout ? "1" : "0") && f.Registry.Writes == 0 && f.Process.Starts == 0);
    });
}
foreach (var absent in new[] { false, true })
    Test("menu-only activation and new-session restore preserves alignment " + absent, f => {
        f.Profile = new ShellFeatureSelection(false, false, false, true).Apply(new());
        if (absent) f.Registry.State = new(false, "", "original-absent");
        var registry = f.Registry.State; var originals = f.Originals(); var r = f.Activate();
        Assert(r.State == ShellActivationState.Active && f.Registry.State == registry && f.Registry.Writes == 0);
        var journal = JsonSerializer.Deserialize<ActivationJournal>(File.ReadAllText(Path.Combine(f.Logs, r.JournalId.ToString("N") + ".json")))!;
        Assert(journal.Profile.SkipTaskbarLayout && journal.Profile.SkipTaskbarSizing && journal.Steps.Single(s => s.Change.Target == ActivationTarget.TaskbarAlignment).WritePhase == "unchanged");
        var core = new ShellActivationCoordinator(new WindowsShellActivationHost(f.Registry, f.Environment, f.Process), new FileActivationJournalStore(f.Logs));
        Assert(core.Restore(core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.Restored);
        Assert(f.Registry.State == registry && f.Registry.Writes == 0 && originals.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
    });
Test("layout-only activation keeps size and Explorer modules disabled then restores", f => {
    f.Profile = new ShellFeatureSelection(true, false, false, false).Apply(new()); var originals = f.Originals();
    var r = f.Activate(); Assert(r.State == ShellActivationState.Active && f.Registry.State.Data == "1");
    foreach (var id in new[] { "taskbar-icon-size", "explorer-frame-classic", "explorer-context-menu-classic" }) Assert(File.ReadAllText(Path.Combine(f.PackageRoot, "AppData/Engine/Mods", id + ".ini")).Contains("Disabled=1\r\n"));
    Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.Restored && f.Registry.State.Data == "0");
    Assert(originals.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
});
Test("menu-only final alignment drift still blocks launch", f => {
    f.Profile = new ShellFeatureSelection(false, false, false, true).Apply(new());
    f.Process.BeforeStart = () => f.Registry.State = f.Registry.State with { Revision = "external-alignment" };
    var r = f.Activate(); Assert(r.State == ShellActivationState.ManualReview && f.Process.Starts == 0 && f.Registry.Writes == 0);
});
foreach (var original in new[] { "0", "1", "" })
    Test("all-left real files restore exact alignment and bytes " + original, f => {
        f.Profile = new(LeftAlignedApps: true); f.Registry.State = new(original != "", original, "original-all-left");
        var originals = f.Originals(); var capture = f.Capture();
        var alignment = capture.Preparation.Changes.Single(c => c.Target == ActivationTarget.TaskbarAlignment);
        Assert(alignment.DesiredExists && alignment.DesiredData == "0" && f.Registry.Writes == 0);
        var start = capture.Preparation.Changes.Single(c => c.Target == ActivationTarget.StartButtonMod);
        Assert(Encoding.Unicode.GetString(Convert.FromBase64String(start.DesiredData)).Contains("Disabled=1\r\n"));
        var result = f.Core.Activate(capture);
        Assert(result.State == ShellActivationState.Active && f.Registry.State.Exists && f.Registry.State.Data == "0");
        Assert(File.ReadAllText(Path.Combine(f.PackageRoot, "AppData/Engine/Mods/taskbar-start-button-position.ini")).Contains("Disabled=1\r\n"));
        var core = new ShellActivationCoordinator(new WindowsShellActivationHost(f.Registry, f.Environment, f.Process), new FileActivationJournalStore(f.Logs));
        Assert(core.Restore(core.CaptureRestoreConfirmation(result.JournalId)).State == ShellActivationState.Restored);
        Assert(f.Registry.State.Exists == (original != "") && f.Registry.State.Data == original);
        Assert(f.Registry.Writes == (original == "0" ? 0 : 2) && originals.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
    });
foreach (var original in new[] { "0", "1", "" })
    Test("skipped all-left leaves alignment and revision untouched " + original, f => {
        f.Profile = new(LeftAlignedApps: true, SkipTaskbarLayout: true);
        f.Registry.State = new(original != "", original, "untouched-skip"); var registry = f.Registry.State; var originals = f.Originals();
        var result = f.Activate(); Assert(result.State == ShellActivationState.Active && f.Registry.State == registry && f.Registry.Writes == 0);
        Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(result.JournalId)).State == ShellActivationState.Restored);
        Assert(f.Registry.State == registry && f.Registry.Writes == 0 && originals.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
    });
Test("all-left refuses external alignment drift before resume", f => {
    f.Profile = new(LeftAlignedApps: true); var active = f.Activate(); f.Process.Running = false;
    f.Registry.State = new(true, "1", "external-change"); var writes = f.Registry.Writes;
    Reject(() => f.Core.CaptureResumeConfirmation(active.JournalId));
    Assert(f.Registry.State.Data == "1" && f.Registry.Writes == writes && f.Process.Starts == 1);
});
Test("all-left field is backward compatible and round trips through profile files", f => {
    var legacy = JsonSerializer.Serialize(new ShellProfile()); Assert(!legacy.Contains("LeftAlignedApps", StringComparison.Ordinal));
    var old = JsonSerializer.Deserialize<ShellProfile>(legacy)!; Assert(!old.LeftAlignedApps);
    var profile = old with { LeftAlignedApps = true }; var file = Path.Combine(Path.GetDirectoryName(f.Logs)!, "all-left-profile.json");
    ShellProfileFile.Save(file, profile, "missing");
    Assert(ShellProfileFile.Read(file) == profile && File.ReadAllText(file).Contains("LeftAlignedApps", StringComparison.Ordinal));
    Assert(JsonSerializer.Serialize(JsonSerializer.Deserialize<ShellProfile>(legacy)) == legacy);
});
Test("legacy profiles omit default scope fields to retain journal fingerprints", f => {
    var text = JsonSerializer.Serialize(new ShellProfile()); Assert(!text.Contains("SkipTaskbar"));
    var old = JsonSerializer.Deserialize<ShellProfile>(text)!; Assert(!old.SkipTaskbarLayout && !old.SkipTaskbarSizing);
    var modified = old with { SkipTaskbarLayout = true }; Assert(JsonSerializer.Deserialize<ShellProfile>(JsonSerializer.Serialize(modified)) == modified);
});
Test("legacy runtime rejects requested styling before any writes", f => {
    f.Profile = f.Profile with { TranslucentTaskbar = true }; Reject(()=>f.Capture()); Assert(f.Registry.Writes==0&&f.Process.Starts==0);
});
foreach (var compact in new[]{false,true}) foreach(var translucent in new[]{false,true})
Test("optional style owns and restores eighth target " + compact + translucent, f => {
    f.AddStyle(); f.Profile = f.Profile with { CompactTray=compact, TranslucentTaskbar=translucent };
    var capture=f.Capture(); Assert(capture.Preparation.Changes.Length==8);
    var style=capture.Preparation.Changes.Single(c=>c.Target==ActivationTarget.TaskbarStyleMod);
    var text=Encoding.Unicode.GetString(Convert.FromBase64String(style.DesiredData));
    Assert(text.Contains("Disabled="+((compact||translucent)?"0":"1")) && text.Contains("Rectangle#BackgroundFill")==translucent && text.Contains("NotifyItemIcon")==compact);
    var r=f.Core.Activate(capture); Assert(r.State==ShellActivationState.Active);
    f.Process.Running=false; var resumed=f.Core.Resume(f.Core.CaptureResumeConfirmation(r.JournalId)); Assert(resumed.State==ShellActivationState.Active);
    Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.Restored);
    Assert(!File.Exists(Path.Combine(f.PackageRoot,"AppData/Engine/Mods/windows-11-taskbar-styler.ini")));
});
Test("style file without reviewed asset is rejected", f => {
    File.WriteAllText(Path.Combine(f.PackageRoot,"AppData/Engine/Mods/windows-11-taskbar-styler.ini"),"[Mod]\nDisabled=1"); Reject(()=>f.Check());
});
Test("disabled removed launcher permits installation migration", f => {
    var login=Login(f); login.SetEnabled(true); login.SetEnabled(false);
    var folder=Path.GetDirectoryName(f.Logs)!; var exe=Path.Combine(folder,"new.exe"); File.WriteAllText(exe,"stub");
    var next=new ShellLoginRegistration(Path.Combine(folder,"login"),Path.Combine(folder,"startup"),exe,f.PackageRoot+"-new");
    next.SetEnabled(true); Assert(next.Enabled); Reject(()=>login.SetEnabled(false));
});
Test("legacy serialization omits style defaults and scopes remove styles", f => {
    var text=JsonSerializer.Serialize(new ShellProfile()); Assert(!text.Contains("CompactTray")&&!text.Contains("TranslucentTaskbar"));
    var draft=new ShellProfile(CompactTray:true,TranslucentTaskbar:true); var scoped=new ShellFeatureSelection(false).Apply(draft);
    Assert(!scoped.CompactTray&&!scoped.TranslucentTaskbar&&draft.CompactTray&&draft.TranslucentTaskbar);
});
foreach(var translucent in new[]{false,true})
Test("native background ninth target apply resume restore " + translucent, f => {
 f.AddStyle();f.AddBackdrop();f.Profile=f.Profile with {TranslucentTaskbar=translucent,CompactTray=true};
 var capture=f.Capture();Assert(capture.Preparation.Changes.Length==9);
 var text=Encoding.Unicode.GetString(Convert.FromBase64String(capture.Preparation.Changes.Single(c=>c.Target==ActivationTarget.TaskbarBackdropMod).DesiredData));
 Assert(text.Contains("Disabled="+(translucent?"0":"1"))&&text.Contains("onlyWhenMaximized=0")&&text.Contains("color.transparency=0"));
 var result=f.Core.Activate(capture);Assert(result.State==ShellActivationState.Active);f.Process.Running=false;
 Assert(f.Core.Resume(f.Core.CaptureResumeConfirmation(result.JournalId)).State==ShellActivationState.Active);
 Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(result.JournalId)).State==ShellActivationState.Restored);
 Assert(!File.Exists(Path.Combine(f.PackageRoot,"AppData/Engine/Mods/taskbar-background-helper.ini")));
});
Test("native background without style asset is rejected", f=>{f.AddBackdrop();Reject(()=>f.Check());});
Test("adaptive missing asset rejects before writes", f => {
 f.AddStyle();f.AddBackdrop();f.Profile=f.Profile with {TranslucentTaskbar=true,FollowMaximizedTheme=true};
 Reject(()=>f.Capture());Assert(f.Registry.Writes==0&&f.Process.Starts==0&&!Directory.Exists(f.Logs));
});
Test("adaptive requires static fallback", f => {f.AddStyle();f.AddAdaptive();Reject(()=>f.Check());});
Test("adaptive apply resume restore shares ninth target and preserves binaries", f => {
 f.AddStyle();f.AddBackdrop();f.AddAdaptive();f.Profile=f.Profile with {TranslucentTaskbar=true,FollowMaximizedTheme=true,CompactTray=true};
 var original=f.Originals();var capture=f.Capture();Assert(capture.Preparation.Changes.Length==9);
 var text=Encoding.Unicode.GetString(Convert.FromBase64String(capture.Preparation.Changes.Single(c=>c.Target==ActivationTarget.TaskbarBackdropMod).DesiredData));
 Assert(text.Contains("LibraryFileName=taskbar-background-helper_1.2-classicdesk.1.dll")&&text.Contains("followMaximizedWindow=1")&&!text.Contains("onlyWhenMaximized"));
 var active=f.Core.Activate(capture);Assert(active.State==ShellActivationState.Active);f.Process.Running=false;
 Assert(f.Core.Resume(f.Core.CaptureResumeConfirmation(active.JournalId)).State==ShellActivationState.Active);
 Assert(f.Core.Restore(f.Core.CaptureRestoreConfirmation(active.JournalId)).State==ShellActivationState.Restored);
 Assert(original.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
 Assert(!File.Exists(Path.Combine(f.PackageRoot,"AppData/Engine/Mods/taskbar-background-helper.ini"))&&f.Check().VerifiedAssets==20);
});
Test("adaptive optional serialization and feature dependency", f => {
 Assert(!JsonSerializer.Serialize(new ShellProfile()).Contains("FollowMaximizedTheme"));
 Reject(()=>new ShellProfile(FollowMaximizedTheme:true).Validate());
 var draft=new ShellProfile(TranslucentTaskbar:true,FollowMaximizedTheme:true);draft.Validate();
 Assert(JsonSerializer.Deserialize<ShellProfile>(JsonSerializer.Serialize(draft))==draft);
 var scoped=new ShellFeatureSelection(false).Apply(draft);Assert(!scoped.FollowMaximizedTheme&&!scoped.TranslucentTaskbar);
});
var failures=checks.Count(x=>!(bool)x.GetType().GetProperty("passed")!.GetValue(x)!);
var source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/ClassicDesk/ShellActivationHost.cs"));
var report=new { passed=checks.Count-failures,failed=failures,filter=args.Length>1?args[1]:null,realRegistryWrites=0,realProcessStarts=0,realProcessStops=0,realWindowInteractions=0,testMode="real isolated package files; injected fake registry/environment/process only",sourceSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),checks };
var output=Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})); Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})); return failures==0?0:1;
sealed class Fixture {
 public string PackageRoot,Logs; public ShellProfile Profile=new(); public FakeAlignment Registry=new(); public FakeProcesses Process=new(); public FakeEnvironment Environment; public WindowsShellActivationHost Host; public ShellActivationCoordinator Core;
 public Fixture(string root) { Directory.CreateDirectory(root); PackageRoot=Path.Combine(root,"package"); Logs=Path.Combine(root,"journals"); ShellBackendConfig.CreateDisabledConfiguration(PackageRoot,new()); string[] assets=["windhawk.exe","windhawk-x64-helper.exe","Engine/1.7.3/32/windhawk.dll","Engine/1.7.3/64/windhawk.dll","Engine/1.7.3/32/msdia140_windhawk.dll","Engine/1.7.3/32/symsrv_windhawk.dll","Engine/1.7.3/32/symsrv.yes","Engine/1.7.3/64/msdia140_windhawk.dll","Engine/1.7.3/64/symsrv_windhawk.dll","Engine/1.7.3/64/symsrv.yes","AppData/Engine/Mods/64/libc++.whl","AppData/Engine/Mods/64/libunwind.whl","AppData/Engine/Mods/64/windhawk-mod-shim.dll","AppData/Engine/Mods/64/taskbar-start-button-position_1.3.2.dll","AppData/Engine/Mods/64/taskbar-icon-size_1.3.10.dll","AppData/Engine/Mods/64/explorer-frame-classic_1.0.8.dll","AppData/Engine/Mods/64/explorer-context-menu-classic_1.0.2.dll"]; var list=new List<object>(); foreach(var a in assets) { var p=Path.Combine(PackageRoot,a); Directory.CreateDirectory(Path.GetDirectoryName(p)!); var bytes=Encoding.UTF8.GetBytes("NON-EXECUTABLE-FAKE-FIXTURE:"+a); File.WriteAllBytes(p,bytes); list.Add(new {path=a,bytes=bytes.Length,sha256=Convert.ToHexString(SHA256.HashData(bytes))}); } File.WriteAllText(Path.Combine(PackageRoot,"runtime-assets-manifest.json"),JsonSerializer.Serialize(new {engineVersion="1.7.3",assets=list})); Environment=new(Process); Host=new(Registry,Environment,Process); Core=new(Host,new FileActivationJournalStore(Logs)); }
 public void AddStyle() {
    var path=Path.Combine(PackageRoot,WindowsShellActivationHost.StyleAsset); var bytes=Encoding.UTF8.GetBytes("NON-EXECUTABLE-STYLE-FIXTURE"); File.WriteAllBytes(path,bytes);
    ChangeManifest(doc=>doc["assets"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new{path=WindowsShellActivationHost.StyleAsset,bytes=bytes.Length,sha256=Convert.ToHexString(SHA256.HashData(bytes))}))));
 }
 public void AddBackdrop() {
    var path=Path.Combine(PackageRoot,WindowsShellActivationHost.BackdropAsset); var bytes=Encoding.UTF8.GetBytes("NON-EXECUTABLE-BACKDROP-FIXTURE"); File.WriteAllBytes(path,bytes);
    ChangeManifest(doc=>doc["assets"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new{path=WindowsShellActivationHost.BackdropAsset,bytes=bytes.Length,sha256=Convert.ToHexString(SHA256.HashData(bytes))}))));
 }
 public void AddAdaptive() {
    var path=Path.Combine(PackageRoot,WindowsShellActivationHost.AdaptiveBackdropAsset);var bytes=Encoding.UTF8.GetBytes("NON-EXECUTABLE-ADAPTIVE-FIXTURE");File.WriteAllBytes(path,bytes);
    ChangeManifest(doc=>doc["assets"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new{path=WindowsShellActivationHost.AdaptiveBackdropAsset,bytes=bytes.Length,sha256=Convert.ToHexString(SHA256.HashData(bytes))}))));
 }
 public ActivationPackageCheck Check()=>WindowsShellActivationHost.CheckPackage(PackageRoot);
 public ActivationConfirmation Capture()=>Core.CaptureConfirmation(Check().Package,Profile);
 public ActivationResult Activate()=>Core.Activate(Capture());
 public Dictionary<string,byte[]> Originals()=>Directory.GetFiles(PackageRoot,"*.ini",SearchOption.AllDirectories).ToDictionary(x=>x,File.ReadAllBytes);

 public void ChangeManifest(Action<System.Text.Json.Nodes.JsonObject> edit) { var path=Path.Combine(PackageRoot,"runtime-assets-manifest.json"); var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject(); edit(node); File.WriteAllText(path,node.ToJsonString()); }
}
sealed class FakeAlignment:IActivationAlignment { public ActivationItemState State=new(true,"0","original"); public int Reads,Writes; public bool ThrowAfterWrite; public ActivationItemState Read(){Reads++;return State;} public ActivationItemState CompareExchange(ActivationItemState expected,bool exists,string data){if(State!=expected)throw new IOException("fake CAS");Writes++;State=new(exists,data,"fake-"+Writes);if(ThrowAfterWrite)throw new IOException("unknown fake write");return State;} }
sealed class FakeEnvironment(FakeProcesses process):IActivationEnvironment { public string Revision="fake-windows11-explorer-session"; public ActivationPresence Sab,Other; public bool RequireStoppedIdentity; public ActivationEnvironmentObservation Inspect(ActivationDaemonIdentity? allowedDaemon)=>new(Revision,Sab,Other!=ActivationPresence.Absent?Other:(process.Running || RequireStoppedIdentity && process.Identity is not null)&&allowedDaemon!=process.Identity?ActivationPresence.Present:ActivationPresence.Absent); }
sealed class FakeProcesses:IActivationProcesses { public int Starts,Stops; public Action? BeforeStart; public bool Running,Foreign,StopUnknown; public string Behavior="normal"; public ActivationDaemonIdentity? Identity; public ActivationStartResult Start(VerifiedActivationPackage p,string token,Action verifyBeforeStart){BeforeStart?.Invoke();try{verifyBeforeStart();}catch(Exception e){return new(ActivationOutcome.RejectedWithoutChange,null,e.Message);}Starts++;if(Behavior=="reject")return new(ActivationOutcome.RejectedWithoutChange,null);Running=true;Identity=new(7000+Starts,DateTime.UtcNow.Ticks,p.ExecutablePath,p.ExecutableSha256,token);return Behavior=="unknown"?new(ActivationOutcome.Unknown,null):new(ActivationOutcome.Confirmed,Identity);} public ActivationDaemonHealth Inspect(ActivationDaemonIdentity i)=>Foreign?ActivationDaemonHealth.DifferentProcess:i==Identity?(Running?ActivationDaemonHealth.SameProcessRunning:ActivationDaemonHealth.OwnedProcessExited):ActivationDaemonHealth.OwnedProcessExited; public ActivationStopResult Stop(ActivationDaemonIdentity i){if(i!=Identity||Foreign)throw new Exception("never stop foreign");Stops++;if(StopUnknown)return new(ActivationOutcome.Unknown);Running=false;return new(ActivationOutcome.Confirmed);} }
namespace ClassicDesk { public sealed record ShellPreviewOptions(bool ClassicRibbon,bool StartOnLeft,int IconSize,int TaskbarHeight,bool ClassicContextMenu,bool Mica=true,int TaskbarButtonWidth=44,int SmallIconSize=16,int SmallTaskbarButtonWidth=32,bool OtherSystemButtonsOnLeft=true,bool StartMenuOnLeft=true,bool SearchMenuOnLeft=false,bool ClassicMenuWithCtrl=true,bool UseClassicNavigationBar=false,bool LeftAlignedApps=false); }
