using ClassicDesk;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

var results = new List<object>();
var root = Path.Combine(AppContext.BaseDirectory, "isolated-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException or ArgumentException or FormatException) { return; } throw new Exception("expected rejection"); }
void Test(string name, Action<Fixture> action)
{
    var fixture = new Fixture(Path.Combine(root, results.Count.ToString()));
    try { action(fixture); results.Add(new { name, passed = true }); }
    catch (Exception e) { results.Add(new { name, passed = false, error = e.ToString(), activation = fixture.LastActivation, storageErrors = fixture.Store.Errors }); }
}
Test("activate records all originals, intents and owned daemon; visual claim stays false", f =>
{
    var r = f.Activate(); Check(r.State == ShellActivationState.Active && !r.VisualEffectVerified);
    var j = f.Journal(); Check(j.Steps.Count == 7 && j.Steps.All(s => s.WritePhase == "confirmed"));
    Check(f.Host.Writes == 7 && f.Host.Starts == 1 && f.Host.Stops == 0 && j.Daemon == f.Host.Identity);
    Check(f.Host.IntentChecks == 8 && !f.Host.Leased);
});
Test("restore owned daemon then reverse seven writes with durable intent", f =>
{
    var r = f.Activate(); var restore = f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId));
    Check(restore.State == ShellActivationState.Restored && f.Host.Stops == 1 && f.Host.Restores == 7);
    Check(f.Host.RestoreOrder.SequenceEqual(Enum.GetValues<ActivationTarget>().Where(t => t <= ActivationTarget.EngineSettings).Reverse()));
    Check(f.Host.States.All(p => p.Value.Data == f.Host.Original[p.Key].Data));
    Check(f.Journal().Steps.All(s => s.RestorePhase == "confirmed") && f.Host.IntentChecks == 16);
});
Test("missing originals restored as absence", f =>
{
    f.Host.States[ActivationTarget.TaskbarAlignment] = new(false, "", "missing-alignment");
    f.Host.States[ActivationTarget.MainSettings] = new(false, "", "missing-main");
    var r = f.Activate(); Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.Restored);
    Check(!f.Host.States[ActivationTarget.TaskbarAlignment].Exists && !f.Host.States[ActivationTarget.MainSettings].Exists);
});
Test("all-centered profile independently journals alignment 0 to 1", f =>
{
    f.Profile = new(StartOnLeft: false); var r = f.Activate();
    Check(r.State == ShellActivationState.Active && f.Journal().Steps[0].Change.DesiredData == "1");
    Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.Restored);
    Check(f.Host.States[ActivationTarget.TaskbarAlignment].Data == "0");
});
foreach (var original in new[] { "0", "1", "" })
    Test("all-left independently restores original alignment " + original, f =>
    {
        f.Profile = new(LeftAlignedApps: true);
        f.Host.States[ActivationTarget.TaskbarAlignment] = new(original != "", original, "all-left-original");
        var r = f.Activate(); Check(r.State == ShellActivationState.Active);
        Check(f.Journal().Steps.Single(s => s.Change.Target == ActivationTarget.TaskbarAlignment).Change.DesiredData == "0");
        Check(f.Host.States[ActivationTarget.TaskbarAlignment].Data == "0");
        Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.Restored);
        Check(f.Host.States[ActivationTarget.TaskbarAlignment].Exists == (original != "") && f.Host.States[ActivationTarget.TaskbarAlignment].Data == original);
    });
foreach (var allLeft in new[] { false, true })
    Test("host cannot substitute opposite alignment " + allLeft, f =>
    {
        f.Profile = new(LeftAlignedApps: allLeft); f.Host.ForcedAlignment = allLeft ? "1" : "0";
        Reject(() => f.Capture()); Check(f.Host.Writes == 0 && f.Host.Starts == 0 && !Directory.Exists(f.LogDirectory));
    });
