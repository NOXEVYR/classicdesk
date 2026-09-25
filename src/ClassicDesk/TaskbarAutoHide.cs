using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace ClassicDesk;

public sealed record TaskbarAutoHideState(string Scope, uint Flags)
{
    public const uint AutoHide = 1;
    public bool Enabled => (Flags & AutoHide) != 0;
}
public interface ITaskbarAutoHideHost
{
    TaskbarAutoHideState Read();
    void Write(TaskbarAutoHideState expected, uint flags);
}

/// <summary>Current interactive desktop only. Never writes registry values or restarts Explorer.</summary>
public sealed class WindowsTaskbarAutoHideHost : ITaskbarAutoHideHost
{
    public TaskbarAutoHideState Read()
    {
        var (window, scope) = Ready();
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>(), Window = window };
        // GETSTATE has no error sentinel: zero is a valid state. Check the shell separately.
        uint flags = unchecked((uint)SHAppBarMessage(4, ref data).ToUInt64());
        if (Ready() != (window, scope)) throw new IOException("资源管理器已变化，请重新检查。");
        return new(scope, flags);
    }
    public void Write(TaskbarAutoHideState expected, uint flags)
    {
        if (((flags ^ expected.Flags) & ~TaskbarAutoHideState.AutoHide) != 0) throw new InvalidOperationException("只允许修改自动隐藏状态。");
        if (Read() != expected) throw new IOException("任务栏状态已变化，请重新检查。");
        var (window, scope) = Ready();
        if (scope != expected.Scope) throw new IOException("任务栏会话已变化。");
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>(), Window = window, Param = new IntPtr(unchecked((long)flags)) };
        // SETSTATE always returns TRUE according to the API contract. The coordinator must read back.
        SHAppBarMessage(10, ref data);
    }
    static (IntPtr Window, string Scope) Ready()
    {
        var window = FindWindow("Shell_TrayWnd", null);
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint pid) == 0) throw new IOException("Windows 任务栏尚未就绪。");
        using var explorer = Process.GetProcessById(checked((int)pid));
        using var self = Process.GetCurrentProcess();
        var expectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (explorer.SessionId != self.SessionId || !string.Equals(explorer.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("无法确认当前用户桌面的资源管理器身份。");
        foreach (ProcessModule module in explorer.Modules)
            if (module.ModuleName.Contains("StartAllBack", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Contains("StartIsBack", StringComparison.OrdinalIgnoreCase))
                throw new IOException("资源管理器仍加载 StartAllBack / StartIsBack，不能确认原生自动隐藏行为；请保留现有工具管理此设置。");
        if (SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 2, 1500, out _) == IntPtr.Zero) throw new IOException("Windows 任务栏未响应，请稍后重新检查。");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法确认当前用户。");
        return (window, $"{sid}/{self.SessionId}/{pid}/{explorer.StartTime.ToUniversalTime().Ticks}/{window.ToInt64()}");
    }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct AppBarData { public uint Size; public IntPtr Window; public uint Callback, Edge; public Rect Bounds; public IntPtr Param; }
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}

public sealed record TaskbarAutoHideJournal(int Schema, Guid Id, string Phase, TaskbarAutoHideState Original, uint DesiredFlags);
public sealed record TaskbarAutoHideReview(TaskbarAutoHideState Current, string Revision, TaskbarAutoHideJournal? Journal,
    string Title, string Detail, bool CanApply, bool CanRestore);

