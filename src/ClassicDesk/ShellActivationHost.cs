using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ClassicDesk;

public interface IActivationAlignment
{
    ActivationItemState Read();
    ActivationItemState CompareExchange(ActivationItemState expected, bool exists, string data);
}
public sealed record ActivationEnvironmentObservation(string Revision, ActivationPresence StartAllBack, ActivationPresence OtherWindhawk);
public interface IActivationEnvironment
{
    ActivationEnvironmentObservation Inspect(ActivationDaemonIdentity? allowedDaemon);
}
public interface IActivationProcesses
{
    ActivationStartResult Start(VerifiedActivationPackage package, string ownershipToken, Action verifyBeforeStart);
    ActivationDaemonHealth Inspect(ActivationDaemonIdentity identity);
    ActivationStopResult Stop(ActivationDaemonIdentity identity);
}
public sealed record ActivationPackageCheck(VerifiedActivationPackage Package, string ManifestPath, int VerifiedAssets, long VerifiedBytes);

/// <summary>Real filesystem adapter with explicitly injected registry/environment/process seams.
/// Construction is inert. CreateForWindows opts into native adapters; callers must not use it in fake tests.
/// No method auto-discovers a package and then activates it. SHA-256 identifies reviewed bytes, not a signature.
/// File leases coordinate this package's cooperating writers; external writers can still race revision checks.</summary>
public sealed class WindowsShellActivationHost(IActivationAlignment alignment, IActivationEnvironment environment, IActivationProcesses processes) : IActivationHost
{
    const string Metadata = ".classicdesk-activation";
    static readonly Dictionary<ActivationTarget, string> Targets = new()
    {
        [ActivationTarget.StartButtonMod] = "AppData/Engine/Mods/taskbar-start-button-position.ini",
        [ActivationTarget.IconSizeMod] = "AppData/Engine/Mods/taskbar-icon-size.ini",
        [ActivationTarget.ExplorerFrameMod] = "AppData/Engine/Mods/explorer-frame-classic.ini",
        [ActivationTarget.ContextMenuMod] = "AppData/Engine/Mods/explorer-context-menu-classic.ini",
        [ActivationTarget.MainSettings] = "AppData/settings.ini", [ActivationTarget.EngineSettings] = "AppData/Engine/settings.ini"
    };
    static readonly string[] RequiredAssets = ["windhawk.exe", "windhawk-x64-helper.exe", "Engine/1.7.3/32/windhawk.dll", "Engine/1.7.3/64/windhawk.dll",
        "Engine/1.7.3/32/msdia140_windhawk.dll", "Engine/1.7.3/32/symsrv_windhawk.dll", "Engine/1.7.3/32/symsrv.yes",
        "Engine/1.7.3/64/msdia140_windhawk.dll", "Engine/1.7.3/64/symsrv_windhawk.dll", "Engine/1.7.3/64/symsrv.yes",
        "AppData/Engine/Mods/64/libc++.whl", "AppData/Engine/Mods/64/libunwind.whl", "AppData/Engine/Mods/64/windhawk-mod-shim.dll",
        "AppData/Engine/Mods/64/taskbar-start-button-position_1.3.2.dll", "AppData/Engine/Mods/64/taskbar-icon-size_1.3.10.dll",
        "AppData/Engine/Mods/64/explorer-frame-classic_1.0.8.dll", "AppData/Engine/Mods/64/explorer-context-menu-classic_1.0.2.dll"];
    string? leaseRoot; int leaseThread;
    public static WindowsShellActivationHost CreateForWindows() => new(new WindowsActivationAlignment(), new WindowsActivationEnvironment(), new WindowsActivationProcesses());