Test("skipped layout cannot receive malicious alignment write", f =>
{
    f.Profile = new(LeftAlignedApps: true, SkipTaskbarLayout: true); f.Host.ForcedAlignment = "1";
    Reject(() => f.Capture()); Check(f.Host.Writes == 0 && f.Host.Starts == 0);
});
Test("all-left external alignment change blocks restore without overwriting", f =>
{
    f.Profile = new(LeftAlignedApps: true); f.Host.States[ActivationTarget.TaskbarAlignment] = new(true, "1", "original-centered");
    var r = f.Activate(); f.Host.States[ActivationTarget.TaskbarAlignment] = new(true, "1", "external-centered");
    Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.ManualReview);
    Check(f.Host.States[ActivationTarget.TaskbarAlignment].Data == "1" && f.Host.Restores == 0 && f.Host.Stops == 0);
});
Test("already desired values create no ownership or rollback writes", f =>
{
    foreach (var t in Enum.GetValues<ActivationTarget>().Where(t => t <= ActivationTarget.EngineSettings)) f.Host.States[t] = new(true, FakeHost.Desired(t), "already-" + t);
    var r = f.Activate(); Check(f.Host.Writes == 0 && f.Journal().Steps.All(s => s.WritePhase == "unchanged"));
    Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.Restored && f.Host.Restores == 0);
});
foreach (var presence in new[] { ActivationPresence.Present, ActivationPresence.Unknown })
{
    Test("StartAllBack " + presence + " blocks before journal", f => { f.Host.Sab = presence; Reject(() => f.Activate()); Check(f.Host.Writes == 0 && !Directory.Exists(f.LogDirectory)); });
    Test("other Windhawk " + presence + " blocks before journal", f => { f.Host.Other = presence; Reject(() => f.Activate()); Check(f.Host.Writes == 0); });
}
foreach (var target in Enum.GetValues<ActivationTarget>().Where(t => t <= ActivationTarget.EngineSettings))
    Test("stale confirmation rejects " + target, f => { var c = f.Capture(); f.Host.External(target); Reject(() => f.Core.Activate(c)); Check(f.Host.Writes == 0 && !f.Host.Leased); });
Test("environment revision drift rejects stale confirmation", f => { var c = f.Capture(); f.Host.EnvironmentRevision = "changed"; Reject(() => f.Core.Activate(c)); Check(f.Host.Writes == 0); });
Test("package revision drift rejected", f => { f.Host.PackageRevision = new('B', 64); Reject(() => f.Activate()); Check(f.Host.Writes == 0); });
Test("malformed hash rejected before host inspect", f => { f.Package = f.Package with { ManifestSha256 = "bad" }; Reject(() => f.Activate()); Check(f.Host.Inspections == 0); });
Test("normalized executable path cannot escape package", f => { f.Package = f.Package with { ExecutablePath = Path.Combine(f.Package.Root, "..", "foreign.exe") }; Reject(() => f.Activate()); Check(f.Host.Inspections == 0); });
Test("confirmation mutation rejected before package lease", f => { var c = f.Capture(); c.Preparation.Changes[1] = c.Preparation.Changes[1] with { DesiredData = FakeHost.B64("tampered") }; Reject(() => f.Core.Activate(c)); Check(f.Host.Writes == 0 && f.Host.LeaseAcquisitions == 0); });
foreach (var target in Enum.GetValues<ActivationTarget>().Where(t => t <= ActivationTarget.EngineSettings))
    Test("definite write refusal compensates only previous owned steps " + target, f =>
    {
        f.Host.FailTarget = target; f.Host.WriteBehavior = "reject"; var r = f.Activate();
        Check(r.State == ShellActivationState.RolledBack && f.Host.Starts == 0 && f.Host.Restores == (int)target);
        Check(f.Host.States.All(p => p.Value.Data == f.Host.Original[p.Key].Data));
    });
foreach (var behavior in new[] { "unknown", "throw", "bad-receipt" })
    Test("uncertain write preserves unknown target " + behavior, f =>
    {
        f.Host.FailTarget = ActivationTarget.ExplorerFrameMod; f.Host.WriteBehavior = behavior; var r = f.Activate();
        Check(r.State == ShellActivationState.ManualReview && f.Host.Starts == 0 && f.Host.Restores == 3);
        Check(f.Host.States[ActivationTarget.ExplorerFrameMod].Data == FakeHost.Desired(ActivationTarget.ExplorerFrameMod));
        Check(f.Core.ReadForReview(r.JournalId).RequiresManualReview);
    });
Test("external revision after confirmed write is not rolled back", f =>
{
    f.Host.AfterWrite = t => { if (t == ActivationTarget.IconSizeMod) f.Host.External(t); };
    var r = f.Activate(); Check(r.State == ShellActivationState.ManualReview && f.Host.Restores == 2 && f.Host.Starts == 0);
    Check(f.Host.States[ActivationTarget.IconSizeMod].Revision.StartsWith("external"));
});
Test("confirmed no-start failure compensates every owned write", f => { f.Host.StartBehavior = "reject"; var r = f.Activate(); Check(r.State == ShellActivationState.RolledBack && f.Host.Restores == 7 && f.Host.Stops == 0); });
foreach (var behavior in new[] { "unknown", "throw", "bad-identity" })
    Test("uncertain daemon start does not compensate or stop " + behavior, f => { f.Host.StartBehavior = behavior; var r = f.Activate(); Check(r.State == ShellActivationState.ManualReview && f.Host.Restores == 0 && f.Host.Stops == 0); });
