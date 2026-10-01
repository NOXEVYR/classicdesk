using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClassicDesk;

public sealed record ClassicStartApp(string Id, string Name, string Category, string SourcePath);

/// <summary>A synthetic snapshot supports previews but cannot authorize application launch.</summary>
public sealed class ClassicStartCatalogSnapshot
{
    public IReadOnlyList<ClassicStartApp> Apps { get; }
    public IReadOnlyList<string> Errors { get; }
    internal IReadOnlyList<string> AllowedRoots { get; }

    public ClassicStartCatalogSnapshot(IEnumerable<ClassicStartApp> apps, IEnumerable<string>? errors = null)
        : this(apps, errors ?? [], []) { }

    internal ClassicStartCatalogSnapshot(IEnumerable<ClassicStartApp> apps, IEnumerable<string> errors, IEnumerable<string> roots)
    {
        Apps = Array.AsReadOnly(apps.ToArray());
        Errors = Array.AsReadOnly(errors.ToArray());
        AllowedRoots = Array.AsReadOnly(roots.ToArray());
    }
}

/// <summary>Explicit read-only discovery of local shortcut files. Shortcut contents are never read or executed.</summary>
public static class ClassicStartCatalog
{
    public const int MaximumEntries = 10000;
    public const int MaximumDirectories = 4096;
    public const int MaximumScanItems = 25000;
    static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    static readonly StringComparer NameComparer = CultureInfo.GetCultureInfo("zh-CN").CompareInfo.GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth);

    public static ClassicStartCatalogSnapshot Scan(IEnumerable<string>? roots = null)
    {
        roots ??= [Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)];
        var allowed = new List<string>(); var errors = new List<string>(); var apps = new List<ClassicStartApp>();
        var seen = new HashSet<string>(PathComparer); var visited = new HashSet<string>(PathComparer); var examined = 0;
        foreach (var supplied in roots)
        {
            string root;
            try
            {
                root = Path.TrimEndingDirectorySeparator(ClassicStartSafety.LocalPath(supplied));
                ClassicStartSafety.NoReparse(root, requireExists: true);
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("开始菜单目录不存在：" + root);
                if (allowed.Contains(root, PathComparer)) continue;
                allowed.Add(root);
            }
            catch (Exception ex) when (Expected(ex)) { errors.Add("扫描目录失败：" + supplied + " — " + ex.Message); continue; }
            var stack = new Stack<(string Path, int Depth)>(); stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (directory, depth) = stack.Pop();
                if (!visited.Add(directory)) continue;
                if (visited.Count > MaximumDirectories) { errors.Add("目录数已达到扫描上限，结果可能不完整。"); return new(apps, errors, allowed); }
                if (depth > 64) { errors.Add("目录层级超过扫描上限：" + directory); continue; }
                try
                {
                    ClassicStartSafety.NoReparse(directory, requireExists: true);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (++examined > MaximumScanItems) { errors.Add("项目数已达到扫描上限，结果可能不完整。"); return new(apps, errors, allowed); }
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) { errors.Add("已跳过重解析点：" + entry); continue; }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (stack.Count >= MaximumDirectories) { errors.Add("待扫描目录数已达到上限，结果可能不完整。"); return new(apps, errors, allowed); }
                                stack.Push((entry, depth + 1)); continue;
                            }
                            if (!Supported(entry) || !seen.Add(entry)) continue;
                            if (apps.Count >= MaximumEntries) { errors.Add("应用数已达到扫描上限，结果可能不完整。"); return new(apps, errors, allowed); }
                            var canonical = ClassicStartSafety.LocalPath(entry);
                            var relative = Path.GetRelativePath(root, Path.GetDirectoryName(canonical)!);
                            var category = relative == "." ? "应用" : relative.Replace('\\', '/');
                            apps.Add(new(IdFor(canonical), Path.GetFileNameWithoutExtension(canonical), category, canonical));
                        }
                        catch (Exception ex) when (Expected(ex)) { errors.Add("读取项目失败：" + entry + " — " + ex.Message); }
                    }
                }
                catch (Exception ex) when (Expected(ex)) { errors.Add("读取目录失败：" + directory + " — " + ex.Message); }
            }
        }
        return new(apps, errors, allowed);
    }

    public static IReadOnlyList<ClassicStartApp> Query(ClassicStartCatalogSnapshot snapshot, ClassicStartOptions options, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(options); options.Validate();
        var tokens = (query ?? "").Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var found = snapshot.Apps.Where(app => tokens.All(token => Contains(app.Name, token) || Contains(app.Category, token)));
        return Array.AsReadOnly((options.SortAscending
            ? found.OrderBy(a => a.Category, NameComparer).ThenBy(a => a.Name, NameComparer).ThenBy(a => a.Id, StringComparer.Ordinal)
            : found.OrderByDescending(a => a.Category, NameComparer).ThenByDescending(a => a.Name, NameComparer).ThenBy(a => a.Id, StringComparer.Ordinal)).ToArray());
    }

    public static IReadOnlyList<ClassicStartApp> Pinned(ClassicStartCatalogSnapshot snapshot, ClassicStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(options); options.Validate();
        var apps = snapshot.Apps.ToDictionary(a => a.Id, StringComparer.Ordinal);
        return Array.AsReadOnly(options.EffectivePinnedIds.Where(apps.ContainsKey).Take(options.MaxPinned).Select(id => apps[id]).ToArray());
    }

    /// <summary>Resolve immediately before a user click launch. This method does not start a process.</summary>
    public static string ResolveLaunch(ClassicStartCatalogSnapshot snapshot, string id)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ClassicStartOptions.ValidAppId(id)) throw new InvalidDataException("应用 ID 格式无效。");
        var matches = snapshot.Apps.Where(a => a.Id == id).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("应用不在当前目录快照中或 ID 不唯一，请重新扫描。");
        var path = ClassicStartSafety.LocalPath(matches[0].SourcePath);
        if (IdFor(path) != id || !Supported(path) || !snapshot.AllowedRoots.Any(root => Within(path, root)))
            throw new InvalidDataException("应用路径不属于允许的开始菜单目录。");
        ClassicStartSafety.NoReparse(path, requireExists: true);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 || !File.Exists(path)) throw new FileNotFoundException("应用快捷方式已移除，请重新扫描。", path);
        return path;
    }

    static bool Within(string path, string root) => path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url" or ".appref-ms";
    static string IdFor(string path)
    {
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        return "app:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }
    static bool Contains(string text, string token) => CultureInfo.GetCultureInfo("zh-CN").CompareInfo.IndexOf(text.Normalize(NormalizationForm.FormKC), token, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth) >= 0;
    static bool Expected(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException;
}

public sealed record ClassicStartPlace(string Id, string Name);

/// <summary>Only these fixed system targets or validated custom directories may be resolved.</summary>
public static class ClassicStartPlaces
{
    public static IReadOnlyList<ClassicStartPlace> All { get; } = Array.AsReadOnly(new[] {
        new ClassicStartPlace("user", "个人文件夹"), new ClassicStartPlace("documents", "文档"), new ClassicStartPlace("pictures", "图片"),
        new ClassicStartPlace("music", "音乐"), new ClassicStartPlace("videos", "视频"), new ClassicStartPlace("downloads", "下载"),
        new ClassicStartPlace("thispc", "此电脑"), new ClassicStartPlace("settings", "设置")
    });

    public static IReadOnlyList<ClassicStartPlace> Visible(ClassicStartOptions options)
    {
        options.Validate();
        return Array.AsReadOnly(options.EffectiveVisiblePlaces.Select(id => All.Single(p => p.Id == id))
            .Concat(options.EffectiveCustomFolders.Select(f => new ClassicStartPlace(f.Id, f.Name))).ToArray());
    }

    public static string Resolve(ClassicStartOptions options, string id)
    {
        options.Validate();
        if (options.EffectiveVisiblePlaces.Contains(id, StringComparer.Ordinal))
            return id switch {
                "user" => "shell:Profile", "documents" => "shell:Personal", "pictures" => "shell:My Pictures", "music" => "shell:My Music",
                "videos" => "shell:My Video", "downloads" => "shell:Downloads", "thispc" => "shell:MyComputerFolder", "settings" => "ms-settings:",
                _ => throw new InvalidDataException("未知系统入口。")
            };
        var folder = options.EffectiveCustomFolders.SingleOrDefault(f => f.Id == id) ?? throw new InvalidDataException("入口不在当前菜单配置中。");
        var path = ClassicStartSafety.LocalPath(folder.Path);
        ClassicStartSafety.NoReparse(path, requireExists: true);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("自定义文件夹不存在：" + path);
        return path;
    }
}
