using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassicDesk;

var root = Path.Combine(Path.GetTempPath(), "ClassicDesk-UpdatesFeed-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<object>();
int failed = 0;
async Task Test(string name, Func<Task> run)
{
    try { await run(); checks.Add(new { name, passed = true }); }
    catch (Exception ex) { failed++; checks.Add(new { name, passed = false, error = ex.ToString() }); Console.Error.WriteLine(name + ": " + ex.Message); }
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
async Task Reject(Func<Task> action)
{
    try { await action(); } catch (Exception e) when (e is InvalidDataException or HttpRequestException or IOException or JsonException or OperationCanceledException) { return; }
    throw new Exception("Expected rejection");
}
async Task RejectFixture(Action<Fixture> mutate)
{
    var fixture = new Fixture(); mutate(fixture);
    using var feed = new FrontendUpdateFeed(fixture.Handler);
    await Reject(async () => await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview"));
}
string Fresh(string name)
{
    var path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
}

await Test("official candidate and manifest bound digest", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler);
    var result = await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview");
    Assert(result.Status == FrontendUpdateCheckStatus.Available && result.Candidate!.Manifest.Version == "0.11.17-preview");
    Assert(result.Candidate!.ManifestSha256 == Fixture.Hash(f.ManifestBytes));
    Assert(f.Handler.Requests[0] == FrontendUpdateFeed.ReleasesEndpoint);
});
await Test("older release missing manifest explains no compatible update", async () =>
{
    var f = new Fixture("0.11.16-preview"); f.Release["assets"] = new JsonArray(); using var feed = new FrontendUpdateFeed(f.Handler);
    var result = await feed.CheckAsync("0.11.15-preview", Fixture.Build, "preview");
    Assert(result.Status == FrontendUpdateCheckStatus.NoCompatibleUpdate && result.Message.Contains("未提供"));
});
foreach (var version in new[] { "0.11.17-preview", "0.11.15-preview", "0.11.017-preview", "0.11.18-preview+build", "0.11.18-beta", "0.11.18-preview.99999999999999", "0.11.18-preview\n" })
    await Test("same downgrade or invalid semver " + version, async () =>
    {
        var f = new Fixture(version); using var feed = new FrontendUpdateFeed(f.Handler);
        var r = await feed.CheckAsync("0.11.17-preview", new string('b', 40), "preview");
        Assert(r.Candidate == null && f.Handler.Requests.Count == 1);
    });
await Test("numeric semver sorting chooses 0.11.100 before 0.11.99", async () =>
{
    var f = new Fixture("0.11.99-preview"); var next = new Fixture("0.11.100-preview");
    f.ExtraReleases.Add(next.Release); foreach (var pair in next.Bodies) f.Bodies[pair.Key] = pair.Value;
    using var feed = new FrontendUpdateFeed(f.Handler);
    Assert((await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!.Manifest.Version == "0.11.100-preview");
});
await Test("preview cannot silently select stable", async () =>
{
    var f = new Fixture("0.11.18"); using var feed = new FrontendUpdateFeed(f.Handler);
    Assert((await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate == null);
});
await Test("stable cannot silently select preview", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler);
    Assert((await feed.CheckAsync("0.11.16", Fixture.Build, "stable")).Candidate == null);
});
await Test("stable candidate and incremental preview suffix", async () =>
{
    var f = new Fixture("0.11.17"); using var feed = new FrontendUpdateFeed(f.Handler);
    Assert((await feed.CheckAsync("0.11.16", Fixture.Build, "stable")).Candidate != null);
    var p = new Fixture("0.11.17-preview.2"); using var preview = new FrontendUpdateFeed(p.Handler);
    Assert((await preview.CheckAsync("0.11.17-preview.1", Fixture.Build, "preview")).Candidate != null);
});
await Test("draft release ignored", async () =>
{
    var f = new Fixture(); f.Release["draft"] = true; using var feed = new FrontendUpdateFeed(f.Handler);
    Assert((await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate == null);
});
await Test("prerelease flag must match channel", async () =>
{
    var f = new Fixture(); f.Release["prerelease"] = false; using var feed = new FrontendUpdateFeed(f.Handler);
    Assert((await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate == null);
});
foreach (var pair in new (string Field, JsonNode Value)[]
{
    ("Application", JsonValue.Create("Other")!), ("Schema", JsonValue.Create(2)!), ("Platform", JsonValue.Create("linux")!),
    ("Architecture", JsonValue.Create("arm64")!), ("Channel", JsonValue.Create("stable")!), ("Build", JsonValue.Create("main")!)
})
    await Test("reject manifest identity " + pair.Field, () => RejectFixture(f => { f.Manifest[pair.Field] = pair.Value.DeepClone(); f.Rebuild(); }));
await Test("manifest version must match release tag", () => RejectFixture(f => { f.Manifest["Version"] = "0.11.19-preview"; f.SetManifestBytes(Encoding.UTF8.GetBytes(f.Manifest.ToJsonString())); }));
await Test("target commit must be exact build", () => RejectFixture(f => f.Release["target_commitish"] = "main"));
await Test("untrusted release URL", () => RejectFixture(f => f.Release["html_url"] = "https://github.com/attacker/classicdesk/releases/tag/v0.11.17-preview"));
foreach (var url in new[] { "https://evil.test/file", "http://github.com/NOXEVYR/classicdesk/releases/download/v0.11.17-preview/frontend-update.json", "https://github.com/Other/classicdesk/releases/download/v0.11.17-preview/frontend-update.json", "https://github.com/NOXEVYR/classicdesk/releases/download/v0.11.17-preview/frontend-update.json?x=1" })
    await Test("untrusted manifest URL " + url, () => RejectFixture(f => f.Assets[0]!["browser_download_url"] = url));
await Test("manifest asset digest required", () => RejectFixture(f => ((JsonObject)f.Assets[0]!).Remove("digest")));
await Test("manifest asset digest mismatch", () => RejectFixture(f => f.Assets[0]!["digest"] = "sha256:" + new string('0', 64)));
await Test("manifest asset duplicate", () => RejectFixture(f => f.Assets.Add(f.Assets[0]!.DeepClone())));
await Test("manifest duplicate field rejected even with bound digest", () => RejectFixture(f => f.SetManifestBytes(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(f.ManifestBytes).Replace("{", "{\"application\":\"Other\",", StringComparison.Ordinal)))));
await Test("lowercase JSON is accepted without duplicate fields", async () =>
{
    var f = new Fixture(); f.SetManifestBytes(JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<FrontendUpdateManifest>(f.ManifestBytes), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    using var feed = new FrontendUpdateFeed(f.Handler); Assert((await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate != null);
});
await Test("four file whitelist traversal rejected", () => RejectFixture(f => { f.Manifest["Files"]![0]!["Name"] = "../user.config"; f.Rebuild(); }));
await Test("four file whitelist duplicate rejected", () => RejectFixture(f => { f.Manifest["Files"]![1] = f.Manifest["Files"]![0]!.DeepClone(); f.Rebuild(); }));
await Test("missing frontend asset rejected", () => RejectFixture(f => f.Assets.RemoveAt(1)));
await Test("duplicate frontend asset rejected", () => RejectFixture(f => f.Assets.Add(f.Assets[1]!.DeepClone())));
await Test("asset size inconsistent with manifest", () => RejectFixture(f => f.Assets[1]!["size"] = 99));
await Test("asset hash inconsistent with manifest", () => RejectFixture(f => f.Assets[1]!["digest"] = "sha256:" + new string('0', 64)));
await Test("asset from another release rejected", () => RejectFixture(f => f.Assets[1]!["browser_download_url"] = "https://github.com/NOXEVYR/classicdesk/releases/download/v0.11.18-preview/frontend-ClassicDesk.exe"));
await Test("zero file size rejected", () => RejectFixture(f => { f.Manifest["Files"]![0]!["Size"] = 0L; f.Rebuild(); }));
await Test("oversized file rejected", () => RejectFixture(f => { f.Manifest["Files"]![0]!["Size"] = 256L * 1024 * 1024 + 1; f.Rebuild(); }));
await Test("invalid current build rejected before network", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); await Reject(async () => await feed.CheckAsync("0.11.16-preview", "main", "preview")); Assert(f.Handler.Requests.Count == 0);
});
await Test("invalid channel rejected before network", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); await Reject(async () => await feed.CheckAsync("0.11.16-preview", Fixture.Build, "nightly")); Assert(f.Handler.Requests.Count == 0);
});
await Test("HTTPS redirects reject attacker and HTTP", async () =>
{
    foreach (var url in new[] { "https://evil.test/file", "http://release-assets.githubusercontent.com/file", "https://github.com/Other/classicdesk/file", "https://release-assets.githubusercontent.com:444/file" })
    {
        var f = new Fixture(); f.Override = u => u.AbsoluteUri == FrontendUpdateFeed.ReleasesEndpoint ? null : Fixture.Redirect(url);
        using var feed = new FrontendUpdateFeed(f.Handler); await Reject(async () => await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview"));
        Assert(!f.Handler.Requests.Contains(url));
    }
});
await Test("signed official CDN redirect accepted", async () =>
{
    var f = new Fixture(); f.Override = u => u.AbsoluteUri.EndsWith("/frontend-update.json", StringComparison.Ordinal) ? Fixture.Redirect("https://release-assets.githubusercontent.com/asset?token=fake")
        : u.Host == "release-assets.githubusercontent.com" ? Fixture.Response(f.ManifestBytes) : null;
    using var feed = new FrontendUpdateFeed(f.Handler); Assert((await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate != null);
});
await Test("feed redirect rejected", () => RejectFixture(f => f.Override = u => u.AbsoluteUri == FrontendUpdateFeed.ReleasesEndpoint ? Fixture.Redirect("https://release-assets.githubusercontent.com/api") : null));
await Test("redirect loop bounded", () => RejectFixture(f => f.Override = u => u.AbsoluteUri == FrontendUpdateFeed.ReleasesEndpoint ? null : Fixture.Redirect("https://release-assets.githubusercontent.com/loop")));
await Test("oversized feed response refused", () => RejectFixture(f => f.Override = u => u.AbsoluteUri == FrontendUpdateFeed.ReleasesEndpoint ? Fixture.Response(new byte[2 * 1024 * 1024 + 1]) : null));
await Test("unknown length feed stream bounded", () => RejectFixture(f => f.Override = u => u.AbsoluteUri == FrontendUpdateFeed.ReleasesEndpoint ? Fixture.StreamResponse(new byte[2 * 1024 * 1024 + 1]) : null));
await Test("unknown length manifest stream bound to expected size", () => RejectFixture(f => f.Override = u => u.AbsoluteUri.EndsWith("frontend-update.json", StringComparison.Ordinal) ? Fixture.StreamResponse(f.ManifestBytes.Concat(new byte[1]).ToArray()) : null));
await Test("unknown length manifest truncation rejected", () => RejectFixture(f => f.Override = u => u.AbsoluteUri.EndsWith("frontend-update.json", StringComparison.Ordinal) ? Fixture.StreamResponse(f.ManifestBytes.Take(f.ManifestBytes.Length - 1).ToArray()) : null));
await Test("build hash cannot have trailing newline", () => RejectFixture(f => { f.Manifest["Build"] = Fixture.Build + "\n"; f.Rebuild(); }));
await Test("manifest content length refused", () => RejectFixture(f => f.Override = u => u.AbsoluteUri.EndsWith("frontend-update.json", StringComparison.Ordinal) ? Fixture.Response(new byte[1]) : null));
await Test("unsolicited partial response rejected", () => RejectFixture(f => f.Override = u => u.AbsoluteUri.EndsWith("frontend-update.json", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(f.ManifestBytes) } : null));
await Test("compressed asset response rejected", () => RejectFixture(f => f.Override = u =>
{
    if (!u.AbsoluteUri.EndsWith("frontend-update.json", StringComparison.Ordinal)) return null; var r = Fixture.Response(f.ManifestBytes); r.Content.Headers.ContentEncoding.Add("gzip"); return r;
}));

await Test("difference downloads reuse hash-matching files and stage exact manifest", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    var app = Fresh("reuse-app"); foreach (var file in c.Manifest.Files.Take(3)) await File.WriteAllBytesAsync(Path.Combine(app, file.Name), f.FileBytes[file.Name]);
    Assert(await feed.GetDownloadBytesAsync(c, app) == f.FileBytes[c.Manifest.Files[3].Name].Length);
    f.Handler.Requests.Clear(); var stage = await feed.StageAsync(c, app, Path.Combine(root, "ready"));
    Assert(f.Handler.Requests.Count == 1 && f.Handler.Requests[0].EndsWith("frontend-ClassicDesk.runtimeconfig.json", StringComparison.Ordinal));
    Assert(File.ReadAllBytes(Path.Combine(stage.Directory, "frontend-update.json")).SequenceEqual(f.ManifestBytes));
    foreach (var file in c.Manifest.Files) Assert(Fixture.Hash(File.ReadAllBytes(Path.Combine(stage.Directory, file.Name))) == file.Sha256);
});
await Test("same-size stale file downloads by hash", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    var app = Fresh("hash-app"); foreach (var file in c.Manifest.Files) File.WriteAllBytes(Path.Combine(app, file.Name), f.FileBytes[file.Name]);
    File.WriteAllBytes(Path.Combine(app, c.Manifest.Files[0].Name), new byte[c.Manifest.Files[0].Size]); f.Handler.Requests.Clear();
    await feed.StageAsync(c, app, Path.Combine(root, "hash-ready")); Assert(f.Handler.Requests.Count == 1);
});
await Test("existing stage preserved", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    var app = Fresh("existing-app"); var stage = Fresh("existing-stage"); File.WriteAllText(Path.Combine(stage, "user.txt"), "keep");
    await Reject(async () => await feed.StageAsync(c, app, stage)); Assert(File.ReadAllText(Path.Combine(stage, "user.txt")) == "keep");
});
await Test("candidate mutations rejected", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    c.DownloadUrls[c.Manifest.Files[0].Name] = "https://evil.test/file";
    await Reject(async () => await feed.StageAsync(c, Fresh("mutated-app"), Path.Combine(root, "mutation")));
});
await Test("corrupted candidate manifest array rejected", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    c.Manifest.Files[0] = c.Manifest.Files[0] with { Name = "../user.config" };
    await Reject(async () => await feed.GetDownloadBytesAsync(c, Fresh("corrupt-candidate-app")));
});
await Test("candidate cannot transfer to unchecked feed", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    using var uncheckedFeed = new FrontendUpdateFeed(new Fixture().Handler);
    await Reject(async () => await uncheckedFeed.GetDownloadBytesAsync(c, Fresh("unchecked-app")));
});
await Test("partial failed download cannot become Ready", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    f.Override = u => u.AbsoluteUri.EndsWith("frontend-ClassicDesk.dll", StringComparison.Ordinal) ? Fixture.Response([1]) : null;
    var stage = Path.Combine(root, "partial"); await Reject(async () => await feed.StageAsync(c, Fresh("partial-app"), stage));
    Assert(File.Exists(Path.Combine(stage, "ClassicDesk.exe")) && !File.Exists(Path.Combine(stage, "frontend-update.json")));
});
await Test("wrong file digest cannot become Ready", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    f.Override = u => u.AbsoluteUri.EndsWith("frontend-ClassicDesk.dll", StringComparison.Ordinal) ? Fixture.Response(new byte[f.FileBytes["ClassicDesk.dll"].Length]) : null;
    var stage = Path.Combine(root, "bad-hash"); await Reject(async () => await feed.StageAsync(c, Fresh("bad-hash-app"), stage)); Assert(!File.Exists(Path.Combine(stage, "frontend-update.json")));
});
await Test("50 MiB consent gate before stage creation or download", async () =>
{
    var f = new Fixture(); f.Manifest["Files"]![0]!["Size"] = FrontendUpdateFiles.AutoDownloadLimit + 1; f.Rebuild();
    using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    var stage = Path.Combine(root, "large"); var app = Fresh("large-app"); var count = f.Handler.Requests.Count;
    try { await feed.StageAsync(c, app, stage); throw new Exception("Expected consent gate"); }
    catch (FrontendUpdateDownloadConsentException e) { Assert(e.RequiredBytes > FrontendUpdateFiles.AutoDownloadLimit); }
    Assert(!Directory.Exists(stage) && f.Handler.Requests.Count == count);
    f.Override = u => u.AbsoluteUri.EndsWith("frontend-ClassicDesk.exe", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
    await Reject(async () => await feed.StageAsync(c, app, stage, true)); Assert(f.Handler.Requests.Count == count + 1);
});
await Test("cancelled check observes token", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Reject(async () => await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview", cancel.Token));
});
await Test("cancelled partial stage cannot become Ready", async () =>
{
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    using var cancel = new CancellationTokenSource();
    f.Override = u => { if (!u.AbsoluteUri.EndsWith("frontend-ClassicDesk.dll", StringComparison.Ordinal)) return null; cancel.Cancel(); return Fixture.Response(f.FileBytes["ClassicDesk.dll"]); };
    var stage = Path.Combine(root, "cancelled"); await Reject(async () => await feed.StageAsync(c, Fresh("cancelled-app"), stage, false, cancel.Token));
    Assert(!File.Exists(Path.Combine(stage, "frontend-update.json")));
});
await Test("junction installation path refused", async () =>
{
    var target = Fresh("junction-target"); var link = Path.Combine(root, "junction-app");
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
    using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); Assert(process.ExitCode == 0, "Junction fixture failed");
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    await Reject(async () => await feed.GetDownloadBytesAsync(c, link));
});
await Test("junction stage path refused without modifying target", async () =>
{
    var target = Fresh("stage-junction-target"); var link = Path.Combine(root, "stage-junction");
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
    using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); Assert(process.ExitCode == 0);
    var f = new Fixture(); using var feed = new FrontendUpdateFeed(f.Handler); var c = (await feed.CheckAsync("0.11.16-preview", Fixture.Build, "preview")).Candidate!;
    await Reject(async () => await feed.StageAsync(c, Fresh("stage-junction-app"), Path.Combine(link, "new-stage")));
    Assert(Directory.GetFileSystemEntries(target).Length == 0);
});