Test("unconfirmed daemon health stays manual", f => { f.Host.ForcedHealth = ActivationDaemonHealth.Unknown; var r = f.Activate(); Check(r.State == ShellActivationState.ManualReview && f.Host.Restores == 0); });
foreach (var health in new[] { ActivationDaemonHealth.DifferentProcess, ActivationDaemonHealth.Unknown })
    Test("restore never stops reused or unknown process " + health, f => { var r = f.Activate(); var c = f.Core.CaptureRestoreConfirmation(r.JournalId); f.Host.ForcedHealth = health; Check(f.Core.Restore(c).State == ShellActivationState.ManualReview && f.Host.Stops == 0 && f.Host.Restores == 0); });
Test("restore accepts already exited owned daemon without stop", f => { var r = f.Activate(); f.Host.Running = false; Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.Restored && f.Host.Stops == 0); });
Test("external config before restore prevents daemon stop", f => { var r = f.Activate(); var c = f.Core.CaptureRestoreConfirmation(r.JournalId); f.Host.External(ActivationTarget.MainSettings); Check(f.Core.Restore(c).State == ShellActivationState.ManualReview && f.Host.Stops == 0 && f.Host.Restores == 0); });
Test("restore confirmation journal revision changes reject", f => { var r = f.Activate(); var c = f.Core.CaptureRestoreConfirmation(r.JournalId); File.AppendAllText(f.JournalPath(), "\n"); Reject(() => f.Core.Restore(c)); Check(f.Host.Stops == 0); });
foreach (var behavior in new[] { "unknown", "reject", "throw" })
    Test("unconfirmed stop prevents config restore " + behavior, f => { var r = f.Activate(); f.Host.StopBehavior = behavior; Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.ManualReview && f.Host.Restores == 0); });
Test("uncertain restore never retries that target", f => { var r = f.Activate(); f.Host.RestoreBehavior = "unknown"; f.Host.FailRestoreTarget = ActivationTarget.ExplorerFrameMod; var result = f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)); Check(result.State == ShellActivationState.ManualReview && f.Host.Restores == 7); Check(f.Core.ReadForReview(r.JournalId).RequiresManualReview); });
Test("external modification after stopping is preserved", f => { var r = f.Activate(); f.Host.AfterStop = () => f.Host.External(ActivationTarget.MainSettings); Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.ManualReview && f.Host.Restores == 6); Check(f.Host.States[ActivationTarget.MainSettings].Revision.StartsWith("external")); });
Test("initial journal failure permits no host writes", f => { f.Store.Fail = _ => true; Reject(() => f.Activate()); Check(f.Host.Writes == 0 && f.Host.Starts == 0 && !f.Host.Leased); });
Test("write receipt persistence failure halts all host actions; restart requires review", f =>
{
    f.Store.Fail = j => j.Steps[0].WritePhase == "confirmed"; var r = f.Activate();
    Check(r.State == ShellActivationState.ManualReview && f.Host.Writes == 1 && f.Host.Restores == 0 && f.Host.Starts == 0);
    var core = new ShellActivationCoordinator(f.Host, new FileActivationJournalStore(f.LogDirectory));
    Check(core.ReadForReview(r.JournalId).RequiresManualReview && f.Journal().Steps[0].WritePhase == "intent");
    Check(f.Host.Writes == 1);
});
Test("start identity persistence failure never terminates uncertain process", f => { f.Store.Fail = j => j.DaemonPhase == "started"; var r = f.Activate(); Check(r.State == ShellActivationState.ManualReview && f.Host.Starts == 1 && f.Host.Stops == 0 && f.Journal().DaemonPhase == "start-intent"); });
Test("damaged original backup rejected before restore actions", f => { var r = f.Activate(); var j = f.Journal(); j.Steps[0].Change = j.Steps[0].Change with { Before = new(true, "1", "tamper") }; File.WriteAllText(f.JournalPath(), JsonSerializer.Serialize(j)); Reject(() => f.Core.CaptureRestoreConfirmation(r.JournalId)); Check(f.Host.Stops == 0 && f.Host.Restores == 0); });
Test("package lease serializes competing activation", f => { var c = f.Capture(); using var lease = f.Host.AcquirePackageLease(f.Package); Reject(() => f.Core.Activate(c)); Check(f.Host.Writes == 0); });
Test("journal revision protection rejects external edits", f => { var r = f.Activate(); var snapshot = f.Store.Inner.Load(r.JournalId); File.AppendAllText(f.JournalPath(), "\n"); Reject(() => f.Store.Inner.Save(snapshot.Journal)); Check(File.ReadAllText(f.JournalPath()).EndsWith("\n")); });
Test("journal revision protection rejects external deletion", f => { var r = f.Activate(); var snapshot = f.Store.Inner.Load(r.JournalId); var path = f.JournalPath(); File.Delete(path); Reject(() => f.Store.Inner.Save(snapshot.Journal)); Check(!File.Exists(path)); });
Test("new store refuses overwrite without reading existing revision", f => { var r = f.Activate(); var fresh = new FileActivationJournalStore(f.LogDirectory); Reject(() => fresh.Save(f.Journal())); });
Test("journal lock rejects same-id concurrent writer", f => { var id = Guid.NewGuid(); using var lease = f.Store.Inner.Acquire(id); Reject(() => f.Store.Inner.Acquire(id)); });
Test("oversized journal rejected before deserialization", f => { var id = Guid.NewGuid(); using var lease = f.Store.Inner.Acquire(id); using (var file = File.Create(Path.Combine(f.LogDirectory, id.ToString("N") + ".json"))) file.SetLength(16 * 1024 * 1024 + 1); Reject(() => f.Store.Inner.Load(id)); });

