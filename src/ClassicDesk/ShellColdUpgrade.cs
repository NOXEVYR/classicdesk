using System.IO;

namespace ClassicDesk;

public sealed record ColdUpgradeSource(Guid JournalId, string Revision, VerifiedActivationPackage Package,
    ShellProfile AppliedProfile, bool EngineExited);
public sealed record ColdUpgradeTicket(int SchemaVersion, ColdUpgradeSource Source, VerifiedActivationPackage Target);
public sealed record ColdUpgradeResult(string Phase, ActivationResult? Restore, ActivationResult? Activation, string Detail);

public interface IColdUpgradeHost
{
    ColdUpgradeSource ReadSource(string root);
    VerifiedActivationPackage VerifyTarget(string root);
    void RequireCleanShell();
    ActivationResult Restore(ColdUpgradeSource source);
    ActivationResult Activate(VerifiedActivationPackage target, ShellProfile appliedProfile);
}

/// <summary>Explicit cold switch only. Never stops/restarts Explorer, unloads a live engine,
/// writes an autostart entry, or substitutes an edited draft for the applied profile.</summary>
public sealed class ShellColdUpgrade(IColdUpgradeHost host)
{
    public ColdUpgradeTicket Prepare(string sourceRoot, string targetRoot)
    {
        var source = host.ReadSource(sourceRoot);
        var target = host.VerifyTarget(targetRoot);
        source.AppliedProfile.Validate();
        if (SameRoot(source.Package.Root, target.Root)) throw new InvalidOperationException("新旧运行目录必须分开，不能覆盖在用组件。");
        return new(1, source, target);
    }

