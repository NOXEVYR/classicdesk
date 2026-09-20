using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassicDesk;

public enum ActivationTarget { TaskbarAlignment, StartButtonMod, IconSizeMod, ExplorerFrameMod, ContextMenuMod, MainSettings, EngineSettings }
public enum ActivationPresence { Absent, Present, Unknown }
public enum ActivationOutcome { Confirmed, RejectedWithoutChange, Unknown }
public enum ActivationDaemonHealth { SameProcessRunning, OwnedProcessExited, DifferentProcess, Unknown }
public enum ShellActivationState { Prepared, Applying, Starting, Active, Compensating, Restoring, RolledBack, Restored, ManualReview }
public sealed record VerifiedActivationPackage(string Root, string ManifestSha256, string ExecutablePath, string ExecutableSha256);
// For INI targets Data is the exact file bytes in base64, not decoded/re-encoded text.
// For alignment Data is canonical "0"/"1"; absence is Exists=false, Data="".
public sealed record ActivationItemState(bool Exists, string Data, string Revision);
public static class ActivationRecordedState
{
    // TaskbarAl has no per-value version. Windows timestamps the entire Advanced
    // key, including unrelated login settings. Keep strict revisions at commit
    // gates, but compare this single DWORD's value against durable ownership.
    public static bool Matches(ActivationTarget target, ActivationItemState actual, ActivationItemState? recorded) =>
        actual == recorded || (target == ActivationTarget.TaskbarAlignment && recorded is not null &&
            actual.Exists == recorded.Exists && actual.Data == recorded.Data &&
            (actual.Exists ? actual.Data is "0" or "1" : actual.Data == ""));
}
public sealed record ActivationChange(ActivationTarget Target, ActivationItemState Before, bool DesiredExists, string DesiredData);
public sealed record ActivationGuards(string EnvironmentRevision, string PackageRevision,
    ActivationPresence StartAllBack, ActivationPresence OtherWindhawk);
public sealed record ActivationPreparation(ActivationGuards Guards, ActivationChange[] Changes);
public sealed record ActivationConfirmation(VerifiedActivationPackage Package, ShellProfile Profile,
    ActivationPreparation Preparation, string Fingerprint);
public sealed record ActivationWriteResult(ActivationOutcome Outcome, string? OwnershipToken,
    ActivationItemState? After, string? Error = null);
public sealed record ActivationDaemonIdentity(int ProcessId, long CreationTimeUtcTicks, string ExecutablePath,
    string ExecutableSha256, string OwnershipToken);
public sealed record ActivationStartResult(ActivationOutcome Outcome, ActivationDaemonIdentity? Identity, string? Error = null);
public sealed record ActivationStopResult(ActivationOutcome Outcome, string? Error = null);

/// <summary>
/// No real implementation is supplied. Inspect is read-only and must verify the
/// package manifest/binaries, Explorer identity, conflicts and every target revision.
/// Write/RestoreOwned/Start recheck Guards at their commit point; RestoreOwned also
/// checks the opaque ownership token. StopOwned must check the complete process
/// identity, never merely a PID, image name or a matching executable path.
/// Confirmed results are receipts, not inferred from equal values. Exceptions or
/// Unknown mean the side effect may have happened. All methods are synchronous so
/// UI adapters must run this core off the UI thread.
/// AcquirePackageLease must exclude other cooperating transactions for the whole
/// canonical package across processes. Every write still needs revision/ownership
/// checks against unrelated writers; neither lease nor this core provides OS CAS.
/// PackageRevision identifies immutable verified assets, excluding mutable INIs.
/// </summary>
public interface IActivationHost
{
    IDisposable AcquirePackageLease(VerifiedActivationPackage package);
    ActivationPreparation Inspect(VerifiedActivationPackage package, ShellProfile profile, ActivationDaemonIdentity? allowedDaemon = null);
    ActivationItemState Read(VerifiedActivationPackage package, ActivationTarget target);
    ActivationWriteResult Write(VerifiedActivationPackage package, ActivationChange change, ActivationGuards guards);
    ActivationWriteResult RestoreOwned(VerifiedActivationPackage package, ActivationTarget target,
        ActivationWriteResult ownedWrite, ActivationItemState original, ActivationGuards guards);
    ActivationStartResult StartDaemon(VerifiedActivationPackage package, ActivationGuards guards,
        IReadOnlyDictionary<ActivationTarget, ActivationItemState> expectedFinal);
    ActivationDaemonHealth InspectDaemon(ActivationDaemonIdentity identity);
    ActivationStopResult StopOwnedDaemon(ActivationDaemonIdentity identity, ActivationGuards guards);
}

