using System.Text;
using System.Text.Json;
using ClassicDesk;

// All discovery and persistence use synthetic local fixtures; no process launch or actual Start Menu reads.
var reportRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "ClassicDesk-StartCore-reports");
var root = Path.Combine(reportRoot, "fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<object>(); var failures = 0; var skips = new List<string>();
void Test(string name, Action test)
{
    try { test(); checks.Add(new { name, passed = true }); }
    catch (Exception ex) { failures++; checks.Add(new { name, passed = false, error = ex.ToString() }); Console.Error.WriteLine(name + ": " + ex.Message); }
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException) { return; }
    throw new Exception("Expected rejection");
}
void RejectReparse(Action action)
{
    try { action(); }
    catch (InvalidDataException ex) when (ex.Message.Contains("重解析", StringComparison.Ordinal)) { return; }
    throw new Exception("Expected explicit reparse rejection before descendant access");
}
string FileAt(string name, string content)
{
    var p = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, content); return p;
}
void RejectJson(string name, string json) => Test(name, () => Reject(() => ClassicStartProfileFile.Import(FileAt("invalid-" + Guid.NewGuid().ToString("N") + ".json", json))));
string HashId(char c) => "app:" + new string(c, 64);

Test("missing config returns defaults without creating config", () =>
{
    var path = Path.Combine(root, "missing", "start.json"); var state = ClassicStartProfileFile.Load(path);
    Assert(state.Revision == "missing" && state.Options.Style == "win7" && state.Options.IconSize == 24);
    Assert(!Directory.Exists(Path.GetDirectoryName(path)));
});
Test("default options validate", () => new ClassicStartOptions().Validate());
foreach (var style in new[] { "win7", "win10", "compact" }) Test("supported style " + style, () => new ClassicStartOptions(Style: style).Validate());
foreach (var size in new[] { 16, 24, 32 }) Test("supported icon size " + size, () => new ClassicStartOptions(IconSize: size).Validate());
RejectJson("reject empty object", "{}");
RejectJson("reject nonobject", "[]");
RejectJson("reject unknown key", "{\"AutoLaunch\":true}");
RejectJson("reject duplicate key", "{\"Style\":\"win7\",\"Style\":\"win10\"}");
RejectJson("reject case variants", "{\"style\":\"win7\"}");
RejectJson("reject bad style", "{\"Style\":\"win11\"}");
RejectJson("reject icon boundary", "{\"IconSize\":20}");
RejectJson("reject negative pins", "{\"MaxPinned\":-1}");
RejectJson("reject excessive pins", "{\"MaxPinned\":25}");
RejectJson("reject string boolean", "{\"SortAscending\":\"false\"}");
RejectJson("reject fractional count", "{\"MaxPinned\":1.5}");
RejectJson("reject null style", "{\"Style\":null}");
RejectJson("reject null arrays", "{\"PinnedIds\":null}");
RejectJson("reject invalid imported app paths", "{\"PinnedIds\":[\"C:\\\\malicious.exe\"]}");
RejectJson("reject duplicate pin ids", JsonSerializer.Serialize(new { PinnedIds = new[] { HashId('a'), HashId('a') } }));
RejectJson("reject unknown place", "{\"VisiblePlaces\":[\"shutdown\"]}");
RejectJson("reject repeated place", "{\"VisiblePlaces\":[\"user\",\"user\"]}");
RejectJson("reject nested duplicate keys", JsonSerializer.Serialize(new { Style = "win7" })[..^1] + ",\"CustomFolders\":[{\"Id\":\"folder:test\",\"Name\":\"A\",\"Name\":\"B\",\"Path\":\"C:\\\\Users\"}]}");
RejectJson("reject nested unknown keys", "{\"CustomFolders\":[{\"Id\":\"folder:test\",\"Name\":\"A\",\"Path\":\"C:\\\\Users\",\"Command\":\"calc\"}]}");
RejectJson("reject null folder", "{\"CustomFolders\":[null]}");
RejectJson("reject incomplete folder", "{\"CustomFolders\":[{\"Id\":\"folder:test\"}]}");
RejectJson("reject network folder", "{\"CustomFolders\":[{\"Id\":\"folder:test\",\"Name\":\"A\",\"Path\":\"\\\\\\\\server\\\\share\"}]}");
Test("reject oversize import", () => Reject(() => ClassicStartProfileFile.Import(FileAt("huge.json", new string(' ', ClassicStartProfileFile.MaximumBytes + 1)))));
Test("reject malformed JSON", () => Reject(() => ClassicStartProfileFile.Import(FileAt("broken.json", "{"))));
Test("accept minimal import and preserve defaults", () => Assert(ClassicStartProfileFile.Import(FileAt("minimal.json", "{\"Style\":\"compact\"}")).IconSize == 24));
Test("read BOM and backup exact original bytes", () =>
{
    var p = Path.Combine(root, "bom.json"); var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"Style\":\"win7\"}\r\n")).ToArray(); File.WriteAllBytes(p, bytes);
    var old = ClassicStartProfileFile.Load(p); var updated = ClassicStartProfileFile.Save(old.Options with { Style = "compact" }, old.Revision, p);
    Assert(updated.Revision != old.Revision && ClassicStartProfileFile.Load(p).Options.Style == "compact");
    Assert(Directory.GetFiles(root, "bom.json.backup-*").Single() is var backup && File.ReadAllBytes(backup).SequenceEqual(bytes));
});
Test("roundtrip options order folders icons and places", () =>
{
    var folder = Directory.CreateDirectory(Path.Combine(root, "custom")).FullName;
    var options = new ClassicStartOptions("win10", 32, 2, false, false, false, [HashId('b'), HashId('a')], [new("folder:work", "工作", folder)], ["downloads", "settings"]);
    var p = Path.Combine(root, "roundtrip.json"); ClassicStartProfileFile.Export(p, options); var imported = ClassicStartProfileFile.Import(p);
    Assert(imported.Style == "win10" && imported.IconSize == 32 && !imported.SortAscending && !imported.ShowEntryIcons && !imported.ShowUserHeading);
    Assert(imported.EffectivePinnedIds.SequenceEqual(options.EffectivePinnedIds) && imported.EffectiveVisiblePlaces.SequenceEqual(options.EffectiveVisiblePlaces));
    Assert(imported.EffectiveCustomFolders.Single() == options.EffectiveCustomFolders.Single());
});
Test("export refuses overwriting user file", () =>
{
    var p = FileAt("do-not-overwrite.json", "user data"); Reject(() => ClassicStartProfileFile.Export(p, new())); Assert(File.ReadAllText(p) == "user data");
});
Test("save revision detects external byte modification", () =>
{
    var p = Path.Combine(root, "revision.json"); var first = ClassicStartProfileFile.Save(new(), "missing", p);
    File.AppendAllText(p, " "); Reject(() => ClassicStartProfileFile.Save(new(Style: "compact"), first.Revision, p)); Assert(File.ReadAllText(p).EndsWith(' '));
});
Test("corrupt original protected even with current raw revision", () =>
{
    var p = FileAt("corrupt-current.json", "{bad"); var revision = ClassicStartProfileFile.Revision(p);
    Reject(() => ClassicStartProfileFile.Load(p)); Reject(() => ClassicStartProfileFile.Save(new(), revision, p)); Assert(File.ReadAllText(p) == "{bad");
});
Test("two simultaneous writers yield one success", () =>
{
    var p = Path.Combine(root, "concurrent.json"); var old = ClassicStartProfileFile.Save(new(), "missing", p); var successes = 0;
    Parallel.For(0, 2, i => { try { ClassicStartProfileFile.Save(new(Style: i == 0 ? "win10" : "compact"), old.Revision, p); Interlocked.Increment(ref successes); } catch (IOException) { } });
    Assert(successes == 1 && Directory.GetFiles(root, "concurrent.json.backup-*").Length == 1);
});
Test("validate pin list length", () => Reject(() => new ClassicStartOptions(PinnedIds: Enumerable.Range(0, 25).Select(i => "app:" + i.ToString("x64")).ToArray()).Validate()));
Test("validate custom folder duplicate ids", () => Reject(() => new ClassicStartOptions(CustomFolders: [new("folder:a", "A", root), new("folder:a", "B", root)]).Validate()));
Test("validate pin maximum inclusive", () => new ClassicStartOptions(MaxPinned: 24, PinnedIds: Enumerable.Range(0, 24).Select(i => "app:" + i.ToString("x64")).ToArray()).Validate());
Test("validate folder maximum inclusive", () => new ClassicStartOptions(CustomFolders: Enumerable.Range(0, 16).Select(i => new ClassicStartFolder("folder:a" + i, "A", root)).ToArray()).Validate());
Test("reject folder maximum overflow", () => Reject(() => new ClassicStartOptions(CustomFolders: Enumerable.Range(0, 17).Select(i => new ClassicStartFolder("folder:a" + i, "A", root)).ToArray()).Validate()));
foreach (var p in new[] { "relative", "C:\\a\\..\\b", "C:\\a:stream", "\\\\server\\share", "\\\\?\\C:\\test", "C:\\a.", "C:\\a ", "C:\\%HOME%", "C:\\CON", "C:\\NUL.txt", "C:\\COM1", "C:\\bad*name", "C:\\CONOUT$" })
    Test("reject path " + p, () => Reject(() => new ClassicStartOptions(CustomFolders: [new("folder:test", "Test", p)]).Validate()));