    /// <summary>Read only. Caller chooses the local package and may pin its expected manifest SHA-256.
    /// Only one recognized runtime manifest may exist; ambiguous packages are rejected.</summary>
    public static ActivationPackageCheck CheckPackage(string root, string? expectedManifestSha256 = null)
    {
        root = CanonicalRoot(root);
        var manifests = new[] { "runtime-assets-manifest.json", "runtime-manifest.json" }.Where(n => File.Exists(SafePath(root, n))).ToArray();
        if (manifests.Length != 1) throw new InvalidDataException("必须有且仅有一份明确的运行资产清单。");
        var manifestPath = SafePath(root, manifests[0]); var bytes = ReadBounded(manifestPath, 1024 * 1024); var manifestHash = Hash(bytes);
        if (expectedManifestSha256 is not null && !EqualHash(manifestHash, expectedManifestSha256)) throw new InvalidDataException("运行资产清单修订与已确认包不符。");
        using var document = JsonDocument.Parse(bytes); var json = document.RootElement;
        if (json.GetProperty("engineVersion").GetString() != "1.7.3") throw new InvalidDataException("仅支持已核查的 Windhawk 1.7.3 协议。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; string? executableHash = null;
        foreach (var asset in json.GetProperty("assets").EnumerateArray())
        {
            var relative = asset.GetProperty("path").GetString() ?? throw new InvalidDataException("资产路径缺失。");
            if (!seen.Add(relative.Replace('\\', '/'))) throw new InvalidDataException("资产重复。");
            var path = SafePath(root, relative); var expectedBytes = asset.GetProperty("bytes").GetInt64(); var expectedHash = asset.GetProperty("sha256").GetString();
            if (expectedBytes < 0 || expectedBytes > 64 * 1024 * 1024 || new FileInfo(path).Length != expectedBytes) throw new InvalidDataException("资产大小不匹配：" + relative);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(SHA256.HashData(file));
            if (!EqualHash(actualHash, expectedHash)) throw new InvalidDataException("资产 hash 不匹配：" + relative);
            if (relative == "windhawk.exe") executableHash = actualHash;
            total += expectedBytes;
            if (seen.Count > 128 || total > 256 * 1024 * 1024) throw new InvalidDataException("资产清单超出此精简后端的范围。");
        }
        if (RequiredAssets.Any(a => !seen.Contains(a)) || executableHash is null) throw new InvalidDataException("运行包缺少必要引擎、模块或依赖。");
        var knownModFiles = Targets.Where(p => p.Key is not (ActivationTarget.MainSettings or ActivationTarget.EngineSettings)).Select(p => p.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ContainedFiles(SafePath(root, "AppData/Engine/Mods"), true).Where(p => Path.GetExtension(p).Equals(".ini", StringComparison.OrdinalIgnoreCase)))
            if (!knownModFiles.Contains(Path.GetRelativePath(root, file).Replace('\\', '/'))) throw new InvalidDataException("存在不属于本方案的额外模块配置，拒绝启用安全模式之外的模块。");
        foreach (var folder in new[] { root, SafePath(root, "Engine"), SafePath(root, "AppData/Engine/Mods") })
            foreach (var file in ContainedFiles(folder, folder != root))
                if (new[] { ".dll", ".exe", ".whl" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && !seen.Contains(Path.GetRelativePath(root, file).Replace('\\', '/')))
                    throw new InvalidDataException("运行目录出现未列入清单的可执行文件：" + Path.GetFileName(file));
        using var owner = JsonDocument.Parse(ReadBounded(SafePath(root, "classicdesk-package-owner.json"), 65536));
        if (owner.RootElement.GetProperty("owner").GetString() != "ClassicDesk" || !Guid.TryParse(owner.RootElement.GetProperty("packageId").GetString(), out _))
            throw new InvalidDataException("包归属标记无效。");
        // Validate both storage roots. Never permit engine or AppData to resolve outside this reviewed package.
        var main = ReadIni(SafePath(root, "windhawk.ini")); var engine = ReadIni(SafePath(root, "Engine/1.7.3/engine.ini"));
        Require(main, "Storage", "Portable", "1"); Require(main, "Storage", "EnginePath", "Engine\\1.7.3"); Require(main, "Storage", "AppDataPath", "AppData");
        Require(main, "Storage", "UIPath", "UI"); Require(main, "Storage", "CompilerPath", "Compiler");
        Require(engine, "Storage", "Portable", "1"); Require(engine, "Storage", "AppDataPath", "..\\..\\AppData\\Engine");
        return new(new(root, manifestHash, SafePath(root, "windhawk.exe"), executableHash), manifestPath, seen.Count, total);
    }
    public IDisposable AcquirePackageLease(VerifiedActivationPackage package)
    {
        if (leaseRoot is not null) throw new IOException("当前宿主已持有事务 lease。");
        Validate(package); var directory = SafePath(package.Root, Metadata); Directory.CreateDirectory(directory); RejectReparse(directory);
        var file = new FileStream(SafePath(package.Root, Metadata + "/package.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        leaseRoot = package.Root; leaseThread = Environment.CurrentManagedThreadId;
        return new ActionLease(() => { leaseRoot = null; leaseThread = 0; file.Dispose(); });
    }
    public ActivationPreparation Inspect(VerifiedActivationPackage package, ShellProfile profile, ActivationDaemonIdentity? allowedDaemon = null)
    {
        Validate(package); profile.Validate(); var observed = environment.Inspect(allowedDaemon);
        var guards = new ActivationGuards(observed.Revision, package.ManifestSha256, observed.StartAllBack, observed.OtherWindhawk);
        var before = Enum.GetValues<ActivationTarget>().ToDictionary(t => t, t => Read(package, t));
        var desired = DesiredFiles(profile, before);
        return new(guards, Enum.GetValues<ActivationTarget>().Select(t => new ActivationChange(t, before[t], true,
            t == ActivationTarget.TaskbarAlignment ? "1" : Convert.ToBase64String(desired[t]))).ToArray());
    }
    public ActivationItemState Read(VerifiedActivationPackage package, ActivationTarget target)
    {
        if (!Enum.IsDefined(target)) throw new InvalidDataException("未知目标。");
        return target == ActivationTarget.TaskbarAlignment ? alignment.Read() : ReadState(SafePath(package.Root, Targets[target]));
    }
    public ActivationWriteResult Write(VerifiedActivationPackage package, ActivationChange change, ActivationGuards guards)
    {
        Guard(package, guards); if (Read(package, change.Target) != change.Before) return Rejected("写入前修订冲突。");
        var token = Guid.NewGuid().ToString("N"); var record = new OwnershipRecord(token, package.ManifestSha256, change.Target, change.Before, null, "intent", null);
        SaveOwnership(package, record, false);
        try
        {
            var after = Exchange(package, change.Target, change.Before, change.DesiredExists, change.DesiredData,
                () => Guard(package, guards));
            SaveOwnership(package, record with { After = after, Phase = "confirmed" }, true);
            return new(ActivationOutcome.Confirmed, token, after);
        }
        catch (Exception e) { return new(ActivationOutcome.Unknown, null, null, e.Message); }
    }
    public ActivationWriteResult RestoreOwned(VerifiedActivationPackage package, ActivationTarget target, ActivationWriteResult ownedWrite, ActivationItemState original, ActivationGuards guards)
    {
        Guard(package, guards, AllowedProcess(package));
        var record = LoadOwnership(package, ownedWrite.OwnershipToken);
        if (record.Phase != "confirmed" || record.Target != target || record.After != ownedWrite.After || record.Before != original || record.After != Read(package, target)) return Rejected("恢复归属或修订不匹配。");
        SaveOwnership(package, record with { Phase = "restore-intent" }, true);
        try
        {
            var after = Exchange(package, target, record.After!, original.Exists, original.Data,
                () => Guard(package, guards, AllowedProcess(package)));
            SaveOwnership(package, record with { Phase = "restored", Restored = after }, true);
            return new(ActivationOutcome.Confirmed, "restore-" + record.Token, after);
        }
        catch (Exception e) { return new(ActivationOutcome.Unknown, null, null, e.Message); }
    }
    public ActivationStartResult StartDaemon(VerifiedActivationPackage package, ActivationGuards guards,
        IReadOnlyDictionary<ActivationTarget, ActivationItemState> expectedFinal)
    {
        Guard(package, guards);
        var frozen = expectedFinal.ToDictionary(p => p.Key, p => p.Value);
        void VerifyFinal()
        {
            Guard(package, guards);
            if (frozen.Count != 7 || Enum.GetValues<ActivationTarget>().Any(t => !frozen.ContainsKey(t))) throw new InvalidDataException("启动必须提供完整七目标冻结后态。");
            foreach (var pair in frozen) if (Read(package, pair.Key) != pair.Value) throw new InvalidOperationException("启动提交前配置或 TaskbarAl 已偏离确认后的归属状态。");
        }
        VerifyFinal();
        var token = Guid.NewGuid().ToString("N");
        var path = SafePath(package.Root, Metadata + "/daemon-" + token + ".json");
        WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(new ProcessOwnership(token, package.ManifestSha256, "start-intent", null)));
        ActivationStartResult started;
        try { started = processes.Start(package, token, VerifyFinal); }
        catch (Exception e) { return new(ActivationOutcome.Unknown, null, e.Message); }
        if (started.Outcome != ActivationOutcome.Confirmed || started.Identity is null) return started;
        var identity = started.Identity;
        if (identity.OwnershipToken != token || !SamePath(identity.ExecutablePath, package.ExecutablePath) || !EqualHash(identity.ExecutableSha256, package.ExecutableSha256))
            return new(ActivationOutcome.Unknown, null, "进程身份回执无效。");
        try { ReplaceOwnedFile(path, JsonSerializer.SerializeToUtf8Bytes(new ProcessOwnership(token, package.ManifestSha256, "started", identity))); }
        catch (Exception e) { return new(ActivationOutcome.Unknown, null, "进程已启动但归属记录未保存：" + e.Message); }
        return started;
    }
    public ActivationDaemonHealth InspectDaemon(ActivationDaemonIdentity identity)
    {
        try { ValidateProcessRecord(identity); return processes.Inspect(identity); }
        catch { return ActivationDaemonHealth.Unknown; }
    }
    public ActivationStopResult StopOwnedDaemon(ActivationDaemonIdentity identity, ActivationGuards guards)
    {
        var root = Path.GetDirectoryName(identity.ExecutablePath)!; var checkedPackage = CheckPackage(root, guards.PackageRevision).Package;
        Guard(checkedPackage, guards, identity); ValidateProcessRecord(identity);
        if (processes.Inspect(identity) != ActivationDaemonHealth.SameProcessRunning) return new(ActivationOutcome.RejectedWithoutChange, "不是确认归属的存活进程。");
        var result = processes.Stop(identity);
        if (result.Outcome == ActivationOutcome.Confirmed)
        {
            if (processes.Inspect(identity) != ActivationDaemonHealth.OwnedProcessExited) return new(ActivationOutcome.Unknown, "进程退出未确认。");
            ReplaceOwnedFile(SafePath(root, Metadata + "/daemon-" + identity.OwnershipToken + ".json"),
                JsonSerializer.SerializeToUtf8Bytes(new ProcessOwnership(identity.OwnershipToken, guards.PackageRevision, "stopped", identity)));
        }
        return result;
    }

    static Dictionary<ActivationTarget, byte[]> DesiredFiles(ShellProfile profile, Dictionary<ActivationTarget, ActivationItemState> before)
    {
        var unknown = new ShellBackendEnvironment(DateTime.MinValue, "X64", null, null, null, [], [], [], [], "unknown", []);
        var plan = ShellBackendPlanner.CreatePlan(profile, unknown);
        var output = new Dictionary<ActivationTarget, byte[]>();
        foreach (var pair in Targets.Where(p => p.Key is not (ActivationTarget.MainSettings or ActivationTarget.EngineSettings)))
        {
            var id = Path.GetFileNameWithoutExtension(pair.Value); var mod = plan.Modules.Single(m => m.Id == id);
            // Deterministic while the original file is unchanged, so review fingerprints don't drift with wall clock time.
            int previous = 0;
            if (before[pair.Key].Exists)
            {
                var ini = ParseIni(Convert.FromBase64String(before[pair.Key].Data));
                if (ini.TryGetValue("Mod\0SettingsChangeTime", out var stamp) && !int.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out previous)) throw new InvalidDataException("模块设置时间戳无效。");
            }
            var next = previous == int.MaxValue ? 1 : previous + 1;
            output[pair.Key] = IniBytes(ShellBackendConfig.BuildModIni(mod, mod.Selected, DateTimeOffset.FromUnixTimeSeconds(next)));
        }
        foreach (var target in new[] { ActivationTarget.MainSettings, ActivationTarget.EngineSettings })
        {
            var ini = before[target].Exists ? ParseIni(Convert.FromBase64String(before[target].Data)) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ini["Settings\0SafeMode"] = "0";
            if (target == ActivationTarget.MainSettings)
                foreach (var name in new[] { "HideTrayIcon", "DisableUpdateCheck", "DontAutoShowToolkit", "DisableToolkitHotkey" }) ini["Settings\0" + name] = "1";
            output[target] = IniBytes(RenderIni(ini));
        }
        return output;
    }
    ActivationItemState Exchange(VerifiedActivationPackage package, ActivationTarget target, ActivationItemState before, bool exists, string data, Action verifyGuards)
    {
        verifyGuards();
        if (target == ActivationTarget.TaskbarAlignment) return alignment.CompareExchange(before, exists, data);
        var path = SafePath(package.Root, Targets[target]); if (ReadState(path) != before) throw new IOException("文件提交前修订变化。");
        if (!exists) { if (File.Exists(path)) File.Delete(path); }
        else
        {
            var bytes = Convert.FromBase64String(data); if (bytes.Length > 1024 * 1024) throw new InvalidDataException("配置过大。");
            var temporary = path + ".classicdesk-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteNew(temporary, bytes);
                void VerifyCurrent()
                {
                    verifyGuards();
                    if (ReadState(path) != before) throw new IOException("替换前修订变化。");
                }
                VerifyCurrent();
                if (before.Exists) ActivationFileReplace.Commit(temporary, path, VerifyCurrent);
                else File.Move(temporary, path, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var after = ReadState(path); if (after.Exists != exists || after.Data != data) throw new IOException("配置回读不匹配。"); return after;
    }
    void Guard(VerifiedActivationPackage package, ActivationGuards guards, ActivationDaemonIdentity? allowed = null)
    {
        if (leaseRoot is null || !SamePath(leaseRoot, package.Root) || leaseThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("必须在同线程包级事务 lease 内写入。");
        Validate(package); var current = environment.Inspect(allowed);
        if (current.Revision != guards.EnvironmentRevision || guards.PackageRevision != package.ManifestSha256 || current.StartAllBack != ActivationPresence.Absent || current.OtherWindhawk != ActivationPresence.Absent)
            throw new IOException("宿主环境、冲突或包修订变化。");
    }
    static void Validate(VerifiedActivationPackage package)
    {
        var actual = CheckPackage(package.Root, package.ManifestSha256).Package;
        if (!SamePath(actual.ExecutablePath, package.ExecutablePath) || !EqualHash(actual.ExecutableSha256, package.ExecutableSha256)) throw new InvalidDataException("包执行文件身份不符。");
    }
    ActivationDaemonIdentity? AllowedProcess(VerifiedActivationPackage package)
    {
        var directory = SafePath(package.Root, Metadata);
        if (!Directory.Exists(directory)) return null;
        var candidates = Directory.GetFiles(directory, "daemon-*.json").Select(p => JsonSerializer.Deserialize<ProcessOwnership>(ReadBounded(p, 65536)))
            .Where(p => p?.PackageRevision == package.ManifestSha256 && p.Identity is not null && p.Phase == "started"
                && processes.Inspect(p.Identity) == ActivationDaemonHealth.SameProcessRunning).ToArray();
        return candidates.Length == 1 ? candidates[0]!.Identity : null;
    }
    static void ValidateProcessRecord(ActivationDaemonIdentity identity)
    {
        ValidateToken(identity.OwnershipToken); var root = Path.GetDirectoryName(identity.ExecutablePath)!;
        var record = JsonSerializer.Deserialize<ProcessOwnership>(ReadBounded(SafePath(root, Metadata + "/daemon-" + identity.OwnershipToken + ".json"), 65536));
        if (record?.Identity != identity || record.Phase is not ("started" or "stopped")) throw new InvalidDataException("守护进程不属于已保存的本工具启动记录。");
    }
    static OwnershipRecord LoadOwnership(VerifiedActivationPackage package, string? token)
    {
        ValidateToken(token); var record = JsonSerializer.Deserialize<OwnershipRecord>(ReadBounded(SafePath(package.Root, Metadata + "/write-" + token + ".json"), 4 * 1024 * 1024));
        if (record is null || record.Token != token || record.PackageRevision != package.ManifestSha256) throw new InvalidDataException("归属记录不匹配。"); return record;
    }
    static void SaveOwnership(VerifiedActivationPackage package, OwnershipRecord record, bool replace)
    {
        var path = SafePath(package.Root, Metadata + "/write-" + record.Token + ".json"); var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (replace) ReplaceOwnedFile(path, bytes); else WriteNew(path, bytes);
    }
    static void ReplaceOwnedFile(string path, byte[] bytes)
    {
        RejectReparse(path); var original = ReadState(path); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNew(temp, bytes);
            ActivationFileReplace.Commit(temp, path, () =>
            { if (ReadState(path) != original) throw new IOException("归属文件被外部修改。"); });
            if (!ReadBounded(path, 4 * 1024 * 1024).AsSpan().SequenceEqual(bytes)) throw new IOException("归属文件回读失败。");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    static ActivationWriteResult Rejected(string error) => new(ActivationOutcome.RejectedWithoutChange, null, null, error);
    static void ValidateToken(string? token) { if (token is null || token.Length != 32 || !Guid.TryParseExact(token, "N", out _)) throw new InvalidDataException("归属 token 无效。"); }
    public static ActivationItemState ReadState(string path)
    {
        RejectReparse(path); if (!File.Exists(path)) return new(false, "", "missing");
        var info = new FileInfo(path); var bytes = ReadBounded(path, 4 * 1024 * 1024);
        var revision = Hash(bytes) + ":" + info.CreationTimeUtc.Ticks + ":" + info.LastWriteTimeUtc.Ticks;
        return new(true, Convert.ToBase64String(bytes), revision);
    }
    internal static string CanonicalRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal) || root.IndexOf(':', 2) >= 0) throw new InvalidDataException("只接受明确的本地绝对目录。");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); RejectReparse(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root); return root;
    }
    internal static string SafePath(string root, string relative)
    {
        root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is "" or "." or ".." || p.TrimEnd(' ', '.') != p)) throw new InvalidDataException("包内相对路径无效。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越出包目录。");
        RejectReparse(path); return path;
    }
    internal static void RejectReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("包路径不能经过重解析点。");
    }
    static IEnumerable<string> ContainedFiles(string directory, bool recursive)
    {
        RejectReparse(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectReparse(path);
            if (Directory.Exists(path)) { if (recursive) foreach (var child in ContainedFiles(path, true)) yield return child; }
            else yield return path;
        }
    }
    internal static byte[] ReadBounded(string path, int limit)
    {
        RejectReparse(path); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException("文件过大。"); var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    static void WriteNew(string path, byte[] bytes)
    { RejectReparse(path); using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); file.Write(bytes); file.Flush(true); }
    static Dictionary<string, string> ReadIni(string path) => ParseIni(ReadBounded(path, 1024 * 1024));
    static Dictionary<string, string> ParseIni(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 255 || bytes[1] != 254) throw new InvalidDataException("INI 必须保留 UTF-16LE BOM。");
        var text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2); var section = "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var equal = line.IndexOf('='); if (section.Length == 0 || equal <= 0) throw new InvalidDataException("INI 行无效。");
            if (!values.TryAdd(section + "\0" + line[..equal].Trim(), line[(equal + 1)..].Trim())) throw new InvalidDataException("INI 存在重复键。");
        }
        return values;
    }
    static string RenderIni(Dictionary<string, string> values)
    {
        var text = new StringBuilder(); foreach (var group in values.GroupBy(v => v.Key.Split('\0')[0]))
        { text.Append('[').Append(group.Key).Append("]\r\n"); foreach (var pair in group) text.Append(pair.Key.Split('\0')[1]).Append('=').Append(pair.Value).Append("\r\n"); text.Append("\r\n"); } return text.ToString();
    }
    static void Require(Dictionary<string, string> ini, string section, string name, string value)
    { if (!ini.TryGetValue(section + "\0" + name, out var actual) || !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("便携存储根不符合已审查布局：" + name); }
    static byte[] IniBytes(string text) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static bool EqualHash(string? a, string? b) => a?.Length == 64 && b?.Length == 64 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    sealed record OwnershipRecord(string Token, string PackageRevision, ActivationTarget Target, ActivationItemState Before, ActivationItemState? After, string Phase, ActivationItemState? Restored);
    sealed record ProcessOwnership(string Token, string PackageRevision, string Phase, ActivationDaemonIdentity? Identity);
    sealed class ActionLease(Action release) : IDisposable { public void Dispose() => release(); }
}

