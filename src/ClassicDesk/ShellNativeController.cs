using System.IO;
using System.Text.Json;
using System.Windows;

namespace ClassicDesk;

/// <summary>Explicit settings-window actions. No timer, startup activation, service installation, or autostart.</summary>
public sealed class ShellNativeController : IShellNativeController
{
    // This manifest and every referenced asset were reviewed from the fixed official package.
    // A self-consistent replacement manifest is not trusted by this application.
    public const string ReviewedRuntimeManifest = "87EBC2871F686E9E26DFF7CCDCC65CD181AC98DB80EAEC442B8D82720C3E139E";
    readonly string packageRoot, journalDirectory;
    readonly IActivationHost host;
    readonly ShellActivationCoordinator coordinator;
    readonly SemaphoreSlim gate = new(1, 1);
    // Default review scope remains taskbar-only; the panel can opt into other features.
    public static ShellProfile TaskbarOnly(ShellProfile proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal); proposal.Validate();
        return proposal with { ClassicRibbon = false, UseClassicNavigationBar = false, ClassicContextMenu = false };
    }
    public ShellNativeController(string root, string journals, IActivationHost adapter)
    {
        packageRoot = Path.GetFullPath(root); journalDirectory = Path.GetFullPath(journals); host = adapter;
        coordinator = new(host, new FileActivationJournalStore(journalDirectory));
    }
    public static void Open(Window owner, ShellProfile profile)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "原生组件-未启用"));
        var journals = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "NativeTransactions");
        var panel = new ShellNativePanel(profile, new ShellNativeController(root, journals, WindowsShellActivationHost.CreateForWindows())) { Owner = owner };
        // User clicked the settings button. Never called on app startup or by offscreen tests.
        panel.ShowDialog();
    }

    public Task<ShellNativeReview> ReviewAsync(ShellProfile profile) => Serialized(() => Review(profile));
    public Task<ActivationResult> EnableAsync(ShellNativeReview review) => Serialized(() =>
    {
        if (!review.CanEnable || review.EnableTicket is null) throw new InvalidOperationException("请先检查当前方案。");
        if (PendingRecords().Length != 0) throw new InvalidOperationException("出现未结束的切换记录，请先检查恢复状态。");
        var verified = WindowsShellActivationHost.CheckPackage(packageRoot, ReviewedRuntimeManifest).Package;
        if (verified != review.EnableTicket.Package) throw new InvalidOperationException("增强组件已变化，请重新检查。");
        return coordinator.Activate(review.EnableTicket);
    });
    public Task<ActivationResult> RestoreAsync(ShellNativeReview review) => Serialized(() =>
    {
        if (!review.CanRestore || review.RestoreTicket is null) throw new InvalidOperationException("请先检查恢复记录。");
        return coordinator.Restore(review.RestoreTicket);
    });
    async Task<T> Serialized<T>(Func<T> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(operation).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    ShellNativeReview Review(ShellProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile); profile.Validate();
        var pending = PendingRecords();
        if (!Directory.Exists(packageRoot) && pending.Length != 0)
            return new("有未完成的恢复记录，增强组件缺失",
                $"检测到 {pending.Length} 份未结束的切换记录，当前无法核对增强组件。\n" +
                string.Join("\n", pending.Select(j => $"记录 {j.Id:N} · {j.State}")) +
                "\n请先恢复原来使用的同版本运行组件，再重新检查。记录已保留；此时不会启用新方案、停止进程或覆盖桌面设置。");
        if (!Directory.Exists(packageRoot)) return new("公开预览版未包含增强运行组件", "此下载包只包含 ClassicDesk 前端。第三方增强组件的公开分发材料尚在整理，当前不能启用真实桌面改造；预览、方案保存和停用配置生成仍可使用。");
        var check = WindowsShellActivationHost.CheckPackage(packageRoot, ReviewedRuntimeManifest);
        if (pending.Length > 1) return new("有多份未结束的切换记录", "为避免覆盖已有设置，需要先核对本机恢复记录。本次不进行切换。");
        if (pending.Length == 1)
        {
            var journal = pending[0];
            if (journal.State != ShellActivationState.Active)
                return new("上次切换需要检查", $"记录 {journal.Id:N}\n上次操作停在 {journal.State}，保留了原始数据。不会自动重试或覆盖当前设置。");
            if (!string.Equals(Path.GetFullPath(journal.Package.Root), packageRoot, StringComparison.OrdinalIgnoreCase))
                return new("另一份 ClassicDesk 正在管理增强", "请使用原运行目录恢复，避免启动第二个引擎。");
            var ticket = coordinator.CaptureRestoreConfirmation(journal.Id);
            return new("已有方案正在使用", "先恢复上次方案，再启用新方案。恢复只处理本工具确认拥有、且未被外部修改的设置。", CanRestore: true, RestoreTicket: ticket);
        }
        var modules = ShellBackendPlanner.CreatePlan(profile, new ShellBackendEnvironment(DateTime.MinValue, "X64", null, null, null, [], [], [], [], "unknown", [])).Modules.Where(m => m.Selected).ToArray();
        if (modules.Length == 0)
            return new("未选择增强功能", "请勾选需要的增强。仅选择原生居中布局或 Windows 11 原生样式时，不启动后台引擎；可在 Windows 设置中调整原生对齐。");
        var preparation = host.Inspect(check.Package, profile);
        if (preparation.Guards.StartAllBack != ActivationPresence.Absent)
            return new("现有桌面组件尚未退出", "检测到 StartAllBack 仍被 Explorer 加载，或无法完整核对其运行状态。停用勾选不等于组件已经卸载；即使已重启，也需要以重新检查结果为准。当前保留原桌面，不并行启用增强，也不会自动注销或重启系统。");
        if (preparation.Guards.OtherWindhawk != ActivationPresence.Absent)
            return new("已有增强引擎正在运行", "检测到另一个 Windhawk 实例，当前不能并行启动。请先处理已有实例，再检查此方案。");
        var confirmation = coordinator.CaptureConfirmation(check.Package, profile);
        return new("所选功能可以试用", $"本次按勾选启用 {modules.Length} 个增强模块，其余模块保持停用。" +
            (profile.SkipTaskbarLayout ? "保留当前任务栏对齐。" : "任务栏对齐将设为应用居中，并保存原值。") +
            "\n启用前备份本包设置；恢复会撤销本次组合。不会重启资源管理器。实际效果及整体占用尚未实机验收。", CanEnable: true, EnableTicket: confirmation);
    }
    ActivationJournal[] PendingRecords()
    {
        // Initial review is read-only. No directory or lock exists until an explicit transaction.
        if (!Directory.Exists(journalDirectory)) return [];
        WindowsShellActivationHost.RejectReparse(journalDirectory);
        var files = Directory.EnumerateFiles(journalDirectory, "*.json", SearchOption.TopDirectoryOnly).Take(513).ToArray();
        if (files.Length > 512) throw new InvalidDataException("恢复记录过多，请先整理已有记录。");
        var pending = new List<ActivationJournal>();
        foreach (var path in files)
        {
            WindowsShellActivationHost.RejectReparse(path);
            var bytes = WindowsShellActivationHost.ReadBounded(path, 16 * 1024 * 1024);
            var journal = JsonSerializer.Deserialize<ActivationJournal>(bytes) ?? throw new InvalidDataException("恢复记录为空。");
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || journal.Id != id || journal.SchemaVersion != 1 || !Enum.IsDefined(journal.State))
                throw new InvalidDataException("恢复记录无法识别，已保留原文件。");
            if (journal.State is not (ShellActivationState.Restored or ShellActivationState.RolledBack)) pending.Add(journal);
        }
        return pending.ToArray();
    }
}
