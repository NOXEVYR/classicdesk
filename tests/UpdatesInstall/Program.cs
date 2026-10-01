using ClassicDesk;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.FirstOrDefault() == "--child-parent") { File.WriteAllText(args[1], "ready"); Thread.Sleep(1500); return 0; }
if (args.FirstOrDefault() == "--frontend-update-ack") { FrontendUpdateInstaller.Acknowledge(args[1]); Thread.Sleep(1500); return 0; }
if (args.FirstOrDefault() == "--apply-frontend-update")
{
    var result = FrontendUpdateInstaller.Run(args[1]); File.WriteAllText(args[1] + ".result", JsonSerializer.Serialize(result)); return result.Success ? 0 : 2;
}
var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "ClassicDesk-updates-checks-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var checks = new List<object>(); var failed = 0;
void Check(string name, Action action)
{
    try { action(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; checks.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("应拒绝此请求。"); }
Fixture Make(string name, bool real = false)
{
    var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
    var app = Path.Combine(directory, "app"); var stage = Path.Combine(directory, "stage"); var work = Path.Combine(directory, "transactions");
    Directory.CreateDirectory(app); Directory.CreateDirectory(stage);
    foreach (var file in FrontendUpdateFiles.Names)
    {
        if (real)
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(app, file));
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(stage, file));
            if (file.EndsWith(".json")) File.AppendAllText(Path.Combine(app, file), "\n ");
            if (file == "ClassicDesk.dll") using (var stream = File.Open(Path.Combine(app, file), FileMode.Append)) stream.WriteByte(0);
        }
        else { File.WriteAllText(Path.Combine(app, file), "original:" + file); File.WriteAllText(Path.Combine(stage, file), "new:" + file); }
    }
    FrontendUpdateManifest Manifest(string path, string version) => new(1, "ClassicDesk", version, "preview", "windows", "x64", new string('a', 40),
        FrontendUpdateFiles.Names.Select(file => new FrontendUpdateFile(file, "frontend-" + file, new FileInfo(Path.Combine(path, file)).Length, Hash(Path.Combine(path, file)))).ToArray());
    var old = Manifest(app, "0.11.17-preview"); var target = Manifest(stage, "0.11.18-preview");
    File.WriteAllText(Path.Combine(app, FrontendUpdateFiles.InstallationManifest), JsonSerializer.Serialize(old));
    File.WriteAllText(Path.Combine(stage, "frontend-update.json"), JsonSerializer.Serialize(target));
    File.WriteAllText(Path.Combine(app, "user-settings.json"), "user-sentinel"); File.WriteAllText(Path.Combine(app, "service.ps1"), "service-sentinel");
    return new(app, stage, work, old, new(stage, target, Hash(Path.Combine(stage, "frontend-update.json"))), new(101, 10, Path.Combine(app, "ClassicDesk.exe")));
}
FrontendUpdatePrepared Prepare(Fixture f) => FrontendUpdateInstaller.Prepare(f.App, f.Target, f.Work, f.Parent);
void Original(Fixture f)
{
    foreach (var file in f.Original.Files) Assert(Hash(Path.Combine(f.App, file.Name)) == file.Sha256, "回退字节不匹配。");
    Assert(File.ReadAllText(Path.Combine(f.App, "user-settings.json")) == "user-sentinel", "用户文件改变。");
    Assert(File.ReadAllText(Path.Combine(f.App, "service.ps1")) == "service-sentinel", "服务脚本改变。");
}
Check("registered-old-files-and-persistent-backup", () =>
{
    var f = Make("prepare"); var p = Prepare(f);
    Assert(File.Exists(p.HelperExecutablePath), "未生成辅助程序。");
    foreach (var file in f.Original.Files) Assert(Hash(Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "backup", file.Name)) == file.Sha256, "备份不匹配。");
    Assert(FrontendUpdateInstaller.InspectPending(f.Work).Single().State == "Prepared", "未登记未完成事务。");
    Throws(() => Prepare(f)); Original(f);
});
Check("missing-registration-refused", () => { var f = Make("missing"); File.Delete(Path.Combine(f.App, FrontendUpdateFiles.InstallationManifest)); Throws(() => Prepare(f)); });
Check("old-file-hash-mismatch-refused", () => { var f = Make("old-mutated"); File.AppendAllText(Path.Combine(f.App, "ClassicDesk.dll"), "external"); Throws(() => Prepare(f)); });
Check("manifest-original-bytes-bound", () => { var f = Make("manifest-mutated"); File.AppendAllText(Path.Combine(f.Stage, "frontend-update.json"), "\n"); Throws(() => Prepare(f)); });
Check("stage-manifest-object-bound", () => { var f = Make("manifest-object"); Throws(() => FrontendUpdateInstaller.Prepare(f.App, f.Target with { Manifest = f.Target.Manifest with { Build = new string('b', 40) } }, f.Work, f.Parent)); });
Check("same-or-nested-paths-refused", () => { var f = Make("nested"); Throws(() => FrontendUpdateInstaller.Prepare(f.App, f.Target with { Directory = f.App }, f.Work, f.Parent)); Throws(() => FrontendUpdateInstaller.Prepare(f.App, f.Target, Path.Combine(f.App, "work"), f.Parent)); });
Check("parent-exit-timeout-zero-writes", () => { var f = Make("timeout"); var p = Prepare(f); var h = new FakeHost(f.Parent); var r = FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); Assert(r.State == "Rejected", r.Message); Assert(h.Starts == 0, "不应启动。"); Original(f); });
Check("pid-reuse-zero-writes", () => { var f = Make("pid-reuse"); var p = Prepare(f); var h = new FakeHost(f.Parent with { StartUtcTicks = 11 }); var r = FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); Assert(r.State == "Rejected", r.Message); Original(f); });
Check("staged-file-changed-after-prepare", () => { var f = Make("stage-changed"); var p = Prepare(f); File.AppendAllText(Path.Combine(f.Stage, "ClassicDesk.dll"), "external"); Assert(FrontendUpdateInstaller.Run(p.RequestPath, new FakeHost(), 0, 0).State == "Rejected", "应拒绝改变目标。"); Original(f); });
Check("destination-changed-after-prepare", () => { var f = Make("destination-changed"); var p = Prepare(f); File.AppendAllText(Path.Combine(f.App, "ClassicDesk.dll"), "external"); var expected = Hash(Path.Combine(f.App, "ClassicDesk.dll")); Assert(FrontendUpdateInstaller.Run(p.RequestPath, new FakeHost(), 0, 0).State == "Rejected", "应拒绝改变安装文件。"); Assert(Hash(Path.Combine(f.App, "ClassicDesk.dll")) == expected, "覆盖外部修改。"); });
Check("new-window-ack-success-and-sentinels", () => { var f = Make("ack-success"); var p = Prepare(f); var h = new FakeHost { Acknowledge = true }; var r = FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 100); Assert(r.Success && r.State == "Completed", r.Message); foreach (var file in f.Target.Manifest.Files) Assert(Hash(Path.Combine(f.App, file.Name)) == file.Sha256, "目标不匹配。"); Assert(Hash(Path.Combine(f.App, FrontendUpdateFiles.InstallationManifest)) == f.Target.ManifestSha256, "未保存官方清单原始字节。"); Assert(File.ReadAllText(Path.Combine(f.App, "user-settings.json")) == "user-sentinel", "用户文件改变。"); });
Check("start-is-not-success-timeout-pending", () => { var f = Make("ack-timeout"); var p = Prepare(f); var h = new FakeHost(); var r = FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); Assert(!r.Success && r.State == "RecoveryRequired", r.Message); Throws(() => Prepare(f)); h.Running = null; Assert(FrontendUpdateInstaller.Recover(p.RequestPath, h, 0).State == "RolledBack", "未恢复。"); Original(f); });
Check("launch-failure-restores-exact-original", () => { var f = Make("launch-failed"); var p = Prepare(f); var h = new FakeHost { LaunchFails = true }; var r = FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); Assert(r.State == "RolledBack", r.Message); Original(f); });
Check("unknown-external-change-never-overwritten", () => { var f = Make("unknown"); var p = Prepare(f); var h = new FakeHost(); FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); h.Running = null; File.WriteAllText(Path.Combine(f.App, "ClassicDesk.dll"), "external edit"); var r = FrontendUpdateInstaller.Recover(p.RequestPath, h, 0); Assert(r.State == "Unknown", r.Message); Assert(File.ReadAllText(Path.Combine(f.App, "ClassicDesk.dll")) == "external edit", "覆盖外改。"); Throws(() => Prepare(f)); });
Check("crash-after-intent-recovery", () => { var f = Make("crash-intent"); var p = Prepare(f); var path = Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "journal.json"); var j = JsonNode.Parse(File.ReadAllText(path))!; j["State"] = "Applying"; j["Intent"] = "ClassicDesk.exe"; File.WriteAllText(path, j.ToJsonString()); File.Copy(Path.Combine(f.Stage, "ClassicDesk.exe"), Path.Combine(f.App, "ClassicDesk.exe"), true); Assert(FrontendUpdateInstaller.Recover(p.RequestPath, new FakeHost(), 0).State == "RolledBack", "未恢复意图写入。"); Original(f); });
Check("crash-unrecorded-launch-requires-human-review", () => { var f = Make("crash-starting"); var p = Prepare(f); var path = Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "journal.json"); var j = JsonNode.Parse(File.ReadAllText(path))!; j["State"] = "Starting"; File.WriteAllText(path, j.ToJsonString()); Assert(FrontendUpdateInstaller.Recover(p.RequestPath, new FakeHost(), 0).State == "RecoveryRequired", "不应猜测启动身份。"); Original(f); });
Check("request-tamper-refused", () => { var f = Make("request-mutated"); var p = Prepare(f); File.AppendAllText(p.RequestPath, "\n"); Throws(() => FrontendUpdateInstaller.Run(p.RequestPath, new FakeHost(), 0, 0)); Original(f); });
Check("forged-window-ack-refused", () => { var f = Make("forged-ack"); var p = Prepare(f); var h = new FakeHost { ForgeAck = true }; var r = FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); Assert(!r.Success && r.State == "RecoveryRequired", r.Message); });
Check("ack-after-installed-bytes-change-refused", () => { var f = Make("ack-file-changed"); var p = Prepare(f); var h = new FakeHost { BeforeAck = path => File.AppendAllText(Path.Combine(f.App, "ClassicDesk.dll"), "external") }; var r = FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); Assert(!r.Success && r.State == "RecoveryRequired", r.Message); Throws(() => FrontendUpdateInstaller.Acknowledge(Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "ack-challenge.json"), h.Running!)); h.Running = null; Assert(FrontendUpdateInstaller.Recover(p.RequestPath, h, 0).State == "Unknown", "应保留外部改动。"); });
Check("recovery-waits-exact-started-process", () => { var f = Make("recovery-running"); var p = Prepare(f); var h = new FakeHost(); FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); var expected = Hash(Path.Combine(f.App, "ClassicDesk.dll")); Assert(FrontendUpdateInstaller.Recover(p.RequestPath, h, 0).State == "RecoveryRequired", "应等待新进程退出。"); Assert(Hash(Path.Combine(f.App, "ClassicDesk.dll")) == expected, "运行中写回退。"); h.Running = h.Running! with { StartUtcTicks = 99 }; Assert(FrontendUpdateInstaller.Recover(p.RequestPath, h, 0).State == "RecoveryRequired", "应拒绝复用PID。"); });
Check("startup-exits-before-ack-restores-original", () => { var f = Make("startup-exits"); var p = Prepare(f); var r = FrontendUpdateInstaller.Run(p.RequestPath, new FakeHost { ExitBeforeAck = true }, 0, 0); Assert(r.State == "RolledBack", r.Message); Original(f); });
Check("backup-tamper-blocks-rollback", () => { var f = Make("backup-mutated"); var p = Prepare(f); var h = new FakeHost(); FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 0); h.Running = null; File.AppendAllText(Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "backup", "ClassicDesk.dll"), "external"); var installedHash = Hash(Path.Combine(f.App, "ClassicDesk.dll")); Assert(FrontendUpdateInstaller.Recover(p.RequestPath, h, 0).State == "Unknown", "应拒绝损坏备份。"); Assert(Hash(Path.Combine(f.App, "ClassicDesk.dll")) == installedHash, "应先全量核对再恢复。"); });
Check("journal-corruption-visible-and-blocks-prepare", () => { var f = Make("bad-journal"); var p = Prepare(f); File.WriteAllText(Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "journal.json"), "{broken"); Assert(FrontendUpdateInstaller.InspectPending(f.Work).Single().State == "Unknown", "损坏日志不应隐去。"); Throws(() => Prepare(f)); });
Check("four-file-allowlist-refuses-escape", () => { var f = Make("escape"); var files = f.Target.Manifest.Files.ToArray(); files[0] = files[0] with { Name = "../user-settings.json" }; Throws(() => FrontendUpdateInstaller.Prepare(f.App, f.Target with { Manifest = f.Target.Manifest with { Files = files } }, f.Work, f.Parent)); });
Check("platform-channel-and-non-increment-version-refused", () => { var f = Make("bad-contract"); foreach (var target in new[] { f.Target.Manifest with { Channel = "stable" }, f.Target.Manifest with { Architecture = "arm64" }, f.Target.Manifest with { Build = "wrong" }, f.Target.Manifest with { Version = f.Original.Version } }) Throws(() => FrontendUpdateInstaller.Prepare(f.App, f.Target with { Manifest = target }, f.Work, f.Parent)); });
Check("strict-semver-refuses-leading-zero-newline-overflow", () => { var f = Make("semver-bad"); foreach (var version in new[] { "00.11.18-preview", "0.011.18-preview", "0.11.18-preview\n", "2147483648.11.18-preview", "0.11.18-preview.01", "0.11.18-preview.2147483648" }) Throws(() => FrontendUpdateInstaller.Prepare(f.App, f.Target with { Manifest = f.Target.Manifest with { Version = version } }, f.Work, f.Parent)); });
Check("preview-number-semantic-increment", () => { var f = Make("preview-increment"); var old = f.Original with { Version = "0.11.18-preview.1" }; File.WriteAllText(Path.Combine(f.App, FrontendUpdateFiles.InstallationManifest), JsonSerializer.Serialize(old)); var target = f.Target.Manifest with { Version = "0.11.18-preview.2" }; File.WriteAllText(Path.Combine(f.Stage, "frontend-update.json"), JsonSerializer.Serialize(target)); var stage = f.Target with { Manifest = target, ManifestSha256 = Hash(Path.Combine(f.Stage, "frontend-update.json")) }; var p = FrontendUpdateInstaller.Prepare(f.App, stage, f.Work, f.Parent); Assert(FrontendUpdateInstaller.Run(p.RequestPath, new FakeHost { Acknowledge = true }, 0, 100).Success, "preview.1→preview.2 应允许。"); });
Check("stable-semantic-increment-with-original-manifest", () => { var f = Make("stable"); var old = f.Original with { Version = "0.11.17", Channel = "stable" }; File.WriteAllText(Path.Combine(f.App, FrontendUpdateFiles.InstallationManifest), JsonSerializer.Serialize(old)); var target = f.Target.Manifest with { Version = "0.11.18", Channel = "stable" }; File.WriteAllText(Path.Combine(f.Stage, "frontend-update.json"), JsonSerializer.Serialize(target)); var stage = f.Target with { Manifest = target, ManifestSha256 = Hash(Path.Combine(f.Stage, "frontend-update.json")) }; var p = FrontendUpdateInstaller.Prepare(f.App, stage, f.Work, f.Parent); Assert(FrontendUpdateInstaller.Run(p.RequestPath, new FakeHost { Acknowledge = true }, 0, 100).Success, "stable更新应允许。"); Assert(FrontendUpdateInstaller.ReadInstallation(f.App).Channel == "stable", "通道改变。"); });
Check("cross-channel-update-refused", () => { var f = Make("cross-channel"); var target = f.Target.Manifest with { Version = "0.11.18", Channel = "stable" }; File.WriteAllText(Path.Combine(f.Stage, "frontend-update.json"), JsonSerializer.Serialize(target)); Throws(() => FrontendUpdateInstaller.Prepare(f.App, f.Target with { Manifest = target, ManifestSha256 = Hash(Path.Combine(f.Stage, "frontend-update.json")) }, f.Work, f.Parent)); Original(f); });
Check("cooperative-durable-lock-refuses-write", () => { var f = Make("lock"); var p = Prepare(f); using var gate = new FileStream(Path.Combine(f.Work, "installer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); Throws(() => FrontendUpdateInstaller.Run(p.RequestPath, new FakeHost(), 0, 0)); Original(f); });
Check("crash-partial-unknown-bytes-not-overwritten", () => { var f = Make("crash-partial"); var p = Prepare(f); var journalPath = Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "journal.json"); var j = JsonNode.Parse(File.ReadAllText(journalPath))!; j["State"] = "Applying"; j["Intent"] = "ClassicDesk.dll"; File.WriteAllText(journalPath, j.ToJsonString()); File.WriteAllText(Path.Combine(f.App, "ClassicDesk.dll"), "unknown partial content"); Assert(FrontendUpdateInstaller.Recover(p.RequestPath, new FakeHost(), 0).State == "Unknown", "应要求人工核对未知内容。"); Assert(File.ReadAllText(Path.Combine(f.App, "ClassicDesk.dll")) == "unknown partial content", "未知字节被覆盖。"); });
Check("crash-before-rename-retains-owned-temp-and-can-retry", () => { var f = Make("crash-temp"); var p = Prepare(f); var temporary = Path.Combine(f.App, "ClassicDesk.dll.frontend-update." + p.TransactionId + ".tmp"); File.Copy(Path.Combine(f.Stage, "ClassicDesk.dll"), temporary); var journalPath = Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "journal.json"); var j = JsonNode.Parse(File.ReadAllText(journalPath))!; j["State"] = "Applying"; j["Intent"] = "ClassicDesk.dll"; File.WriteAllText(journalPath, j.ToJsonString()); Assert(FrontendUpdateInstaller.Recover(p.RequestPath, new FakeHost(), 0).State == "RolledBack", "应恢复完整旧版本。"); Assert(!File.Exists(temporary), "旧临时文件没有留存移走。"); Assert(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(p.RequestPath)!, "retained-temporary")).Length == 1, "应保留可核对临时文件。"); Original(f); var retry = Prepare(f); var result = FrontendUpdateInstaller.Run(retry.RequestPath, new FakeHost { Acknowledge = true }, 0, 100); Assert(result.Success && result.State == "Completed", "恢复后重试未完成：" + result.Message); });
Check("unknown-temporary-file-not-moved-or-overwritten", () => { var f = Make("unknown-temp"); var p = Prepare(f); var temporary = Path.Combine(f.App, "ClassicDesk.dll.frontend-update." + p.TransactionId + ".tmp"); File.WriteAllText(temporary, "unknown external temporary"); Assert(FrontendUpdateInstaller.Recover(p.RequestPath, new FakeHost(), 0).State == "Unknown", "应标记未知临时文件。"); Assert(File.ReadAllText(temporary) == "unknown external temporary", "未知临时文件被覆盖。"); Original(f); });
Check("completed-or-rejected-request-never-reruns", () => { var f = Make("no-rerun"); var p = Prepare(f); var h = new FakeHost { Acknowledge = true }; Assert(FrontendUpdateInstaller.Run(p.RequestPath, h, 0, 100).Success, "首次应成功。"); var next = new FakeHost(); Assert(!FrontendUpdateInstaller.Run(p.RequestPath, next, 0, 0).Success && next.Starts == 0, "终态事务不应再执行。"); });
Check("reparse-stage-refused", () => { var f = Make("reparse"); var junction = Path.Combine(Path.GetDirectoryName(f.App)!, "stage-link"); var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(junction); info.ArgumentList.Add(f.Stage); using var child = Process.Start(info)!; child.WaitForExit(); if (child.ExitCode != 0) throw new Exception("无法生成隔离junction夹具。"); Throws(() => FrontendUpdateInstaller.Prepare(f.App, f.Target with { Directory = junction }, f.Work, f.Parent)); });
Check("real-child-helper-exit-replace-restart-ack", () =>
{
    var f = Make("real-process", true); var parentInfo = new ProcessStartInfo(Path.Combine(f.App, "ClassicDesk.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = f.App };
    parentInfo.ArgumentList.Add("--child-parent"); parentInfo.ArgumentList.Add(Path.Combine(f.App, "parent-ready"));
    using var parent = Process.Start(parentInfo) ?? throw new Exception("合成父进程未启动。");
    var identity = new FrontendProcessIdentity(parent.Id, parent.StartTime.ToUniversalTime().Ticks, Path.Combine(f.App, "ClassicDesk.exe"));
    var p = FrontendUpdateInstaller.Prepare(f.App, f.Target, f.Work, identity);
    var helperInfo = new ProcessStartInfo(p.HelperExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(p.HelperExecutablePath)! };
    helperInfo.ArgumentList.Add("--apply-frontend-update"); helperInfo.ArgumentList.Add(p.RequestPath);
    using var helper = Process.Start(helperInfo) ?? throw new Exception("辅助进程未启动。");
    Assert(helper.WaitForExit(15000), "辅助进程未完成；没有强杀。"); Assert(helper.ExitCode == 0, File.Exists(p.RequestPath + ".result") ? File.ReadAllText(p.RequestPath + ".result") : "辅助进程失败。");
    Assert(parent.HasExited, "没有等待父进程退出。"); var r = JsonSerializer.Deserialize<FrontendUpdateInstallResult>(File.ReadAllText(p.RequestPath + ".result"))!; Assert(r.Success, r.Message);
    foreach (var file in f.Target.Manifest.Files) Assert(Hash(Path.Combine(f.App, file.Name)) == file.Sha256, "实际安装字节不符。");
    Assert(File.ReadAllText(Path.Combine(f.App, "user-settings.json")) == "user-sentinel", "用户哨兵改变。"); Assert(File.ReadAllText(Path.Combine(f.App, "service.ps1")) == "service-sentinel", "服务哨兵改变。");
    Thread.Sleep(1600);
});
File.WriteAllText(Path.Combine(root, "updates-install-report.json"), JsonSerializer.Serialize(new { passed = checks.Count - failed, failed, checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{checks.Count - failed}/{checks.Count} checks; {root}"); return failed == 0 ? 0 : 1;

static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
sealed record Fixture(string App, string Stage, string Work, FrontendUpdateManifest Original, FrontendUpdateStage Target, FrontendProcessIdentity Parent);
sealed class FakeHost(FrontendProcessIdentity? running = null) : IFrontendUpdateHost
{
    public FrontendProcessIdentity? Running = running;
    public bool Acknowledge, LaunchFails, ForgeAck, ExitBeforeAck; public Action<string>? BeforeAck; public int Starts;
    public FrontendProcessIdentity? ReadProcess(int pid) => Running?.Pid == pid ? Running : null;
    public void Delay(TimeSpan delay) { }
    public FrontendProcessIdentity Start(string executablePath, string acknowledgementPath)
    {
        Starts++; if (LaunchFails) throw new FrontendUpdateLaunchException("synthetic launch failed", false);
        Running = new(202, 20, executablePath);
        BeforeAck?.Invoke(acknowledgementPath);
        if (Acknowledge) FrontendUpdateInstaller.Acknowledge(acknowledgementPath, Running);
        if (ForgeAck) File.WriteAllText(acknowledgementPath + ".ack", "{\"Token\":\"wrong\",\"Process\":{\"Pid\":202,\"StartUtcTicks\":20,\"ExecutablePath\":\"C:/wrong\"}}");
        var identity = Running; if (ExitBeforeAck) Running = null; return identity;
    }
}