/// <summary>Only Read is read-only. CompareExchange performs the explicitly requested current-user write.
/// RegQueryInfoKey timestamp is conservative for the whole Advanced key and is not an atomic registry CAS.</summary>
public sealed class WindowsActivationAlignment : IActivationAlignment
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public ActivationItemState Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, false) ?? throw new IOException("Explorer Advanced 注册表键不可用。");
        var exists = key.GetValueNames().Contains("TaskbarAl", StringComparer.OrdinalIgnoreCase); var value = key.GetValue("TaskbarAl");
        if (exists && (key.GetValueKind("TaskbarAl") != RegistryValueKind.DWord || value is not int number || number is not (0 or 1))) throw new InvalidDataException("TaskbarAl 不是受支持的 DWORD 0/1。");
        var status = RegQueryInfoKey(key.Handle, null, 0, 0, out _, out _, out _, out _, out _, out _, out _, out var stamp);
        if (status != 0) throw new IOException("无法读取注册表修订：" + status);
        var data = exists ? ((int)value!).ToString(CultureInfo.InvariantCulture) : "";
        return new(exists, data, stamp.ToString(CultureInfo.InvariantCulture) + ":" + (exists ? "dword:" + data : "missing"));
    }
    public ActivationItemState CompareExchange(ActivationItemState expected, bool exists, string data)
    {
        if ((exists && data is not ("0" or "1")) || (!exists && data != "")) throw new InvalidDataException("TaskbarAl 目标无效。");
        if (Read() != expected) throw new IOException("TaskbarAl 修订变化。");
        using var key = Registry.CurrentUser.OpenSubKey(Key, true) ?? throw new IOException("TaskbarAl 不可写。");
        if (Read() != expected) throw new IOException("TaskbarAl 提交前修订变化。");
        if (exists) key.SetValue("TaskbarAl", int.Parse(data, CultureInfo.InvariantCulture), RegistryValueKind.DWord); else key.DeleteValue("TaskbarAl", false);
        key.Flush(); var after = Read(); if (after.Exists != exists || after.Data != data) throw new IOException("TaskbarAl 回读不一致。"); return after;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, StringBuilder? cls, nint classLength, nint reserved, out uint subKeys, out uint maxSubkey, out uint maxClass, out uint values, out uint maxValueName, out uint maxValueData, out uint security, out long lastWriteTime);
}