Test("fresh restore confirmation accepts new Explorer session without rewriting original evidence", f => { var r=f.Activate(); f.Host.Running=false; f.Host.EnvironmentRevision="explorer-new-session"; AssertRestore(f,r); void AssertRestore(Fixture fixture,ActivationResult result) { Check(fixture.Core.Restore(fixture.Core.CaptureRestoreConfirmation(result.JournalId)).State==ShellActivationState.Restored); var j=fixture.Journal(); Check(j.Guards.EnvironmentRevision=="explorer-build-and-session-1"&&j.RestoreGuards?.EnvironmentRevision=="explorer-new-session"); } });
Test("another Explorer change after restore confirmation blocks stop and overwrite", f => { var r=f.Activate(); f.Host.EnvironmentRevision="explorer-new-session"; var c=f.Core.CaptureRestoreConfirmation(r.JournalId); f.Host.EnvironmentRevision="explorer-newer-session"; Check(f.Core.Restore(c).State==ShellActivationState.ManualReview&&f.Host.Stops==0&&f.Host.Restores==0); });
Test("late external edit to an already restored target cannot claim restored", f =>
{
    var active = f.Activate();
    f.Host.AfterRestore = target => { if (target == ActivationTarget.TaskbarAlignment) f.Host.External(ActivationTarget.EngineSettings); };
    var result = f.Core.Restore(f.Core.CaptureRestoreConfirmation(active.JournalId));
    Check(result.State == ShellActivationState.ManualReview && f.Host.Restores == 7 && f.Host.Stops == 1);
    Check(f.Host.States[ActivationTarget.EngineSettings].Revision.StartsWith("external"));
});
Test("late external edit to an unchanged target cannot claim restored", f =>
{
    f.Host.States[ActivationTarget.EngineSettings] = new(true, FakeHost.Desired(ActivationTarget.EngineSettings), "already-desired");
    var active = f.Activate();
    f.Host.AfterRestore = target => { if (target == ActivationTarget.TaskbarAlignment) f.Host.External(ActivationTarget.EngineSettings); };
    var result = f.Core.Restore(f.Core.CaptureRestoreConfirmation(active.JournalId));
    Check(result.State == ShellActivationState.ManualReview && f.Host.Restores == 6 && f.Host.Stops == 1);
});
foreach (int code in new[] { 32, 33, 1175 })
    Test("unchanged replacement retries transient Windows error " + code, f =>
    {
        var (staged, target) = ReplacementFiles(f); int attempts = 0, checks = 0;
        ActivationFileReplace.Commit(staged, target, () => { checks++; Check(File.ReadAllText(target) == "original"); },
            () => { if (++attempts <= 2) throw NativeError(code); File.Move(staged, target, true); }, _ => { });
        Check(attempts == 3 && checks == 3 && File.ReadAllText(target) == "replacement");
    });
foreach (int code in new[] { 5, 1176, 1177 })
    Test("uncertain or permanent replacement error is not retried " + code, f =>
    {
        var (staged, target) = ReplacementFiles(f); int attempts = 0;
        Reject(() => ActivationFileReplace.Commit(staged, target, () => { },
            () => { attempts++; throw NativeError(code); }, _ => throw new Exception("must not wait")));
        Check(attempts == 1 && File.ReadAllText(target) == "original");
    });
