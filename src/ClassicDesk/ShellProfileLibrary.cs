using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicDesk;

public sealed record ShellLibraryEntry(Guid Id, string Name, ShellProfile Profile, bool Archived, DateTimeOffset UpdatedUtc);
public sealed record ShellLibrarySnapshot(IReadOnlyList<ShellLibraryEntry> Entries, string Revision);

/// <summary>On-demand local storage. Names are metadata, never filesystem paths.</summary>
public sealed class ShellProfileLibrary(string path)
{
    const int MaxBytes = 1024 * 1024, MaxEntries = 128;
    readonly string filePath = Path.GetFullPath(path);
    sealed record Document(int SchemaVersion, ShellLibraryEntry[] Entries);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public ShellLibrarySnapshot Read()
    {
        if (!File.Exists(filePath)) return new(Array.Empty<ShellLibraryEntry>(), "missing");
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new InvalidDataException("方案库超过 1 MB，已保留原文件。");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        var doc = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new InvalidDataException("方案库为空。");
        if (doc.SchemaVersion != 1 || doc.Entries is null) throw new InvalidDataException("方案库版本无法识别。");
        Validate(doc.Entries);
        return new(Array.AsReadOnly(doc.Entries), Hash(bytes));
    }
    public ShellLibrarySnapshot Add(ShellLibrarySnapshot snapshot, string name, ShellProfile profile)
        => Write(snapshot, snapshot.Entries.Append(new(Guid.NewGuid(), CleanName(name), profile, false, DateTimeOffset.UtcNow)).ToArray());
    public ShellLibrarySnapshot Rename(ShellLibrarySnapshot snapshot, Guid id, string name)
        => Replace(snapshot, id, e => e with { Name = CleanName(name), UpdatedUtc = DateTimeOffset.UtcNow });
    public ShellLibrarySnapshot Update(ShellLibrarySnapshot snapshot, Guid id, ShellProfile profile)
        => Replace(snapshot, id, e => e.Archived ? throw new InvalidOperationException("请先恢复已归档方案。") : e with { Profile = profile, UpdatedUtc = DateTimeOffset.UtcNow });
    public ShellLibrarySnapshot SetArchived(ShellLibrarySnapshot snapshot, Guid id, bool archived)
        => Replace(snapshot, id, e => e with { Archived = archived, UpdatedUtc = DateTimeOffset.UtcNow });
    public ShellLibrarySnapshot Duplicate(ShellLibrarySnapshot snapshot, Guid id)
    {
        var entry = Find(snapshot, id);
        for (int i = 1; i <= MaxEntries + 1; i++)
        {
            var suffix = " · 副本 " + i; var name = entry.Name[..Math.Min(entry.Name.Length, 40 - suffix.Length)] + suffix;
            if (!snapshot.Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))) return Add(snapshot, name, entry.Profile);
        }
        throw new InvalidOperationException("方案库已满。");
    }
    public static ShellLibraryEntry Find(ShellLibrarySnapshot snapshot, Guid id)
        => snapshot.Entries.SingleOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("方案不存在，请刷新方案库。");
    ShellLibrarySnapshot Replace(ShellLibrarySnapshot snapshot, Guid id, Func<ShellLibraryEntry, ShellLibraryEntry> change)
    {
        var entry = Find(snapshot, id);
        return Write(snapshot, snapshot.Entries.Select(e => e.Id == id ? change(entry) : e).ToArray());
    }
    static string CleanName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl)) throw new InvalidDataException("方案名称需为 1–40 个字符，不能包含换行。");
        return name;
    }
    static void Validate(ShellLibraryEntry[] entries)
    {
        if (entries.Length > MaxEntries) throw new InvalidDataException("方案库最多保留 128 份方案（含归档）。");
        var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e is null || e.Id == Guid.Empty || !ids.Add(e.Id) || e.Name != CleanName(e.Name) || !names.Add(e.Name)) throw new InvalidDataException("方案名称或标识为空、重复或无效。");
            if (e.Profile is null) throw new InvalidDataException("方案参数缺失。");
            e.Profile.Validate();
        }
    }
    ShellLibrarySnapshot Write(ShellLibrarySnapshot snapshot, ShellLibraryEntry[] entries)
    {
        Validate(entries);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, entries), Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("方案库超过大小限制。");
        var directory = Path.GetDirectoryName(filePath)!; Directory.CreateDirectory(directory);
        using var coordination = new FileStream(filePath + ".save.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        void CheckRevision() { if (Read().Revision != snapshot.Revision) throw new IOException("方案库已在其他窗口改变，请刷新后重试；当前草稿仍保留。"); }
        CheckRevision();
        var temporary = Path.Combine(directory, ".library-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            CheckRevision();
            if (snapshot.Revision == "missing") File.Move(temporary, filePath, false);
            else ActivationFileReplace.Commit(temporary, filePath, CheckRevision);
            return new(Array.AsReadOnly(entries), Hash(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