var programs = Directory.CreateDirectory(Path.Combine(root, "programs")).FullName;
var common = Directory.CreateDirectory(Path.Combine(root, "common")).FullName;
FileAt("programs/办公/文档工具.lnk", "not parsed"); FileAt("programs/办公/网页.url", "[InternetShortcut]\nURL=https://example.invalid");
FileAt("programs/工具/Alpha.appref-ms", "not parsed"); FileAt("programs/工具/Ignore.exe", "not launched");
FileAt("programs/工具/Ignore.txt", "not parsed"); FileAt("common/办公/文档工具.lnk", "duplicate display name, distinct identity");
var catalog = ClassicStartCatalog.Scan([programs, common]);
Test("scan shortcut types only and retain same named apps", () => Assert(catalog.Apps.Count == 4 && catalog.Errors.Count == 0 && catalog.Apps.Count(a => a.Name == "文档工具") == 2));
Test("scan current synthetic roots doesn't read shortcut contents", () => Assert(catalog.Apps.Any(a => a.Name == "网页") && catalog.Apps.Any(a => a.Name == "Alpha")));
Test("root repeated and overlapping paths do not duplicate apps", () => Assert(ClassicStartCatalog.Scan([programs, programs, Path.Combine(programs, "办公")]).Apps.Count == 3));
Test("stable ids across repeated scan", () => Assert(catalog.Apps.Select(a => a.Id).Order().SequenceEqual(ClassicStartCatalog.Scan([programs, common]).Apps.Select(a => a.Id).Order())));
Test("Chinese search by name", () => Assert(ClassicStartCatalog.Query(catalog, new(), "文档").Count == 2));
Test("search matches category and whitespace tokens", () => Assert(ClassicStartCatalog.Query(catalog, new(), " 办公  网页 ").Single().Name == "网页"));
Test("case and fullwidth search", () => Assert(ClassicStartCatalog.Query(catalog, new(), "ＡＬＰＨＡ").Single().Name == "Alpha"));
Test("no matching apps returns empty", () => Assert(ClassicStartCatalog.Query(catalog, new(), "absent").Count == 0));
Test("descending sorts category and name", () => Assert(ClassicStartCatalog.Query(catalog, new()).Select(a => (a.Category, a.Name)).Reverse()
    .SequenceEqual(ClassicStartCatalog.Query(catalog, new(SortAscending: false)).Select(a => (a.Category, a.Name)))));
