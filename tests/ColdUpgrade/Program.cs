using ClassicDesk;
using System.IO;
using System.Text.Json;

var checks = new List<object>();
void Check(bool value) { if (!value) throw new Exception("assertion failed"); }
void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException) { return; } throw new Exception("expected rejection"); }
void Test(string name, Action<Fake> body)
{
    try { body(new()); checks.Add(new { name, passed = true, error = "" }); }
    catch (Exception e) { checks.Add(new { name, passed = false, error = e.ToString() }); }
}
Test("preparation does not restore or start a running engine", h => { var t = h.Core.Prepare(h.Old.Root, h.New.Root); Check(!t.Source.EngineExited && h.Restores == 0 && h.Starts == 0 && h.CleanChecks == 0); });
Test("same root cannot become an upgrade", h => { h.New = h.Old; Reject(() => h.Core.Prepare(h.Old.Root, h.New.Root)); });
Test("running old engine blocks all writes", h => { var t = h.Ticket(); Reject(() => h.Core.Apply(t)); Check(h.Restores == 0 && h.Starts == 0); });
Test("engine exit after preparation permits cold switch", h => { var t = h.Ticket(); h.Source = h.Source with { EngineExited = true }; var r = h.Core.Apply(t); Check(r.Phase == "activated-unverified" && h.Restores == 1 && h.Starts == 1 && h.CleanChecks == 2); });
Test("exact applied profile is carried over including menu and explorer settings", h => { h.Source = h.Source with { AppliedProfile = new ShellProfile(StartOnLeft: false, IconSize: 28, ClassicRibbon: false, ClassicContextMenu: false, TranslucentTaskbar: true, FollowMaximizedTheme: true), EngineExited = true }; var t = h.Ticket(); h.Core.Apply(t); Check(h.StartedProfile == t.Source.AppliedProfile); });
Test("loaded shell component blocks restore", h => { h.Source = h.Source with { EngineExited = true }; h.FailCleanAt = 1; Reject(() => h.Core.Apply(h.Ticket())); Check(h.Restores == 0 && h.Starts == 0); });
Test("shell changes during restore block launch", h => { h.Source = h.Source with { EngineExited = true }; h.FailCleanAt = 2; Check(h.Core.Apply(h.Ticket()).Phase == "activation-needs-review" && h.Restores == 1 && h.Starts == 0); });
Test("source journal revision changes block replay", h => { var t = h.Ticket(); h.Source = h.Source with { Revision = "changed", EngineExited = true }; Reject(() => h.Core.Apply(t)); Check(h.Restores == 0); });
Test("source journal identity changes block replay", h => { var t = h.Ticket(); h.Source = h.Source with { JournalId = Guid.NewGuid(), EngineExited = true }; Reject(() => h.Core.Apply(t)); });
Test("applied profile changes block stale ticket", h => { var t = h.Ticket(); h.Source = h.Source with { AppliedProfile = h.Source.AppliedProfile with { IconSize = 32 }, EngineExited = true }; Reject(() => h.Core.Apply(t)); });
Test("target bytes changes block restoration", h => { var t = h.Ticket(); h.New = h.New with { ManifestSha256 = new('D', 64) }; Reject(() => h.Core.Apply(t)); Check(h.Restores == 0); });
Test("target changes between restore and start block launch", h => { h.Source = h.Source with { EngineExited = true }; var t = h.Ticket(); h.AfterRestore = () => h.New = h.New with { ExecutableSha256 = new('E', 64) }; Check(h.Core.Apply(t).Phase == "activation-needs-review" && h.Starts == 0); });
Test("unknown source state blocks preparation", h => { h.SourceError = true; Reject(() => h.Ticket()); Check(h.Restores == 0 && h.Starts == 0); });
Test("unrecognized schema never observes host", h => { var t = h.Ticket() with { SchemaVersion = 2 }; var reads = h.Reads; Reject(() => h.Core.Apply(t)); Check(reads == h.Reads); });
Test("restore failure never starts new engine", h => { h.Source = h.Source with { EngineExited = true }; h.RestoreState = ShellActivationState.ManualReview; Check(h.Core.Apply(h.Ticket()).Phase == "restore-needs-review" && h.Starts == 0); });
Test("restore receipt from another journal is rejected", h => { h.Source = h.Source with { EngineExited = true }; h.WrongRestoreId = true; Check(h.Core.Apply(h.Ticket()).Phase == "restore-needs-review" && h.Starts == 0); });
Test("restore error despite restored state blocks launch", h => { h.Source = h.Source with { EngineExited = true }; h.RestoreError = "uncertain"; Check(h.Core.Apply(h.Ticket()).Phase == "restore-needs-review" && h.Starts == 0); });
Test("uncertain new launch remains pending without retry", h => { h.Source = h.Source with { EngineExited = true }; h.StartState = ShellActivationState.ManualReview; var r = h.Core.Apply(h.Ticket()); Check(r.Phase == "activation-needs-review" && h.Starts == 1 && h.Restores == 1); });
Test("launch exception never restarts old engine", h => { h.Source = h.Source with { EngineExited = true }; h.StartThrows = true; Check(h.Core.Apply(h.Ticket()).Phase == "activation-needs-review" && h.Starts == 1 && h.Restores == 1); });
Test("ticket survives JSON round trip without altering profile", h => { var original = h.Ticket(); var t = JsonSerializer.Deserialize<ColdUpgradeTicket>(JsonSerializer.Serialize(original))!; Check(t == original); });
var failed = checks.Count(c => !(bool)c.GetType().GetProperty("passed")!.GetValue(c)!);
var report = new { passed = checks.Count - failed, failed, realProcessStarts = 0, realProcessStops = 0, realSettingsWrites = 0, checks };
var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 1) File.WriteAllText(Path.GetFullPath(args[0]), text);
Console.WriteLine(text);
return failed == 0 ? 0 : 1;

sealed class Fake : IColdUpgradeHost
{
    public VerifiedActivationPackage Old = new(Path.GetFullPath("old"), new('A', 64), Path.GetFullPath("old/windhawk.exe"), new('B', 64));
    public VerifiedActivationPackage New = new(Path.GetFullPath("new"), new('C', 64), Path.GetFullPath("new/windhawk.exe"), new('B', 64));
    public ColdUpgradeSource Source;
    public ShellColdUpgrade Core;
    public int Restores, Starts, CleanChecks, FailCleanAt, Reads;
    public bool SourceError, WrongRestoreId, StartThrows;
    public string? RestoreError;
    public Action? AfterRestore;
    public ShellActivationState RestoreState = ShellActivationState.Restored, StartState = ShellActivationState.Active;
    public ShellProfile? StartedProfile;
    public Fake() { Source = new(Guid.NewGuid(), "revision-1", Old, new(), false); Core = new(this); }
    public ColdUpgradeTicket Ticket() => Core.Prepare(Old.Root, New.Root);
    public ColdUpgradeSource ReadSource(string root) { Reads++; if (SourceError) throw new IOException("unknown state"); return Source; }
    public VerifiedActivationPackage VerifyTarget(string root) => New;
    public void RequireCleanShell() { if (++CleanChecks == FailCleanAt) throw new IOException("module still loaded"); }
    public ActivationResult Restore(ColdUpgradeSource source) { Restores++; AfterRestore?.Invoke(); return new(WrongRestoreId ? Guid.NewGuid() : source.JournalId, RestoreState, RestoreError); }
    public ActivationResult Activate(VerifiedActivationPackage target, ShellProfile profile) { Starts++; StartedProfile = profile; if (StartThrows) throw new IOException("uncertain launch"); return new(Guid.NewGuid(), StartState, null); }
}
