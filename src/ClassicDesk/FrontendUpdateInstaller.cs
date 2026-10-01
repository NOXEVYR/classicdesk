using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassicDesk;

public sealed record FrontendUpdatePrepared(string RequestPath, string HelperExecutablePath, string TransactionId);
public sealed record FrontendUpdateInstallResult(bool Success, string State, string Message);
public sealed record FrontendUpdatePending(string RequestPath, string State, string Message);
public sealed class FrontendUpdateLaunchException(string message, bool mayHaveStarted, Exception? inner = null) : IOException(message, inner)
{ public bool MayHaveStarted { get; } = mayHaveStarted; }
public interface IFrontendUpdateHost
{
    FrontendProcessIdentity? ReadProcess(int pid);
    void Delay(TimeSpan delay);
    FrontendProcessIdentity Start(string executablePath, string acknowledgementPath);
}

/// <summary>Only the four registered frontend files and their installation manifest are writable.</summary>
public static class FrontendUpdateInstaller
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private sealed record Entry(string Name, string OriginalHash, string TargetHash);
    private sealed record Request(int Schema, string Id, string AppRoot, string StageRoot, string WorkRoot,
        string TransactionRoot, FrontendProcessIdentity Parent, FrontendUpdateManifest Original,
        FrontendUpdateManifest Target, string TargetManifestHash, Entry[] Entries);
    private sealed record Journal(string Id, string RequestHash, string State, string? Intent, string[] Written,
        FrontendProcessIdentity? Started, string Message);
    private sealed record AckChallenge(string Token, string ExecutablePath, FrontendUpdateManifest Manifest, string ManifestHash);
    private sealed record Ack(string Token, FrontendProcessIdentity Process);

    public static FrontendProcessIdentity CurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks,
            Path.GetFullPath(process.MainModule?.FileName ?? throw new IOException("无法读取当前程序路径。")));
    }

    public static FrontendUpdateManifest ReadInstallation(string appRoot)
    {
        appRoot = SafeRoot(appRoot); var path = Path.Combine(appRoot, FrontendUpdateFiles.InstallationManifest);
        if (!File.Exists(path)) throw new InvalidDataException("当前前端没有安装登记，请手动安装包含登记清单的新版 bootstrap。");
        var manifest = Read<FrontendUpdateManifest>(path); ValidateManifest(manifest); VerifyFiles(appRoot, manifest); return manifest;
    }

    public static FrontendUpdatePending[] InspectPending(string workRoot)
    {
        workRoot = SafeRoot(workRoot); if (!Directory.Exists(workRoot)) return [];
        var result = new List<FrontendUpdatePending>();
        foreach (var directory in Directory.EnumerateDirectories(workRoot))
        {
            var requestPath = Path.Combine(directory, "request.json");
            try
            {
                NoReparse(directory); var journal = Read<Journal>(Path.Combine(directory, "journal.json"));
                if (journal.State is not ("Completed" or "RolledBack" or "Rejected")) result.Add(new(requestPath, journal.State, journal.Message));
            }
            catch (Exception error) { result.Add(new(requestPath, "Unknown", "事务日志不可读取，请人工核对：" + error.Message)); }
        }
        return result.ToArray();
    }

    public static FrontendUpdatePrepared Prepare(string appRoot, FrontendUpdateStage stage, string workRoot,
        FrontendProcessIdentity parent)
    {
        appRoot = SafeRoot(appRoot); var stageRoot = SafeRoot(stage.Directory); workRoot = SafeRoot(workRoot);
        Disjoint(appRoot, stageRoot, workRoot);
        if (parent.Pid <= 0 || parent.StartUtcTicks <= 0 || !Same(parent.ExecutablePath, Path.Combine(appRoot, "ClassicDesk.exe")))
            throw new InvalidDataException("更新只接受当前前端的精确进程身份。");
        Directory.CreateDirectory(workRoot);
        using var applicationGate = ApplicationLock(appRoot); using var gate = Lock(workRoot);
        EnsureNoPending(workRoot);
        var original = ReadInstallation(appRoot);
        ValidateManifest(original); ValidateManifest(stage.Manifest);
        if (original.Channel != stage.Manifest.Channel) throw new InvalidDataException("更新通道必须与已安装前端一致。");
        if (CompareVersions(stage.Manifest.Version, original.Version) <= 0) throw new InvalidDataException("版本必须递增。");
        VerifyFiles(appRoot, original); VerifyFiles(stageRoot, stage.Manifest);
        if (!Hex(stage.ManifestSha256, 64)) throw new InvalidDataException("目标清单摘要无效。");
        var officialManifest = Path.Combine(stageRoot, "frontend-update.json");
        RequireHash(officialManifest, stage.ManifestSha256);
        if (!ManifestEquals(Read<FrontendUpdateManifest>(officialManifest), stage.Manifest)) throw new InvalidDataException("官方清单与暂存目标不匹配。");
        var id = Guid.NewGuid().ToString("N"); var tx = Path.Combine(workRoot, id);
        Directory.CreateDirectory(tx); Directory.CreateDirectory(Path.Combine(tx, "helper"));
        Directory.CreateDirectory(Path.Combine(tx, "backup"));
        var targetPath = Path.Combine(tx, "target-manifest.json");
        File.Copy(officialManifest, targetPath); RequireHash(targetPath, stage.ManifestSha256);
        var entries = FrontendUpdateFiles.Names.Select(name => new Entry(name,
            Hash(Path.Combine(appRoot, name)), Hash(Path.Combine(stageRoot, name))))
            .Append(new Entry(FrontendUpdateFiles.InstallationManifest,
                Hash(Path.Combine(appRoot, FrontendUpdateFiles.InstallationManifest)), Hash(targetPath))).ToArray();
        foreach (var name in FrontendUpdateFiles.Names)
        {
            var source = Path.Combine(appRoot, name); var copy = Path.Combine(tx, "helper", name);
            File.Copy(source, copy); RequireHash(copy, entries.Single(e => e.Name == name).OriginalHash);
        }
        foreach (var entry in entries)
        {
            var source = Path.Combine(appRoot, entry.Name); var backup = Path.Combine(tx, "backup", entry.Name);
            File.Copy(source, backup); RequireHash(backup, entry.OriginalHash);
        }
        foreach (var entry in entries) RequireHash(Path.Combine(appRoot, entry.Name), entry.OriginalHash);
        var request = new Request(1, id, appRoot, stageRoot, workRoot, tx, parent, original, stage.Manifest,
            stage.ManifestSha256, entries);
        var requestPath = Path.Combine(tx, "request.json"); Save(requestPath, request);
        Save(Path.Combine(tx, "journal.json"), new Journal(id, Hash(requestPath), "Prepared", null, [], null, "等待前端退出。"));
        return new(requestPath, Path.Combine(tx, "helper", "ClassicDesk.exe"), id);
    }

    public static FrontendUpdateInstallResult Run(string requestPath, IFrontendUpdateHost? host = null,
        int exitTimeoutMilliseconds = 30000, int acknowledgementTimeoutMilliseconds = 30000)
    {
        host ??= new SystemHost();
        var request = LoadRequest(requestPath); using var applicationGate = ApplicationLock(request.AppRoot); using var gate = Lock(request.WorkRoot);
        var journalPath = Path.Combine(request.TransactionRoot, "journal.json");
        var journal = LoadJournal(requestPath, request);
        if (journal.State != "Prepared") return new(false, journal.State, "事务已经执行或需要手动恢复。");
        var writing = false;
        try
        {
            WaitForExit(host, request.Parent, exitTimeoutMilliseconds);
            VerifyFiles(request.StageRoot, request.Target);
            RequireHash(Path.Combine(request.TransactionRoot, "target-manifest.json"), request.Entries[^1].TargetHash);
            foreach (var entry in request.Entries) RequireHash(Path.Combine(request.AppRoot, entry.Name), entry.OriginalHash);
            journal = journal with { State = "Applying" }; Save(journalPath, journal);
            foreach (var entry in request.Entries)
            {
                RevalidatePaths(request); RequireHash(Path.Combine(request.AppRoot, entry.Name), entry.OriginalHash);
                RequireHash(Path.Combine(request.TransactionRoot, "backup", entry.Name), entry.OriginalHash);
                journal = journal with { Intent = entry.Name }; Save(journalPath, journal); writing = true;
                Replace(entry.Name == FrontendUpdateFiles.InstallationManifest
                    ? Path.Combine(request.TransactionRoot, "target-manifest.json") : Path.Combine(request.StageRoot, entry.Name),
                    Path.Combine(request.AppRoot, entry.Name), entry.TargetHash, entry.OriginalHash, request.Id);
                journal = journal with { Written = journal.Written.Append(entry.Name).ToArray(), Intent = null }; Save(journalPath, journal);
            }
            var ackPath = Path.Combine(request.TransactionRoot, "ack-challenge.json");
            Save(ackPath, new AckChallenge(Guid.NewGuid().ToString("N"), Path.Combine(request.AppRoot, "ClassicDesk.exe"), request.Target, request.TargetManifestHash));
            journal = journal with { State = "Starting", Message = "等待新前端窗口加载确认。" }; Save(journalPath, journal);
            // Persist launch intent first: a crash between Start and identity persistence is unresolved, never successful.
            var started = host.Start(Path.Combine(request.AppRoot, "ClassicDesk.exe"), ackPath);
            if (!Same(started.ExecutablePath, Path.Combine(request.AppRoot, "ClassicDesk.exe"))) throw new IOException("重启程序路径不匹配。");
            journal = journal with { Started = started, State = "AwaitingAcknowledgement" }; Save(journalPath, journal);
            var challenge = Read<AckChallenge>(ackPath);
            var loops = Math.Max(1, acknowledgementTimeoutMilliseconds / 100);
            for (var i = 0; i < loops; i++)
            {
                RevalidatePaths(request);
                var current = host.ReadProcess(started.Pid);
                if (current != null && !IdentityEquals(current, started)) throw new IOException("重启进程 PID 已复用。");
                var responsePath = ackPath + ".ack";
                if (File.Exists(responsePath))
                {
                    var ack = Read<Ack>(responsePath);
                    if (ack.Token != challenge.Token || !IdentityEquals(ack.Process, started) || current == null)
                        throw new IOException("新前端确认身份无效。");
                    foreach (var entry in request.Entries) RequireHash(Path.Combine(request.AppRoot, entry.Name), entry.TargetHash);
                    journal = journal with { State = "Completed", Message = "新前端已确认窗口加载。" }; Save(journalPath, journal);
                    return new(true, journal.State, journal.Message);
                }
                if (current == null) throw new IOException("新前端在确认前退出。");
                host.Delay(TimeSpan.FromMilliseconds(100));
            }
            throw new TimeoutException("新前端未确认窗口加载；保留待恢复事务。");
        }
        catch (Exception error)
        {
            journal = journal with { Message = error.Message };
            if (!writing)
            {
                journal = journal with { State = "Rejected" }; Save(journalPath, journal);
                return new(false, journal.State, journal.Message);
            }
            // An unrecorded launch may still be alive. Never roll back while its identity is unknown.
            if (journal.State == "Starting" && error is not FrontendUpdateLaunchException { MayHaveStarted: false } || journal.Started is { } started && host.ReadProcess(started.Pid) is not null)
            {
                journal = journal with { State = "RecoveryRequired" }; Save(journalPath, journal);
                return new(false, journal.State, journal.Message);
            }
            return RollBack(request, journal, journalPath);
        }
    }

    /// <summary>Called only after the new frontend's main window Loaded event has fired.</summary>
    public static void Acknowledge(string acknowledgementPath)
        => Acknowledge(acknowledgementPath, CurrentProcessIdentity());

    internal static void Acknowledge(string acknowledgementPath, FrontendProcessIdentity identity)
    {
        var path = Path.GetFullPath(acknowledgementPath); NoReparse(path);
        if (Path.GetFileName(path) != "ack-challenge.json") throw new InvalidDataException("确认文件名无效。");
        var challenge = Read<AckChallenge>(path);
        if (!Same(challenge.ExecutablePath, identity.ExecutablePath) || identity.Pid <= 0 || identity.StartUtcTicks <= 0)
            throw new InvalidDataException("确认程序身份不匹配。");
        ValidateManifest(challenge.Manifest);
        var appRoot = Path.GetDirectoryName(challenge.ExecutablePath)!;
        VerifyFiles(appRoot, challenge.Manifest);
        RequireHash(Path.Combine(appRoot, FrontendUpdateFiles.InstallationManifest), challenge.ManifestHash);
        Save(path + ".ack", new Ack(challenge.Token, identity));
    }

    public static FrontendUpdateInstallResult Recover(string requestPath, IFrontendUpdateHost? host = null,
        int exitTimeoutMilliseconds = 30000)
    {
        host ??= new SystemHost(); var request = LoadRequest(requestPath); using var applicationGate = ApplicationLock(request.AppRoot); using var gate = Lock(request.WorkRoot);
        var journal = LoadJournal(requestPath, request); var journalPath = Path.Combine(request.TransactionRoot, "journal.json");
        if (journal.State is "Completed" or "RolledBack" or "Rejected") return new(journal.State == "Completed", journal.State, journal.Message);
        try
        {
            WaitForExit(host, request.Parent, exitTimeoutMilliseconds);
            if (journal.Started is { } started) WaitForExit(host, started, exitTimeoutMilliseconds);
            else if (journal.State is "Starting" or "RecoveryRequired")
                return new(false, "RecoveryRequired", "启动身份未记录，必须人工确认新前端已退出；保留原始备份。");
            return RollBack(request, journal, journalPath);
        }
        catch (Exception error) { return new(false, "RecoveryRequired", error.Message); }
    }

    private static FrontendUpdateInstallResult RollBack(Request request, Journal journal, string journalPath)
    {
        try
        {
            RevalidatePaths(request);
            foreach (var entry in request.Entries) RequireHash(Path.Combine(request.TransactionRoot, "backup", entry.Name), entry.OriginalHash);
            // Check every ownership boundary before restoring any file.
            foreach (var entry in request.Entries)
            {
                var current = Hash(Path.Combine(request.AppRoot, entry.Name));
                if (current != entry.OriginalHash && current != entry.TargetHash)
                    throw new IOException($"{entry.Name} 内容归属未知，请人工核对；不会覆盖外部修改。");
                var temporary = TemporaryPath(Path.Combine(request.AppRoot, entry.Name), request.Id);
                if (File.Exists(temporary))
                {
                    var temporaryHash = Hash(temporary);
                    if (temporaryHash != entry.OriginalHash && temporaryHash != entry.TargetHash)
                        throw new IOException($"{entry.Name} 更新临时文件内容未知，请人工核对。");
                }
            }
            journal = journal with { State = "RollingBack" }; Save(journalPath, journal);
            foreach (var entry in request.Entries.Reverse())
            {
                RevalidatePaths(request); var destination = Path.Combine(request.AppRoot, entry.Name);
                var current = Hash(destination);
                if (current == entry.OriginalHash) continue;
                if (current != entry.TargetHash) throw new IOException($"{entry.Name} 在恢复时被外部修改。");
                journal = journal with { Intent = entry.Name }; Save(journalPath, journal);
                // An interrupted install can leave a complete owned target temporary file.
                RetainTemporary(request, entry);
                Replace(Path.Combine(request.TransactionRoot, "backup", entry.Name), destination, entry.OriginalHash, entry.TargetHash, request.Id);
            }
            foreach (var entry in request.Entries) RetainTemporary(request, entry);
            journal = journal with { State = "RolledBack", Intent = null }; Save(journalPath, journal);
            return new(false, journal.State, journal.Message);
        }
        catch (Exception error)
        {
            journal = journal with { State = "Unknown", Message = error.Message }; Save(journalPath, journal);
            return new(false, journal.State, journal.Message);
        }
    }

    private static Request LoadRequest(string path)
    {
        path = Path.GetFullPath(path); NoReparse(path); var request = Read<Request>(path);
        if (request.Schema != 1 || !Regex.IsMatch(request.Id, @"^[a-f0-9]{32}\z") ||
            !Same(path, Path.Combine(request.TransactionRoot, "request.json")) ||
            !Same(request.TransactionRoot, Path.Combine(request.WorkRoot, request.Id))) throw new InvalidDataException("事务路径无效。");
        RevalidatePaths(request); ValidateManifest(request.Original); ValidateManifest(request.Target);
        if (request.Original.Channel != request.Target.Channel) throw new InvalidDataException("事务更新通道改变。");
        if (CompareVersions(request.Target.Version, request.Original.Version) <= 0 || !Hex(request.TargetManifestHash, 64)) throw new InvalidDataException("事务目标版本无效。");
        if (request.Parent.Pid <= 0 || request.Parent.StartUtcTicks <= 0 || !Same(request.Parent.ExecutablePath, Path.Combine(request.AppRoot, "ClassicDesk.exe"))) throw new InvalidDataException("父进程身份无效。");
        var names = FrontendUpdateFiles.Names.Append(FrontendUpdateFiles.InstallationManifest).ToArray();
        if (request.Entries.Length != names.Length || !request.Entries.Select(e => e.Name).SequenceEqual(names) || request.Entries.Any(e => !Hex(e.OriginalHash, 64) || !Hex(e.TargetHash, 64))) throw new InvalidDataException("事务文件清单无效。");
        if (!string.Equals(request.Entries[^1].TargetHash, request.TargetManifestHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装清单未绑定官方摘要。");
        foreach (var entry in request.Entries.Take(4))
        {
            if (!string.Equals(request.Original.Files.Single(f => f.Name == entry.Name).Sha256, entry.OriginalHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.Target.Files.Single(f => f.Name == entry.Name).Sha256, entry.TargetHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("事务摘要与清单不匹配。");
        }
        return request;
    }
    private static Journal LoadJournal(string path, Request request)
    {
        var journal = Read<Journal>(Path.Combine(request.TransactionRoot, "journal.json"));
        if (journal.Id != request.Id || journal.RequestHash != Hash(path)) throw new InvalidDataException("事务登记已改变，请人工核对。");
        return journal;
    }
    private static void RevalidatePaths(Request request)
    {
        Disjoint(SafeRoot(request.AppRoot), SafeRoot(request.StageRoot), SafeRoot(request.WorkRoot));
        NoReparse(request.TransactionRoot);
        foreach (var name in FrontendUpdateFiles.Names.Append(FrontendUpdateFiles.InstallationManifest))
        { NoReparse(Path.Combine(request.AppRoot, name)); NoReparse(TemporaryPath(Path.Combine(request.AppRoot, name), request.Id)); NoReparse(Path.Combine(request.TransactionRoot, "backup", name)); }
        foreach (var name in FrontendUpdateFiles.Names) NoReparse(Path.Combine(request.StageRoot, name));
        NoReparse(Path.Combine(request.StageRoot, "frontend-update.json"));
        foreach (var name in new[] { "request.json", "journal.json", "target-manifest.json", "ack-challenge.json", "ack-challenge.json.ack" }) NoReparse(Path.Combine(request.TransactionRoot, name));
    }
    private static void ValidateManifest(FrontendUpdateManifest manifest)
    {
        if (manifest.Schema != 1 || manifest.Application != "ClassicDesk" || manifest.Channel is not ("preview" or "stable") || manifest.Platform != "windows" || manifest.Architecture != "x64" ||
            !Hex(manifest.Build, 40) || !SemanticVersion.TryParse(manifest.Version, out var version) || version!.Preview != (manifest.Channel == "preview") || manifest.Files == null || manifest.Files.Length != 4 ||
            !manifest.Files.Select(f => f.Name).Order().SequenceEqual(FrontendUpdateFiles.Names.Order()) ||
            manifest.Files.Any(f => f.Size <= 0 || f.Size > 256L * 1024 * 1024 || !Hex(f.Sha256, 64) || f.AssetName != "frontend-" + f.Name)) throw new InvalidDataException("前端安装清单不符合固定平台/通道/四文件契约。");
    }
    private static int CompareVersions(string a, string b)
    {
        if (!SemanticVersion.TryParse(a, out var left) || !SemanticVersion.TryParse(b, out var right)) throw new InvalidDataException("语义版本无效。");
        return left!.CompareTo(right!);
    }
    private sealed record SemanticVersion(int Major, int Minor, int Patch, int? PreviewNumber, bool Preview) : IComparable<SemanticVersion>
    {
        public static bool TryParse(string? value, out SemanticVersion? parsed)
        {
            parsed = null;
            var match = Regex.Match(value ?? "", @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(preview)(?:\.(0|[1-9][0-9]*))?)?\z", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return false;
            int? number = null;
            if (match.Groups[5].Success) { if (!int.TryParse(match.Groups[5].Value, out var n)) return false; number = n; }
            parsed = new(major, minor, patch, number, match.Groups[4].Success); return true;
        }
        public int CompareTo(SemanticVersion? other)
        {
            if (other == null) return 1;
            var result = Major.CompareTo(other.Major); if (result != 0) return result;
            result = Minor.CompareTo(other.Minor); if (result != 0) return result;
            result = Patch.CompareTo(other.Patch); if (result != 0) return result;
            if (Preview != other.Preview) return Preview ? -1 : 1;
            return Nullable.Compare(PreviewNumber, other.PreviewNumber);
        }
    }
    private static bool ManifestEquals(FrontendUpdateManifest a, FrontendUpdateManifest b)
        => a.Schema == b.Schema && a.Application == b.Application && a.Version == b.Version && a.Channel == b.Channel &&
        a.Platform == b.Platform && a.Architecture == b.Architecture && a.Build == b.Build && a.Files.OrderBy(f => f.Name).SequenceEqual(b.Files.OrderBy(f => f.Name));
    private static void VerifyFiles(string root, FrontendUpdateManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(root, file.Name); NoReparse(path);
            if (new FileInfo(path).Length != file.Size) throw new InvalidDataException($"{file.Name} 大小不匹配。");
            RequireHash(path, file.Sha256);
        }
    }
    private static void WaitForExit(IFrontendUpdateHost host, FrontendProcessIdentity identity, int timeout)
    {
        for (var i = 0; i <= Math.Max(0, timeout / 100); i++)
        {
            var current = host.ReadProcess(identity.Pid); if (current == null) return;
            if (!IdentityEquals(current, identity)) throw new IOException("进程 PID 已复用或身份已改变，拒绝写入。");
            if (i == Math.Max(0, timeout / 100)) break; host.Delay(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException("前端未在等待期限内退出，拒绝写入。");
    }
    private static bool IdentityEquals(FrontendProcessIdentity a, FrontendProcessIdentity b)
        => a.Pid == b.Pid && a.StartUtcTicks == b.StartUtcTicks && Same(a.ExecutablePath, b.ExecutablePath);
    private static string SafeRoot(string path) { var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); NoReparse(full); if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!)) throw new IOException("不接受磁盘根目录。"); return full; }
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static void Disjoint(params string[] roots)
    {
        for (var i = 0; i < roots.Length; i++) for (var j = i + 1; j < roots.Length; j++)
            if (Same(roots[i], roots[j]) || roots[i].StartsWith(roots[j] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || roots[j].StartsWith(roots[i] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("应用、暂存、事务目录必须独立且不嵌套。");
    }
    private static void NoReparse(string path)
    {
        for (var cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新路径包含链接或重解析点。");
    }
    private static FileStream Lock(string root) { var path = Path.Combine(root, "installer.lock"); NoReparse(path); return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    private sealed class ApplicationGate(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
    private static IDisposable ApplicationLock(string appRoot)
    {
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(appRoot.ToUpperInvariant())));
        var mutex = new Mutex(false, "ClassicDeskFrontendUpdate-" + key);
        try { if (!mutex.WaitOne(0)) throw new IOException("此应用已有更新事务正在操作。"); }
        catch (AbandonedMutexException) { /* Durable journals still block pending transactions. */ }
        catch { mutex.Dispose(); throw; }
        return new ApplicationGate(mutex);
    }
    private static void EnsureNoPending(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            NoReparse(directory); var path = Path.Combine(directory, "journal.json");
            if (!File.Exists(path)) throw new IOException("事务目录缺少日志，请人工核对。");
            if (Read<Journal>(path).State is not ("Completed" or "RolledBack" or "Rejected")) throw new IOException("存在未完成事务，请先恢复。");
        }
    }
    private static bool Hex(string value, int length) => value != null && value.Length == length && value.All(Uri.IsHexDigit);
    private static string Hash(string path) { NoReparse(path); using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static void RequireHash(string path, string expected) { if (!string.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase)) throw new IOException($"{Path.GetFileName(path)} 摘要已改变。"); }
    private static T Read<T>(string path) { NoReparse(path); return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("登记文件为空。"); }
    private static void Save<T>(string path, T value)
    {
        NoReparse(path); var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json); var temp = path + ".tmp"; NoReparse(temp);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temp, path, true); if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException("事务日志回读失败。");
    }
    private static string TemporaryPath(string destination, string id) => destination + ".frontend-update." + id + ".tmp";
    private static void RetainTemporary(Request request, Entry entry)
    {
        var path = TemporaryPath(Path.Combine(request.AppRoot, entry.Name), request.Id); NoReparse(path);
        if (!File.Exists(path)) return;
        var hash = Hash(path);
        if (hash != entry.TargetHash && hash != entry.OriginalHash) throw new IOException("临时文件归属未知，请人工核对。");
        var directory = Path.Combine(request.TransactionRoot, "retained-temporary"); NoReparse(directory); Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, entry.Name + "." + hash + ".tmp"); NoReparse(archive);
        if (File.Exists(archive)) throw new IOException("临时文件留存路径已存在，请人工核对。");
        File.Move(path, archive); RequireHash(archive, hash);
    }
    private static void Replace(string source, string destination, string expected, string ownedHash, string id)
    {
        RequireHash(source, expected); var temp = TemporaryPath(destination, id); NoReparse(temp);
        if (File.Exists(temp)) throw new IOException("更新临时文件已存在，请人工核对。");
        using (var input = File.OpenRead(source)) using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
        // Files stay complete across a crash. This is a cooperative hash check immediately before an atomic rename,
        // not an OS compare-and-swap against a hostile writer outside the installation mutex.
        RequireHash(temp, expected); RequireHash(destination, ownedHash); File.Move(temp, destination, true); RequireHash(destination, expected);
    }
    private sealed class SystemHost : IFrontendUpdateHost
    {
        public FrontendProcessIdentity? ReadProcess(int pid)
        {
            Process process;
            try { process = Process.GetProcessById(pid); } catch (ArgumentException) { return null; }
            using (process) { if (process.HasExited) return null; return new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule?.FileName ?? throw new IOException("进程路径不可读。")); }
        }
        public void Delay(TimeSpan delay) => Thread.Sleep(delay);
        public FrontendProcessIdentity Start(string executablePath, string acknowledgementPath)
        {
            var info = new ProcessStartInfo(executablePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executablePath)!, CreateNoWindow = true };
            info.ArgumentList.Add("--frontend-update-ack"); info.ArgumentList.Add(acknowledgementPath);
            Process? process;
            try { process = Process.Start(info); }
            catch (Exception error) { throw new FrontendUpdateLaunchException("前端启动失败。", false, error); }
            if (process == null) throw new FrontendUpdateLaunchException("前端启动失败。", false);
            using (process)
            {
                try { return new(process.Id, process.StartTime.ToUniversalTime().Ticks, executablePath); }
                catch (Exception error) { throw new FrontendUpdateLaunchException("前端已启动，但身份读取失败。", true, error); }
            }
        }
    }
}
