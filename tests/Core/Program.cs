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
    catch (Exception e) { results.Add(new { name, passed = false, error = e.ToString() }); }
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
    Check(f.Host.RestoreOrder.SequenceEqual(Enum.GetValues<ActivationTarget>().Reverse()));
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
Test("already desired values create no ownership or rollback writes", f =>
{
    foreach (var t in Enum.GetValues<ActivationTarget>()) f.Host.States[t] = new(true, FakeHost.Desired(t), "already-" + t);
    var r = f.Activate(); Check(f.Host.Writes == 0 && f.Journal().Steps.All(s => s.WritePhase == "unchanged"));
    Check(f.Core.Restore(f.Core.CaptureRestoreConfirmation(r.JournalId)).State == ShellActivationState.Restored && f.Host.Restores == 0);
});
foreach (var presence in new[] { ActivationPresence.Present, ActivationPresence.Unknown })
{
    Test("StartAllBack " + presence + " blocks before journal", f => { f.Host.Sab = presence; Reject(() => f.Activate()); Check(f.Host.Writes == 0 && !Directory.Exists(f.LogDirectory)); });
    Test("other Windhawk " + presence + " blocks before journal", f => { f.Host.Other = presence; Reject(() => f.Activate()); Check(f.Host.Writes == 0); });
}
foreach (var target in Enum.GetValues<ActivationTarget>())
    Test("stale confirmation rejects " + target, f => { var c = f.Capture(); f.Host.External(target); Reject(() => f.Core.Activate(c)); Check(f.Host.Writes == 0 && !f.Host.Leased); });
Test("environment revision drift rejects stale confirmation", f => { var c = f.Capture(); f.Host.EnvironmentRevision = "changed"; Reject(() => f.Core.Activate(c)); Check(f.Host.Writes == 0); });
Test("package revision drift rejected", f => { f.Host.PackageRevision = new('B', 64); Reject(() => f.Activate()); Check(f.Host.Writes == 0); });
Test("malformed hash rejected before host inspect", f => { f.Package = f.Package with { ManifestSha256 = "bad" }; Reject(() => f.Activate()); Check(f.Host.Inspections == 0); });
Test("normalized executable path cannot escape package", f => { f.Package = f.Package with { ExecutablePath = Path.Combine(f.Package.Root, "..", "foreign.exe") }; Reject(() => f.Activate()); Check(f.Host.Inspections == 0); });
Test("confirmation mutation rejected before package lease", f => { var c = f.Capture(); c.Preparation.Changes[1] = c.Preparation.Changes[1] with { DesiredData = FakeHost.B64("tampered") }; Reject(() => f.Core.Activate(c)); Check(f.Host.Writes == 0 && f.Host.LeaseAcquisitions == 0); });
foreach (var target in Enum.GetValues<ActivationTarget>())
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
    public Fixture(string root)
    {
        Directory.CreateDirectory(root); LogDirectory = Path.Combine(root, "journals");
        Package = new(root, new('A', 64), Path.Combine(root, "windhawk.exe"), new('C', 64));
        Host = new FakeHost(LogDirectory); Store = new(new FileActivationJournalStore(LogDirectory)); Core = new(Host, Store);
    }
    public ActivationConfirmation Capture() => Core.CaptureConfirmation(Package, Profile);
    public ActivationResult Activate() => Core.Activate(Capture());
    public string JournalPath() => Directory.GetFiles(LogDirectory, "*.json").Single();
    public ActivationJournal Journal() => JsonSerializer.Deserialize<ActivationJournal>(File.ReadAllText(JournalPath()))!;
}
sealed class FaultStore(FileActivationJournalStore inner) : IActivationJournalStore
{
    public FileActivationJournalStore Inner = inner;
    public Func<ActivationJournal, bool>? Fail;
    public IDisposable Acquire(Guid id) => Inner.Acquire(id);
    public ActivationJournalSnapshot Load(Guid id) => Inner.Load(id);
    public void Save(ActivationJournal journal) { if (Fail?.Invoke(journal) == true) throw new IOException("injected persistence failure"); Inner.Save(journal); }
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
    public ActivationDaemonHealth? ForcedHealth;
    public ActivationDaemonIdentity? Identity;
    public Action<ActivationTarget>? AfterWrite; public Action? AfterStop;
    public List<ActivationTarget> RestoreOrder = [];
    public FakeHost(string logDirectory)
    {
        logs = logDirectory;
        foreach (var t in Enum.GetValues<ActivationTarget>()) States[t] = new(true, t == ActivationTarget.TaskbarAlignment ? "0" : B64("original:" + t), "initial:" + t);
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
        return new(new(EnvironmentRevision, PackageRevision, Sab, other), Enum.GetValues<ActivationTarget>().Select(t => new ActivationChange(t, States[t], true, Desired(t))).ToArray());
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
        if (owners.GetValueOrDefault(target) != ownedWrite.OwnershipToken || States[target] != ownedWrite.After) return new(ActivationOutcome.RejectedWithoutChange, null, null);
        var after = States[target] = new(original.Exists, original.Data, "restored-" + ++revision);
        if (FailRestoreTarget == target && RestoreBehavior == "unknown") return new(ActivationOutcome.Unknown, null, null);
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
namespace ClassicDesk { public sealed record ShellPreviewOptions(bool ClassicRibbon, bool StartOnLeft, int IconSize, int TaskbarHeight, bool ClassicContextMenu, bool Mica = true, int TaskbarButtonWidth = 44, int SmallIconSize = 16, int SmallTaskbarButtonWidth = 32, bool OtherSystemButtonsOnLeft = true, bool StartMenuOnLeft = true, bool SearchMenuOnLeft = false, bool ClassicMenuWithCtrl = true, bool UseClassicNavigationBar = false); }