async Task FeedToInstalled(string currentVersion, string targetVersion)
{
    var original = new Fixture(currentVersion);
    var target = new Fixture(targetVersion);
    foreach (var name in FrontendUpdateFiles.Names) target.SetFilePayload(name, Encoding.UTF8.GetBytes("new version " + targetVersion + " " + name));
    var app = Fresh("chain-app"); var work = Fresh("chain-work");
    foreach (var file in original.FileBytes) File.WriteAllBytes(Path.Combine(app, file.Key), file.Value);
    File.WriteAllBytes(Path.Combine(app, FrontendUpdateFiles.InstallationManifest), original.ManifestBytes);
    File.WriteAllText(Path.Combine(app, "private-profile.json"), "preserve user file");
    Assert(FrontendUpdateInstaller.ReadInstallation(app).Version == currentVersion);
    using var feed = new FrontendUpdateFeed(target.Handler);
    var checkedUpdate = await feed.CheckAsync(currentVersion, Fixture.Build, targetVersion.Contains('-') ? "preview" : "stable");
    Assert(checkedUpdate.Status == FrontendUpdateCheckStatus.Available);
    var candidate = checkedUpdate.Candidate!;
    Assert(candidate.Manifest.Files.All(f => f.AssetName == "frontend-" + f.Name));
    var stage = await feed.StageAsync(candidate, app, Path.Combine(root, "chain-stage-" + Guid.NewGuid().ToString("N")));
    Assert(File.ReadAllBytes(Path.Combine(stage.Directory, "frontend-update.json")).SequenceEqual(target.ManifestBytes));
    var identity = new FrontendProcessIdentity(8001, DateTime.UtcNow.Ticks, Path.Combine(app, "ClassicDesk.exe"));
    var prepared = FrontendUpdateInstaller.Prepare(app, stage, work, identity);
    var host = new ChainHost();
    var installed = FrontendUpdateInstaller.Run(prepared.RequestPath, host, 0, 100);
    Assert(installed.Success && installed.State == "Completed", installed.Message);
    Assert(host.StartCount == 1 && host.Acknowledged);
    var registration = FrontendUpdateInstaller.ReadInstallation(app);
    Assert(registration.Version == targetVersion && registration.Channel == candidate.Manifest.Channel);
    Assert(registration.Files.Length == 4 && registration.Files.All(f => f.AssetName == "frontend-" + f.Name));
    Assert(File.ReadAllBytes(Path.Combine(app, FrontendUpdateFiles.InstallationManifest)).SequenceEqual(target.ManifestBytes), "Installer must preserve the exact feed-bound manifest bytes");
    foreach (var file in candidate.Manifest.Files)
    {
        var actual = File.ReadAllBytes(Path.Combine(app, file.Name));
        Assert(actual.LongLength == file.Size && Fixture.Hash(actual) == file.Sha256);
    }
    Assert(File.ReadAllText(Path.Combine(app, "private-profile.json")) == "preserve user file");
    Assert(FrontendUpdateInstaller.InspectPending(work).Length == 0);
}
await Test("full official preview feed stage install ACK registration chain", () => FeedToInstalled("0.11.16-preview", "0.11.17-preview"));
await Test("full official stable feed stage install ACK registration chain", () => FeedToInstalled("0.11.16", "0.11.17"));
await Test("full numeric preview suffix feed stage install ACK registration chain", () => FeedToInstalled("0.11.17-preview.1", "0.11.17-preview.2"));