Test("replacement retries are bounded", f =>
{
    var (staged, target) = ReplacementFiles(f); int attempts = 0, waits = 0;
    Reject(() => ActivationFileReplace.Commit(staged, target, () => { },
        () => { attempts++; throw NativeError(1175); }, _ => waits++));
    Check(attempts == 6 && waits == 5 && File.ReadAllText(target) == "original");
});
Test("external destination edit while waiting is preserved", f =>
{
    var (staged, target) = ReplacementFiles(f); int attempts = 0;
    Reject(() => ActivationFileReplace.Commit(staged, target,
        () => { if (File.ReadAllText(target) != "original") throw new IOException("revision conflict"); },
        () => { attempts++; throw NativeError(1175); }, _ => File.WriteAllText(target, "external")));
    Check(attempts == 1 && File.ReadAllText(target) == "external");
});
Test("staged replacement edit while waiting is rejected", f =>
{
    var (staged, target) = ReplacementFiles(f); int attempts = 0;
    Reject(() => ActivationFileReplace.Commit(staged, target, () => { },
        () => { attempts++; throw NativeError(1175); }, _ => File.WriteAllText(staged, "tampered")));
    Check(attempts == 1 && File.ReadAllText(target) == "original");
});
Test("Windows inherited staging timestamps do not invalidate identical bytes", f =>
{
    var (staged, target) = ReplacementFiles(f); int attempts = 0;
    ActivationFileReplace.Commit(staged, target, () => Check(File.ReadAllText(target) == "original"),
        () => { if (++attempts == 1) { File.SetCreationTimeUtc(staged, DateTime.UtcNow.AddDays(-1)); File.SetLastWriteTimeUtc(staged, DateTime.UtcNow.AddDays(-1)); throw NativeError(1175); } File.Move(staged, target, true); }, _ => { });
    Check(attempts == 2 && File.ReadAllText(target) == "replacement");
});
Test("changed environment gate aborts replacement retry", f =>
{
    var (staged, target) = ReplacementFiles(f); int attempts = 0; bool safe = true;
    Reject(() => ActivationFileReplace.Commit(staged, target,
        () => { if (!safe) throw new InvalidOperationException("environment changed"); },
        () => { attempts++; throw NativeError(1175); }, _ => safe = false));
    Check(attempts == 1 && File.ReadAllText(target) == "original");
});
Test("real Windows reader lock can release before replacement retry", f =>
{
    var (staged, target) = ReplacementFiles(f); int waits = 0;
    using var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    ActivationFileReplace.Commit(staged, target, () => Check(File.ReadAllText(target) == "original"),
        wait: milliseconds => { waits++; reader.Dispose(); Thread.Sleep(milliseconds); });
    Check(waits > 0 && File.ReadAllText(target) == "replacement");
});
static IOException NativeError(int code) => new("simulated Windows replacement failure", unchecked((int)0x80070000) | code);
static (string Staged, string Target) ReplacementFiles(Fixture f)
{
    var root = Path.GetDirectoryName(f.LogDirectory)!;
    var staged = Path.Combine(root, "replacement.tmp"); var target = Path.Combine(root, "current.json");
    File.WriteAllText(staged, "replacement"); File.WriteAllText(target, "original"); return (staged, target);
}
Test("resume restarts owned exited engine without rewriting original settings", f => {
    var active=f.Activate(); var original=f.Journal().Steps.Select(s=>JsonSerializer.Serialize(s)).ToArray(); var writes=f.Host.Writes;
    f.Host.Running=false; f.Host.EnvironmentRevision="new-boot";
    var result=f.Core.Resume(f.Core.CaptureResumeConfirmation(active.JournalId));
    Check(result.State==ShellActivationState.Active && f.Host.Running && f.Host.Starts==2 && f.Host.Writes==writes && f.Host.Restores==0 && f.Host.Stops==0);
    Check(f.Journal().Steps.Select(s=>JsonSerializer.Serialize(s)).SequenceEqual(original));
    Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(active.JournalId)).State==ShellActivationState.Restored);
    Check(f.Host.States.All(p=>p.Value.Data==f.Host.Original[p.Key].Data));
});
Test("resume refuses live engine", f=>{var r=f.Activate();Reject(()=>f.Core.CaptureResumeConfirmation(r.JournalId));Check(f.Host.Starts==1);});
Test("resume refuses unknown or foreign process", f=>{var r=f.Activate();f.Host.ForcedHealth=ActivationDaemonHealth.DifferentProcess;Reject(()=>f.Core.CaptureResumeConfirmation(r.JournalId));Check(f.Host.Stops==0&&f.Host.Starts==1);});
Test("resume refuses changed alignment value", f=>{var r=f.Activate();f.Host.Running=false;f.Host.States[ActivationTarget.TaskbarAlignment]=new(true,"0","external-value");Reject(()=>f.Core.CaptureResumeConfirmation(r.JournalId));Check(f.Host.Starts==1);});
Test("unrelated Advanced key timestamp permits resume and preserves original receipts", f=>{var r=f.Activate();var steps=JsonSerializer.Serialize(f.Journal().Steps);f.Host.Running=false;f.Host.External(ActivationTarget.TaskbarAlignment);Check(f.Core.Resume(f.Core.CaptureResumeConfirmation(r.JournalId)).State==ShellActivationState.Active);Check(f.Host.Writes==7&&JsonSerializer.Serialize(f.Journal().Steps)==steps);Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State==ShellActivationState.Restored);Check(f.Host.States[ActivationTarget.TaskbarAlignment].Data=="0");});
Test("alignment metadata drift after confirmation still blocks start", f=>{var r=f.Activate();f.Host.Running=false;var c=f.Core.CaptureResumeConfirmation(r.JournalId);f.Host.External(ActivationTarget.TaskbarAlignment);Reject(()=>f.Core.Resume(c));Check(f.Host.Starts==1);});
Test("applied rule audit rejects altered module even when daemon is running", f=>{var r=f.Activate();f.Core.VerifyAppliedConfiguration(r.JournalId);f.Host.External(ActivationTarget.ContextMenuMod);Reject(()=>f.Core.VerifyAppliedConfiguration(r.JournalId));Check(f.Host.Stops==0&&f.Host.Starts==1);});
Test("applied rule audit tolerates unrelated registry metadata without rewriting", f=>{var r=f.Activate();f.Host.External(ActivationTarget.TaskbarAlignment);f.Core.VerifyAppliedConfiguration(r.JournalId);Check(f.Host.Writes==7&&f.Host.Restores==0);});
Test("resume refuses late target drift", f=>{var r=f.Activate();f.Host.Running=false;var ticket=f.Core.CaptureResumeConfirmation(r.JournalId);f.Host.External(ActivationTarget.IconSizeMod);Reject(()=>f.Core.Resume(ticket));Check(f.Host.Starts==1);});
Test("resume refuses late conflicting plugin", f=>{var r=f.Activate();f.Host.Running=false;var ticket=f.Core.CaptureResumeConfirmation(r.JournalId);f.Host.Sab=ActivationPresence.Present;Reject(()=>f.Core.Resume(ticket));Check(f.Host.Starts==1);});
Test("resume definite launch rejection preserves recovery record and does not restore", f=>{var r=f.Activate();f.Host.Running=false;f.Host.StartBehavior="reject";var result=f.Core.Resume(f.Core.CaptureResumeConfirmation(r.JournalId));Check(result.State==ShellActivationState.Active&&result.Error!=null&&!f.Host.Running&&f.Host.Restores==0);});
Test("resume uncertain start blocks repeat", f=>{var r=f.Activate();f.Host.Running=false;f.Host.StartBehavior="unknown";var result=f.Core.Resume(f.Core.CaptureResumeConfirmation(r.JournalId));Check(result.State==ShellActivationState.ManualReview&&f.Host.Stops==0);Reject(()=>f.Core.CaptureResumeConfirmation(r.JournalId));});
Test("resume journal failure before launch does not start", f=>{var r=f.Activate();f.Host.Running=false;var ticket=f.Core.CaptureResumeConfirmation(r.JournalId);f.Store.Fail=j=>j.State==ShellActivationState.Starting;var result=f.Core.Resume(ticket);Check(result.State==ShellActivationState.ManualReview&&f.Host.Starts==1);});
var failed = results.Count(x => !(bool)x.GetType().GetProperty("passed")!.GetValue(x)!);
var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ClassicDesk/ShellActivation.cs"));
var report = new { passed = results.Count - failed, failed, mode = "fake-host and isolated journal files only", realHostWrites = 0, realProcessStarts = 0, realProcessStops = 0, realDesktopInteractions = 0, sourceSha256 = File.Exists(source) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) : null, checks = results };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "report.json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, json);
Console.WriteLine(json); return failed == 0 ? 0 : 1;