/// <summary>Durable intent before every host write; ambiguous writes are never automatically retried.</summary>
public sealed class TaskbarAutoHideController
{
    readonly ITaskbarAutoHideHost host;
    readonly string directory, journalPath;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "TaskbarAutoHide");
    public TaskbarAutoHideController(ITaskbarAutoHideHost host, string directory)
    {
        this.host = host; this.directory = Path.GetFullPath(directory); journalPath = Path.Combine(this.directory, "current.json");
    }
    public TaskbarAutoHideReview Review()
    {
        var (journal, revision) = Load(); var state = host.Read();
        if (journal is null || journal.Phase == "restored") return new(state, revision, journal, "任务栏状态已读取", "仅检查当前桌面的自动隐藏状态；尚未提交更改。", true, false);
        if (journal.Phase is "apply-intent" or "restore-intent") return new(state, revision, journal, "上次操作结果不确定", "保留了操作前记录。为避免覆盖设置，本窗口不会重试或自动恢复。请通过 Windows 任务栏设置核对，并保留日志供人工处理。", false, false);
        bool owned = state.Scope == journal.Original.Scope && state.Flags == journal.DesiredFlags;
        return new(state, revision, journal, owned ? "本次设置已回读确认" : "任务栏状态或会话已变化",
            owned ? "可先恢复本次修改，再提交其他设置。实际隐藏与唤出效果仍需检查。" : "保留原记录；当前状态不再符合恢复条件，不会覆盖外部修改或在新会话中沿用旧确认。", false, owned);
    }
    public string Apply(TaskbarAutoHideReview ticket, bool enabled)
    {
        using var lease = Lease(); var fresh = CheckTicket(ticket);
        if (!fresh.CanApply) throw new InvalidOperationException("请先处理或恢复上次记录。");
        uint desired = enabled ? fresh.Current.Flags | TaskbarAutoHideState.AutoHide : fresh.Current.Flags & ~TaskbarAutoHideState.AutoHide;
        if (desired == fresh.Current.Flags) return "状态已一致，本次没有修改任务栏。";
        var journal = new TaskbarAutoHideJournal(1, Guid.NewGuid(), "apply-intent", fresh.Current, desired);
        if (fresh.Journal is not null) Archive(fresh.Journal);
        var revision = Save(journal, fresh.Revision);
        host.Write(fresh.Current, desired);
        var after = host.Read();
        if (after.Scope != fresh.Current.Scope || after.Flags != desired) throw new IOException("提交后的状态未能确认。操作意图已保留，不会自动重试或恢复。");
        Save(journal with { Phase = "applied" }, revision);
        return "自动隐藏状态已回读确认；请检查鼠标触边时的隐藏与唤出效果。";
    }
    public string Restore(TaskbarAutoHideReview ticket)
    {
        using var lease = Lease(); var fresh = CheckTicket(ticket);
        if (!fresh.CanRestore || fresh.Journal is null) throw new InvalidOperationException("当前状态不满足恢复条件，原记录已保留。");
        var journal = fresh.Journal;
        var revision = Save(journal with { Phase = "restore-intent" }, fresh.Revision);
        host.Write(fresh.Current, journal.Original.Flags);
        if (host.Read() != journal.Original) throw new IOException("恢复后的状态未能确认。恢复意图已保留，不会自动重试。");
        Save(journal with { Phase = "restored" }, revision);
        return "原自动隐藏状态已回读确认；请检查实际桌面效果。";
    }
    TaskbarAutoHideReview CheckTicket(TaskbarAutoHideReview ticket)
    {
        var fresh = Review();
        if (fresh.Revision != ticket.Revision || fresh.Current != ticket.Current) throw new IOException("检查后状态或恢复记录已变化，请重新检查。");
        return fresh;
    }
    FileStream Lease()
    {
        RejectReparse(directory); Directory.CreateDirectory(directory); RejectReparse(directory);
        var path = Path.Combine(directory, ".lock"); RejectReparse(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    (TaskbarAutoHideJournal? Journal, string Revision) Load()
    {
        RejectReparse(directory); RejectReparse(journalPath);
        if (!File.Exists(journalPath))
        {
            if (File.Exists(Path.Combine(directory, "history.marker")) || (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any())) throw new IOException("当前恢复记录缺失，但存在历史或临时记录；请先人工核对，未创建新记录。");
            return (null, "missing");
        }
        var bytes = ReadBounded(journalPath);
        var journal = JsonSerializer.Deserialize<TaskbarAutoHideJournal>(bytes) ?? throw new InvalidDataException("自动隐藏恢复记录为空。");
        if (journal.Schema != 1 || journal.Id == Guid.Empty || journal.Original is null || string.IsNullOrWhiteSpace(journal.Original.Scope) ||
            journal.Phase is not ("apply-intent" or "applied" or "restore-intent" or "restored") ||
            (journal.DesiredFlags ^ journal.Original.Flags) != TaskbarAutoHideState.AutoHide)
            throw new InvalidDataException("自动隐藏恢复记录无效，已保留原文件。");
        return (journal, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    string Save(TaskbarAutoHideJournal journal, string expected)
    {
        if (Load().Revision != expected) throw new IOException("恢复记录已变化。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Json);
        var temp = Path.Combine(directory, $"intent-{Guid.NewGuid():N}.json");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        // For a first journal, the temporary file itself is intentionally considered recovery evidence.
        if (expected != "missing" && Load().Revision != expected) throw new IOException("提交前恢复记录已变化。");
        RejectReparse(journalPath);
        File.Move(temp, journalPath, expected != "missing");
        var marker = Path.Combine(directory, "history.marker"); RejectReparse(marker);
        if (!File.Exists(marker)) { using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.WriteByte(1); file.Flush(true); }
        var revision = Convert.ToHexString(SHA256.HashData(bytes));
        if (Load().Revision != revision) throw new IOException("恢复记录落盘回读未能确认，停止后续操作。");
        return revision;
    }
    void Archive(TaskbarAutoHideJournal journal)
    {
        var path = Path.Combine(directory, $"restored-{journal.Id:N}.json"); RejectReparse(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Json);
        if (File.Exists(path)) { if (!ReadBounded(path).SequenceEqual(bytes)) throw new IOException("历史恢复记录冲突。"); return; }
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true);
    }
    static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 65536) throw new InvalidDataException("自动隐藏恢复记录过大。");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    static void RejectReparse(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("恢复记录路径不能使用重解析链接。");
    }
}
