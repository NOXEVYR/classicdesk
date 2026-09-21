using ClassicDesk;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2) throw new ArgumentException("Reviewed bundle and new test output directory required");
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new IOException("Use a new test output directory");
Directory.CreateDirectory(output);
var fixture = Path.Combine(output, "fixture");
Directory.CreateDirectory(fixture);
foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    var target = Path.Combine(fixture, Path.GetRelativePath(source, file));
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file, target);
}
var metadata = Path.Combine(fixture, "service-package.json");
var original = File.ReadAllBytes(metadata);
var bundle = ShellServicePackage.Verify(fixture);
var results = new List<object>();
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
void Test(string name, Action run)
{
    try { run(); results.Add(new { name, passed = true, error = "" }); }
    catch (Exception e) { results.Add(new { name, passed = false, error = e.ToString() }); }
    finally { File.WriteAllBytes(metadata, original); }
}
void Write(ShellServiceBundle value) => File.WriteAllText(metadata, JsonSerializer.Serialize(value));
void Tamper(string relative, string text, bool reseal)
{
    var path = Path.Combine(fixture, relative);var before = File.ReadAllBytes(path);
    try
    {
        File.WriteAllText(path, text, new System.Text.UnicodeEncoding(false, true));
        if (reseal)
        {
            var bytes = File.ReadAllBytes(path);
            Write(bundle with { Files = bundle.Files.Select(f => f.Path == relative ? new(relative, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))) : f).ToArray() });
        }
        Reject(() => ShellServicePackage.Verify(fixture));
    }
    finally { File.WriteAllBytes(path, before); }
}
Test("reviewed offline bundle verifies", () => Check(ShellServicePackage.Verify(fixture).Files.Count == bundle.Files.Count));
Test("trailing separator accepted", () => Check(ShellServicePackage.Verify(fixture + Path.DirectorySeparatorChar).SchemaVersion == 1));
Test("unknown state rejected", () => { Write(bundle with { State = "active" }); Reject(() => ShellServicePackage.Verify(fixture)); });
Test("unreviewed host rejected", () => { Write(bundle with { HostSha256 = new('A', 64) }); Reject(() => ShellServicePackage.Verify(fixture)); });
Test("duplicate asset rejected", () => { Write(bundle with { Files = bundle.Files.Append(bundle.Files[0]).ToArray() }); Reject(() => ShellServicePackage.Verify(fixture)); });
Test("path traversal rejected", () => { Write(bundle with { Files = bundle.Files.Select((f, i) => i == 0 ? f with { Path = "../escape" } : f).ToArray() }); Reject(() => ShellServicePackage.Verify(fixture)); });
Test("asset size budget enforced", () => { Write(bundle with { Files = bundle.Files.Select((f, i) => i == 0 ? f with { Bytes = long.MaxValue } : f).ToArray() }); Reject(() => ShellServicePackage.Verify(fixture)); });
Test("modified configuration rejected", () => Tamper("Runtime/AppData/Engine/settings.ini", "[Settings]\r\nInclude=*\r\n", false));
Test("self-consistent broad injection scope rejected", () => Tamper("Runtime/AppData/Engine/settings.ini", "[Settings]\r\nSafeMode=0\r\nInclude=*\r\n", true));
Test("self-consistent unknown module library rejected", () => Tamper("Runtime/AppData/Engine/Mods/taskbar-start-button-position.ini", "[Mod]\r\nLibraryFileName=unreviewed.dll\r\n", true));
Test("self-consistent profile substitution rejected", () => { Write(bundle with { Source = bundle.Source with { AppliedProfile = bundle.Source.AppliedProfile with { StartOnLeft = !bundle.Source.AppliedProfile.StartOnLeft } } }); Reject(() => ShellServicePackage.Verify(fixture)); });
Test("extra file rejected", () => { var path=Path.Combine(fixture,"extra.ini");try { File.WriteAllText(path,"extra");Reject(() => ShellServicePackage.Verify(fixture)); } finally { File.Delete(path); } });
Test("service states never offer a second engine or legacy restore", () =>
{
    foreach (var state in new uint[] { 0, 1, 2, 4, 7 })
        foreach (var start in new[] { 2, 3, 4 })
        { var r = new ShellServiceObservation(true, start, state, "test").Review();Check(!r.CanEnable && !r.CanRestore && !r.CanResume && !r.IsRunning); }
});
Test("running old service does not report staged update as applied", () =>
{
    var review = new ShellServiceObservation(true, 2, 4, "test", true).Review();
    Check(review.Title.Contains("等待重启") && !review.IsRunning && !review.CanEnable && !review.CanRestore && !review.CanResume);
});
Check(ShellServicePackage.Verify(fixture).Files.Count == bundle.Files.Count);
int failed = results.Count(r => !(bool)r.GetType().GetProperty("passed")!.GetValue(r)!);
var report = JsonSerializer.Serialize(new { passed = results.Count - failed, failed, serviceWrites = 0, engineStarts = 0, checks = results }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output,"report.json"), report);Console.WriteLine(report);
return failed == 0 ? 0 : 1;