var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
await Test("successful schedule persists 24h across restart", () =>
{
    var path = Path.Combine(Fresh("schedule-success"), "state.json"); var schedule = new FrontendUpdateSchedule(path);
    Assert(schedule.TryBeginCheck(now, out _)); Assert(File.Exists(path)); schedule.CompleteCheck(true, now);
    var restarted = new FrontendUpdateSchedule(path); Assert(!restarted.TryBeginCheck(now.AddHours(23), out _)); Assert(restarted.TryBeginCheck(now.AddHours(24), out _)); return Task.CompletedTask;
});
await Test("failed schedule doubles backoff to 24h and restarts", () =>
{
    var path = Path.Combine(Fresh("schedule-failure"), "state.json"); var time = now;
    foreach (var hours in new[] { 1, 2, 4, 8, 16, 24, 24 })
    {
        var schedule = new FrontendUpdateSchedule(path); Assert(schedule.TryBeginCheck(time, out _)); schedule.CompleteCheck(false, time);
        var restarted = new FrontendUpdateSchedule(path); Assert(restarted.State.NextCheckUtc == time.AddHours(hours)); Assert(!restarted.TryBeginCheck(time.AddHours(hours).AddSeconds(-1), out _)); time = time.AddHours(hours);
    }
    return Task.CompletedTask;
});
await Test("interrupted reservation survives restart without tight retry", () =>
{
    var path = Path.Combine(Fresh("schedule-interrupt"), "state.json"); var s = new FrontendUpdateSchedule(path); Assert(s.TryBeginCheck(now, out _));
    var restarted = new FrontendUpdateSchedule(path); Assert(!restarted.TryBeginCheck(now.AddMinutes(10), out _, true)); Assert(restarted.TryBeginCheck(now.AddHours(1), out _));
    Assert(restarted.State.FailureCount == 2); return Task.CompletedTask;
});
await Test("disabled automatic schedule allows manual with one minute throttle", () =>
{
    var path = Path.Combine(Fresh("schedule-disabled"), "state.json"); var s = new FrontendUpdateSchedule(path); s.SetEnabled(false);
    Assert(!new FrontendUpdateSchedule(path).TryBeginCheck(now, out _)); Assert(s.TryBeginCheck(now, out _, true)); s.CompleteCheck(true, now);
    Assert(!s.TryBeginCheck(now.AddSeconds(59), out _, true)); Assert(s.TryBeginCheck(now.AddMinutes(1), out _, true)); s.CompleteCheck(true, now.AddMinutes(1));
    s.SetEnabled(true); Assert(!s.TryBeginCheck(now.AddHours(1), out _)); return Task.CompletedTask;
});
foreach (var corrupt in new[] { "broken json", "{}", "{\"Schema\":1,\"Schema\":1}", "null", "[]" })
    await Test("corrupt schedule preserved " + corrupt, () =>
    {
        var path = Path.Combine(Fresh("schedule-corrupt"), "state.json"); File.WriteAllText(path, corrupt); var s = new FrontendUpdateSchedule(path);
        Assert(s.IsCorrupt && !s.TryBeginCheck(now, out _) && !s.TryBeginCheck(now, out _, true));
        try { s.SetEnabled(false); throw new Exception("Expected corrupt refusal"); } catch (InvalidDataException) { }
        Assert(File.ReadAllText(path) == corrupt); return Task.CompletedTask;
    });