public sealed class WindowsActivationEnvironment : IActivationEnvironment
{
    // Installation evidence remains informational. Only a complete loaded-module
    // observation can establish the current session's StartAllBack conflict state.
    public static ActivationPresence ClassifyStartAllBack(string loaded) => loaded switch
    { "detected" => ActivationPresence.Present, "not-detected" => ActivationPresence.Absent, _ => ActivationPresence.Unknown };
    public ActivationEnvironmentObservation Inspect(ActivationDaemonIdentity? allowedDaemon)
    {
        var snapshot = ShellBackendPlanner.Detect();
        var sab = ClassifyStartAllBack(snapshot.StartAllBackLoaded);
        var identities = new List<string>(); var other = ActivationPresence.Absent;
        foreach (var process in Process.GetProcessesByName("windhawk"))
        {
            using (process)
            {
                try { if (allowedDaemon is null || !WindowsActivationProcesses.Matches(process, allowedDaemon)) other = ActivationPresence.Present; }
                catch { other = ActivationPresence.Unknown; }
            }
        }
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    identities.Add(process.Id + ":" + process.StartTime.ToUniversalTime().Ticks);
                    foreach (ProcessModule module in process.Modules)
                        if (module.ModuleName.Equals("windhawk.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            var ownedEngineRoot = allowedDaemon is null ? null : Path.Combine(Path.GetDirectoryName(allowedDaemon.ExecutablePath)!, "Engine") + Path.DirectorySeparatorChar;
                            if (ownedEngineRoot is null || !module.FileName.StartsWith(ownedEngineRoot, StringComparison.OrdinalIgnoreCase)) other = ActivationPresence.Present;
                        }
                }
                catch { sab = ActivationPresence.Unknown; }
            }
        }
        if (identities.Count == 0 || snapshot.ReadErrors.Count != 0 || snapshot.Architecture != "X64" || snapshot.Build is null or < 22000) sab = ActivationPresence.Unknown;
        var canonical = JsonSerializer.Serialize(new { snapshot.Build, snapshot.Revision, snapshot.Architecture, binaries = snapshot.SystemBinaries, explorer = identities.Order(), session = Process.GetCurrentProcess().SessionId });
        return new(WindowsShellActivationHost.Hash(Encoding.UTF8.GetBytes(canonical)), sab, other);
    }
}