sealed class Fixture
{
    public string LogDirectory { get; }
    public ShellProfile Profile = new();
    public VerifiedActivationPackage Package;
    public FakeHost Host;
    public FaultStore Store;
    public ShellActivationCoordinator Core;
    public ActivationResult? LastActivation;
    public Fixture(string root)
    {
        Directory.CreateDirectory(root); LogDirectory = Path.Combine(root, "journals");
        Package = new(root, new('A', 64), Path.Combine(root, "windhawk.exe"), new('C', 64));
        Host = new FakeHost(LogDirectory); Store = new(new FileActivationJournalStore(LogDirectory)); Core = new(Host, Store);
    }
    public ActivationConfirmation Capture() => Core.CaptureConfirmation(Package, Profile);
    public ActivationResult Activate() => LastActivation = Core.Activate(Capture());
    public string JournalPath() => Directory.GetFiles(LogDirectory, "*.json").Single();
    public ActivationJournal Journal() => JsonSerializer.Deserialize<ActivationJournal>(File.ReadAllText(JournalPath()))!;
}
sealed class FaultStore(FileActivationJournalStore inner) : IActivationJournalStore
{
    public FileActivationJournalStore Inner = inner;
    public Func<ActivationJournal, bool>? Fail;
    public List<string> Errors = [];
    public IDisposable Acquire(Guid id) => Inner.Acquire(id);
    public ActivationJournalSnapshot Load(Guid id) => Inner.Load(id);
    public void Save(ActivationJournal journal)
    {
        try { if (Fail?.Invoke(journal) == true) throw new IOException("injected persistence failure"); Inner.Save(journal); }
        catch (Exception e) { Errors.Add($"0x{e.HResult:X8}: {e}"); throw; }
    }
}
sealed class FakeHost : IActivationHost
{
    readonly string logs; int revision;
    public Dictionary<ActivationTarget, ActivationItemState> States = [];
    public Dictionary<ActivationTarget, ActivationItemState> Original = [];
    readonly Dictionary<ActivationTarget, string> owners = [];
    public ActivationPresence Sab, Other;
    public string EnvironmentRevision = "explorer-build-and-session-1", PackageRevision = new('A', 64);
    public bool Running, Leased; public int Writes, Restores, Starts, Stops, Inspections, IntentChecks, LeaseAcquisitions;
    public ActivationTarget? FailTarget, FailRestoreTarget;
    public string WriteBehavior = "normal", StartBehavior = "normal", StopBehavior = "normal", RestoreBehavior = "normal";
    public string? ForcedAlignment;
    public ActivationDaemonHealth? ForcedHealth;
    public ActivationDaemonIdentity? Identity;
    public Action<ActivationTarget>? AfterWrite, AfterRestore; public Action? AfterStop;
    public List<ActivationTarget> RestoreOrder = [];
    public FakeHost(string logDirectory)
    {
        logs = logDirectory;
        foreach (var t in Enum.GetValues<ActivationTarget>().Where(t => t <= ActivationTarget.EngineSettings)) States[t] = new(true, t == ActivationTarget.TaskbarAlignment ? "0" : B64("original:" + t), "initial:" + t);
        Original = new(States);
    }
    public static string B64(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
    public static string Desired(ActivationTarget t) => t == ActivationTarget.TaskbarAlignment ? "1" : B64("desired:" + t);
    public IDisposable AcquirePackageLease(VerifiedActivationPackage package)
    { if (Leased) throw new IOException("competing package transaction"); Leased = true; LeaseAcquisitions++; return new Lease(() => Leased = false); }
    public ActivationPreparation Inspect(VerifiedActivationPackage package, ShellProfile profile, ActivationDaemonIdentity? allowedDaemon = null)
    {
        Inspections++;
        var other = Other != ActivationPresence.Absent ? Other : Running && allowedDaemon != Identity ? ActivationPresence.Present : ActivationPresence.Absent;
        return new(new(EnvironmentRevision, PackageRevision, Sab, other), Enum.GetValues<ActivationTarget>().Where(t => t <= ActivationTarget.EngineSettings).Select(t =>
            t == ActivationTarget.TaskbarAlignment && profile.SkipTaskbarLayout && ForcedAlignment is null
                ? new ActivationChange(t, States[t], States[t].Exists, States[t].Data)
                : new ActivationChange(t, States[t], true, t == ActivationTarget.TaskbarAlignment ? ForcedAlignment ?? (profile.LeftAlignedApps ? "0" : "1") : Desired(t))).ToArray());
    }
    public ActivationItemState Read(VerifiedActivationPackage package, ActivationTarget target) => States[target];
    void CommitGuard(ActivationGuards guards)
    { if (!Leased || guards.EnvironmentRevision != EnvironmentRevision || guards.PackageRevision != PackageRevision || Sab != ActivationPresence.Absent || Other != ActivationPresence.Absent) throw new IOException("guard failed"); }
    void Intent(string phase, ActivationTarget? target = null)
    {
        var journal = JsonSerializer.Deserialize<ActivationJournal>(File.ReadAllText(Directory.GetFiles(logs, "*.json").Single()))!;
        bool valid = target is null ? journal.DaemonPhase == phase : phase == "write" ? journal.Steps.Single(s => s.Change.Target == target).WritePhase == "intent" : journal.Steps.Single(s => s.Change.Target == target).RestorePhase == "intent";
        if (!valid || journal.Steps.Count != 7 || journal.Steps.Any(s => s.Change.Before is null)) throw new Exception("missing durable backup/intent");
        IntentChecks++;
    }
    public ActivationWriteResult Write(VerifiedActivationPackage package, ActivationChange change, ActivationGuards guards)
    {
        CommitGuard(guards); Intent("write", change.Target); Writes++;
        if (States[change.Target] != change.Before) return new(ActivationOutcome.RejectedWithoutChange, null, null, "CAS mismatch");
        if (FailTarget == change.Target && WriteBehavior == "reject") return new(ActivationOutcome.RejectedWithoutChange, null, null);
        var token = "owned-write-" + ++revision; owners[change.Target] = token;
        var after = States[change.Target] = new(change.DesiredExists, change.DesiredData, "write-" + revision);
        if (FailTarget == change.Target && WriteBehavior == "throw") throw new IOException("uncertain write");
        if (FailTarget == change.Target && WriteBehavior == "unknown") return new(ActivationOutcome.Unknown, null, null);
        if (FailTarget == change.Target && WriteBehavior == "bad-receipt") return new(ActivationOutcome.Confirmed, "", after);
        AfterWrite?.Invoke(change.Target); return new(ActivationOutcome.Confirmed, token, after);
    }
    public void External(ActivationTarget target) => States[target] = States[target] with { Revision = "external-" + ++revision };
    public ActivationWriteResult RestoreOwned(VerifiedActivationPackage package, ActivationTarget target, ActivationWriteResult ownedWrite, ActivationItemState original, ActivationGuards guards)
    {
        CommitGuard(guards); Intent("restore", target); Restores++; RestoreOrder.Add(target);
        if (owners.GetValueOrDefault(target) != ownedWrite.OwnershipToken || !ActivationRecordedState.Matches(target, States[target], ownedWrite.After)) return new(ActivationOutcome.RejectedWithoutChange, null, null);
        var after = States[target] = new(original.Exists, original.Data, "restored-" + ++revision);
        if (FailRestoreTarget == target && RestoreBehavior == "unknown") return new(ActivationOutcome.Unknown, null, null);
        AfterRestore?.Invoke(target);
        return new(ActivationOutcome.Confirmed, "owned-restore-" + revision, after);
    }
    public ActivationStartResult StartDaemon(VerifiedActivationPackage package, ActivationGuards guards, IReadOnlyDictionary<ActivationTarget, ActivationItemState> expectedFinal)
    {
        CommitGuard(guards); Intent("start-intent"); Starts++;
        if (StartBehavior == "reject") return new(ActivationOutcome.RejectedWithoutChange, null);
        Running = true; Identity = new(424242, 123456789, package.ExecutablePath, package.ExecutableSha256, "fake-owned-process");
        if (StartBehavior == "throw") throw new IOException("uncertain launch");
        if (StartBehavior == "unknown") return new(ActivationOutcome.Unknown, null);
        if (StartBehavior == "bad-identity") return new(ActivationOutcome.Confirmed, Identity with { ProcessId = 0 });
        return new(ActivationOutcome.Confirmed, Identity);
    }
    public ActivationDaemonHealth InspectDaemon(ActivationDaemonIdentity identity) => ForcedHealth ?? (identity != Identity ? ActivationDaemonHealth.DifferentProcess : Running ? ActivationDaemonHealth.SameProcessRunning : ActivationDaemonHealth.OwnedProcessExited);
    public ActivationStopResult StopOwnedDaemon(ActivationDaemonIdentity identity, ActivationGuards guards)
    {
        CommitGuard(guards); Intent("stop-intent"); Stops++;
        if (InspectDaemon(identity) != ActivationDaemonHealth.SameProcessRunning) throw new Exception("must never stop foreign process");
        if (StopBehavior == "reject") return new(ActivationOutcome.RejectedWithoutChange);
        Running = false; AfterStop?.Invoke();
        if (StopBehavior == "throw") throw new IOException("uncertain stop");
        return new(StopBehavior == "unknown" ? ActivationOutcome.Unknown : ActivationOutcome.Confirmed);
    }
    sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
}
namespace ClassicDesk { public sealed record ShellPreviewOptions(bool ClassicRibbon, bool StartOnLeft, int IconSize, int TaskbarHeight, bool ClassicContextMenu, bool Mica = true, int TaskbarButtonWidth = 44, int SmallIconSize = 16, int SmallTaskbarButtonWidth = 32, bool OtherSystemButtonsOnLeft = true, bool StartMenuOnLeft = true, bool SearchMenuOnLeft = false, bool ClassicMenuWithCtrl = true, bool UseClassicNavigationBar = false, bool LeftAlignedApps = false); }