Test("pinned order preserved and missing ids retained in config", () =>
{
    var ids = new[] { catalog.Apps[2].Id, HashId('c'), catalog.Apps[0].Id }; var options = new ClassicStartOptions(MaxPinned: 2, PinnedIds: ids);
    Assert(ClassicStartCatalog.Pinned(catalog, options).Select(a => a.Id).SequenceEqual(new[] { ids[0], ids[2] }) && options.EffectivePinnedIds.Length == 3);
});
Test("zero maximum pins hides pins", () => Assert(ClassicStartCatalog.Pinned(catalog, new(MaxPinned: 0, PinnedIds: [catalog.Apps[0].Id])).Count == 0));
Test("resolve current scanned shortcut", () => Assert(ClassicStartCatalog.ResolveLaunch(catalog, catalog.Apps[0].Id) == catalog.Apps[0].SourcePath));
Test("reject arbitrary path as launch id", () => Reject(() => ClassicStartCatalog.ResolveLaunch(catalog, "C:\\Windows\\System32\\calc.exe")));
Test("reject missing catalog id", () => Reject(() => ClassicStartCatalog.ResolveLaunch(catalog, HashId('d'))));
Test("synthetic snapshots cannot launch", () => Reject(() => ClassicStartCatalog.ResolveLaunch(new(catalog.Apps), catalog.Apps[0].Id)));
Test("removed shortcut rejected at click", () =>
{
    var path = FileAt("programs/Removed.lnk", ""); var snapshot = ClassicStartCatalog.Scan([programs]); var app = snapshot.Apps.Single(a => a.Name == "Removed");
    File.Delete(path); Reject(() => ClassicStartCatalog.ResolveLaunch(snapshot, app.Id));
});
Test("shortcut replaced by directory rejected at click", () =>
{
    var path = FileAt("programs/Replaced.lnk", ""); var snapshot = ClassicStartCatalog.Scan([programs]); var app = snapshot.Apps.Single(a => a.Name == "Replaced");
    File.Delete(path); Directory.CreateDirectory(path); Reject(() => ClassicStartCatalog.ResolveLaunch(snapshot, app.Id));
});
Test("scan failure is visible", () => Assert(ClassicStartCatalog.Scan([Path.Combine(root, "no-such-root"), "\\\\server\\share"]).Errors.Count == 2));
Test("visible system place order and custom directory", () =>
{
    var options = new ClassicStartOptions(CustomFolders: [new("folder:work", "工作", root)], VisiblePlaces: ["downloads", "user"]);
    Assert(ClassicStartPlaces.Visible(options).Select(p => p.Id).SequenceEqual(new[] { "downloads", "user", "folder:work" }));
    Assert(ClassicStartPlaces.Resolve(options, "folder:work") == root);
});
Test("fixed system targets only", () =>
{
    var options = new ClassicStartOptions(VisiblePlaces: ClassicStartPlaces.All.Select(p => p.Id).ToArray());
    Assert(ClassicStartPlaces.Resolve(options, "settings") == "ms-settings:" && ClassicStartPlaces.Resolve(options, "downloads") == "shell:Downloads");
    foreach (var place in ClassicStartPlaces.All) Assert(ClassicStartPlaces.Resolve(options, place.Id).StartsWith("shell:") || place.Id == "settings");
    Reject(() => ClassicStartPlaces.Resolve(options, "ms-settings:privacy"));
});
Test("hidden system places cannot resolve", () => Reject(() => ClassicStartPlaces.Resolve(new(VisiblePlaces: []), "settings")));
Test("custom directory cannot be executable file", () => Reject(() => ClassicStartPlaces.Resolve(new(CustomFolders: [new("folder:bad", "Bad", FileAt("bad.exe", ""))]), "folder:bad")));
Test("custom nonexistent directory cannot resolve", () => Reject(() => ClassicStartPlaces.Resolve(new(CustomFolders: [new("folder:bad", "Bad", Path.Combine(root, "missing-folder"))]), "folder:bad")));

