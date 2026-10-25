using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace ClassicDesk;

public sealed record ExplorerPrelaunchState(string Scope, int? Value, bool Supported = true);
public interface IExplorerPrelaunchHost
{
    ExplorerPrelaunchState Read();
    void Write(ExplorerPrelaunchState expected, int? value);
}

/// <summary>Uses the installed Folder Options definition, not an assumed build-number capability.
/// This controls future prelaunch only. It does not destroy or convert cached windows.</summary>
public sealed class WindowsExplorerPrelaunchHost : IExplorerPrelaunchHost
{
    const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string Definition = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Folder\ShouldPrelaunchFileExplorer";
    const string Name = "ShouldPrelaunchFileExplorer";

    public ExplorerPrelaunchState Read()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem || identity.User is null) throw new IOException("此设置必须由交互用户管理，不能写入系统服务账户。");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var definition = machine.OpenSubKey(Definition, false);
        if (definition is null) return new(identity.User.Value, null, false);
        // Honor Windows' own per-user 0/1 checkbox only. Unsupported/moved options fail without writing.
        if (definition?.GetValue("HKeyRoot") is not int hive || unchecked((uint)hive) != 0x80000001 ||
            definition.GetValue("RegPath") is not string path || !path.Equals(Advanced, StringComparison.OrdinalIgnoreCase) ||
            definition.GetValue("ValueName") is not string name || name != Name ||
            definition.GetValue("Type") is not string type || type != "checkbox" ||
            definition.GetValue("CheckedValue") is not int on || on != 1 ||
            definition.GetValue("UncheckedValue") is not int off || off != 0 ||
            definition.GetValue("DefaultValue") is not int fallback || fallback != 1)
            throw new NotSupportedException("当前系统未提供已识别的资源管理器预加载选项。");
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.OpenSubKey(Advanced, false) ?? throw new IOException("Explorer 设置不可用。");
        int? value = null;
        if (key.GetValueNames().Contains(Name, StringComparer.OrdinalIgnoreCase))
        {
            if (key.GetValueKind(Name) != RegistryValueKind.DWord || key.GetValue(Name) is not int number || number is not (0 or 1))
                throw new InvalidDataException("资源管理器预加载值不是受支持的 DWORD 0/1。");
            value = number;
        }
        return new(identity.User.Value, value);
    }

    public void Write(ExplorerPrelaunchState expected, int? value)
    {
        if (!expected.Supported) throw new NotSupportedException("系统没有此原生选项，不能写入。");
        if (value is not (null or 0 or 1)) throw new ArgumentOutOfRangeException(nameof(value));
        if (Read() != expected) throw new IOException("预加载选项或用户已变化，未写入。");
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.OpenSubKey(Advanced, true) ?? throw new IOException("Explorer 设置不可写。");
        if (Read() != expected) throw new IOException("提交前预加载选项已变化，未写入。");
        // These are conservative comparison gates, not an OS-level atomic compare/exchange.
        if (value.HasValue) key.SetValue(Name, value.Value, RegistryValueKind.DWord);
        else key.DeleteValue(Name, false);
        key.Flush();
        if (Read() != expected with { Value = value }) throw new IOException("预加载选项回读不一致，保留恢复记录。");
    }
}

public sealed record ExplorerPrelaunchJournal(int Schema, Guid Id, string Phase, ExplorerPrelaunchState Original);
public sealed record ExplorerPrelaunchReview(ExplorerPrelaunchState Current, string Revision,
    ExplorerPrelaunchJournal? Journal, bool CanPrepare, bool CanRestore);

/// <summary>Explicit per-user compatibility transaction. No engine, window, input, or boot operations.
/// Durable intent precedes a write; unknown writes remain pending and are never retried automatically.</summary>
public sealed class ExplorerPrelaunchController(IExplorerPrelaunchHost host, string directory)
{
    readonly string directory = Path.GetFullPath(directory);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassicDesk", "ExplorerPrelaunch");
    string JournalPath => Path.Combine(directory, "current.json");

    public ExplorerPrelaunchReview Review()
    {
        var current = host.Read(); Validate(current);
        var (journal, revision) = Load();
        bool prepare = current.Supported && (journal is null || journal.Phase == "restored");
        bool restore = current.Supported && journal is { Phase: "applied" } && current == journal.Original with { Value = 0 };
        return new(current, revision, journal, prepare, restore);
    }

    public static ExplorerPrelaunchController Current() => new(new WindowsExplorerPrelaunchHost(), DefaultDirectory);

    // Called by explicit activation/preparation, never by ordinary frontend startup.
    public string EnsureRibbon()
    {
        var review = Review();
        if (!review.Current.Supported && (review.Journal is null || review.Journal.Phase == "restored"))
            return "系统未提供窗口预加载选项，无需调整。";
        if (review.CanRestore) return "原生预加载已按现有恢复记录关闭，未重复修改。";
        if (!review.CanPrepare) throw new IOException("预加载设置存在未完成或冲突的记录；未启动增强，请先核对恢复记录。");
        return PrepareRibbon(review);
    }