await Test("oversized corrupt schedule preserved", () =>
{
    var path = Path.Combine(Fresh("schedule-oversize"), "state.json"); File.WriteAllText(path, new string('x', 17000)); var s = new FrontendUpdateSchedule(path);
    Assert(s.IsCorrupt && !s.TryBeginCheck(now, out _)); Assert(new FileInfo(path).Length == 17000); return Task.CompletedTask;
});
await Test("concurrent schedule objects reload reservation", () =>
{
    var path = Path.Combine(Fresh("schedule-concurrent"), "state.json"); var a = new FrontendUpdateSchedule(path); var b = new FrontendUpdateSchedule(path);
    Assert(a.TryBeginCheck(now, out _)); Assert(!b.TryBeginCheck(now, out _)); a.CompleteCheck(true, now); Assert(!b.TryBeginCheck(now.AddHours(1), out _)); return Task.CompletedTask;
});

var report = JsonSerializer.Serialize(new { total = checks.Count, passed = checks.Count - failed, failed, fixtureRoot = root, checks }, new JsonSerializerOptions { WriteIndented = true });
var reportPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "report.json");
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!); File.WriteAllText(reportPath, report);
Console.WriteLine($"UpdatesFeed: {checks.Count - failed}/{checks.Count} passed; report: {reportPath}; fixtures: {root}");
return failed == 0 ? 0 : 1;