// Symlink creation may require Windows Developer Mode. Report unavailable coverage explicitly.
var prepared = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
var link = prepared is null ? Path.Combine(programs, "linked") : Path.Combine(prepared, "scan", "linked");
var outside = prepared is null ? Directory.CreateDirectory(Path.Combine(root, "outside")).FullName : Path.Combine(prepared, "target");
if (prepared is null) FileAt("outside/Outside.lnk", "");
try
{
    if (prepared is null) Directory.CreateSymbolicLink(link, outside);
    var scanRoots = prepared is null ? new[] { programs } : new[] { programs, Path.Combine(prepared, "scan") };
    Test("scanner skips reparse directory and reports it", () => { var scan = ClassicStartCatalog.Scan(scanRoots); Assert(!scan.Apps.Any(a => a.Name == "Outside") && scan.Errors.Any(e => e.Contains("重解析"))); });
    Test("scanner rejects reparse scan root", () => { var scan = ClassicStartCatalog.Scan([link]); Assert(scan.Apps.Count == 0 && scan.Errors.Count > 0); });
    Test("custom folder reparse rejected", () => RejectReparse(() => ClassicStartPlaces.Resolve(new(CustomFolders: [new("folder:link", "Link", link)]), "folder:link")));
    Test("custom folder reparse ancestor rejected", () => RejectReparse(() => ClassicStartPlaces.Resolve(new(CustomFolders: [new("folder:link", "Link", Path.Combine(link, "child"))]), "folder:link")));
    Test("configuration under reparse rejected", () => RejectReparse(() => ClassicStartProfileFile.Save(new(), "missing", Path.Combine(link, "config.json"))));
    if (prepared is not null) Test("launch ancestor replaced by junction rejected", () =>
    {
        var path = FileAt("programs/Swap/Before.lnk", ""); var scan = ClassicStartCatalog.Scan([programs]); var app = scan.Apps.Single(a => a.Name == "Before");
        var directory = Path.GetDirectoryName(path)!;
        Directory.Move(directory, Path.Combine(root, "saved-swap"));
        Directory.Move(Path.Combine(prepared, "scan", "spare"), directory);
        RejectReparse(() => ClassicStartCatalog.ResolveLaunch(scan, app.Id));
    });
    else Test("launch replaced with reparse rejected", () =>
    {
        var path = FileAt("programs/LinkSwap.lnk", ""); var scan = ClassicStartCatalog.Scan([programs]); var app = scan.Apps.Single(a => a.Name == "LinkSwap");
        File.Delete(path); File.CreateSymbolicLink(path, Path.Combine(outside, "Outside.lnk")); RejectReparse(() => ClassicStartCatalog.ResolveLaunch(scan, app.Id));
    });
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skips.Add("Reparse fixture unavailable: " + ex.Message); }

Directory.CreateDirectory(reportRoot);
File.WriteAllText(Path.Combine(reportRoot, "start-core-report.json"), JsonSerializer.Serialize(new { passed = checks.Count - failures, failed = failures, skipped = skips, fixtures = root, checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"StartCore: {checks.Count - failures}/{checks.Count} passed; {skips.Count} coverage skips. Report: {Path.Combine(reportRoot, "start-core-report.json")}");
Environment.ExitCode = failures == 0 ? 0 : 1;
