using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ClassicDesk;

public sealed record ShellServiceFile(string Path, long Bytes, string Sha256);
public sealed record ShellServiceLayoutSource(string Installation, string ReceiptSha256);
public sealed record ShellServiceBundle(int SchemaVersion, string State, string ServiceName, string OwnerSid,
    ColdUpgradeSource Source, string RuntimeManifest, string HostSha256, string EngineInclude,
    IReadOnlyList<ShellServiceFile> Files,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ShellProfile? TargetProfile = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ShellServiceLayoutSource? LayoutSource = null);

/// <summary>Offline, account-specific preparation. Does not register, start or stop a service,
/// write the registry, change the live runtime, or change startup preferences.</summary>
public static class ShellServicePackage
{
    public const string ServiceName = "ClassicDeskShell";
    public const string ReviewedHostSha256 = "14603CC429E3A109AAD6133E163131DCEC890F2F8F7D614554B435CE2468A67D";
    public const string EngineInclude = @"%SystemRoot%\System32\winlogon.exe|%SystemRoot%\System32\userinit.exe|%SystemRoot%\explorer.exe|%SystemRoot%\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe";
    static readonly Encoding Ini = new UnicodeEncoding(false, true);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ShellServiceBundle Prepare(string sourceRuntime, string serviceExe, string destination, string journals)
    {
        if (!Environment.Is64BitOperatingSystem || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new InvalidOperationException("此启动组件仅支持已核对的 Windows x64。");
        var source = new WindowsColdUpgradeHost(journals).ReadSource(sourceRuntime);
        if (source.Package.ManifestSha256 != ShellNativeController.ReviewedRuntimeManifest)
            throw new InvalidOperationException("先完成当前新版的切换，再准备开机组件。");
        serviceExe = Path.GetFullPath(serviceExe);
        WindowsShellActivationHost.RejectReparse(serviceExe);
        if (Hash(serviceExe) != ReviewedHostSha256) throw new InvalidDataException("启动程序与已核对构建不符。");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确认当前用户。");
        if (!Path.IsPathFullyQualified(destination) || destination.StartsWith(@"\\")) throw new InvalidDataException("必须指定本地绝对新目录。");
        destination = Path.GetFullPath(destination);
        WindowsShellActivationHost.RejectReparse(destination);
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("父目录不存在。");
        if (!CreateDirectory(destination, 0)) throw new IOException("预备目录必须全新且不存在。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        var runtime = Path.Combine(destination, "Runtime");
        ShellBackendConfig.CreateDisabledConfiguration(runtime, source.AppliedProfile);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source.Package.Root, "runtime-assets-manifest.json")));
        foreach (var asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            var relative = asset.GetProperty("path").GetString()!;
            CopyChecked(source.Package.Root, runtime, relative, asset.GetProperty("bytes").GetInt64(), asset.GetProperty("sha256").GetString()!);
        }
        File.Copy(Path.Combine(source.Package.Root, "runtime-assets-manifest.json"), Path.Combine(runtime, "runtime-assets-manifest.json"));
        ShellNativeController.CheckReviewedPackage(runtime);
        // Reuse only settings validated by the existing profile-to-module compiler.
        var environment = new ShellBackendEnvironment(DateTime.UtcNow, "X64", null, null, null, [], [], [], [], "unknown", []);
        var plan = ShellBackendPlanner.CreatePlan(source.AppliedProfile, environment, runtime);
        foreach (var mod in plan.Modules)
            File.WriteAllText(Path.Combine(runtime, "AppData", "Engine", "Mods", mod.Id + ".ini"), ShellBackendConfig.BuildModIni(mod, mod.Selected, DateTimeOffset.UnixEpoch), Ini);
        File.WriteAllText(Path.Combine(runtime, "AppData", "Engine", "settings.ini"),
            "[Settings]\r\nSafeMode=0\r\nInclude=" + EngineInclude + "\r\nExclude=*\r\nInjectIntoCriticalProcesses=0\r\nInjectIntoGames=0\r\nInjectIntoIncompatiblePrograms=0\r\n", Ini);
        // Warm caches contain only PDBs and module cache INIs. No process status,
        // task logs, executable code, saved credentials or user documents are copied.
        long cacheBytes = 0; int cacheCount = 0;
        var cacheRoot = Path.Combine(source.Package.Root, "AppData", "Engine");
        foreach (var directory in new[] { "Symbols", "ModsWritable" })
        {
            var folder = Path.Combine(cacheRoot, directory);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in SafeFiles(folder, directory == "Symbols"))
            {
                if (directory == "Symbols" ? !file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) :
                    !plan.Modules.Any(m => Path.GetFileName(file) == m.Id + ".ini")) continue;
                var relative = Path.GetRelativePath(source.Package.Root, file);
                var length = new FileInfo(file).Length;
                cacheBytes += length;
                if (++cacheCount > 256 || cacheBytes > 128 * 1024 * 1024) throw new InvalidDataException("符号缓存超出预备范围。");
                CopyChecked(source.Package.Root, runtime, relative, length, Hash(file));
            }
        }
        File.Copy(serviceExe, Path.Combine(destination, "ClassicDeskShell.exe"));
        var files = SafeFiles(destination, true).Order(StringComparer.Ordinal).Select(path =>
            new ShellServiceFile(Path.GetRelativePath(destination, path).Replace('\\', '/'), new FileInfo(path).Length, Hash(path))).ToArray();
        var bundle = new ShellServiceBundle(1, "prepared-not-installed", ServiceName, sid, source,
            source.Package.ManifestSha256, ReviewedHostSha256, EngineInclude, files);
        File.WriteAllText(Path.Combine(destination, "service-package.json"), JsonSerializer.Serialize(bundle, Json), new UTF8Encoding(false));
        return Verify(destination);
    }

    public static ShellServiceBundle Verify(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var bytes = WindowsShellActivationHost.ReadBounded(Path.Combine(folder, "service-package.json"), 2 * 1024 * 1024);
        var bundle = JsonSerializer.Deserialize<ShellServiceBundle>(bytes) ?? throw new InvalidDataException("启动组件清单为空。");
        if (bundle.SchemaVersion != 1 || bundle.State != "prepared-not-installed" || bundle.ServiceName != ServiceName || bundle.RuntimeManifest != ShellNativeController.ReviewedRuntimeManifest ||
            bundle.HostSha256 != ReviewedHostSha256 || bundle.EngineInclude != EngineInclude || bundle.Files.Count is < 25 or > 512)
            throw new InvalidDataException("启动组件清单与已核对构建不符。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in bundle.Files)
        {
            var path = Within(folder, file.Path);
            if (file.Bytes < 0 || file.Bytes > 64 * 1024 * 1024 || (total += file.Bytes) > 256 * 1024 * 1024 ||
                !seen.Add(file.Path.Replace('\\', '/')) || new FileInfo(path).Length != file.Bytes || Hash(path) != file.Sha256)
                throw new InvalidDataException("启动组件文件被修改或重复：" + file.Path);
        }
        var actual = SafeFiles(folder, true).Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/')).Where(p => p != "service-package.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(seen) || Hash(Path.Combine(folder, "ClassicDeskShell.exe")) != ReviewedHostSha256)
            throw new InvalidDataException("启动组件包含遗漏或额外文件。");
        ShellNativeController.CheckReviewedPackage(Path.Combine(folder, "Runtime"));
        if ((bundle.TargetProfile is null) != (bundle.LayoutSource is null))
            throw new InvalidDataException("开机方案更新缺少来源或目标。");
        if (bundle.LayoutSource is not null && (!Path.IsPathFullyQualified(bundle.LayoutSource.Installation) ||
            !System.Text.RegularExpressions.Regex.IsMatch(bundle.LayoutSource.ReceiptSha256, "^[A-F0-9]{64}$")))
            throw new InvalidDataException("开机方案来源记录无效。");
        VerifyConfiguration(Path.Combine(folder, "Runtime"), bundle.TargetProfile ?? bundle.Source.AppliedProfile);
        return bundle;
    }
    public static void VerifyConfiguration(string runtime, ShellProfile profile)
    {
        profile.Validate();
        var environment = new ShellBackendEnvironment(DateTime.UtcNow, "X64", null, null, null, [], [], [], [], "unknown", []);
        var modules = ShellBackendPlanner.CreatePlan(profile, environment, runtime).Modules;
        var expected = modules.ToDictionary(m => "AppData/Engine/Mods/" + m.Id + ".ini",
            m => ShellBackendConfig.BuildModIni(m, m.Selected, DateTimeOffset.UnixEpoch));
        expected["Engine/1.7.3/engine.ini"] = "[Storage]\r\nPortable=1\r\nAppDataPath=..\\..\\AppData\\Engine\r\n";
        expected["AppData/Engine/settings.ini"] = "[Settings]\r\nSafeMode=0\r\nInclude=" + EngineInclude + "\r\nExclude=*\r\nInjectIntoCriticalProcesses=0\r\nInjectIntoGames=0\r\nInjectIntoIncompatiblePrograms=0\r\n";
        foreach (var item in expected)
        {
            var path = Within(runtime, item.Key);
            var actual = WindowsShellActivationHost.ReadBounded(path, 512 * 1024);
            if (!actual.SequenceEqual(Ini.GetPreamble().Concat(Ini.GetBytes(item.Value))))
                throw new InvalidDataException("开机配置与已应用方案不一致：" + item.Key);
        }
        var modDirectory = Within(runtime, "AppData/Engine/Mods");
        if (Directory.GetFiles(modDirectory, "*.ini").Length != modules.Count)
            throw new InvalidDataException("出现未知的开机模块配置。");
    }
    static IEnumerable<string> SafeFiles(string root, bool recursive)
    {
        WindowsShellActivationHost.RejectReparse(root);
        foreach (var file in Directory.EnumerateFiles(root)) { WindowsShellActivationHost.RejectReparse(file); yield return file; }
        if (recursive) foreach (var directory in Directory.EnumerateDirectories(root))
            foreach (var file in SafeFiles(directory, true)) yield return file;
    }
    static string Within(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidDataException("清单路径无效。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("清单越界。");
        WindowsShellActivationHost.RejectReparse(path);return path;
    }
    static string Hash(string path)
    {
        WindowsShellActivationHost.RejectReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    static void CopyChecked(string source, string target, string relative, long bytes, string hash)
    {
        var from = Within(source, relative); var to = Within(target, relative);
        if (new FileInfo(from).Length != bytes || !Hash(from).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("来源资产变化。");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!); WindowsShellActivationHost.RejectReparse(to);
        File.Copy(from, to, false);
        if (new FileInfo(to).Length != bytes || !Hash(to).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("资产复制校验失败。");
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateDirectory(string path, nint security);
}
