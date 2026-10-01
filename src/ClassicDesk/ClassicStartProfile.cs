using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicDesk;

public sealed record ClassicStartFolder(string Id, string Name, string Path);

/// <summary>Independent menu preferences. These never enable a shell engine or collect usage history.</summary>
public sealed record ClassicStartOptions(string Style = "win7", int IconSize = 24, int MaxPinned = 12,
    bool SortAscending = true, bool ShowEntryIcons = true, bool ShowUserHeading = true,
    string[]? PinnedIds = null, ClassicStartFolder[]? CustomFolders = null, string[]? VisiblePlaces = null)
{
    [JsonIgnore] public string[] EffectivePinnedIds => PinnedIds ?? [];
    [JsonIgnore] public ClassicStartFolder[] EffectiveCustomFolders => CustomFolders ?? [];
    [JsonIgnore] public string[] EffectiveVisiblePlaces => VisiblePlaces ?? ["user", "documents", "pictures", "music", "thispc", "settings"];

    public void Validate()
    {
        if (Style is not ("win7" or "win10" or "compact") || IconSize is not (16 or 24 or 32) || MaxPinned is < 0 or > 24)
            throw new InvalidDataException("开始菜单样式、图标尺寸或固定数量超出可选范围。");
        var pins = EffectivePinnedIds;
        if (pins.Length > 24 || pins.Any(id => !ValidAppId(id)) || pins.Distinct(StringComparer.Ordinal).Count() != pins.Length)
            throw new InvalidDataException("固定应用必须是目录快照中的唯一应用 ID，最多 24 项。");
        var folders = EffectiveCustomFolders;
        if (folders.Length > 16 || folders.Any(f => f is null) || folders.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != folders.Length)
            throw new InvalidDataException("自定义文件夹不能重复，最多 16 项。");
        foreach (var folder in folders)
        {
            if (folder.Id is null || !folder.Id.StartsWith("folder:", StringComparison.Ordinal) || folder.Id.Length is < 8 or > 71 ||
                folder.Id[7..].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') ||
                string.IsNullOrWhiteSpace(folder.Name) || folder.Name.Length > 80 || folder.Name.Any(char.IsControl))
                throw new InvalidDataException("自定义文件夹的 ID 或名称无效。");
            ClassicStartSafety.LocalPath(folder.Path);
        }
        var visible = EffectiveVisiblePlaces;
        if (visible.Length > ClassicStartPlaces.All.Count || visible.Any(id => !ClassicStartPlaces.All.Any(p => p.Id == id)) ||
            visible.Distinct(StringComparer.Ordinal).Count() != visible.Length)
            throw new InvalidDataException("系统入口包含未知或重复 ID。");
    }

    internal static bool ValidAppId(string? id) => id is not null && id.Length == 68 && id.StartsWith("app:", StringComparison.Ordinal) &&
        id[4..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal ClassicStartOptions Freeze() => this with { PinnedIds = [.. EffectivePinnedIds], CustomFolders = [.. EffectiveCustomFolders], VisiblePlaces = [.. EffectiveVisiblePlaces] };
}

public sealed record ClassicStartProfileSnapshot(ClassicStartOptions Options, string Revision);

/// <summary>Strict, bounded JSON; original-byte backups and optimistic revisions protect existing choices.</summary>
public static class ClassicStartProfileFile
{
    public const int MaximumBytes = 65536;
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "classic-start.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly HashSet<string> Keys = ["Style", "IconSize", "MaxPinned", "SortAscending", "ShowEntryIcons", "ShowUserHeading", "PinnedIds", "CustomFolders", "VisiblePlaces"];

    public static ClassicStartProfileSnapshot Load(string? path = null)
    {
        var bytes = Read(path ?? DefaultPath, missingAllowed: true);
        return new(bytes is null ? new ClassicStartOptions().Freeze() : Decode(bytes), Hash(bytes));
    }

    public static string Revision(string? path = null) => Hash(Read(path ?? DefaultPath, missingAllowed: true));
    public static ClassicStartOptions Import(string path) => Decode(Read(path, missingAllowed: false)!);

    public static ClassicStartProfileSnapshot Save(ClassicStartOptions options, string expectedRevision, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        var frozen = options.Freeze(); frozen.Validate();
        var target = Prepare(path ?? DefaultPath);
        using var held = Acquire(target + ".lock");
        var original = Read(target, missingAllowed: true);
        if (Hash(original) != expectedRevision) throw new IOException("开始菜单配置已被另一个窗口修改，请重新载入后保存。");
        // Never repair corrupt data by silently overwriting it, even if the caller knows its raw revision.
        if (original is not null) Decode(original);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frozen, JsonOptions);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("开始菜单配置不能超过 64 KB。");
        if (original is not null)
        {
            var backup = target + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N");
            using var backupStream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            backupStream.Write(original); backupStream.Flush(true);
        }
        WriteAtomic(target, bytes, overwrite: true);
        return new(frozen, Hash(bytes));
    }

    /// <summary>Export never replaces an existing file. Use Save with a revision for managed preferences.</summary>
    public static void Export(string path, ClassicStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var frozen = options.Freeze(); frozen.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frozen, JsonOptions);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("开始菜单配置不能超过 64 KB。");
        WriteAtomic(Prepare(path), bytes, overwrite: false);
    }

    static string Prepare(string path)
    {
        var target = ClassicStartSafety.LocalPath(path);
        ClassicStartSafety.NoReparse(target, requireExists: false);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        ClassicStartSafety.NoReparse(target, requireExists: false);
        return target;
    }

    static byte[]? Read(string path, bool missingAllowed)
    {
        var target = ClassicStartSafety.LocalPath(path);
        ClassicStartSafety.NoReparse(target, requireExists: false);
        try
        {
            using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("开始菜单配置不能超过 64 KB。");
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            if (memory.Length > MaximumBytes) throw new InvalidDataException("开始菜单配置不能超过 64 KB。");
            return memory.ToArray();
        }
        catch (FileNotFoundException) when (missingAllowed) { return null; }
        catch (DirectoryNotFoundException) when (missingAllowed) { return null; }
    }

    static string Hash(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static ClassicStartOptions Decode(byte[] bytes)
    {
        try
        {
            ReadOnlyMemory<byte> json = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) json = json[3..];
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            CheckKeys(doc.RootElement, Keys);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name is "PinnedIds" or "CustomFolders" or "VisiblePlaces")
                {
                    if (property.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("列表设置必须是 JSON 数组。");
                    if (property.Name == "CustomFolders")
                        foreach (var folder in property.Value.EnumerateArray()) CheckKeys(folder, ["Id", "Name", "Path"], requireAll: true);
                }
            }
            var options = doc.RootElement.Deserialize<ClassicStartOptions>() ?? throw new InvalidDataException("配置为空。");
            options.Validate(); return options.Freeze();
        }
        catch (JsonException ex) { throw new InvalidDataException("开始菜单配置损坏或字段类型不正确；原文件已保留。", ex); }
    }

    static void CheckKeys(JsonElement element, HashSet<string> allowed, bool requireAll = false)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("请选择 ClassicDesk 开始菜单 JSON 对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
            if (!allowed.Contains(p.Name) || !seen.Add(p.Name)) throw new InvalidDataException("配置含未知或重复参数。");
        if (seen.Count == 0 || requireAll && seen.Count != allowed.Count) throw new InvalidDataException("配置缺少必要参数。");
    }

    static FileStream Acquire(string path)
    {
        ClassicStartSafety.NoReparse(path, requireExists: false);
        var deadline = Environment.TickCount64 + 2000;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(20); }
        }
    }

    static void WriteAtomic(string target, byte[] bytes, bool overwrite)
    {
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            ClassicStartSafety.NoReparse(target, requireExists: false);
            File.Move(temp, target, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal static class ClassicStartSafety
{
    internal static string LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('%'))
            throw new InvalidDataException("只能使用明确的本地绝对路径，不能使用网络、设备或变量路径。");
        if (OperatingSystem.IsWindows() && (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))) throw new InvalidDataException("本地路径格式无效。");
        var relative = path[Path.GetPathRoot(path)!.Length..];
        if (relative.Contains(':') || relative.Split(['\\', '/']).Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("路径不能包含相对跳转、数据流或不明确的文件名。");
        if (OperatingSystem.IsWindows() && relative.Split(['\\', '/']).Any(p => p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Reserved(p)))
            throw new InvalidDataException("路径包含非法或设备文件名。");
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType is DriveType.Network or DriveType.NoRootDirectory)
            throw new InvalidDataException("不能使用网络映射盘或不存在的磁盘。");
        return full;
    }

    internal static void NoReparse(string path, bool requireExists)
    {
        // Inspect ancestors from the volume down before accessing a descendant through a junction.
        var ancestors = new Stack<string>(); var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            ancestors.Push(current);
            current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        while (ancestors.Count > 0)
        {
            current = ancestors.Pop();
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不能访问重解析点或链接路径：" + current);
            }
            catch (FileNotFoundException) when (!requireExists) { return; }
            catch (DirectoryNotFoundException) when (!requireExists) { return; }
        }
    }

    static bool Reserved(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