public sealed class ActivationJournalStep
{
    public ActivationChange Change { get; set; } = null!;
    public string WritePhase { get; set; } = "planned";
    public ActivationWriteResult? WriteReceipt { get; set; }
    public string RestorePhase { get; set; } = "none";
    public ActivationWriteResult? RestoreReceipt { get; set; }
    public string? Error { get; set; }
}
public sealed class ActivationJournal
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public VerifiedActivationPackage Package { get; set; } = null!;
    public ShellProfile Profile { get; set; } = null!;
    public ActivationGuards Guards { get; set; } = null!;
    public ActivationGuards? RestoreGuards { get; set; }
    public string ConfirmationFingerprint { get; set; } = "";
    public ShellActivationState State { get; set; }
    public List<ActivationJournalStep> Steps { get; set; } = [];
    public string DaemonPhase { get; set; } = "not-started";
    public ActivationDaemonIdentity? Daemon { get; set; }
    public string? Error { get; set; }
    public bool VisualEffectVerified { get; set; } // This core never promotes a visual claim.
}
public sealed record ActivationJournalSnapshot(ActivationJournal Journal, string Revision);
public sealed record ActivationRestoreConfirmation(Guid JournalId, string JournalRevision, ActivationPreparation Current, string Fingerprint);
public sealed record ActivationResumeConfirmation(Guid JournalId, string JournalRevision, ActivationPreparation Current, string Fingerprint);
public sealed record ActivationResult(Guid JournalId, ShellActivationState State, string? Error, bool VisualEffectVerified = false);
public sealed record ActivationRecoveryReview(ActivationJournal Journal, bool RequiresManualReview, string Reason);

public interface IActivationJournalStore
{
    IDisposable Acquire(Guid id);
    ActivationJournalSnapshot Load(Guid id);
    void Save(ActivationJournal journal);
}