    public string PrepareRibbon(ExplorerPrelaunchReview ticket)
    {
        using var lease = Lease(); var fresh = Check(ticket);
        if (!fresh.CanPrepare) throw new IOException("已有预加载恢复记录，请先核对或恢复，未重复写入。");
        if (fresh.Current.Value == 0) return "预加载已经关闭，未修改且未取得原设置归属。";
        if (fresh.Journal is not null) Archive(fresh.Journal);
        var journal = new ExplorerPrelaunchJournal(1, Guid.NewGuid(), "apply-intent", fresh.Current);
        var revision = Save(journal, fresh.Revision);
        host.Write(fresh.Current, 0);
        if (host.Read() != fresh.Current with { Value = 0 }) throw new IOException("预加载写入结果未确认，不会自动重试。");
        Save(journal with { Phase = "applied" }, revision);
        return "已保存原值并关闭未来窗口预加载；现有窗口未改动，Win10 布局、点击和开机仍需验收。";
    }

    public string Restore(ExplorerPrelaunchReview ticket)
    {
        using var lease = Lease(); var fresh = Check(ticket);
        if (!fresh.CanRestore || fresh.Journal is null) throw new IOException("当前值不属于已确认的本次修改，未覆盖设置。");
        var journal = fresh.Journal;
        var revision = Save(journal with { Phase = "restore-intent" }, fresh.Revision);
        host.Write(fresh.Current, journal.Original.Value);
        if (host.Read() != journal.Original) throw new IOException("预加载恢复结果未确认，不会自动重试。");
        Save(journal with { Phase = "restored" }, revision);
        return "原预加载选项已恢复并回读确认；没有关闭窗口或重启 Explorer。";
    }

    ExplorerPrelaunchReview Check(ExplorerPrelaunchReview ticket)
    {
        var fresh = Review();
        if (ticket.Current != fresh.Current || ticket.Revision != fresh.Revision) throw new IOException("检查后的设置或恢复记录已变化。");
        return fresh;
    }
    FileStream Lease()
    {
        RejectReparse(directory); Directory.CreateDirectory(directory); RejectReparse(directory);
        var path = Path.Combine(directory, ".lock"); RejectReparse(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    (ExplorerPrelaunchJournal? Journal, string Revision) Load()
    {
        RejectReparse(JournalPath);
        if (!File.Exists(JournalPath))
        {
            if (File.Exists(Path.Combine(directory, "history.marker")) ||
                Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any())
                throw new IOException("存在历史或未完成记录，当前恢复记录缺失；停止修改。");
            return (null, "missing");
        }
        var bytes = ReadBounded(JournalPath);
        var journal = JsonSerializer.Deserialize<ExplorerPrelaunchJournal>(bytes) ?? throw new InvalidDataException("恢复记录为空。");
        if (journal.Schema != 1 || journal.Id == Guid.Empty || journal.Original is null || !journal.Original.Supported || journal.Original.Value == 0 ||
            journal.Phase is not ("apply-intent" or "applied" or "restore-intent" or "restored"))
            throw new InvalidDataException("恢复记录无效。");
        Validate(journal.Original);
        return (journal, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    string Save(ExplorerPrelaunchJournal journal, string expected)
    {
        if (Load().Revision != expected) throw new IOException("恢复记录修订变化。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Json);
        string temp = Path.Combine(directory, $"intent-{Guid.NewGuid():N}.json");
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        if (expected != "missing" && Load().Revision != expected) throw new IOException("提交前恢复记录修订变化。");
        RejectReparse(JournalPath); File.Move(temp, JournalPath, expected != "missing");
        string marker = Path.Combine(directory, "history.marker"); RejectReparse(marker);
        if (!File.Exists(marker))
        {
            using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.WriteByte(1); file.Flush(true);
        }
        string revision = Convert.ToHexString(SHA256.HashData(bytes));
        if (Load().Revision != revision) throw new IOException("恢复记录落盘未确认。");
        return revision;
    }
    void Archive(ExplorerPrelaunchJournal journal)
    {
        string path = Path.Combine(directory, $"restored-{journal.Id:N}.json"); RejectReparse(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Json);
        if (File.Exists(path)) { if (!ReadBounded(path).SequenceEqual(bytes)) throw new IOException("历史记录冲突。"); return; }
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true);
    }
    static void Validate(ExplorerPrelaunchState state)
    {
        if (string.IsNullOrWhiteSpace(state.Scope) || state.Value is not (null or 0 or 1)) throw new InvalidDataException("预加载状态无效。");
    }
    static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 65536) throw new InvalidDataException("恢复记录过大。");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    static void RejectReparse(string path)
    {
        for (string? item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("恢复记录路径不能使用重解析链接。");
    }
}