sealed class Fixture
{
    public const string Build = "1111111111111111111111111111111111111111";
    public JsonObject Manifest { get; }
    public JsonObject Release { get; private set; } = null!;
    public JsonArray Assets => (JsonArray)Release["assets"]!;
    public byte[] ManifestBytes { get; private set; } = [];
    public Dictionary<string, byte[]> Bodies { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> FileBytes { get; } = new(StringComparer.Ordinal);
    public List<JsonObject> ExtraReleases { get; } = [];
    public Func<Uri, HttpResponseMessage?>? Override { get; set; }
    public FakeHandler Handler { get; }
    public Fixture(string version = "0.11.17-preview")
    {
        foreach (var name in FrontendUpdateFiles.Names) FileBytes[name] = Encoding.UTF8.GetBytes("payload " + name);
        var files = FrontendUpdateFiles.Names.Select(n => new FrontendUpdateFile(n, "frontend-" + n, FileBytes[n].Length, Hash(FileBytes[n]))).ToArray();
        Manifest = (JsonObject)JsonSerializer.SerializeToNode(new FrontendUpdateManifest(1, "ClassicDesk", version, version.Contains('-') ? "preview" : "stable", "windows", "x64", Build, files))!;
        Handler = new FakeHandler(u =>
        {
            var overridden = Override?.Invoke(u); if (overridden != null) return overridden;
            if (u.AbsoluteUri == FrontendUpdateFeed.ReleasesEndpoint)
            {
                var releases = new JsonArray(Release.DeepClone()); foreach (var r in ExtraReleases) releases.Add(r.DeepClone());
                return Response(Encoding.UTF8.GetBytes(releases.ToJsonString()));
            }
            if (!Bodies.TryGetValue(u.AbsoluteUri, out var bytes)) throw new Exception("Unexpected request " + u);
            return Response(bytes);
        });
        Rebuild();
    }
    public void Rebuild()
    {
        var version = Manifest["Version"]!.GetValue<string>(); var tag = "v" + version;
        var assets = new JsonArray(); ManifestBytes = Encoding.UTF8.GetBytes(Manifest.ToJsonString());
        assets.Add(Asset("frontend-update.json", ManifestBytes.Length, Hash(ManifestBytes), tag));
        foreach (var node in (JsonArray)Manifest["Files"]!)
        {
            var file = node!; var name = file["Name"]!.GetValue<string>(); var assetName = file["AssetName"]!.GetValue<string>();
            assets.Add(Asset(assetName, file["Size"]!.GetValue<long>(), file["Sha256"]!.GetValue<string>(), tag));
            if (FileBytes.TryGetValue(name, out var body)) Bodies[Url(tag, assetName)] = body;
        }
        Bodies[Url(tag, "frontend-update.json")] = ManifestBytes;
        Release = new JsonObject { ["draft"] = false, ["prerelease"] = version.Contains('-'), ["tag_name"] = tag,
            ["html_url"] = "https://github.com/NOXEVYR/classicdesk/releases/tag/" + tag, ["target_commitish"] = Build, ["assets"] = assets };
    }
    public void SetManifestBytes(byte[] bytes)
    {
        ManifestBytes = bytes; var asset = Assets[0]!; asset["size"] = bytes.Length; asset["digest"] = "sha256:" + Hash(bytes);
        Bodies[asset["browser_download_url"]!.GetValue<string>()] = bytes;
    }
    public void SetFilePayload(string name, byte[] bytes)
    {
        FileBytes[name] = bytes;
        var file = ((JsonArray)Manifest["Files"]!).Single(f => f!["Name"]!.GetValue<string>() == name)!;
        file["Size"] = bytes.LongLength;
        file["Sha256"] = Hash(bytes);
        Rebuild();
    }
    static string Url(string tag, string asset) => "https://github.com/NOXEVYR/classicdesk/releases/download/" + tag + "/" + asset;
    static JsonObject Asset(string name, long size, string hash, string tag) => new() { ["name"] = name, ["size"] = size, ["digest"] = "sha256:" + hash, ["browser_download_url"] = Url(tag, name) };
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    public static HttpResponseMessage StreamResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableStream(bytes)) };
    public static HttpResponseMessage Redirect(string uri)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(uri); return response;
    }
}
sealed class UnseekableStream(byte[] bytes) : Stream
{
    readonly MemoryStream input = new(bytes);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
}
sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!.AbsoluteUri);
        var response = respond(request.RequestUri); response.RequestMessage = request; return Task.FromResult(response);
    }
}
sealed class ChainHost : IFrontendUpdateHost
{
    FrontendProcessIdentity? started;
    public int StartCount { get; private set; }
    public bool Acknowledged { get; private set; }
    public FrontendProcessIdentity? ReadProcess(int pid) => started?.Pid == pid ? started : null;
    public void Delay(TimeSpan delay) { }
    public FrontendProcessIdentity Start(string executablePath, string acknowledgementPath)
    {
        StartCount++;
        started = new FrontendProcessIdentity(9001, DateTime.UtcNow.Ticks, executablePath);
        FrontendUpdateInstaller.Acknowledge(acknowledgementPath, started);
        Acknowledged = true;
        return started;
    }
}