    public ColdUpgradeResult Apply(ColdUpgradeTicket ticket)
    {
        if (ticket.SchemaVersion != 1 || ticket.Source is null || ticket.Target is null)
            throw new InvalidDataException("无法识别冷切换记录。");
        var current = Prepare(ticket.Source.Package.Root, ticket.Target.Root);
        // EngineExited is an observation, not an identity: it may become true after a reboot.
        if (current.Source with { EngineExited = ticket.Source.EngineExited } != ticket.Source || current.Target != ticket.Target)
            throw new InvalidOperationException("运行包、已应用方案或恢复记录已变化，请重新准备切换。");
        if (!current.Source.EngineExited)
            throw new InvalidOperationException("旧引擎仍在运行或退出状态未知，未卸载组件、未切换新版。");
        host.RequireCleanShell();
        var restored = host.Restore(current.Source);
        if (restored.JournalId != current.Source.JournalId || restored.State != ShellActivationState.Restored || restored.Error is not null)
            return new("restore-needs-review", restored, null, "旧记录未完整恢复；没有启动新版。");
        try
        {
            // A second instance or binary replacement may appear during restoration.
            if (host.VerifyTarget(ticket.Target.Root) != ticket.Target) throw new InvalidOperationException("新版组件在恢复期间发生变化。");
            host.RequireCleanShell();
            var active = host.Activate(ticket.Target, current.Source.AppliedProfile);
            return new(active.State == ShellActivationState.Active && active.Error is null ? "activated-unverified" : "activation-needs-review",
                restored, active, "已按原来应用的方案执行切换；视觉效果和开机首次显示需另行验收。");
        }
        catch (Exception e)
        {
            // Do not resume an old engine or repeat an uncertain launch automatically.
            return new("activation-needs-review", restored, null, "旧记录已恢复，新版切换需检查：" + e.Message);
        }
    }
    static bool SameRoot(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}

public sealed class WindowsColdUpgradeHost(string journals) : IColdUpgradeHost
{
    ShellNativeController Controller(string root) => new(root, journals, new NoLiveUnloadHost(WindowsShellActivationHost.CreateForWindows()));
    public ColdUpgradeSource ReadSource(string root)
    {
        var login = ShellLoginRegistration.Current();
        if (login.Enabled || File.Exists(login.EntryPath))
            throw new InvalidOperationException("旧登录恢复入口仍存在，请先通过原版恢复入口关闭，避免重启后再次启动旧组件。");
        return Controller(root).CaptureColdUpgradeSource();
    }
    public VerifiedActivationPackage VerifyTarget(string root)
    {
        var package = ShellNativeController.CheckReviewedPackage(root).Package;
        if (package.ManifestSha256 != ShellNativeController.ReviewedRuntimeManifest)
            throw new InvalidDataException("冷切换目标必须是当前核对的新版运行包。");
        return package;
    }
    public void RequireCleanShell()
    {
        ShellServiceStatus.RequireAbsent();
        // Inspect both processes: the positioning component also loads in StartMenuExperienceHost.
        // Access failures are not evidence that a module has gone away.
        foreach (var name in new[] { "explorer", "StartMenuExperienceHost", "windhawk" })
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        if (name == "windhawk") throw new InvalidOperationException("检测到增强引擎，暂不切换。");
                        foreach (System.Diagnostics.ProcessModule module in process.Modules)
                        {
                            var file = module.ModuleName.ToLowerInvariant();
                            if (file.StartsWith("windhawk") || file.StartsWith("taskbar-") || file.StartsWith("explorer-frame-classic_") ||
                                file.StartsWith("explorer-context-menu-classic_") || file.StartsWith("windows-11-taskbar-styler_"))
                                throw new InvalidOperationException("外壳进程仍加载增强组件，暂不切换。");
                        }
                    }
                    catch (System.ComponentModel.Win32Exception e) { throw new IOException("无法确认外壳组件已经退出。", e); }
                }
    }
    public ActivationResult Restore(ColdUpgradeSource source)
    {
        var controller = Controller(source.Package.Root);
        if (controller.CaptureColdUpgradeSource() != source) throw new InvalidOperationException("恢复前旧记录发生变化。");
        RequireCleanShell();
        var review = controller.ReviewAsync(source.AppliedProfile).GetAwaiter().GetResult();
        if (!review.CanRestore || !review.CanResume || review.IsRunning) throw new InvalidOperationException("旧引擎未确认停止。");
        return controller.RestoreAsync(review).GetAwaiter().GetResult();
    }
    public ActivationResult Activate(VerifiedActivationPackage target, ShellProfile appliedProfile)
    {
        if (VerifyTarget(target.Root) != target) throw new InvalidOperationException("新版组件已变化。");
        RequireCleanShell();
        var controller = Controller(target.Root);
        var review = controller.ReviewAsync(appliedProfile).GetAwaiter().GetResult();
        if (!review.CanEnable || review.IsRunning) throw new InvalidOperationException(review.Title + "：" + review.Detail);
        return controller.EnableAsync(review).GetAwaiter().GetResult();
    }

    // Even if an engine reappears between observations, this path cannot unload it.
    sealed class NoLiveUnloadHost(IActivationHost inner) : IActivationHost
    {
        public IDisposable AcquirePackageLease(VerifiedActivationPackage p) => inner.AcquirePackageLease(p);
        public ActivationPreparation Inspect(VerifiedActivationPackage p, ShellProfile s, ActivationDaemonIdentity? allowedDaemon = null) => inner.Inspect(p, s, allowedDaemon);
        public ActivationItemState Read(VerifiedActivationPackage p, ActivationTarget t) => inner.Read(p, t);
        public ActivationWriteResult Write(VerifiedActivationPackage p, ActivationChange c, ActivationGuards g) => inner.Write(p, c, g);
        public ActivationWriteResult RestoreOwned(VerifiedActivationPackage p, ActivationTarget t, ActivationWriteResult r, ActivationItemState o, ActivationGuards g) => inner.RestoreOwned(p, t, r, o, g);
        public ActivationStartResult StartDaemon(VerifiedActivationPackage p, ActivationGuards g, IReadOnlyDictionary<ActivationTarget, ActivationItemState> expectedFinal) => inner.StartDaemon(p, g, expectedFinal);
        public ActivationDaemonHealth InspectDaemon(ActivationDaemonIdentity i) => inner.InspectDaemon(i);
        public ActivationStopResult StopOwnedDaemon(ActivationDaemonIdentity i, ActivationGuards g) => new(ActivationOutcome.RejectedWithoutChange, "冷切换不停止正在运行的增强引擎；请保留恢复记录。");
    }
}

public sealed partial class ShellNativeController
{
    internal ColdUpgradeSource CaptureColdUpgradeSource()
    {
        var package = CheckReviewedPackage(packageRoot).Package;
        var pending = PendingRecords();
        if (pending.Length != 1 || pending[0].Package != package || pending[0].State != ShellActivationState.Active)
            throw new InvalidOperationException("必须只有一份与旧运行包一致的完整已应用记录。");
        var id = pending[0].Id;
        var store = new FileActivationJournalStore(journalDirectory);
        var before = store.Load(id);
        coordinator.VerifyAppliedConfiguration(id);
        var after = store.Load(id);
        if (before.Revision != after.Revision || after.Journal.Daemon is null) throw new InvalidOperationException("读取期间恢复记录变化。");
        var health = host.InspectDaemon(after.Journal.Daemon);
        if (health is not (ActivationDaemonHealth.OwnedProcessExited or ActivationDaemonHealth.SameProcessRunning))
            throw new InvalidOperationException("旧引擎身份无法确认。");
        return new(id, after.Revision, package, after.Journal.Profile, health == ActivationDaemonHealth.OwnedProcessExited);
    }
}