/// <summary>Explicit caller-owned audit directory only. Cooperating writers use a per-journal lock.
/// Revision checking plus File.Replace is not an atomic CAS against unrelated writers.</summary>
public sealed class FileActivationJournalStore : IActivationJournalStore
{
    readonly string folder;
    readonly Dictionary<Guid, string?> revisions = [];
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public FileActivationJournalStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("日志目录必须是显式绝对路径。", nameof(directory));
        folder = Path.GetFullPath(directory);
    }
    string FilePath(Guid id) => Path.Combine(folder, id.ToString("N") + ".json");
    public IDisposable Acquire(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("事务 ID 不能为空。");
        RejectReparseChain();
        Directory.CreateDirectory(folder);
        RejectReparseChain();
        return new FileStream(Path.Combine(folder, id.ToString("N") + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public ActivationJournalSnapshot Load(Guid id)
    {
        RejectReparseChain();
        if (new FileInfo(FilePath(id)).Length > 16 * 1024 * 1024) throw new InvalidDataException("日志过大。");
        var bytes = File.ReadAllBytes(FilePath(id));
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("日志过大。");
        var journal = JsonSerializer.Deserialize<ActivationJournal>(bytes, Json) ?? throw new InvalidDataException("日志为空。");
        if (journal.Id != id) throw new InvalidDataException("日志 ID 与文件不匹配。");
        var revision = Hash(bytes); revisions[id] = revision; return new(journal, revision);
    }
    public void Save(ActivationJournal journal)
    {
        RejectReparseChain();
        var path = FilePath(journal.Id);
        var expected = revisions.GetValueOrDefault(journal.Id);
        void CheckRevision()
        {
            if (File.Exists(path) && new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("日志过大。");
            var current = File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
            if (current != expected) throw new IOException("日志被外部修改、删除或新建，拒绝覆盖。");
        }
        CheckRevision();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Json);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("日志过大。");
        var temporary = Path.Combine(folder, ".activation-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            CheckRevision();
            if (expected is null) File.Move(temporary, path);
            else ActivationFileReplace.Commit(temporary, path, () => { RejectReparseChain(); CheckRevision(); });
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new IOException("日志回读失败。");
            revisions[journal.Id] = Hash(bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    void RejectReparseChain()
    {
        for (var d = new DirectoryInfo(folder); d is not null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("日志目录不能经过重解析点。");
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>
/// Retry only Windows errors that leave both replacement names intact. Every
/// attempt rechecks the caller's original revision and the staged bytes. Never
/// retry a transaction, an uncertain rename, or a process/registry operation.
/// </summary>
internal static class ActivationFileReplace
{
    internal static void Commit(string temporary, string destination, Action verifyCurrent,
        Action? replace = null, Action<int>? wait = null)
    {
        var staged = Stamp(temporary);
        replace ??= () => File.Replace(temporary, destination, null);
        wait ??= Thread.Sleep;
        for (int attempt = 0; ; attempt++)
        {
            verifyCurrent();
            if (Stamp(temporary) != staged) throw new IOException("待提交文件已变化，停止替换。");
            try { replace(); return; }
            catch (IOException e) when (attempt < 5 && e.HResult is
                unchecked((int)0x80070020) or unchecked((int)0x80070021) or unchecked((int)0x80070497))
            {
                // Sharing/lock violation or ERROR_UNABLE_TO_REMOVE_REPLACED.
                // 1176/1177 can have partial rename effects and are never retried.
                wait(20 << attempt);
            }
        }
    }
    static string Stamp(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("待提交文件不能经过重解析点。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("待提交文件过大。");
        // ReplaceFile can transfer destination metadata to the staged file even
        // on failure. Validate its content, not inherited timestamps. The caller
        // still verifies the destination's complete original revision each time.
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class ShellActivationCoordinator(IActivationHost host, IActivationJournalStore store)
{
    public ActivationResumeConfirmation CaptureResumeConfirmation(Guid id)
    {
        using var lease = store.Acquire(id);
        var snapshot = store.Load(id); var journal = snapshot.Journal; ValidateJournal(journal);
        if (journal.State != ShellActivationState.Active || host.InspectDaemon(journal.Daemon!) != ActivationDaemonHealth.OwnedProcessExited)
            throw new InvalidOperationException("仅能继续已确认退出、且尚未恢复的增强事务。");
        var current = Clone(host.Inspect(journal.Package, journal.Profile));
        ValidatePreparation(journal.Package, journal.Profile, current); RequireSafe(current.Guards, journal.Package);
        VerifyResumeTargets(journal);
        return new(id, snapshot.Revision, current, Digest(new { id, snapshot.Revision, current }));
    }

    public ActivationResult Resume(ActivationResumeConfirmation confirmation)
    {
        using var lease = store.Acquire(confirmation.JournalId);
        var snapshot = store.Load(confirmation.JournalId); var journal = snapshot.Journal; ValidateJournal(journal);
        using var packageLease = host.AcquirePackageLease(journal.Package);
        if (snapshot.Revision != confirmation.JournalRevision || journal.State != ShellActivationState.Active ||
            confirmation.Fingerprint != Digest(new { id = confirmation.JournalId, Revision = confirmation.JournalRevision, current = confirmation.Current }))
            throw new InvalidOperationException("继续运行的确认记录已变化。");
        if (host.InspectDaemon(journal.Daemon!) != ActivationDaemonHealth.OwnedProcessExited)
            throw new InvalidOperationException("原引擎未确认退出，不启动第二个实例。");
        var current = host.Inspect(journal.Package, journal.Profile);
        ValidatePreparation(journal.Package, journal.Profile, current); RequireSafe(current.Guards, journal.Package);
        if (Digest(current) != Digest(confirmation.Current)) throw new InvalidOperationException("继续运行前宿主或配置发生变化。");
        var expected = VerifyResumeTargets(journal);
        // Preserve the exact original settings and write receipts. Resume changes only
        // the owned daemon identity; it never restores/reapplies the desktop profile.
        var previous = journal.Daemon;
        try
        {
            journal.State = ShellActivationState.Starting; journal.DaemonPhase = "start-intent"; journal.Error = null; Persist(journal);
            ActivationStartResult result;
            try { result = host.StartDaemon(journal.Package, current.Guards, expected); }
            catch (Exception e) { result = new(ActivationOutcome.Unknown, null, e.Message); }
            if (result.Outcome == ActivationOutcome.RejectedWithoutChange)
            {
                journal.State = ShellActivationState.Active; journal.DaemonPhase = "started"; journal.Daemon = previous;
                journal.Error = result.Error ?? "本次未启动增强，原恢复记录保持不变。"; Persist(journal); return Result(journal);
            }
            if (result.Outcome != ActivationOutcome.Confirmed || !ValidDaemon(journal.Package, result.Identity))
            { journal.DaemonPhase = "start-unknown"; journal.Error = result.Error ?? "继续启动结果未知，停止自动处理。"; return Manual(journal); }
            journal.Daemon = result.Identity; journal.DaemonPhase = "started"; Persist(journal);
            if (host.InspectDaemon(journal.Daemon!) != ActivationDaemonHealth.SameProcessRunning)
            { journal.Error = "继续启动后的引擎身份未确认。"; return Manual(journal); }
            foreach (var pair in expected)
                if (host.Read(journal.Package, pair.Key) != pair.Value)
                { journal.Error = "继续启动期间配置变化，保留记录供检查。"; return Manual(journal); }
            journal.State = ShellActivationState.Active; Persist(journal); return Result(journal);
        }
        catch (JournalFailure e) { return PersistenceStopped(journal, e); }
        catch (Exception e) { journal.Error = e.Message; return TryManual(journal); }
    }

    Dictionary<ActivationTarget, ActivationItemState> VerifyResumeTargets(ActivationJournal journal)
    {
        var expected = journal.Steps.ToDictionary(s => s.Change.Target,
            s => s.WritePhase == "confirmed" ? s.WriteReceipt!.After! : s.Change.Before);
        foreach (var pair in expected.ToArray())
        {
            var current = host.Read(journal.Package, pair.Key);
            if (!ActivationRecordedState.Matches(pair.Key, current, pair.Value))
                throw new InvalidOperationException("增强配置或原生对齐被外部修改，不自动覆盖；请在应用中检查恢复记录。");
            // Freeze the current revision for the start gate; never rewrite the
            // original receipt, original value or registry just to refresh it.
            expected[pair.Key] = current;
        }
        return expected;
    }

    public void VerifyAppliedConfiguration(Guid id)
    {
        using var lease = store.Acquire(id);
        var journal = store.Load(id).Journal; ValidateJournal(journal);
        if (journal.State != ShellActivationState.Active) throw new InvalidOperationException("没有已应用的完整规则。");
        VerifyResumeTargets(journal);
    }

    public ActivationConfirmation CaptureConfirmation(VerifiedActivationPackage package, ShellProfile profile)
    {
        ValidatePackage(package); profile.Validate();
        var preparation = Clone(host.Inspect(package, profile)); ValidatePreparation(package, profile, preparation);
        RequireSafe(preparation.Guards, package);
        return new(package, profile, preparation, Digest(new { package, profile, preparation }));
    }

    public ActivationResult Activate(ActivationConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var package = confirmation.Package; var profile = confirmation.Profile;
        ValidatePackage(package); profile.Validate(); ValidatePreparation(package, profile, confirmation.Preparation);
        if (confirmation.Fingerprint != Digest(new { package, profile, preparation = confirmation.Preparation }))
            throw new InvalidDataException("确认载荷已被修改。");
        using var packageLease = host.AcquirePackageLease(package);
        var fresh = host.Inspect(package, profile); ValidatePreparation(package, profile, fresh); RequireSafe(fresh.Guards, package);
        if (Digest(fresh) != Digest(confirmation.Preparation)) throw new InvalidOperationException("确认后宿主、TaskbarAl、包或文件修订已变化，请重新检查。");
        var journal = new ActivationJournal { Id = Guid.NewGuid(), Package = package, Profile = profile, Guards = fresh.Guards,
            ConfirmationFingerprint = confirmation.Fingerprint, State = ShellActivationState.Prepared,
            Steps = Clone(fresh).Changes.Select(c => new ActivationJournalStep { Change = c }).ToList() };
        using var lease = store.Acquire(journal.Id);
        store.Save(journal); // All exact originals and desired values durable before the first host mutation.
        try
        {
            journal.State = ShellActivationState.Applying; Persist(journal);
            foreach (var step in journal.Steps)
            {
                EnsureGuards(journal);
                if (host.Read(package, step.Change.Target) != step.Change.Before) throw new ActivationFailure("写入前目标修订变化。");
                if (SameValue(step.Change.Before, step.Change.DesiredExists, step.Change.DesiredData))
                { step.WritePhase = "unchanged"; Persist(journal); continue; }
                step.WritePhase = "intent"; Persist(journal);
                ActivationWriteResult result;
                try { result = host.Write(package, step.Change, journal.Guards); }
                catch (Exception e) { result = new(ActivationOutcome.Unknown, null, null, e.Message); }
                step.WriteReceipt = result;
                if (result.Outcome != ActivationOutcome.Confirmed || !ValidReceipt(result, step.Change.DesiredExists, step.Change.DesiredData))
                {
                    step.WritePhase = result.Outcome == ActivationOutcome.RejectedWithoutChange ? "rejected" : "unknown";
                    step.Error = result.Error ?? "写入未取得可验证归属。"; Persist(journal); throw new ActivationFailure(step.Error);
                }
                step.WritePhase = "confirmed"; Persist(journal);
                if (host.Read(package, step.Change.Target) != result.After) throw new ActivationFailure("写入回读或修订与归属回执不一致。");
            }
            EnsureGuards(journal);
            var expectedFinal = journal.Steps.ToDictionary(s => s.Change.Target,
                s => s.WritePhase == "confirmed" ? s.WriteReceipt!.After! : s.Change.Before);
            foreach (var pair in expectedFinal)
                if (host.Read(package, pair.Key) != pair.Value) throw new ActivationFailure("启动前最终状态或归属修订已变化。");
            journal.State = ShellActivationState.Starting; journal.DaemonPhase = "start-intent"; Persist(journal);
            ActivationStartResult start;
            try { start = host.StartDaemon(package, journal.Guards, expectedFinal); }
            catch (Exception e) { start = new(ActivationOutcome.Unknown, null, e.Message); }
            if (start.Outcome == ActivationOutcome.RejectedWithoutChange)
            { journal.DaemonPhase = "not-started"; Persist(journal); throw new ActivationFailure(start.Error ?? "守护进程未启动。"); }
            if (start.Outcome != ActivationOutcome.Confirmed || !ValidDaemon(package, start.Identity))
            { journal.DaemonPhase = "start-unknown"; journal.Error = start.Error ?? "启动结果或身份不确定。"; return Manual(journal); }
            journal.Daemon = start.Identity; journal.DaemonPhase = "started"; Persist(journal);
            var health = host.InspectDaemon(journal.Daemon!);
            if (health != ActivationDaemonHealth.SameProcessRunning)
            { journal.Error = "启动后的进程身份/存活未确认。"; return Manual(journal); }
            journal.State = ShellActivationState.Active; Persist(journal); return Result(journal);
        }
        catch (JournalFailure e) { return PersistenceStopped(journal, e); }
        catch (Exception e)
        {
            journal.Error = e.Message;
            if (journal.DaemonPhase is "start-intent" or "start-unknown" or "started") return TryManual(journal);
            return Compensate(journal, false);
        }
    }

    public ActivationRestoreConfirmation CaptureRestoreConfirmation(Guid id)
    {
        using var lease = store.Acquire(id);
        var snapshot = store.Load(id); ValidateJournal(snapshot.Journal);
        if (snapshot.Journal.State != ShellActivationState.Active) throw new InvalidOperationException("只有已确认 Active 事务可进入标准恢复；其他状态需人工检查。");
        var current = Clone(host.Inspect(snapshot.Journal.Package, snapshot.Journal.Profile, snapshot.Journal.Daemon));
        ValidatePreparation(snapshot.Journal.Package, snapshot.Journal.Profile, current);
        return new(id, snapshot.Revision, current, Digest(new { id, snapshot.Revision, current }));
    }

    public ActivationResult Restore(ActivationRestoreConfirmation confirmation)
    {
        using var lease = store.Acquire(confirmation.JournalId);
        var loaded = store.Load(confirmation.JournalId); var journal = loaded.Journal; ValidateJournal(journal);
        using var packageLease = host.AcquirePackageLease(journal.Package);
        if (loaded.Revision != confirmation.JournalRevision || journal.State != ShellActivationState.Active)
            throw new InvalidOperationException("确认后的日志已变化，恢复未执行。");
        if (confirmation.Fingerprint != Digest(new { id = confirmation.JournalId, Revision = confirmation.JournalRevision, current = confirmation.Current }))
            throw new InvalidDataException("恢复确认载荷已变化。");
        try
        {
            var current = host.Inspect(journal.Package, journal.Profile, journal.Daemon);
            ValidatePreparation(journal.Package, journal.Profile, current); RequireSafe(current.Guards, journal.Package);
            if (Digest(current) != Digest(confirmation.Current)) throw new ActivationFailure("恢复确认后宿主或目标已变化。");
            foreach (var step in journal.Steps.Where(s => s.WritePhase == "confirmed"))
                if (!ActivationRecordedState.Matches(step.Change.Target, host.Read(journal.Package, step.Change.Target), step.WriteReceipt!.After))
                    throw new ActivationFailure("恢复前发现外部修改，不停止进程或覆盖数据。");
            var health = host.InspectDaemon(journal.Daemon!);
            if (health is ActivationDaemonHealth.DifferentProcess or ActivationDaemonHealth.Unknown)
                throw new ActivationFailure("原守护进程身份不匹配或未知，不会停止其他进程。");
            journal.State = ShellActivationState.Restoring;
            journal.RestoreGuards = current.Guards; // Keep original activation evidence immutable.
            if (health == ActivationDaemonHealth.SameProcessRunning)
            {
                journal.DaemonPhase = "stop-intent"; Persist(journal);
                ActivationStopResult stopped;
                try { stopped = host.StopOwnedDaemon(journal.Daemon!, journal.RestoreGuards); }
                catch (Exception e) { stopped = new(ActivationOutcome.Unknown, e.Message); }
                if (stopped.Outcome != ActivationOutcome.Confirmed)
                { journal.DaemonPhase = "stop-unknown"; journal.Error = stopped.Error ?? "停止结果未确认。"; return Manual(journal); }
                if (host.InspectDaemon(journal.Daemon!) != ActivationDaemonHealth.OwnedProcessExited)
                { journal.DaemonPhase = "stop-unknown"; journal.Error = "停止回读未确认，不继续恢复。"; return Manual(journal); }
            }
            journal.DaemonPhase = "stopped"; Persist(journal);
            return Compensate(journal, true);
        }
        catch (JournalFailure e) { return PersistenceStopped(journal, e); }
        catch (Exception e) { journal.Error = e.Message; return TryManual(journal); }
    }

    public ActivationRecoveryReview ReadForReview(Guid id)
    {
        using var lease = store.Acquire(id); var journal = store.Load(id).Journal; ValidateJournal(journal);
        var manual = journal.State is not (ShellActivationState.Active or ShellActivationState.Restored or ShellActivationState.RolledBack)
            || journal.Steps.Any(s => s.WritePhase is "intent" or "unknown" || s.RestorePhase is "intent" or "unknown");
        return new(journal, manual, manual ? "未完成意图或不确定归属；不从当前值相等推断已写入，不自动重放。" : "可审查的已确认记录；不代表视觉效果已验收。");
    }

    ActivationResult Compensate(ActivationJournal journal, bool restoring)
    {
        var uncertain = journal.Steps.Any(s => s.WritePhase is "intent" or "unknown");
        try
        {
            journal.State = restoring ? ShellActivationState.Restoring : ShellActivationState.Compensating; Persist(journal);
            foreach (var step in journal.Steps.AsEnumerable().Reverse().Where(s => s.WritePhase == "confirmed" && s.RestorePhase != "confirmed"))
            {
                try
                {
                    var operationGuards = restoring ? journal.RestoreGuards ?? throw new InvalidDataException("恢复环境快照缺失。") : journal.Guards;
                    EnsureGuards(journal, restoring ? journal.Daemon : null, operationGuards);
                    var currentOwned = host.Read(journal.Package, step.Change.Target);
                    if (restoring ? !ActivationRecordedState.Matches(step.Change.Target, currentOwned, step.WriteReceipt!.After) : currentOwned != step.WriteReceipt!.After)
                    { step.RestorePhase = "conflict"; step.Error = "当前值或修订已不属于本事务。"; uncertain = true; Persist(journal); continue; }
                    step.RestorePhase = "intent"; Persist(journal);
                    ActivationWriteResult receipt;
                    try { receipt = host.RestoreOwned(journal.Package, step.Change.Target, step.WriteReceipt!, step.Change.Before, operationGuards); }
                    catch (Exception e) { receipt = new(ActivationOutcome.Unknown, null, null, e.Message); }
                    step.RestoreReceipt = receipt;
                    if (!ValidReceipt(receipt, step.Change.Before.Exists, step.Change.Before.Data))
                    { step.RestorePhase = receipt.Outcome == ActivationOutcome.RejectedWithoutChange ? "conflict" : "unknown"; uncertain = true; }
                    else
                    {
                        step.RestorePhase = "confirmed"; Persist(journal);
                        if (host.Read(journal.Package, step.Change.Target) != receipt.After)
                        { step.RestorePhase = "conflict"; uncertain = true; }
                    }
                    Persist(journal);
                }
                catch (JournalFailure) { throw; }
                catch (Exception e) { step.Error = e.Message; uncertain = true; Persist(journal); }
            }
            // Later writes can race an external edit to an earlier restored
            // target. Recheck the complete final state before claiming success.
            try
            {
                EnsureGuards(journal, restoring ? journal.Daemon : null, restoring ? journal.RestoreGuards : journal.Guards);
                foreach (var step in journal.Steps)
                {
                    var expected = step.RestorePhase == "confirmed" ? step.RestoreReceipt!.After : step.Change.Before;
                    var currentFinal = host.Read(journal.Package, step.Change.Target);
                    if (restoring ? !ActivationRecordedState.Matches(step.Change.Target, currentFinal, expected) : currentFinal != expected)
                    { uncertain = true; step.Error ??= "恢复结束时发现目标修订变化，保留当前数据。"; }
                }
                if (restoring && host.InspectDaemon(journal.Daemon!) != ActivationDaemonHealth.OwnedProcessExited)
                { uncertain = true; journal.Error ??= "恢复结束时未能确认原增强进程已退出。"; }
            }
            catch (Exception e) { uncertain = true; journal.Error ??= e.Message; }
            journal.State = uncertain ? ShellActivationState.ManualReview : restoring ? ShellActivationState.Restored : ShellActivationState.RolledBack;
            Persist(journal); return Result(journal);
        }
        catch (JournalFailure e) { return PersistenceStopped(journal, e); }
    }

    void EnsureGuards(ActivationJournal journal, ActivationDaemonIdentity? allowed = null, ActivationGuards? operationGuards = null)
    {
        var observation = host.Inspect(journal.Package, journal.Profile, allowed);
        RequireSafe(observation.Guards, journal.Package);
        if (observation.Guards != (operationGuards ?? journal.Guards)) throw new ActivationFailure("宿主环境修订发生变化。");
    }
    void Persist(ActivationJournal journal)
    { try { store.Save(journal); } catch (Exception e) { throw new JournalFailure(e); } }
    ActivationResult Manual(ActivationJournal journal)
    { journal.State = ShellActivationState.ManualReview; Persist(journal); return Result(journal); }
    ActivationResult TryManual(ActivationJournal journal)
    { try { return Manual(journal); } catch (JournalFailure e) { return PersistenceStopped(journal, e); } }
    static ActivationResult PersistenceStopped(ActivationJournal journal, Exception error) =>
        new(journal.Id, ShellActivationState.ManualReview, "日志持久化失败，已停止后续宿主操作；磁盘可能仅保留意图，必须人工检查。" + error.GetBaseException().Message);
    static ActivationResult Result(ActivationJournal journal) => new(journal.Id, journal.State, journal.Error);
    static bool SameValue(ActivationItemState state, bool exists, string data) => state.Exists == exists && state.Data == data;
    static bool ValidReceipt(ActivationWriteResult receipt, bool exists, string data) => receipt.Outcome == ActivationOutcome.Confirmed
        && !string.IsNullOrWhiteSpace(receipt.OwnershipToken) && receipt.After is not null
        && !string.IsNullOrWhiteSpace(receipt.After.Revision) && SameValue(receipt.After, exists, data);
    static bool ValidDaemon(VerifiedActivationPackage package, ActivationDaemonIdentity? daemon) => daemon is not null
        && daemon.ProcessId > 0 && daemon.CreationTimeUtcTicks > 0 && !string.IsNullOrWhiteSpace(daemon.OwnershipToken)
        && string.Equals(daemon.ExecutablePath, package.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(daemon.ExecutableSha256, package.ExecutableSha256, StringComparison.OrdinalIgnoreCase);
    static void RequireSafe(ActivationGuards guards, VerifiedActivationPackage package)
    {
        if (guards is null || guards.StartAllBack != ActivationPresence.Absent || guards.OtherWindhawk != ActivationPresence.Absent
            || string.IsNullOrWhiteSpace(guards.EnvironmentRevision) || guards.PackageRevision != package.ManifestSha256)
            throw new ActivationFailure("包修订、StartAllBack 或其他 Windhawk 实例尚未明确满足启用/恢复条件。");
    }
    static void ValidatePackage(VerifiedActivationPackage package)
    {
        if (package is null || !Path.IsPathFullyQualified(package.Root) || !Path.IsPathFullyQualified(package.ExecutablePath)
            || !Path.GetFullPath(package.ExecutablePath).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.Root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !IsHash(package.ManifestSha256) || !IsHash(package.ExecutableSha256)) throw new InvalidDataException("包身份无效。");
    }
    static void ValidatePreparation(VerifiedActivationPackage package, ShellProfile profile, ActivationPreparation preparation)
    {
        if (preparation is null || preparation.Changes is null || preparation.Changes.Length != 7
            || preparation.Changes.Select(c => c.Target).Distinct().Count() != 7) throw new InvalidDataException("必须提供完整且唯一的七个目标（含主程序和引擎的独立安全模式配置）。");
        foreach (var change in preparation.Changes)
        {
            if (!Enum.IsDefined(change.Target) || change.Before is null || string.IsNullOrWhiteSpace(change.Before.Revision)) throw new InvalidDataException("目标或修订无效。");
            ValidateValue(change.Target, change.Before.Exists, change.Before.Data); ValidateValue(change.Target, change.DesiredExists, change.DesiredData);
            // Both supported layouts keep application buttons centered. StartOnLeft
            // only controls the extra mod; alignment remains a separately owned write.
            if (change.Target == ActivationTarget.TaskbarAlignment)
            {
                if (profile.SkipTaskbarLayout && !SameValue(change.Before, change.DesiredExists, change.DesiredData))
                    throw new InvalidDataException("未选择任务栏布局时必须保留原始对齐值。");
                if (!profile.SkipTaskbarLayout && (!change.DesiredExists || change.DesiredData != "1"))
                    throw new InvalidDataException("两种布局均需明确、独立且可恢复的 TaskbarAl=1 对齐计划。");
            }
        }
    }
    static void ValidateValue(ActivationTarget target, bool exists, string data)
    {
        if (data is null || data.Length > 2 * 1024 * 1024 || (!exists && data != "")) throw new InvalidDataException("值载荷无效。");
        if (!exists) return;
        if (target == ActivationTarget.TaskbarAlignment)
        { if (data is not ("0" or "1")) throw new InvalidDataException("TaskbarAl 必须为 0/1 或不存在。"); }
        else if (Convert.FromBase64String(data).Length > 1024 * 1024) throw new InvalidDataException("配置备份过大。");
    }
    static void ValidateJournal(ActivationJournal journal)
    {
        if (journal.SchemaVersion != 1 || journal.Id == Guid.Empty || journal.Profile is null || journal.Guards is null
            || !Enum.IsDefined(journal.State) || journal.Steps is null) throw new InvalidDataException("日志结构无效。");
        ValidatePackage(journal.Package); journal.Profile.Validate();
        if (journal.RestoreGuards is not null) RequireSafe(journal.RestoreGuards, journal.Package);
        ValidatePreparation(journal.Package, journal.Profile, new(journal.Guards, journal.Steps.Select(s => s.Change).ToArray()));
        if (journal.ConfirmationFingerprint != Digest(new { package = journal.Package, profile = journal.Profile,
            preparation = new ActivationPreparation(journal.Guards, journal.Steps.Select(s => s.Change).ToArray()) }))
            throw new InvalidDataException("日志备份/计划与原确认指纹不符。");
        foreach (var step in journal.Steps)
        {
            if (step.WritePhase is not ("planned" or "unchanged" or "intent" or "rejected" or "unknown" or "confirmed")
                || step.RestorePhase is not ("none" or "intent" or "unknown" or "confirmed" or "conflict")) throw new InvalidDataException("日志阶段无效。");
            if (step.WritePhase == "confirmed" && (step.WriteReceipt is null || !ValidReceipt(step.WriteReceipt, step.Change.DesiredExists, step.Change.DesiredData)))
                throw new InvalidDataException("日志缺少写入归属回执。");
            if (step.RestorePhase != "none" && step.WritePhase != "confirmed") throw new InvalidDataException("未确认写入不能记为已恢复。");
            if (step.RestorePhase == "confirmed" && (step.RestoreReceipt is null || !ValidReceipt(step.RestoreReceipt, step.Change.Before.Exists, step.Change.Before.Data)))
                throw new InvalidDataException("日志缺少恢复归属回执。");
        }
        if (journal.DaemonPhase is not ("not-started" or "start-intent" or "start-unknown" or "started" or "stop-intent" or "stop-unknown" or "stopped"))
            throw new InvalidDataException("守护进程阶段无效。");
        if ((journal.DaemonPhase is "started" or "stop-intent" or "stop-unknown" or "stopped") && !ValidDaemon(journal.Package, journal.Daemon))
            throw new InvalidDataException("日志中的进程身份无效。");
        if (journal.State == ShellActivationState.Active && (journal.DaemonPhase != "started" || journal.Steps.Any(s => s.WritePhase is not ("confirmed" or "unchanged") || s.RestorePhase != "none")))
            throw new InvalidDataException("Active 日志存在未确认项目。");
    }
    static bool IsHash(string value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    static ActivationPreparation Clone(ActivationPreparation preparation) => preparation with { Changes = preparation.Changes.ToArray() };
    sealed class ActivationFailure(string message) : InvalidOperationException(message);
    sealed class JournalFailure(Exception inner) : IOException("事务日志未持久化。", inner);
}