/// <summary>Native process adapter, never used by isolated tests. Uses fixed v1.7.3 daemon protocol.
/// Process/window identity and exit are checked; module initialization/visual restoration are not inferred.</summary>
public sealed class WindowsActivationProcesses : IActivationProcesses
{
    public ActivationStartResult Start(VerifiedActivationPackage package, string ownershipToken, Action verifyBeforeStart)
    {
        if (Process.GetProcessesByName("windhawk").Any()) return new(ActivationOutcome.RejectedWithoutChange, null, "已有 Windhawk 进程。");
        Process? process = null;
        try
        {
            // No startup has happened if this last snapshot check refuses.
            try { verifyBeforeStart(); } catch (Exception e) { return new(ActivationOutcome.RejectedWithoutChange, null, e.Message); }
            process = Process.Start(new ProcessStartInfo(package.ExecutablePath) { Arguments = "-tray-only", WorkingDirectory = package.Root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (process is null) return new(ActivationOutcome.Unknown, null, "未取得启动进程句柄。");
            var identity = new ActivationDaemonIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, package.ExecutablePath, package.ExecutableSha256, ownershipToken);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 10000)
            {
                if (process.HasExited) return new(ActivationOutcome.Unknown, null, "启动进程已退出，不能接管其他实例。");
                if (Matches(process, identity) && FindOwnedWindow(identity) != 0) return new(ActivationOutcome.Confirmed, identity);
                Thread.Sleep(50);
            }
            return new(ActivationOutcome.Unknown, null, "未确认同一进程的 daemon 窗口；不会强停。");
        }
        catch (Exception e) { return new(ActivationOutcome.Unknown, null, e.Message); }
        finally { process?.Dispose(); }
    }
    public ActivationDaemonHealth Inspect(ActivationDaemonIdentity identity)
    {
        try { using var process = Process.GetProcessById(identity.ProcessId); if (!Matches(process, identity)) return ActivationDaemonHealth.DifferentProcess; return process.HasExited ? ActivationDaemonHealth.OwnedProcessExited : ActivationDaemonHealth.SameProcessRunning; }
        catch (ArgumentException) { return ActivationDaemonHealth.OwnedProcessExited; }
        catch { return ActivationDaemonHealth.Unknown; }
    }
    public ActivationStopResult Stop(ActivationDaemonIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!Matches(process, identity)) return new(ActivationOutcome.RejectedWithoutChange, "进程身份不匹配。");
            var window = FindOwnedWindow(identity); if (window == 0) return new(ActivationOutcome.RejectedWithoutChange, "没有该进程拥有的 daemon 窗口。");
            if (!Matches(process, identity) || GetWindowThreadProcessId(window, out var pid) == 0 || pid != identity.ProcessId) return new(ActivationOutcome.RejectedWithoutChange, "发送退出前身份变化。");
            // Fixed official main_window.h: UWM_PORTABLE_APP_COMMAND=WM_APP, kExit=2.
            // No -exit subprocess, focus/foreground call, WM_CLOSE, Kill or TerminateProcess.
            if (!PostMessage(window, 0x8000, 2, 0)) return new(ActivationOutcome.Unknown, "退出消息未确认。");
            return process.WaitForExit(10000) ? new(ActivationOutcome.Confirmed) : new(ActivationOutcome.Unknown, "等待自有进程退出超时，未强停。");
        }
        catch (Exception e) { return new(ActivationOutcome.Unknown, e.Message); }
    }
    internal static bool Matches(Process process, ActivationDaemonIdentity identity)
    {
        if (process.Id != identity.ProcessId || process.StartTime.ToUniversalTime().Ticks != identity.CreationTimeUtcTicks) return false;
        var path = process.MainModule?.FileName; if (path is null || !WindowsShellActivationHost.SamePath(path, identity.ExecutablePath)) return false;
        using var file = File.OpenRead(path); return WindowsShellActivationHost.EqualHash(Convert.ToHexString(SHA256.HashData(file)), identity.ExecutableSha256);
    }
    static nint FindOwnedWindow(ActivationDaemonIdentity identity)
    {
        nint found = 0;
        EnumWindows((window, _) => { var name = new StringBuilder(128); GetClassName(window, name, name.Capacity); GetWindowThreadProcessId(window, out var pid); if (pid == identity.ProcessId && name.ToString() == "WindhawkDaemon") { found = window; return false; } return true; }, 0);
        return found;
    }
    delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
